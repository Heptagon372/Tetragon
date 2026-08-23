# Anti-Bot Fetch 백엔드 설계 (Akamai / TLS 지문 우회)

> 작성일 2026-08-23 · 대상: 공급처 수집(scraping) 경로의 봇 차단 대응
> 근거 문서: [ARCHITECTURE.md](ARCHITECTURE.md) · 관련 코드: [AmazonSupplierPlugin.cs](../src/Tetragon.Plugins.Suppliers/AmazonSupplierPlugin.cs), [Http.cs](../src/Tetragon.Plugins.Suppliers/Http.cs)

---

## 0. 왜 필요한가 (문제 정의)

현재 공급처 수집은 전부 `HttpClient("scraper")` 한 경로다 ([Http.cs](../src/Tetragon.Plugins.Suppliers/Http.cs)).
이 경로는 **HTTP 헤더**만 브라우저처럼 흉내낸다. 하지만 Akamai Bot Manager / Amazon의 봇 탐지는
헤더가 아니라 그 아래 계층에서 걸러낸다:

| # | 탐지 계층 | 현재 `HttpClient` 상태 | 대응 위치 |
|---|---|---|---|
| 1 | HTTP 헤더 / **TLS(JA3/JA4) 지문** | ❌ .NET 스택 고유 지문 노출 | fetch 사이드카 |
| 2 | **JavaScript 센서 실행** (`_abck` 쿠키) | ❌ JS 미실행 | fetch 사이드카 (헤드리스 브라우저) |
| 3 | 브라우저 지문 (navigator/WebGL/Canvas) | ❌ 없음 | stealth 플러그인 |
| 4 | 행동 분석 (마우스/스크롤/타이핑) | ❌ 없음 | 행동 시뮬레이터 |
| 5 | IP 평판 / Rate limit | ⚠️ 서버 IP 단일 | 주거용 프록시 풀 |

[AmazonSupplierPlugin.cs](../src/Tetragon.Plugins.Suppliers/AmazonSupplierPlugin.cs)의 주석이 1번을 실측으로 증언한다:
> "같은 헤더를 보내도 curl/브라우저는 정상 페이지(265KB)를, .NET HttpClient는 캡차 페이지(3.8KB)를 받는다."

**결론: 헤더 튜닝으로는 못 넘는다. 실제 브라우저 엔진 + 실제 IP가 필요하다.**

---

## 1. 설계 원칙 — 왜 사이드카인가

.NET 프로세스 안에 Playwright/Chromium을 띄우는 선택지도 있으나 다음 이유로 **분리한다**:

- **런타임 이질성**: 봇 우회 생태계(stealth 플러그인, 프록시 매니저, 캡차 SDK)는 Node.js에 압도적으로 성숙해 있다.
- **자원 격리**: Chromium은 인스턴스당 300~700MB. API 프로세스(가격계산·이벤트버스·SSE)와 메모리/크래시를 공유하면 안 된다.
- **수평 확장**: "Docker 기반 분산 실행"은 fetch 노드를 N개로 늘리는 것. API를 늘리는 것과 무관해야 한다.
- **차단 폭발 반경 격리**: 프록시 소진·IP 밴은 fetch 계층에 갇히고, 나머지 파이프라인은 정상 동작.
- **기존 구조 보존**: Tetragon의 플러그인 계약(OCP)을 그대로 유지 — 플러그인은 "어떻게 가져오는지" 모르고 포트만 안다.

```
┌─────────────────────────── Tetragon.Api (.NET, 단일 프로세스) ───────────────────────────┐
│  ISupplierPlugin (Amazon/Taobao/Ali/…)                                                    │
│        │  CollectAsync(url)                                                                │
│        ▼                                                                                   │
│  1) HttpClient("scraper") 로 직접 시도  ── 성공? → 파싱                                    │
│        │  차단(캡차/403/빈 HTML) 감지 시 에스컬레이션                                      │
│        ▼                                                                                   │
│  2) IBrowserFetcher (신규 포트)  ────────────► HTTP ────┐                                  │
└────────────────────────────────────────────────────────┼──────────────────────────────┘
                                                           ▼
                        ┌───────────── tetragon-fetch 사이드카 (Node + Playwright) ──────────────┐
                        │  REST: POST /fetch { url, waitFor, behavior, proxyPolicy, captcha }     │
                        │   ├─ 프록시 매니저 (주거용 IP 풀, 세션 스티키/로테이트)                │
                        │   ├─ playwright-extra + stealth (webdriver/지문 위장)                   │
                        │   ├─ 행동 시뮬레이터 (마우스·스크롤·타이핑, 무작위 대기)               │
                        │   ├─ 챌린지 감지 → 캡차 풀이 서비스 연동                                │
                        │   └─ 응답: { html, status, cookies, finalUrl, solvedChallenge }         │
                        └─────────────────────────────────────────────────────────────────────┘
                              ▲ Docker로 N개 복제 (docker compose --scale fetch=4)
```

---

## 2. .NET 쪽 변경 (최소 침습)

### 2.1 신규 포트 — `IBrowserFetcher`

`Tetragon.Plugin.Abstractions`에 추가. 플러그인은 이 계약만 안다.

```csharp
// src/Tetragon.Plugin.Abstractions/BrowserFetch.cs
namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// TLS 지문·JS 챌린지로 HttpClient가 차단되는 사이트를 실제 브라우저 엔진으로 가져온다.
/// 구현은 Infrastructure에 있고(외부 fetch 사이드카 호출), 플러그인은 계약만 안다.
/// 사이드카가 꺼져 있거나 미구성이면 IsAvailable == false → 플러그인은 직접 경로만 쓴다.
/// </summary>
public interface IBrowserFetcher
{
    bool IsAvailable { get; }
    Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest request, CancellationToken ct);
}

public sealed record BrowserFetchRequest
{
    public required Uri Url { get; init; }
    /// <summary>DOM 준비 판정용 CSS 셀렉터 (예: 상품명 요소). null이면 networkidle.</summary>
    public string? WaitForSelector { get; init; }
    /// <summary>행동 시뮬레이션 강도. Akamai 행동분석(4단계) 대응.</summary>
    public BehaviorProfile Behavior { get; init; } = BehaviorProfile.Human;
    /// <summary>프록시 지역/세션 정책. 공급처별로 다름(예: amazon.com→US, coupang→KR).</summary>
    public string ProxyPolicy { get; init; } = "residential-kr";
    /// <summary>캡차 자동 풀이 허용 여부(비용 발생). 기본 끔.</summary>
    public bool AllowCaptchaSolve { get; init; }
    /// <summary>같은 상품 여러 요청을 한 세션으로 묶기 위한 키(쿠키/IP 스티키).</summary>
    public string? SessionKey { get; init; }
    public string? CookieHeader { get; init; }
}

public enum BehaviorProfile { None, Light, Human }

public sealed record BrowserFetchResult
{
    public required int StatusCode { get; init; }
    public required string Html { get; init; }
    public string FinalUrl { get; init; } = "";
    /// <summary>세션 재사용용 쿠키(_abck 등). 다음 요청에 되돌려준다.</summary>
    public string? SetCookie { get; init; }
    public bool ChallengeSolved { get; init; }
    /// <summary>사이드카가 판단한 차단 여부(모든 재시도 소진 후에도 챌린지면 true).</summary>
    public bool Blocked { get; init; }
}
```

### 2.2 Infrastructure 구현 — `HttpBrowserFetcher`

`Tetragon.Infrastructure/External`에 추가. 이름 있는 `HttpClient("fetch-sidecar")`로 사이드카 REST를 호출.
자격/URL은 기존 `ICredentialProvider` 스코프로 조회(설정 화면 재사용).

```csharp
// scope "fetch": baseUrl, apiKey  (설정 → 시스템 → fetch 사이드카)
public sealed class HttpBrowserFetcher(
    IHttpClientFactory http, ICredentialProvider creds, ILogger<HttpBrowserFetcher> log)
    : IBrowserFetcher
{
    public bool IsAvailable => creds.HasKey("fetch", "baseUrl");

    public async Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest req, CancellationToken ct)
    {
        var baseUrl = creds.Get("fetch", "baseUrl")
            ?? throw new TransientScrapeException("fetch 사이드카 baseUrl 미설정");
        var client = http.CreateClient("fetch-sidecar");
        // POST {baseUrl}/fetch, X-Api-Key: creds.Get("fetch","apiKey")
        // 타임아웃: 브라우저 렌더+행동 시뮬레이션 고려해 60~90s
        // 5xx/네트워크 → TransientScrapeException (기존 재시도 파이프라인이 처리)
        // result.Blocked == true → TransientScrapeException (다른 프록시로 재시도 유도)
        ...
    }
}
```

DI 등록은 [Program.cs](../src/Tetragon.Api/Program.cs)에 한 줄. `HttpClient("fetch-sidecar")`도 여기서 이름 등록(기존 5종 옆에 6번째).

### 2.3 플러그인 에스컬레이션 패턴 (기존 플러그인 재사용)

기존 `CollectAsync`를 **직접 시도 → 차단 감지 → 브라우저 폴백** 2단계로 감싼다.
Amazon 플러그인이 대표 사례 — 지금은 차단 시 그냥 `PermanentScrapeException`을 던지는데,
`IBrowserFetcher.IsAvailable`이면 폴백하도록 바꾼다:

```csharp
// AmazonSupplierPlugin.CollectAsync 내부, 차단 감지 지점
if ((int)response.StatusCode == 503 || IsCaptchaPage(html))
{
    if (browserFetcher.IsAvailable)
    {
        var r = await browserFetcher.FetchAsync(new BrowserFetchRequest {
            Url = canonical,
            WaitForSelector = "#productTitle",
            ProxyPolicy = "residential-us",       // 통화=지역 이슈 대응
            Behavior = BehaviorProfile.Human,
        }, ct);
        if (!r.Blocked) { html = r.Html; /* 계속 파싱 */ }
        else throw new TransientScrapeException("브라우저 경로도 차단됨 — 프록시 소진 가능");
    }
    else throw new PermanentScrapeException(/* 기존 안내 메시지 */);
}
```

**핵심: 파싱 로직은 손대지 않는다.** 사이드카는 "차단 안 된 HTML"을 돌려줄 뿐, 그 뒤 정규화/번역/가격/등록 파이프라인은 그대로다.

> 왜 "직접 먼저, 브라우저는 폴백"인가: 브라우저 fetch는 직접 HTTP보다 **10~50배 느리고 비싸다**(프록시 트래픽 + 캡차 비용). 안 걸리는 요청까지 브라우저로 돌리면 낭비다. Amazon처럼 항상 걸리는 곳은 플러그인이 처음부터 브라우저 경로를 고르게 플래그를 둬도 된다.

---

## 3. Fetch 사이드카 설계 (Node + Playwright)

### 3.1 REST 계약

```
POST /fetch
  Body: BrowserFetchRequest(JSON)
  Resp: BrowserFetchResult(JSON)
GET  /healthz   → { ok, browsersUp, proxyPoolSize }
GET  /metrics   → prometheus (성공률, 차단률, 프록시별 밴율, 캡차 소비량)
```

### 3.2 구성 요소 (문서가 요구한 5가지 매핑)

| 요구사항 | 구현 | 세부 |
|---|---|---|
| **Playwright/Puppeteer + Stealth** | `playwright-extra` + `puppeteer-extra-plugin-stealth` | `navigator.webdriver` 제거, WebGL/Canvas/AudioContext 지문 위장, `chrome.runtime` 주입, 언어/플랫폼 일관성 |
| **한국 주거용 프록시** | 프록시 매니저 모듈 | 풀에서 `ProxyPolicy`에 맞는 IP 선택 → `browser.newContext({ proxy })`. 세션 스티키(SessionKey)로 `_abck` 유효기간 동안 같은 IP 유지, 밴 감지 시 로테이트 |
| **행동 시뮬레이션** | behavior.ts | `sleep(rand(3,6)s)`, 타이핑 글자당 80~200ms, 베지어 곡선 마우스 이동, 불규칙 스크롤 200~600px. `BehaviorProfile`로 강도 조절 |
| **캡차 풀이 연동** | captcha.ts (어댑터) | 챌린지 감지 시(＝`_abck`가 유효 형태로 안 떨어지거나 캡차 iframe 존재) 외부 풀이 서비스 호출. `AllowCaptchaSolve`일 때만(비용 게이트) |
| **Docker 분산 실행** | Dockerfile + compose | 무상태 컨테이너, `--scale fetch=N`. 앞단에 큐/로드밸런서. 컨테이너당 브라우저 컨텍스트 풀(예: 3개) |

### 3.3 요청 처리 흐름

```
POST /fetch
  1. 프록시 선택 (policy + sessionKey 스티키)
  2. context = browser.newContext({ proxy, userAgent, locale, viewport, timezone })
  3. stealth 적용된 page 생성 → cookieHeader 있으면 주입
  4. page.goto(url, waitUntil: 'domcontentloaded')
  5. 행동 시뮬레이션 (Behavior 프로필대로)
  6. waitForSelector 있으면 대기, 없으면 networkidle
  7. 챌린지 감지:
        - 정상 → html 추출
        - 캡차 iframe/인터스티셜 → (AllowCaptchaSolve면) 풀이 후 재검사, 아니면 Blocked
        - _abck 무효 → 프록시 로테이트 후 1회 재시도, 그래도면 Blocked
  8. cookies 수집(_abck 포함) → SetCookie로 반환
  9. context.close()  (컨텍스트는 매 요청 폐기, 브라우저 프로세스는 재사용)
```

### 3.4 디렉터리 (신규, .NET 솔루션 밖)

```
tetragon-fetch/                 # 사이드카 (별도 배포 단위)
├─ src/
│  ├─ server.ts                 # Fastify /fetch /healthz /metrics
│  ├─ browser-pool.ts           # Chromium 프로세스·컨텍스트 풀
│  ├─ stealth.ts                # playwright-extra + stealth 설정
│  ├─ proxy-manager.ts          # 주거용 IP 풀, 스티키/로테이트/밴 추적
│  ├─ behavior.ts               # 마우스/스크롤/타이핑/대기
│  ├─ challenge.ts              # Akamai/캡차 감지
│  └─ captcha/                  # 풀이 서비스 어댑터(인터페이스+구현)
├─ Dockerfile                   # mcr.microsoft.com/playwright 베이스
├─ docker-compose.yml           # fetch(scale=N) + 선택적 redis 큐
└─ package.json
```

---

## 4. 설정·자격증명 (기존 화면 재사용)

기존 `ICredentialProvider` 스코프 체계에 얹는다:

| scope | key | 예시 |
|---|---|---|
| `fetch` | `baseUrl` | `http://tetragon-fetch:8080` |
| `fetch` | `apiKey` | 사이드카 인증 |
| `fetch` | `proxy.residential-kr` | 프록시 제공사 접속 문자열 |
| `fetch` | `captcha.provider` / `captcha.key` | 캡차 서비스 |

`IsAvailable` 판정이 여기 달림 → 미설정이면 시스템은 **기존과 100% 동일하게** 직접 경로만 쓴다(안전한 기본값).

---

## 5. 운영·관측·비용 통제

- **차단률 모니터링**: 사이드카 `/metrics`를 프록시별·공급처별로 노출. 밴율 급증 = 센서 업데이트 신호.
- **캡차 비용 게이트**: `AllowCaptchaSolve` 기본 꺼짐 + 일일 소비 상한. 초과 시 자동 차단.
- **세션 재사용**: `_abck` 쿠키를 `SessionKey`로 캐시해 재획득 비용(가장 비싼 단계)을 줄인다.
- **유지보수 현실**: 참고 문서 지적대로 Akamai는 2~4주마다 센서를 바꾼다. stealth/behavior는 **버전 고정 + 정기 갱신** 대상. `IPlugin.Version`처럼 사이드카도 파서/우회 버전을 응답에 실어 회귀 추적.
- **레이트 리밋 재사용**: 마켓 쪽엔 이미 [CoupangThrottle.cs](../src/Tetragon.Plugins.Markets/CoupangThrottle.cs)가 있다. 공급처 fetch에도 동형 스로틀(공급처별 동시성·간격)을 사이드카 앞단에 둔다.

---

## 6. 단계별 도입 계획

| 단계 | 범위 | 산출물 | 검증 |
|---|---|---|---|
| **P0** | 포트 + 스텁 | `IBrowserFetcher` 계약, `HttpBrowserFetcher`(미설정 시 IsAvailable=false), DI 등록 | 기존 동작 무변화(회귀 0) |
| **P1** | 사이드카 MVP | Node+Playwright+stealth, `/fetch` 단일 프록시, 행동 Light | Amazon 1건 폴백 성공(캡차→정상 HTML) |
| **P2** | 프록시 풀 + 세션 | 주거용 풀, 스티키/로테이트, `_abck` 캐시 | 연속 50건 차단률 목표치 이하 |
| **P3** | 캡차 + 행동 Human | 캡차 어댑터, 정교한 행동, 비용 게이트 | 챌린지 통과율 측정 |
| **P4** | Docker 분산 | compose scale, 큐/LB, /metrics | 동시 N노드 처리량·안정성 |

각 단계는 독립 배포 가능하고, P0만 넣어도 시스템은 깨지지 않는다(폴백이 꺼진 상태 = 현재).

---

## 6.5 검증 로그 (2026-08-23 실측)

`tetragon-fetch/`에 Playwright + `puppeteer-extra-plugin-stealth`로 테스트 빌드를 만들어
**실제 쿠팡 서버**에 붙여 확인한 결과다 (probe: `src/probe.js`, `src/probe-flow.js`).

| 대상 | 방식 (이 머신 IP, 프록시 없음) | 결과 |
|---|---|---|
| 쿠팡 홈 `/` | headless + stealth + 실제 Chrome 채널 | ✅ 200, 1.3MB 실 콘텐츠, 챌린지 없음 |
| 카테고리 `/np/categories/…` | 동일 | ❌ `Access Denied` 313B (`errors.edgesuite.net`) |
| 검색 `/np/search?q=…` | 홈 예열 후 같은 컨텍스트로 진입 | ❌ `Access Denied` 462B |
| example.com (컨트롤) | 동일 | ✅ 정상 (셋업 정상 확인) |

핵심 관찰:
- **홈은 관대, 보호 엔드포인트(검색/카테고리/상품)는 엄격.** 후자는 검증된 `_abck`를 요구한다.
- **`_abck`가 검증 상태(`~0~`)로 넘어가지 않음** — 홈에서 예열해도 `-1`(봇 의심)에 머묾. 즉 Akamai JS 센서가 **headless 브라우저를 봇으로 판정**하고 유효 쿠키를 안 준다.
- `bm_sz`·`ak_bmsc`는 받음 = 센서는 돌지만 통과는 못 함.

**추가 실측 (rebrowser 엔진, 2026-08-23):** `rebrowser-playwright`(CDP `Runtime.enable` 누수 패치) +
stealth + 실제 Chrome 채널 + 홈 예열(25초, 실제 마우스/스크롤)로 재측정.
- 검색 엔드포인트: **403 Access Denied (303B)** — 미통과.
- `_abck`: 예열 내내 검증 상태(`~0~`)로 **끝내 넘어가지 않음.**
- 즉 엔진을 stealth→rebrowser로 올리고 warmup을 해도 결과 동일.

**headless 결론: headless는 (엔진·예열 무관) 통과 불가.** 홈은 같은 IP에서 통과하므로 IP 밴이 아니라 센서가 headless 텔레메트리를 거부해 `_abck`를 검증 안 하는 것이 원인.

### ✅ 성공 (2026-08-23) — Scrapling + headed + real Chrome

`Scrapling`의 `StealthyFetcher`(내부 `patchright` = CDP 탐지 패치 Playwright 포크)로 **보호 엔드포인트 통과 확인**:

| 조합 | 검색 `/np/search` 결과 |
|---|---|
| Scrapling + headless + real_chrome | ❌ 403 (455B) |
| **Scrapling + headed + real_chrome + 25s 예열** | ✅ **200 · 2.47MB · denied=false · 상품 100개 추출** |

**결정적 변수: `headed`(실제 창) + `real_chrome=True`(시스템 Chrome) + 홈에서 20~25초 대기 후 이동.**
프록시 없이 집(주거용) IP로 성공. 홈 예열이 `_abck`를 검증시키고, headed 창이 센서를 통과시킨다.

승리 레시피 (probe: `src/probe_scrapling.py`):
```python
StealthyFetcher.fetch(
    "https://www.coupang.com",      # 홈 먼저
    headless=False,                  # ★ headed 필수 (headless는 403)
    real_chrome=True,                # ★ 시스템 Chrome (번들 아님)
    humanize=True, network_idle=True,
    page_action=lambda page: (       # 홈 25초 대기 → 목표 이동
        page.wait_for_timeout(25000), page.goto(target)),
)
```

**남는 과제:** headed는 창을 띄우므로 서버 무인 실행엔 가상 디스플레이(Xvfb/전용 데스크톱)나 headed 유지 방식이 필요. 대량화 시 주거용 프록시로 IP 로테이션 병행. 사이드카 엔진을 이 Python 레시피로 교체 예정(§6.6 엔진 교체).

## 6.6 도구 선택 재검토 (레퍼런스 종합)

세 참고 문서([hashscraper], [Bright Data], r/webscraping)와 실측을 종합한 우선순위:

| 성공 요인 | 중요도 | 근거 |
|---|---|---|
| **주거용/모바일 프록시(한국)** | ★★★ 필수 | Akamai는 세계 최대급 IP 평판 DB 보유. AWS/Hetzner 등 데이터센터 IP는 **선차단·감점**. 오픈소스 우회 문서들도 전부 "주거용 IP 단일 실행" 기준. |
| **안티디텍트 브라우저** (stealth보다 강한 것) | ★★★ 필수 | `puppeteer-extra-stealth`(현재 사용)는 가장 약한 축. 실측서 headless 센서에 잡힘. 대안: **Camoufox**(Firefox 기반), **SeleniumBase UC 모드**, **NODRIVER**, Node면 **rebrowser-patches**. |
| **headed / new-headless** | ★★ | headless(구형)가 가장 탐지 쉬움. 실제 창 또는 `--headless=new`가 통과율↑. |
| **정적/JSON은 TLS 임퍼소네이션** | ★★ | 브라우저 불필요한 API·정적 페이지는 `curl_cffi`(Python) / `curl-impersonate`로 JA3/JA4 위장이 더 싸고 안정적. |
| **세션 예열 + `_abck` 재사용** | ★ | 홈→목표 순서, 검증된 쿠키 캐시. 단, 센서가 통과해줘야 의미 있음(위 프록시·브라우저 전제 충족 후). |
| **캡차/센서 풀이 서비스** | ★ (비용) | 위를 다 해도 안 되면. Akamai v3 센서 데이터 자체를 다루는 상용 서비스. |

**설계 반영:** §3의 사이드카는 `stealth.ts`를 **엔진 교체 가능**하게 둔다(어댑터). MVP는 stealth,
안 되면 Camoufox/rebrowser로 교체. `ProxyPolicy`는 **주거용 프록시 필수**로 격상(프록시 없으면 보호 엔드포인트는 실패 처리).

> [hashscraper]: https://blog.hashscraper.com/posts/coupang-crawling-2026-complete-guide-everything-about-akamai-bypass
> [Bright Data]: https://brightdata.co.kr/blog/web-data/bypass-akamai-bot-detection

## 7. 리스크·전제

- **법적/ToS**: 대상 사이트 약관 위반 소지 및 저작권(상세 이미지/HTML — 이미 [Supplier.cs](../src/Tetragon.Plugin.Abstractions/Supplier.cs)의 `DetailImageLicense`가 이 문제를 다룬다) 확인은 도입 전 필수. 합법 대안(공식 API: Amazon PA-API, 쿠팡 파트너스 등)이 있으면 우선.
- **비용**: 주거용 프록시(트래픽 과금) + 캡차(건당 과금)가 주요 변동비. 폴백 최소화 설계(§2.3)가 곧 비용 통제.
- **깨지기 쉬움**: 우회는 본질적으로 lagging — 센서 업데이트마다 유지보수 비용 발생. 이 문서의 §5 관측이 조기경보 역할.
