# tetragon-fetch

Tetragon 공급처 수집의 **봇 차단 우회 fetch 사이드카** (Playwright + stealth).
설계: [../docs/ANTIBOT-FETCH-DESIGN.md](../docs/ANTIBOT-FETCH-DESIGN.md)

.NET 쪽 `IBrowserFetcher`(`HttpBrowserFetcher`)가 이 사이드카의 `POST /fetch`를 호출한다.
TLS 지문·JS 챌린지(Akamai 등)로 `HttpClient`가 막히는 사이트를 실제 브라우저로 가져온다.

## 설치

```bash
npm install
npx playwright install chromium   # rebrowser-playwright 버전용. 또는 CHANNEL=chrome로 실제 Chrome 사용
```

> 엔진: `rebrowser-playwright`(CDP `Runtime.enable` 누수 패치) + `puppeteer-stealth` 이중 방어.
> 로컬에서 rebrowser용 브라우저를 안 받았으면 **`CHANNEL=chrome`**(설치된 실제 Chrome)로 실행하면 된다.

## 검증 probe (실측 도구)

```bash
node src/probe.js "https://www.coupang.com/" --channel chrome        # 단일 URL 진단
node src/probe-flow.js 무선마우스 --channel chrome                    # 홈 예열→검색→상세 플로우
node src/probe.js "<url>" --channel chrome --proxy http://ID:PW@host:port   # 프록시 경로
```

probe는 status·`_abck`/`bm_sz` 쿠키 상태·챌린지 문구·콘텐츠 셀렉터로 통과 여부를 판정한다.
**실측 결과(프록시 없음): 쿠팡 홈은 통과, 보호 엔드포인트(검색/카테고리/상품)는 `Access Denied`.**
보호 엔드포인트는 **주거용 프록시 + 강한 안티디텍트 브라우저**가 필요하다 (설계문서 §6.5~6.6).

## 서버 실행

### ✅ 권장: Python · Scrapling 사이드카 (검증된 우회 엔진)

실측에서 유일하게 쿠팡 보호 엔드포인트를 통과한 엔진. `.NET HttpBrowserFetcher`와 동일한 `POST /fetch` 계약.

```bash
pip install -r requirements.txt
scrapling install && patchright install --force chromium   # 최초 1회 (시스템 Chrome 있으면 생략 가능)
python src/server.py                                        # 기본 headed(창) + real_chrome, :8080
# 옵션:  PORT=8080  API_KEY=secret  PROXY=http://ID:PW@host:port  python src/server.py
#        HEADLESS=1  → headless (통과율↓, 서버 무인용 아니면 비권장)
```

승리 레시피: `headed` + `real_chrome` + 홈 25초 예열 → 목표 이동. 검색 엔드포인트 `200 · 2.4MB · 상품 100개` 실측 통과.
`behavior`(None/Light/Human)가 홈 예열 시간(0 / 12s / 25s)을 정한다.

> ⚠️ **headed가 통과의 핵심**이라 기본이 headed(창이 뜸). 무인 서버는 Xvfb 등 가상 디스플레이 위에서 실행.
> 동시성: headed 창 안정성 때문에 한 번에 한 요청만 처리(내부 락).

### 대안: Node · Playwright 사이드카 (headless, 보호 엔드포인트 미통과)

```bash
CHANNEL=chrome PORT=8080 node src/server.js
# 프록시/키:  PROXY=http://ID:PW@host:port  API_KEY=secret  CHANNEL=chrome  node src/server.js
```

headless라 홈 등 관대한 페이지 외 보호 엔드포인트는 막힌다(§검증 로그). 참고/폴백용.

- `GET /healthz` → `{ ok, browserUp }`
- `POST /fetch` body: `{ url, waitForSelector?, behavior?(None|Light|Human), cookieHeader?, proxy?, warmupHost? }`
  → `{ statusCode, html, finalUrl, setCookie, challengeSolved, blocked }`

## Docker 분산

```bash
docker compose up --build --scale fetch=4
```

## .NET 연동 (설정)

`ICredentialProvider`의 `fetch` 스코프에 등록하면 `IBrowserFetcher.IsAvailable`이 켜진다:

| scope | key | 값 |
|---|---|---|
| fetch | baseUrl | `http://localhost:8080` |
| fetch | apiKey | 서버 `API_KEY`와 동일 |
| fetch | proxy.residential-kr | `http://ID:PW@host:port` |
| fetch | proxy.residential-us | (아마존 등 지역별) |

미설정이면 `IsAvailable=false` → 플러그인은 직접 경로만 쓰고 시스템 동작은 도입 전과 동일하다.

## 엔진 교체

`src/server.js`의 `getBrowser()`/stealth 부분만 교체하면 된다. 통과율이 부족하면
Camoufox·SeleniumBase UC·rebrowser-playwright로 바꾼다 (설계문서 §6.6).
