# Tetragon — 구매대행·위탁판매 자동화 플랫폼

해외 쇼핑몰 상품 URL을 붙여넣으면 **수집 → 표준화 → 번역 → 가격계산 → 컴플라이언스 검사 → 국내 오픈마켓 등록**까지 자동으로 처리하는 대량등록 플랫폼입니다.

`mango-platform-design.md` 설계서(Papaya)를 기반으로 구현했습니다.

---

## 빠른 시작

**`start.bat` 을 더블클릭하세요.** 서버 두 개를 띄우고 준비되면 브라우저를 자동으로 엽니다.

| 파일 | 하는 일 |
|---|---|
| `start.bat` | 기존 서버 정리 → (첫 실행 시) 패키지 설치 → API·웹 기동 → 브라우저 열기 |
| `stop.bat` | 두 서버 종료 |

처음 실행하면 프론트엔드 패키지 설치로 1~2분 걸리고, 이후에는 20초쯤이면 뜹니다. `.NET 8 SDK`와 `Node.js`가 없으면 설치 링크를 안내하고 멈춥니다.

수동으로 실행하려면:

```bash
dotnet run --project src/Tetragon.Api --urls http://localhost:5080
```

```bash
cd frontend && npm install && npm run dev
```

- 웹 UI: <http://localhost:5173>
- API 문서(Swagger): <http://localhost:5080/swagger>

**API 키 없이 바로 전체 파이프라인을 시험할 수 있습니다.** 수집 화면에서 아래 URL을 붙여넣으세요 — 시뮬레이션 공급처가 처리합니다.

```
https://mock.shop/item/1001.html
https://mock.shop/item/1002.html
```

DB는 `src/Tetragon.Api/bin/Debug/net8.0/tetragon.db`(SQLite)에 자동 생성되며, 기본 가격 정책과 금지어 16종이 시드됩니다.

---

## 아키텍처

설계서의 Clean Architecture + Plugin Architecture를 그대로 따릅니다. **의존 방향은 항상 바깥 → 안이며, Domain은 아무것도 참조하지 않습니다.**

```
Tetragon.Api              REST + SSE + Swagger (Presentation)
      ↓
Tetragon.Application      UseCase, 파이프라인 핸들러, Port 정의
      ↓
Tetragon.Domain           Product/ScrapeJob/Listing/Order 애그리거트, 상태 머신, Rule 계약
      ↓
Tetragon.SharedKernel     Entity, Money, LocalizedText, IntegrationEvent

Tetragon.Infrastructure   EF Core(SQLite), 인메모리 EventBus, 환율 Provider, 레지스트리
Tetragon.Plugin.Abstractions   ISupplierPlugin / IMarketplaceAdapter / IAiProviderPlugin
Tetragon.Plugins.*        공급처·마켓·AI 구현체
frontend/                 React + TypeScript + Vite
```

### 설계서 대비 대체 구현

로컬 실행을 위해 인프라를 축소했지만, **모두 Port 뒤에 격리되어 있어 교체 시 도메인/애플리케이션 코드는 수정하지 않습니다.**

| 설계서 | 현재 구현 | 교체 지점 |
|---|---|---|
| PostgreSQL + MongoDB | SQLite (JSON 컬럼으로 원본 보존) | `TetragonDbContext`, `DependencyInjection.AddTetragonCore` |
| RabbitMQ Topic Exchange | `System.Threading.Channels` 인메모리 버스 | `IEventBus` / `InMemoryEventBus` |
| SignalR 실시간 푸시 | SSE (`GET /api/v1/stream`) | `IPipelineNotifier` |
| Elasticsearch 조회 모델 | SQLite 직접 조회 | `IProductRepository.SearchAsync` |
| 멀티테넌시 RLS | `TenantId` 컬럼 유지, 단일 테넌트로 동작 | `Tenant.Default` |

`DateTimeOffset`은 SQLite가 `ORDER BY`에 쓸 수 없어 `ConfigureConventions`에서 이진 변환합니다. PostgreSQL로 옮길 때 이 설정만 제거하면 됩니다.

---

## 핵심 엔진

### 가격 Rule Engine (설계서 5.4)

Rule은 순서를 가진 파이프라인이고, 정책은 Rule의 조합입니다. **새 정책 = 새 `IPriceRule` 구현체 등록만으로 적용되며 기존 Rule은 수정하지 않습니다(OCP).**

기본 정책 6단계 — 실제 계산 추적 예시(실시간 환율 사용):

```
원가 281.9 CNY
  exchange-rate     282 →  61,229   CNY→KRW 환율 217.20
  intl-shipping  61,229 →  67,229   해외배송비 +6,000원
  tariff-vat     67,229 →  67,229   목록통관 기준(150 USD) 이하 — 면세
  margin         67,229 →  87,398   마진 30%
  market-fee     87,398 → 100,458   수수료 13% 역산 (판매가 ÷ 0.87)
  psych-rounding 100,458 → 99,900   100원 단위 절사, 끝자리 900
최종 판매가 99,900원
```

모든 단계는 `PriceCalculation`에 스냅샷으로 저장되어 "이 가격이 왜 나왔는지" 상품 상세와 시뮬레이터에서 그대로 확인할 수 있습니다.

환율은 `open.er-api.com`에서 실시간 조회하고 1시간 캐시합니다(키 불필요). 장애 시 캐시 → 정적 폴백 순으로 내려갑니다.

### Compliance Engine (설계서 5.5)

Aho-Corasick 오토마타로 상품명·상세·옵션을 한 번에 검사합니다(O(n), 수백만 건 대비). `Pass` / `Warn` / `Block` 3단계이며 **Block은 파이프라인을 중단하고 사용자 확인 큐로 보냅니다.** 확인 후 `POST /products/{id}/resume`로 재개합니다.

기본 시드: 상표권(나이키·샤넬 등), 표시광고법(최저가·1위), 약사법(의약품·치료) 등 16종.

### 파이프라인 (설계서 6.1)

```
scrape.requested → ProductCollected → ProductNormalized → ProductEnriched
    → PriceCalculated → ComplianceChecked → (Pass) listing.requested → MarketplaceRegistered
                                          → (Block) 사용자 확인 큐
```

각 핸들러는 자기 단계만 처리하고 다음 이벤트를 발행하며, 컨텍스트 간 DB를 직접 참조하지 않습니다. 모든 이벤트는 `correlationId`(= JobId)로 추적됩니다.

**실패 처리:** 일시적 실패는 지수 백오프로 3회 재시도 후 DLQ, **영구 실패(삭제된 상품, 자격증명 누락)는 재시도 없이 즉시 DLQ**로 보냅니다. DLQ Job은 대시보드에 노출되고 `POST /jobs/{id}/retry`로 재처리할 수 있습니다.

---

## 플러그인

새 공급처·마켓·AI는 인터페이스 구현 + `Program.cs`에 한 줄 등록으로 추가됩니다. 기존 코드는 수정하지 않습니다.

`IsLive`(실연동 여부)와 `IsAvailable`(지금 사용 가능한지)은 별개입니다. 실연동 플러그인이라도 자격증명이 없으면 `IsAvailable = false`이고, AI 라우터는 이를 후보에서 제외해 시뮬레이션으로 폴백합니다.

### 공급처

| 코드 | 카테고리 수집 | 상태 | 비고 |
|---|---|---|---|
| `simulated` | ✅ | ✅ 검증됨 | `mock.shop`, `demo.*`, `example.*` 호스트. 키 불필요 |
| `11st` | ❌ | ✅ **검증됨** | 11번가. JSON-LD 스크래핑, **키 불필요** |
| `domeggook` | ✅ | ✅ **검증됨** | 도매꾹/도매매. 공식 OpenAPI (키 필요) |
| `taobao` | ❌ | ⚠️ 로그인 쿠키 필요 | mtop API + `_m_h5_tk` MD5 서명 |
| `aliexpress` | ❌ | ⚠️ 공식 API 키 필요 | 상세 페이지 CSR (아래 참조) |
| `amazon` | ❌ | ⛔ 사실상 차단됨 | TLS 지문 차단 (아래 참조) |

**도매꾹/도매매** — 공식 OpenAPI(`openapi.domeggook.com`)를 사용합니다. **카테고리 단위 대량 수집을 지원하는 유일한 실연동 공급처**이며, 실제 API 키로 카테고리 조회 → 목록 → 상세 수집 → 가격 계산까지 검증했습니다. 국내 도매라 통화가 KRW이고 번역 단계가 필요 없습니다.

실제 API에서 확인한 제약 세 가지 (구현에 반영됨):

1. **모드마다 버전이 다릅니다.** `getCategoryList`는 `ver=1.0`, `getItemList`는 `4.1`, `getItemView`는 `4.4`입니다. 틀리면 "해당 오픈 API 서비스가 없습니다"가 뜹니다.
2. **대분류로는 상품을 조회할 수 없습니다.** 카테고리 코드는 `01_07_03_00_00`(대_중_소_세_세세) 5단이며, 대분류(`XX_00_00_00_00`)를 조건으로 주면 "최소 한 가지 이상의 검색 조건" 오류가 납니다. 중분류 이하를 쓰거나 검색어를 함께 줘야 합니다. UI는 대분류를 `하위 선택 필요`로 표시해 선택 자체를 막습니다.
3. **가격이 수량별 단가 문자열로 옵니다.** `"1+4664|50+4650|100+4620"`(수량+단가) 형식과 단일가 `"1180"` 두 가지가 섞여 옵니다. 최소 수량 구간의 단가를 기준가로 씁니다.

옵션은 `selectOpt`에 **JSON 문자열로 한 번 더 감싸여** 오고, 옵션별 재고는 제공되지 않아 전체 재고를 옵션 수로 배분합니다. 최소구매수량(MOQ)과 공급처 재고는 상품 속성에 저장됩니다.

> 도매꾹은 국내 도매라 **기본 가격 정책의 해외배송비 6,000원이 그대로 붙습니다.** 가격 정책 화면에서 국내용 정책(해외배송비·관세 Rule 제거)을 따로 만들어 수집 시 지정하세요.

**11번가** — 상세 페이지가 schema.org JSON-LD를 서버사이드로 내려줍니다. 실제 상품으로 수집까지 검증했습니다. 다만 검색·카테고리 목록 페이지는 클라이언트 렌더링(2.4KB 셸)이라 카테고리 수집은 지원하지 않습니다.

**아마존 — 실측 결과 서버사이드 수집이 막혀 있습니다.** 상세 페이지 자체는 서버 렌더링이지만, 아마존이 **TLS/HTTP 지문으로 클라이언트를 식별**합니다. 같은 헤더를 보내도 curl은 정상 페이지(265KB)를, .NET HttpClient는 캡차 페이지(3.8KB)를 받습니다. 헤더 조정이나 재시도로는 넘을 수 없어 영구 실패로 처리하고 PA-API를 안내합니다. 파싱 로직 자체는 동작하므로(상품명·이미지·최저 오퍼가 추출 확인) 프록시나 헤드리스 브라우저를 경유하면 사용할 수 있습니다.

> 아마존은 봇 요청에 바이박스 가격을 렌더링하지 않는 경우가 많습니다. 그럴 때 마켓플레이스 최저 오퍼가를 쓰며, 상품 속성의 `가격출처`에 어느 쪽인지 표시합니다.

**AliExpress에 대한 중요한 사실 — 실측 확인:** 2026년 현재 AliExpress 상세 페이지는 **완전 클라이언트 사이드 렌더링**입니다. 서버가 주는 HTML에는 `og:` 메타태그(상품명·대표이미지)만 있고 `window.runParams`는 빈 객체, `<title>`도 빈 문자열이며 **가격·옵션·재고는 서명이 필요한 XHR로만 로드**됩니다. PC/모바일 페이지, 내부 API 엔드포인트를 모두 확인한 결과입니다.

따라서 이 플러그인은 두 모드로 동작합니다.

- **공식 Open Platform 자격증명(`app_key`/`app_secret`/`access_token`)이 있으면** → `aliexpress.ds.product.get`을 HMAC-SHA256 서명으로 호출해 가격·옵션까지 완전 수집합니다.
- **없으면** → 페이지에서 상품 존재 여부와 이름만 확인하고, 가격을 가져올 수 없는 이유와 해결 방법을 담은 오류를 남깁니다. 재시도하지 않습니다.

```
AliExpress 상품 '삼성 갤럭시 A53 A54 A55 S24 S25 용 투…'을(를) 찾았지만 가격·옵션을 가져올 수 없습니다.
AliExpress 상세 페이지는 클라이언트 렌더링이라 가격이 HTML에 없습니다.
설정 → 공급처 → aliexpress에 Open Platform의 app_key / app_secret / access_token을 등록하세요.
```

삭제·판매종료된 상품은 별도로 구분합니다(`og:title`이 빈 문자열인 것이 유일하게 신뢰할 수 있는 신호 — `PAGE_NOT_FOUND_*` 문자열은 모든 페이지의 i18n 번들에 들어 있어 판단 근거로 쓸 수 없습니다).

> 공식 API 경로는 자격증명이 없어 실호출 검증을 하지 못했습니다. 서명 방식과 응답 매핑은 문서 기준으로 구현했습니다.

### 마켓

| 코드 | 등록 경로 | 인증 방식 |
|---|---|---|
| `mockmarket` | ✅ API (검증됨) | 없음. 유효성 검사까지 시뮬레이션 |
| `smartstore` | ⚠️ API | OAuth2 + bcrypt 전자서명, 상품등록 v2 |
| `coupang` | ⚠️ API | HMAC-SHA256 (CEA 스킴), 구매대행(`AGENT_BUY`) |
| `11st` | ⚠️ API | 셀러오피스 OpenAPI (`openapikey` 헤더, XML) |
| `auction` / `gmarket` | 📄 **엑셀 전용** | ESM Plus 대량등록 (아래 참조) |

**옥션·G마켓은 일반 판매자에게 상품등록 API가 열려 있지 않습니다.** 공식 경로는 ESM Plus 대량등록 엑셀 업로드이거나 제휴 솔루션사 API(별도 계약)뿐입니다. 그래서 이 두 어댑터는 API 호출을 흉내내지 않고 `USE_EXCEL` 오류와 함께 엑셀 경로를 안내합니다. 실제 등록은 **엑셀 내보내기 → ESM Plus** 로 하며, 하나의 파일로 옥션·G마켓에 동시 등록됩니다.

> 스마트스토어·쿠팡·11번가는 실제 판매자 계정이 필요해 실호출 검증을 하지 못했습니다. 서명·요청 포맷은 각 API 문서 기준입니다. 쿠팡 어댑터는 `requested: false`로 등록해 자동 판매요청이 나가지 않도록 했습니다.

### AI

| 코드 | 상태 | 비고 |
|---|---|---|
| `simulated-ai` | ✅ 검증됨 | 중국어 상품용어 사전 기반 치환 번역. 키 불필요 |
| `claude` | ⚠️ API 키 필요 | `claude-opus-5`, 구조화 출력으로 입력 순서와 1:1 대응 보장 |

프롬프트는 `PromptTemplates`에 분리해 두었습니다(설계서 5.3의 버전 관리 대상).

---

## 위탁판매 물류 (출고지·반품지)

위탁판매는 판매자가 재고를 갖지 않습니다. 상품은 **공급처 창고에서 구매자에게 바로** 가고 반품도 공급처로 돌아갑니다. 따라서 마켓에 등록할 출고지·반품지는 판매자 주소가 아니라 **상품마다 다른 공급처 주소**여야 합니다. 이걸 판매자 주소로 등록하면 반품이 엉뚱한 곳으로 갑니다.

도매꾹 API는 이 정보를 전부 제공합니다 (실측 확인):

```
seller.company.addr → 출고지    반품배송비 → return.deliAmt (왕복이면 ×2)
return.addr        → 반품지 + 우편번호 + 연락처
deli.feeExtra      → 제주·도서산간 추가 배송비
qty.domeMoq        → 최소구매수량(MOQ)
```

수집한 값은 `LogisticsKeys` 규약으로 Product 속성에 담기고, `ConsignmentLogistics`로 타입이 붙어 마켓 어댑터와 엑셀 양식까지 흘러갑니다. 공급처가 값을 주지 않으면 설정 화면의 판매자 기본값으로 폴백합니다.

**쿠팡은 주소를 직접 넣을 수 없습니다.** 사전 등록된 `outboundShippingPlaceCode` / `returnCenterCode`를 요구하므로, 공급처 주소를 쿠팡에 출고지·반품지로 등록하고 받은 코드를 씁니다. 전략은 **재사용 → 생성 → 폴백** 3단계입니다.

1. 같은 이름(`[위탁] {공급사} 출고지`)이나 주소의 배송지가 이미 있으면 그 코드를 씁니다
2. 없으면 `POST v4/vendors/{id}/outboundShippingCenters`로 새로 만듭니다
3. 생성이 실패해도 판매자의 기존 배송지로 **등록은 계속 진행합니다** — 배송지 문제로 판매 기회를 잃지 않기 위함입니다

`ShippingPlaceMapping`이 (마켓 × 주소)로 코드를 캐시해, 같은 공급처 상품이 수천 개여도 출고지는 한 번만 만들어집니다. 엑셀 양식과 11번가는 주소 문자열을 그대로 받으므로 이 과정이 필요 없습니다.

> **반품지는 자동 생성이 막혀 있습니다.** v4는 `The shipping goods flow info can't be null`, v5는 전화번호를 E.164(`+82327215737`)로 고쳐도 `500 INTERNAL_SERVER_ERROR`가 납니다. 굿스플로(반품 회수 서비스) 계약이 필요한 계정 조건으로 보이며 재시도로는 해결되지 않습니다.
>
> **상품의 `returnAddress`·`returnZipCode` 텍스트 필드에는 공급처 주소가 정상 반영됩니다.** 다만 WING 화면과 실제 반품 회수는 `returnCenterCode`를 따르므로, 공급처로 반품을 받으려면 **WING → 판매자정보 → 반품지 관리에서 공급처 주소를 한 번 등록**해야 합니다. 등록해 두면 이름(`[위탁] {공급사} 반품지`)이나 주소로 자동 매칭해 이후 상품에 그 코드를 씁니다. 그 전까지는 판매자 기존 반품지로 폴백하며, 이때 **반품이 공급처가 아닌 판매자 주소로 갑니다.**

## 출고 소요일 자동 계산

마켓에 표기하는 출고 소요일은 공급처 실적에서 계산합니다. 도매꾹은 `deli.sendAvg`로 평균 출고일을 제공합니다(0.1 = 사실상 당일출고).

```
마켓 표기일 = ceil(공급처 평균 출고일 + 1일)
0.1일 → 2일    0.4일 → 2일    2.0일 → 3일
```

여유 1일을 더하는 이유는 **위탁판매의 구조** 때문입니다. 주문이 들어와도 우리가 공급처에 발주해야 공급처가 출고를 시작하므로, 공급처 평균을 그대로 약속하면 그 발주 시차만큼 지연이 납니다. 지연이 쌓이면 마켓 페널티를 받습니다. 평균값을 못 얻으면 보수적으로 3일로 둡니다.

## 상세설명 이미지 라이선스

공급처 상세페이지 이미지는 **재사용이 허용된 경우에만** 가져옵니다. 도매꾹은 `desc.license.usable`로 허용 여부를 명시합니다.

도매꾹은 상세 영역을 용도별로 나눠 주는데, **가져올 것은 `desc.contents.item` 하나뿐**입니다.

| 필드 | 내용 | 사용 |
|---|---|---|
| `desc.contents.item` | **상품상세** — '더보기'를 눌렀을 때 나오는 길다란 이미지 | ✅ |
| `desc.contents.deli` | 배송 안내 | ❌ |
| `desc.contents.event` | 이벤트 배너 | ❌ |
| `desc.contents.otherItem` | 다른 상품 홍보 | ❌ |
| `desc.notice` | 공급사 공지사항 (상품과 무관) | ❌ |

`notice`나 `otherItem`을 가져오면 남의 공지와 타상품 광고가 우리 상세페이지에 실립니다. 실제로 한 상품에서 `notice`는 이미지 8장, `item`은 1장(860×10,432px)이었습니다.

| 라이선스 | 상품 화면 | 마켓 등록 |
|---|---|---|
| 허용 | 상세 이미지 갤러리 표시 + `사용 허용` 배지 | 공급처 상세 HTML을 그대로 등록 |
| 불허 | `사용 불가` 배지와 사유 표시 | 대표 이미지와 상품명으로만 상세페이지 구성 |

허용되지 않은 이미지를 마켓에 올리면 저작권 문제가 되므로, 수집 단계에서부터 아예 가져오지 않습니다. 상세 HTML은 용량이 커서(수 KB) 상품 상세 응답에 넣지 않고 `GET /api/v1/products/{id}/detail-html`로 분리했습니다.

> HTML 속성에서 뽑은 URL은 `&amp;`가 인코딩된 상태라 그대로 쓰면 깨집니다 — 추출 시 HTML 디코딩을 거칩니다.

## 쿠팡 카테고리 자동 결정

위탁판매는 상품이 수천 개라 카테고리를 사람이 고를 수 없습니다. 쿠팡 추천 API로 상품명에서 카테고리를 예측하고, 그 카테고리가 요구하는 필수 항목을 메타 API로 조회해 자동으로 채웁니다.

```
상품명 → POST openapi/.../categorization/predict → 카테고리 코드
       → GET seller_api/.../category-related-metas/... → 필수 구매옵션 + 고시정보
       → 값 자동 생성 → 등록
```

필수 구매옵션은 타입에 맞게 값을 만들어야 합니다 (실측으로 확인한 함정들):

- **숫자형**은 값+단위 형식(`"1개"`)이며, 단위는 `basicUnit`이 아니라 **`usableUnits` 안에서** 골라야 합니다. `개당 수량`은 `basicUnit`이 "개"인데 허용 단위는 개입/롤/매/매입/세트라 `"1개"`를 보내면 거부됩니다.
- **SELECT형**은 `inputValues` 중에서만 고를 수 있습니다. `"상세페이지 참조"`를 넣으면 거부됩니다.
- **고시정보**는 카테고리마다 필수 항목이 다릅니다(가방 5종, 의료기기 10종). 하나라도 빠지면 거부됩니다.

## 금고 — 발주 자금

위탁판매의 자금 흐름은 한쪽으로 어긋나 있습니다. 구매자가 마켓에 결제해도 **정산까지 수일~수주 걸리는데, 공급처에는 먼저 돈을 내고 발주해야** 합니다. 그 사이를 메우는 운전자금을 담는 곳이 금고입니다.

```
총 잔액 − 발주 예약 = 사용 가능 금액
```

**예약(Reserve) → 확정(Capture) 2단계**로 처리합니다. 발주를 시작할 때 금액을 예약해 두고, 공급처 결제가 확인되면 지출로 확정합니다. 실패하면 예약을 되돌립니다. 이렇게 하지 않으면 여러 주문이 동시에 발주될 때 같은 잔액을 두 번 쓰게 됩니다.

잔액이 부족하면 **발주 자체가 차단**되고 부족분이 표시됩니다. 경고선(기본 10만원) 아래로 떨어지면 미리 알려 줍니다. 모든 입출금은 이력에 남아 잔액이 왜 이 값인지 되짚을 수 있습니다.

## 자동 발주

```
고객 → 쿠팡 주문 → (수집) → Tetragon → 도매꾹 발주 → 공급사가 고객에게 직배송
```

발주 전에 **점검(preflight)**을 먼저 돌려 돈을 쓰기 전에 무엇이 막고 있는지 보여줍니다.

| 점검 항목 | 막히면 |
|---|---|
| 주문 상태 | 이미 처리된 주문이면 차단 |
| 공급처 연결 | 어느 상품에서 왔는지 모르면 차단 |
| 구매자 배송지 | 보낼 주소가 없으면 차단 |
| 금고 잔액 | 부족분을 원 단위로 표시하고 차단 |
| 공급처 발주 권한 | 사유와 해결 방법을 안내 |

> **도매꾹 Open API 키로는 자동 발주가 되지 않습니다.** 주문 관련 mode(`setOrder`·`addOrder`·`getOrderList` …)를 모든 버전으로 호출해도 전부 `403 요청한 API에 대한 호출 권한이 없습니다`가 돌아옵니다. 도매꾹은 상품 조회(Open)와 주문(Private) 스코프를 분리해 두었고, 주문 스코프는 별도 신청·승인이 필요합니다.
>
> 그래서 **「공급사 사이트에서 주문하기」** 버튼으로 수동 발주 경로를 함께 제공합니다. 상품 링크로 바로 이동하고, 배송지는 한 줄 복사해 붙여넣을 수 있습니다. 주문 후 공급처 주문번호와 실제 결제액을 입력하면 마진이 실측 기준으로 정확해집니다. Private 권한을 받으면 코드 수정 없이 자동 발주로 전환됩니다.

## 주문 → 공급처 발주

주문이 들어오면 판매자가 할 일은 하나입니다: **공급처에서 그 상품을 사되 배송지를 구매자 주소로 지정하는 것.** `발주서` 화면이 그에 필요한 것을 한곳에 모읍니다.

| | 내용 |
|---|---|
| ① 무엇을 | 상품·옵션·수량, 공급처 MOQ 경고 |
| ② 어디서 | 공급처명·연락처, **공급처 상품 링크**(클릭 시 바로 주문 페이지) |
| ③ 누구에게 | 구매자 수령인·연락처·주소·요청사항 (한 줄 복사 버튼) |
| ④ 얼마에 | 판매금액 − 공급가 = 마진, **손실 주문 자동 경고** |

주문은 마켓 상품번호 → Listing → Product로 역추적해 공급처가 자동 연결됩니다. 발주 후 공급처 주문번호와 실제 결제액을 입력하면 마진이 실측 기준으로 정확해집니다.

> 도매꾹은 외부 발주 API를 제공하지 않아 완전 자동 발주는 불가능합니다. 사람이 클릭 몇 번으로 끝낼 수 있게 만드는 것이 현실적인 최선입니다.

## 카테고리 단위 대량 수집

URL을 하나씩 넣는 대신 **카테고리를 통째로 훑어** 수집합니다.

```
카테고리 선택 → 훑기(페이지네이션) → 상품별 ScrapeJob 생성 → 기존 파이프라인이 처리
```

- **중복 자동 제외**: 이미 수집한 상품(공급처 + 원본 상품 ID)은 건너뜁니다
- **상한 강제**: 작업당 최대 1000건, 페이지 간 0.6초 지연으로 공급처 부하 관리
- **미리보기**: 수집 전에 어떤 상품이 걸리는지 확인할 수 있습니다
- 훑기(`ICategoryCrawler`)와 수집(`ISupplierPlugin`)을 분리해, 카테고리 목록만 지원하지 않는 공급처도 개별 수집은 그대로 동작합니다

## 엑셀 대량등록

국내 마켓 상당수가 API 대신 엑셀 업로드를 표준 경로로 씁니다. 그래서 엑셀 생성은 부가 기능이 아니라 **API 등록과 동등한 등록 경로**로 취급합니다.

**상품 → 마켓 양식 (내보내기)** — 상품 화면에서 마켓을 고르면 해당 양식의 `.xlsx`가 생성됩니다. 필수 컬럼은 분홍 배경, 별도 '업로드안내' 시트에 업로드 방법과 필수 항목이 들어갑니다. 옵션이 있는 상품은 옵션별로 행이 분리됩니다. 상품번호·카테고리코드는 텍스트 서식으로 고정해 앞자리 0이 사라지거나 지수 표기가 되지 않습니다.

지원 양식: ESM Plus(옥션+G마켓 동시) · 11번가 · 쿠팡 · 스마트스토어

**카테고리 엑셀 왕복 (가져오기)** — 카테고리 목록을 엑셀로 받아 수집할 항목에 `Y`를 표시하고 최대 수량을 적은 뒤 업로드하면, 표시한 카테고리들이 순서대로 대량 수집됩니다.

```
① 카테고리 엑셀 내려받기 → ② 엑셀에서 Y 표시 + 수량 입력 → ③ 업로드 → 자동 대량 수집
```

## 검증된 동작

실제로 실행해 확인한 것들입니다.

**파이프라인 (시뮬레이션 공급처)**
- URL 다건 입력 → 전 단계 통과 → `Ready` → 마켓 등록 → `Listed`
- 실시간 SSE 이벤트가 단계별로 UI에 스트리밍
- 실환율(CNY→KRW 217.20) 기반 6단계 가격 계산 및 추적 저장
- 금지어 검출 → `Blocked` → 확인 후 `resume` → `Ready` 복귀

**카테고리 수집**
- 카테고리 미리보기 → 대량 수집 → 8건 큐 적재 → 전원 파이프라인 통과
- 같은 카테고리 재수집 시 6건 전부 중복 제외 (`queued=0 skipped=6`)
- 카테고리 엑셀 내려받기 → 3개 카테고리에 Y 표시 → 업로드 → 3개 작업 자동 시작

**엑셀**
- ESM Plus 양식 140행 × 24열 생성, 옵션별 행 분리·한글·이미지 URL 정상
- 11번가/쿠팡/스마트스토어 양식 생성 확인

**쿠팡 실등록 성공** — 도매꾹 에코백 → 쿠팡 상품번호 `16322094097` (임시저장)
- 출고 소요일 `2일` (공급처 평균 0.1일 + 여유 1일)
- 상품상세 이미지(860×10,432px 1장)가 쿠팡 CDN(`coupangcdn.com/vendor_inventory/…`)으로 업로드됨 — 라이선스 허용 확인 후 전송
- 카테고리 자동 결정: `69802` 남녀공용캔버스/에코백

**쿠팡 실등록 성공(2)** — 도매꾹 파스 → 쿠팡 상품번호 `16322016461`
- 카테고리 자동 결정: 상품명 → `64095` 파스/스프레이파스
- 필수 구매옵션 자동 채움: 수량 `1개`, 개당 수량 `1개입` (단위를 usableUnits에서 선택)
- 고시정보 10항목 자동 생성 (의료기기)
- 공급처 출고지 자동 생성: `25145282` [위탁] 주식회사주경 출고지
- 배송방법 `SEQUENCIAL` + `NOT_OVERSEAS_PURCHASED` (국내 도매이므로 구매대행 아님)

**위탁판매 물류·발주** (실제 도매꾹 상품)
- 공급처 출고지·반품지·우편번호·연락처·배송비(제주/도서산간)·MOQ 전부 자동 수집
- ESM Plus 엑셀의 `발송지주소`/`반품지주소`/`반품배송비`에 공급처 값이 그대로 반영됨
- 발주서: 주문 → 공급처 자동 연결, 배송지 표시, 마진 계산, 손실 주문 경고 동작
- 상품 상세의 `위탁판매 물류` 카드에 마켓에 등록될 값이 그대로 표시됨

**실연동 공급처**
- 11번가 실제 상품 수집 성공 (JSON-LD)
- 삭제된 AliExpress 상품 → 재시도 없이(attempts=1) 정확한 사유와 함께 DLQ
- 실제 AliExpress 상품 → 상품명 식별 + 자격증명 안내 후 DLQ
- 아마존 → TLS 지문 차단 감지 후 PA-API 안내 (파싱 로직 자체는 정상 동작 확인)

---

## API (설계서 §8)

| 메서드 | 경로 | 설명 |
|---|---|---|
| POST | `/api/v1/products/collect` | URL 대량 수집 → 202 + jobIds |
| GET | `/api/v1/jobs` | 파이프라인 진행 상태 |
| GET | `/api/v1/orders/{id}/purchase-sheet` | 위탁판매 발주서 |
| POST | `/api/v1/orders/{id}/purchase` | 공급처 발주 완료 기록 (주문번호·결제액) |
| GET | `/api/v1/categories/suppliers` | 카테고리 수집 지원 공급처 |
| GET | `/api/v1/categories/{supplier}` | 카테고리 트리 (`?parent=`로 하위) |
| POST | `/api/v1/categories/{supplier}/preview` | 카테고리 상품 미리보기 |
| POST | `/api/v1/categories/{supplier}/collect` | 카테고리 대량 수집 시작 |
| GET | `/api/v1/categories/jobs` | 카테고리 수집 작업 현황 |
| GET | `/api/v1/excel/templates` | 마켓별 엑셀 양식 목록 |
| POST | `/api/v1/excel/listings/{market}` | 상품 → 마켓 대량등록 엑셀 |
| GET | `/api/v1/excel/categories/{supplier}` | 카테고리 목록 엑셀 |
| POST | `/api/v1/excel/categories/import` | 카테고리 엑셀 업로드 → 대량 수집 |
| POST | `/api/v1/jobs/{id}/retry` | 실패/DLQ 재처리 |
| GET | `/api/v1/products` | 상품 목록 (status·keyword 필터) |
| GET | `/api/v1/products/{id}` | 상세 + 가격추적 + 컴플라이언스 + 등록현황 |
| PATCH | `/api/v1/products/{id}` | 상품명·상세 수동 편집 |
| POST | `/api/v1/products/{id}/resume` | 컴플라이언스 차단 해제 |
| GET | `/api/v1/products/{id}/raw` | 스크래핑 원본 JSON |
| GET/POST/PUT | `/api/v1/pricing-policies` | 가격 정책 CRUD |
| POST | `/api/v1/pricing-policies/{id}/simulate` | 가격 시뮬레이션 |
| POST | `/api/v1/listings` | 마켓 대량 등록 요청 |
| POST | `/api/v1/inventory/check` | 재고 동기화 수동 실행 |
| GET/POST/DELETE | `/api/v1/compliance/rules` | 금지어 관리 |
| GET/POST | `/api/v1/orders` | 주문 조회·수집·발주·송장 |
| GET | `/api/v1/dashboard/summary` | 대시보드 집계 |
| GET | `/api/v1/plugins` | 플러그인 상태 |
| GET/PUT | `/api/v1/settings/credentials` | 자격증명 (값은 조회 불가) |
| GET | `/api/v1/stream` | 파이프라인 실시간 이벤트 (SSE) |

---

## 화면

| 경로 | 내용 |
|---|---|
| `/dashboard` | KPI 4종, 파이프라인 단계별 체류, 상태 분포, 마켓별 등록률 |
| `/collect` | URL 대량 입력, SSE 실시간 피드, Job별 파이프라인 스텝퍼 |
| `/categories` | 카테고리 탐색·미리보기·대량 수집, 엑셀 왕복 |
| `/products` | 목록·검색·다중선택 → 마켓 등록 / 재고 동기화 |
| `/products/:id` | 가격 계산 추적, 컴플라이언스 결과, SKU 매트릭스, 등록 현황 |
| `/pricing` | Rule 드래그 구성 정책 빌더 + 실시간 시뮬레이터 |
| `/listings` | 마켓·상태별 등록 현황 |
| `/orders` | 주문 목록, 발주 처리, 송장 등록 |
| `/settings` | 플러그인 상태, 자격증명, 금지어 관리 |

대시보드 차트는 순서형 단일 색상 램프를 사용하며, 다크 서피스(`#171a21`) 기준으로 명도 단조성·대비를 검증했습니다.

---

## 자격증명 등록

설정 화면 또는 API로 등록합니다. 저장 후 값은 다시 조회되지 않으며, 빈 값으로 보내면 기존 값이 유지됩니다.

| 스코프 | 키 |
|---|---|
| `ai:claude` | `api_key` |
| `supplier:domeggook` | `api_key`, `market` (dome=도매꾹 / supply=도매매) |
| `supplier:taobao` | `cookie` (`_m_h5_tk` 포함 필요) |
| `supplier:aliexpress` | `app_key`, `app_secret`, `access_token` |
| `market:smartstore` | `client_id`, `client_secret`, `default_category_id`, `as_telephone` |
| `market:coupang` | `access_key`, `secret_key`, `vendor_id`, 출고지/반품지 코드 |
| `market:11st` | `api_key`, `default_category_code`, 발송지/반품지 순번 |
| `market:esmplus` | 엑셀 생성용 값 (카테고리코드, 발송지/반품지, 배송비 등) |

> 현재 자격증명은 SQLite에 평문 저장됩니다. 설계서 5.6의 KMS/Envelope 암호화는 `ICredentialStore` 구현 교체로 적용할 수 있습니다. **운영 배포 전 반드시 교체하세요.**

---

## 미구현 (설계서 대비)

- 이미지 가공·OCR (중국어 제거) — `IAiProviderPlugin`에 `Ocr`/`ImageEdit` 능력 추가로 확장
- 카테고리 2단 매핑(공급처 → 표준 → 마켓별) — 현재는 마켓별 기본 카테고리 사용
- Outbox 패턴 — 인메모리 버스라 트랜잭션 경계가 하나
- Enrichment Saga 병렬 집계 — 현재 순차 처리
- Scheduler 주기 실행 — 재고 동기화는 수동 트리거만
- 배대지(`IForwarderAdapter`) 연동
- 인증/인가, 멀티테넌시 격리
