# Tetragon 동작 분석 문서

> 작성일 2026-08-23 · 대상: 현재 워킹 디렉터리의 소스 코드 (백엔드 92개 .cs / 약 15,000줄, 프론트 23개 파일 / 약 4,900줄)
> 이 문서는 **코드를 읽고 "실제로 어떻게 굴러가는지"** 를 정리한 것입니다. 기능 소개·사용법은 [README.md](../README.md)를 보세요.

---

## 0. 한눈에 보기

Tetragon은 **위탁판매(드롭쉬핑) 자동화 플랫폼**입니다. 공급처(도매꾹 등) 상품 링크를 넣으면
`수집 → 표준화 → 번역 → 가격계산 → 금지어검사 → 마켓(쿠팡 등) 등록` 파이프라인을 돌리고,
등록 이후에는 `주문 수집 → 공급처 발주 → 송장 반영 → CS 처리` 까지 다룹니다.

| 구분 | 내용 |
|---|---|
| 백엔드 | .NET 8 Minimal API, EF Core 8 + **SQLite**, Swagger, SSE |
| 프론트엔드 | React 19.2 + TypeScript 6 + Vite 8 + TanStack Query 5 + react-router 7 (dev 서버가 `/api`를 5080으로 프록시) |
| 메시징 | `System.Threading.Channels` 기반 **인메모리 이벤트버스** (프로세스 내부) |
| 백그라운드 | `EventDispatcherWorker`(파이프라인 이벤트 소비), `FulfillmentWorker`(주문 이행 자동화 주기 실행) |
| 데이터 | `src/Tetragon.Api/bin/Debug/net8.0/tetragon.db` 단일 파일 (현재 약 34MB, WAL 모드) |
| 외부 연동 | 도매꾹 OpenAPI, 11번가/알리/타오바오/아마존 스크래핑, 쿠팡·스마트스토어·11번가 셀러 API, Claude API, open.er-api.com 환율 |
| 인증/멀티테넌시 | **없음**. `TenantId` 컬럼만 있고 항상 `"default"` |

한 줄 요약: **"한 프로세스 안에서 이벤트 체인으로 돌아가는 모놀리식 파이프라인 + 그 앞의 React 화면"** 입니다.

---

## 1. 실행 방식

### 1.1 기동 절차 (`start.bat`)

```
start.bat
 ├─ dotnet / npm 존재 확인
 ├─ 5080·5173 포트 점유 프로세스 강제 종료
 ├─ frontend/node_modules 없으면 npm install
 ├─ [창1] dotnet run --project src\Tetragon.Api --urls http://localhost:5080
 ├─ [창2] cd frontend && npm run dev           (Vite, 5173)
 └─ 최대 2분 동안 /api/v1/plugins 와 / 가 200 될 때까지 폴링 → 브라우저 열기
```

`stop.bat`은 두 포트를 점유한 프로세스를 찾아 종료합니다. `.claude/launch.json`에도 같은 두 구성(`tetragon-api`, `tetragon-web`)이 있습니다.

### 1.2 API 프로세스 시작 시 일어나는 일 ([Program.cs](../src/Tetragon.Api/Program.cs))

1. `AddTetragonCore("Data Source=<실행폴더>/tetragon.db")` — Application + Infrastructure 전체 등록 ([DependencyInjection.cs](../src/Tetragon.Infrastructure/DependencyInjection.cs))
2. 이름 있는 `HttpClient` 5종 등록(`scraper`, `smartstore`, `coupang`, `claude`, `11st`) — 전부 **gzip 자동 해제 + 쿠키 비활성**. 주석에 따르면 압축 해제를 빠뜨리면 `'0x1F' is an invalid start of a value`가 나서 공통 적용.
3. 플러그인 등록 — 공급처 6종, 마켓 어댑터 6종, 엑셀 양식 4종, AI 2종, 발주 플러그인 1종 (모두 **싱글턴**)
4. `FulfillmentWorker` 호스티드 서비스 등록
5. Swagger, CORS(`localhost:5173`, `localhost:4173`만 허용), JSON 옵션(camelCase + **enum을 문자열로**)
6. **`DbSeeder.InitializeAsync`** ([DbSeeder.cs](../src/Tetragon.Infrastructure/Persistence/DbSeeder.cs))
   - `EnsureCreated` → 누락 테이블/인덱스 생성 → 누락 컬럼 추가(+ NOT NULL 컬럼의 NULL을 빈 값으로 메움) → `PRAGMA journal_mode=WAL`
   - 가격 정책이 하나도 없으면 "기본 정책"(6 Rule) 시드, 금지어가 없으면 16종 시드
   - ⚠️ EF 마이그레이션이 아니라 **자체 스키마 동기화**입니다. 컬럼 추가는 되지만 삭제·타입 변경은 못 합니다.
7. 엔드포인트 16개 그룹 매핑, `/` → `/swagger` 리다이렉트

### 1.3 프로세스 안에서 항상 돌고 있는 것

| 백그라운드 | 역할 | 주기 |
|---|---|---|
| `EventDispatcherWorker` | 이벤트버스 Channel을 읽어 핸들러 실행 | 상시 (이벤트 도착 즉시) |
| `FulfillmentWorker` | 주문 이행 자동화 `FulfillmentAutomation.RunAsync` | 기동 15초 후부터, 정책 OFF면 1분마다 확인만 / ON이면 `IntervalMinutes`(기본 10분) |

---

## 2. 솔루션 구조와 의존 방향

```
Tetragon.Api ──────────────┬─► Tetragon.Application ─┬─► Tetragon.Domain ─► Tetragon.SharedKernel
   (REST/SSE/Swagger)      │                          └─► Tetragon.Plugin.Abstractions ─► SharedKernel
                           ├─► Tetragon.Infrastructure ─► Application
                           ├─► Tetragon.Plugins.Suppliers ─► Plugin.Abstractions
                           ├─► Tetragon.Plugins.Markets   ─► Plugin.Abstractions
                           └─► Tetragon.Plugins.Ai        ─► Plugin.Abstractions
```

| 프로젝트 | 줄 수 | 들어 있는 것 |
|---|---|---|
| `SharedKernel` | 119 | `Entity`, `AggregateRoot`, `IntegrationEvent`(EventId/CorrelationId/CausationId), `Money`, `LocalizedText`, `SourceRef`, `Tenant.Default` |
| `Domain` | 1,800 | 애그리거트: `Product`, `ScrapeJob`, `CategoryCollectJob`, `RawProductRecord`, `Listing`, `ShippingPlaceMapping`, `Order`, `CsTicket`, `AutomationPolicy`, `Wallet`, `PricingPolicy`/`PriceCalculation`, `ComplianceRule`/`ComplianceResult`; 가격 Rule 6종; `MarginHealth`; 통합 이벤트 13종 |
| `Plugin.Abstractions` | 930 | `ISupplierPlugin`, `IMarketplaceAdapter`, `IAiProviderPlugin`, `ICategoryCrawler`, `ISupplierOrderPlugin`, `IOrderFulfillmentProvider`, `IShippingPlaceProvider`, `IMarketExcelTemplate`, `ICredentialProvider`, `RawProduct`/`ListingPayload`/`ConsignmentLogistics` 등 DTO |
| `Application` | 3,100 | Port 인터페이스(`Ports.cs`), 파이프라인 핸들러 8종, UseCase 5종, 서비스(가격/컴플라이언스/정규화/발주/자동화/점검/미리보기) |
| `Infrastructure` | 1,400 | `TetragonDbContext`(SQLite+JSON 컬럼), 리포지토리 12종, `InMemoryEventBus`+디스패처, `PipelineNotifier`(SSE), 환율 Provider, 플러그인 레지스트리, 엑셀 워크북 생성 |
| `Plugins.Suppliers` | 2,270 | simulated / 11st / domeggook(+발주) / taobao / aliexpress / amazon |
| `Plugins.Markets` | 3,010 | mockmarket / smartstore / coupang(+카테고리·출고지·송장·스로틀) / 11st / auction·gmarket(엑셀 안내) / 엑셀 양식 4종 |
| `Plugins.Ai` | 276 | simulated-ai / claude + 프롬프트 템플릿 |
| `Api` | 2,140 | `Program.cs`, 엔드포인트 10파일, DTO, `FulfillmentWorker` |

규칙: Domain은 SharedKernel만 참조. Application은 Infrastructure를 모르고(Port만 정의), Infrastructure가 Application의 Port를 구현. 플러그인은 Abstractions만 알고, **Api가 유일하게 전부를 조립**합니다.

---

## 3. DI 구성과 수명

- **Scoped** (요청/이벤트 단위): `TetragonDbContext`, 모든 리포지토리, 모든 파이프라인 핸들러, `PricingEngine`, `ComplianceEngine`, UseCase, `FulfillmentAutomation`, `SupplierPurchaseService` 등
- **Singleton**: 이벤트버스, `PipelineNotifier`, `IExchangeRateProvider`, 모든 플러그인·레지스트리, `IPriceRule` 6종, `CachedCredentialProvider`, `AiRouter`, `ExcelWorkbookService`, `CoupangThrottle`
- 플러그인이 싱글턴인데 자격증명은 DB(Scoped)에 있어서, 플러그인용으로는 **`CachedCredentialProvider`**(동기·인메모리 캐시, 설정 저장 시 `Invalidate(scope)`)가 따로 있습니다. 핸들러/유스케이스는 `ICredentialStore`(Scoped, DB 직접)를 씁니다. 즉 자격증명 읽기 경로가 **두 개**입니다.
- 도매꾹·시뮬레이션 공급처는 `ISupplierPlugin`과 `ICategoryCrawler` 두 인터페이스로 **같은 인스턴스**를 노출합니다 ([Program.cs:38-44](../src/Tetragon.Api/Program.cs)).

---

## 4. 이벤트 버스와 디스패처 — 파이프라인의 심장

[InMemoryEventBus.cs](../src/Tetragon.Infrastructure/Messaging/InMemoryEventBus.cs)

```
PublishAsync(event) ──► Channel<IntegrationEvent> (unbounded, SingleReader)
                                  │
                     EventDispatcherWorker.ExecuteAsync
                                  │  _processed.TryAdd(EventId)  ← 멱등성 (메모리 Set)
                                  │  SemaphoreSlim(4)            ← 동시 처리 4개
                                  ▼
                     ProcessAsync(event)  (fire-and-forget Task)
                       ├─ scopeFactory.CreateScope()             ← 이벤트마다 DbContext 새로
                       ├─ typeof(IIntegrationEventHandler<>).MakeGenericType(eventType)
                       ├─ GetServices(handlerType) → 등록된 핸들러 전부
                       └─ 리플렉션으로 HandleAsync(event, ct) 호출
```

중요한 성질:

- **프로세스가 죽으면 큐에 있던 이벤트는 사라집니다.** 영속 큐가 아닙니다. (DB에 Job 상태는 남으므로 `/jobs/{id}/retry`로 복구 가능)
- 핸들러 내부 예외는 핸들러 자신이 잡아 Job 실패/DLQ 처리를 합니다. 디스패처까지 올라오면 로그만 남깁니다.
- 동시성 4 → 파이프라인 5단계가 서로 다른 Job에 대해 겹쳐서 돌 수 있고, SQLite WAL이라 쓰기 경합은 직렬화됩니다.
- `_processed` 딕셔너리는 비워지지 않습니다 (프로세스 수명 동안 EventId 누적).

이벤트 ↔ 핸들러 매핑 ([DependencyInjection.cs:79-87](../src/Tetragon.Infrastructure/DependencyInjection.cs)):

| 이벤트 | 핸들러 | 다음에 발행하는 이벤트 |
|---|---|---|
| `ScrapeRequested` | `ScrapeRequestedHandler` | `ProductCollected`, `ProductNormalized` |
| `CategoryCrawlRequested` | `CategoryCrawlHandler` | N × `ScrapeRequested`, `CategoryCrawlCompleted` |
| `ProductNormalized` | `EnrichmentHandler` | `ProductEnriched` |
| `ProductEnriched` | `PricingHandler` | `PriceCalculated` |
| `PriceCalculated` | `ComplianceHandler` | `ComplianceChecked` (+ 자동등록이면 `ListingRequested`) |
| `ListingRequested` | `ListingRequestedHandler` | `MarketplaceRegistered` |
| `InventoryCheckRequested` | `InventoryCheckHandler` | `StockChanged` |
| `StockChanged` | `StockChangedHandler` | — |
| `ProductCollected`, `ComplianceChecked`, `MarketplaceRegistered`, `PipelineFailed`, `CategoryCrawlCompleted` | **핸들러 없음** (발행만 됨 — 확장 포인트) | |

---

## 5. 상품 수집 파이프라인 (핵심 흐름)

### 5.1 진입점 — 어디서 `ScrapeRequested`가 만들어지나

| 진입 | 코드 | 특징 |
|---|---|---|
| `POST /api/v1/products/collect` | `CollectProductsUseCase` | URL 목록 → URL마다 `ScrapeJob` + 이벤트. 잘못된 URL은 `rejected`로 돌려줌 |
| `POST /api/v1/products/quick-list` | `QuickListUseCase` | URL + 마켓코드 → **`AutoListMarkets`가 채워진 Job**. 컴플라이언스 통과 즉시 등록까지 이어짐. 이미 수집한 상품(공급처+원본ID로 `RawProducts` 조회)이면 다시 긁지 않고 `ListingRequested`만 발행 |
| `POST /api/v1/categories/{supplier}/collect` | `CollectCategoryUseCase` → `CategoryCrawlHandler` | 카테고리 훑어서 상품별 Job 생성 (§6) |
| `POST /api/v1/excel/categories/import` | 엑셀 `Y` 표시 행 → 위와 동일 | |
| `POST /api/v1/jobs/{id}/retry` | 직접 발행 | Failed/DeadLettered만. **Listing 단계 실패 + AutoList Job은 `ListingRequested`만 재발행**(재수집 시 상품 중복 방지) |
| `POST /api/v1/products/preview` | `LinkPreviewService` | **DB에 쓰지 않고** 수집→정규화→가격계산→마진판정만 해서 보여줌 |

### 5.2 단계별 동작 ([Handlers.cs](../src/Tetragon.Application/Pipeline/Handlers.cs))

모든 단계는 `JobProgress`([JobProgress.cs](../src/Tetragon.Application/Pipeline/JobProgress.cs))로 `ScrapeJob.Stage/State`를 바꾸고 동시에 `PipelineNotifier.Notify(...)`로 SSE를 쏩니다. 즉 **Job 테이블 갱신과 화면 실시간 알림은 항상 한 쌍**입니다.

**① Collecting + Normalizing — `ScrapeRequestedHandler`**
1. `ISupplierPluginRegistry.Resolve(Uri)` — 호스트로 플러그인 선택. 없으면 예외.
2. `credentials.GetAsync("supplier:{code}")`에서 `cookie`만 꺼내 `ScrapeContext`에 넣음 (API 키 등은 플러그인이 `ICredentialProvider`로 직접 읽음).
3. `plugin.CollectAsync(url, ctx)` → `RawProduct`
4. `ProductNormalizer.Normalize` → `Product`(Draft) → `TransitionTo(Normalized)` → 저장
   - 이미지 URL `//` 보정, 옵션명 `【】`→`[]`, Variant 없으면 단일 SKU(재고 999) 생성, `BasePrice` = 최저 Variant 가격
5. `RawProductRecord`에 원본 JSON 보존 (공급처+원본ID 인덱스 → 중복 판정에 사용)
6. Job에 `ProductId`/`SupplierCode` 부착, `ProductCollected` + `ProductNormalized` 발행

실패 시:
- `PermanentScrapeException`(삭제된 상품, 자격증명 없음, 봇 차단 등) → `RecordPermanentFailure` → **즉시 DeadLettered**
- 그 외 → `RecordFailure` (Attempts++) → `Attempts < 3`이면 **`Task.Run`으로 `2^attempts`초 뒤 같은 이벤트를 새 EventId로 재발행** (fire-and-forget, 프로세스 재시작 시 소실), 3회 소진 시 DeadLettered + `PipelineFailed`

**② Enriching — `EnrichmentHandler`**
- 원문 로케일 = `Name.Values`에서 `ko-KR`이 아닌 첫 키 (없으면 `zh-CN`)
- 번역 배치 한 번: `[상품명, 상세, 옵션그룹명..., 옵션값명...]` 순서로 묶어 `AiRouter.Route(Translate)` 호출 → 결과를 **인덱스 위치로** 되돌려 붙임 (`SetTranslatedName`, `RenameOptions`)
- `ProductNameSeo` 능력이 있는 Provider가 있으면 SEO 상품명으로 덮어씀. 없으면 그냥 넘어감.
- 도매꾹처럼 원문이 `ko-KR`인 경우도 이 단계는 실행됩니다 (시뮬레이션 AI가 그대로 돌려줌).
- 실패 → `Product.Failed`, Job Failed + DLQ (`FailPipelineAsync`). **이 단계부터는 자동 재시도가 없습니다** — 재시도는 1단계(수집)에만 있습니다.

**③ Pricing — `PricingHandler`** ([PricingEngine.cs](../src/Tetragon.Application/Services/PricingEngine.cs))
- 정책 선택: `Job.PricingPolicyId` → 없으면 `IsDefault` 정책 → 없으면 예외
- Variant마다: 환율 조회(KRW면 1) → `PriceContext{IsDomestic = 통화==KRW}` → 정책의 Rule을 `Order` 순으로 `Apply`
- Rule 6종 ([Rules.cs](../src/Tetragon.Domain/Pricing/Rules.cs)): `exchange-rate` → `intl-shipping`(국내면 Skip) → `tariff-vat`(국내면 Skip, USD 150 기준은 **1,350원/USD 고정 근사**) → `margin` → `market-fee`(÷(1−fee)) → `psych-rounding`
- 모든 단계가 `PriceStep(Before, After, 설명)`로 `PriceCalculation.Steps`에 저장 → 상품 상세의 "이 가격이 왜 나왔는지"
- `Product.Variants[].CalculatedPrice` 채움 → `Priced`

**④ Compliance — `ComplianceHandler`** ([ComplianceEngine.cs](../src/Tetragon.Application/Services/ComplianceEngine.cs))
- 매 실행마다 DB의 전체 `ComplianceRule`로 Aho-Corasick 오토마타를 **새로 빌드** (캐시 없음)
- 검사 대상: `ko-KR` 상품명, `ko-KR` 상세, 옵션그룹/값(번역명 우선)
- `Block` 1개라도 → `Product.Blocked`, Job `Compliance/Blocked` (**Job은 완료되지 않음**). `Warn`/`Pass` → `Product.Ready`, Job `Completed`
- Job에 `AutoListMarkets`가 있으면(빠른 등록) 여기서 바로 `ListingRequested` 발행하고 Job을 `Listing/Running`으로 되돌림
- 차단 해제: `POST /products/{id}/resume` → `ResumeBlockedProductUseCase` → `Ready` + 최근 500개 Job 중 해당 상품의 Blocked Job을 `Complete()`

**⑤ Listing — `ListingRequestedHandler`**
1. `ListingPayloadBuilder.Build(product)` — `CalculatedPrice`가 있는 Variant만, 대표가 = 최저 Variant, `ConsignmentLogistics.FromAttributes(...)`로 물류 정보 추출. 계산가가 하나도 없으면 예외 → Job 실패.
2. 마켓코드마다:
   - `Listing` (Product×Market 유니크) 조회/생성
   - `ShippingPlaceResolver.ResolveAsync` (§7) — 쿠팡처럼 코드가 필요한 마켓에서 **출고지 코드를 못 얻으면 등록을 중단**하고 `SHIPPING_PLACE:` 사유 기록
   - `adapter.RegisterAsync(payload, credential)` → 성공 `MarkRegistered(marketItemId, price)` / 실패 `MarkFailed`
   - `MarketplaceRegistered` 발행 + SSE
3. 하나라도 성공하고 상품이 `Ready`면 `Listed`로 전이
4. Job이 있으면 완료 또는 `RecordListingFailure`(Stage=Listing, State=Failed — 재시도 시 등록만 다시 함)

### 5.3 상태 머신

**Product** ([Product.cs:57-66](../src/Tetragon.Domain/Catalog/Product.cs)) — 허용 전이 외에는 `InvalidOperationException`

```
Draft → Normalized → Enriched → Priced → Ready → Listed
                                   │        ↑  ↘
                                   ▼        │   Priced (재계산 허용)
                                Blocked ────┘
 (Draft~Priced) → Failed → Draft
```

**ScrapeJob** — `Stage`(Queued → Collecting → Normalizing → Enriching → Pricing → Compliance → Listing → Completed) × `State`(Pending / Running / Succeeded / Failed / DeadLettered / Blocked). `MaxAttempts = 3`.

### 5.4 실시간 알림 (SSE)

[PipelineNotifier.cs](../src/Tetragon.Infrastructure/Messaging/PipelineNotifier.cs) + `GET /api/v1/stream`

- 구독자마다 Bounded(256, DropOldest) Channel. 최근 200건 링버퍼 보관, 접속 시 **마지막 20건을 먼저 재전송**(재연결 공백 보정).
- 페이로드: `{jobId, stage, state, productId, message, at}`. 카테고리 수집은 `stage="CategoryCrawl"`, 등록은 `stage="Listing"`으로 같은 채널에 흘러갑니다.
- 프로세스 내부 브로드캐스트라 API 인스턴스가 여러 개면 동작하지 않습니다 (설계상 단일 인스턴스).

---

## 6. 카테고리 대량 수집

[CategoryCrawlHandler.cs](../src/Tetragon.Application/Pipeline/CategoryCrawlHandler.cs)

```
CategoryCollectJob(Pending) ─► CategoryCrawlRequested
   └─ crawler.CrawlAsync(page=1..)  (PageSize ≤ 100, 페이지 간 600ms)
        ├─ ExistingSourceIdsAsync(공급처, 원본ID들)  ← RawProducts 인덱스로 중복 제거
        ├─ 신규마다 ScrapeJob 생성 + ScrapeRequested 발행  ← 이후는 §5 파이프라인
        └─ RecordPage(found, queued, skipped) → SSE "N페이지: n건 수집 요청, m건 중복"
   상한: MaxProducts (1~1000, 기본 100) 도달 또는 HasMore=false 에서 종료
```

훑기(`ICategoryCrawler`)와 수집(`ISupplierPlugin`)이 분리돼 있어, 카테고리 목록 API가 없는 공급처(11번가 등)도 개별 URL 수집은 됩니다. 현재 크롤러 구현체는 **도매꾹과 시뮬레이션 둘뿐**입니다.

---

## 7. 마켓 등록과 출고지/반품지 해석

[ShippingPlaceResolver.cs](../src/Tetragon.Application/Services/ShippingPlaceResolver.cs)

위탁판매는 출고지·반품지가 **상품마다 다른 공급처 주소**여야 합니다. 쿠팡은 주소 문자열이 아니라 사전 등록된 코드를 요구하므로:

1. 어댑터가 `IShippingPlaceProvider`가 아니면(엑셀·11번가) → `NotRequired` (주소를 그대로 씀)
2. `ConsignmentLogistics.IsComplete`가 아니면 → `NotRequired` (판매자 기본값 폴백)
3. `ShippingPlaceMapping` (마켓 × 주소키, 유니크 인덱스) 조회 → 이미 코드가 있으면 재사용
4. 없으면 `EnsureOutboundPlaceAsync` / `EnsureReturnCenterAsync` 호출 → 코드 저장(한쪽만 성공해도 그쪽은 보존)
5. **출고지 코드가 없으면 `CanProceed=false`** → 등록 중단. 반품지 코드는 없어도 어댑터가 주소로 대체하므로 막지 않음.

`GET /api/v1/settings/shipping-places/{market}` (`ShippingPlaceAudit`)이 공급처별로 어느 코드가 확보됐고 무엇을 WING에 수동 등록해야 하는지 보여줍니다.

---

## 8. 재고 동기화

[InventorySync.cs](../src/Tetragon.Application/Pipeline/InventorySync.cs)

- 트리거는 **수동뿐**: `POST /api/v1/inventory/check` → 상품별 `InventoryCheckRequested`
- `InventoryCheckHandler`: 공급처 `CheckInventoryAsync` → Variant별 재고 diff → 변경이 있거나 품절이면 `StockChanged`
- `StockChangedHandler`: 등록된 Listing마다 `UpdatePriceStockAsync` (품절이면 재고 0 + `Suspend`, 아니면 총재고 반영)
- 별도로 `POST /api/v1/audit/stock` (`StockAudit`)은 **DB를 바꾸지 않고** 품절/재고부족/원가변동 보고서만 만듭니다 (호출 간 400ms).

---

## 9. 주문 → 발주 → 송장 → CS

### 9.1 Order 애그리거트 ([Order.cs](../src/Tetragon.Domain/Ordering/Order.cs))

```
Imported → SupplierOrdered → AtForwarder → Shipped → Delivered
    └──────────┴───────────────┴──► Cancelled
```

- `Import(...)`: 마켓 주문 + 구매자 배송지 + 쿠팡 식별자(`ShipmentBoxId`, `VendorItemId`)
- `LinkSource(...)`: **마켓 상품번호 → Listing → Product** 역추적으로 공급처·원가·URL 부착
- `MarkSupplierOrdered(주문번호, 실지불액)`, `RegisterTracking(송장)`, `MarkTrackingUploaded()`, `Cancel()`
- `EstimatedMargin` = 판매금액 − (실지불액 or 원가×수량)

### 9.2 금고(Wallet) — 예약/확정 2단계 ([Wallet.cs](../src/Tetragon.Domain/Treasury/Wallet.cs))

```
Available = Balance − Reserved
Reserve(amount)  : Reserved += amount   (Available 부족 시 InsufficientFundsException)
Capture(reserved, actual): Reserved −= reserved; Balance −= actual
Release(amount)  : Reserved −= amount
```

모든 변동은 `WalletTransaction`(Deposit/Withdraw/Reserve/Purchase/Release, `BalanceAfter` 포함)으로 남고, `IWalletRepository.SaveAsync(wallet, tx)`가 둘을 **같은 SaveChanges**로 저장합니다. 테넌트당 금고 1개(유니크 인덱스), 경고선 기본 10만원.

### 9.3 발주 — `SupplierPurchaseService` ([SupplierPurchaseService.cs](../src/Tetragon.Application/Services/SupplierPurchaseService.cs))

`PreflightAsync` (돈 안 씀): `PurchaseOrderSheetBuilder`로 발주서 생성 → 차단 사유 수집(상태≠Imported / 공급처 미연결 / 배송지 없음 / 금고 부족) + 경고(공급가 0, 경고선 이하, 손실 주문) + `ISupplierOrderPlugin.CheckCapabilityAsync` (도매꾹 Open API 키는 **주문 권한이 없어 항상 불가** — 실측 403)

`PurchaseAsync` (돈 나감):
```
preflight 재실행 → 불가면 Rejected
① wallet.Reserve(예상액)  저장
② plugin.PlaceOrderAsync(...)
   예외 → Release 후 Failed
③ 실패 → Release / 성공 → Capture(예상액, 실지불액) → order.MarkSupplierOrdered
   실지불액이 예상의 120% 초과면 경고 로그
```

수동 경로: `POST /orders/{id}/purchase` (공급처 주문번호·실지불액 입력) — 금고를 거치지 않고 상태만 바꿉니다.

### 9.4 자동화 — `FulfillmentAutomation` ([FulfillmentAutomation.cs](../src/Tetragon.Application/Services/FulfillmentAutomation.cs))

한 바퀴(`RunAsync`)는 5단계를 순서대로, **단계가 실패해도 다음 단계로** 넘어갑니다:

| 단계 | 조건 | 하는 일 |
|---|---|---|
| 1. 주문 수집 | `AutoCollectOrders` | `IsAvailable`한 모든 마켓 어댑터에 최근 3일 `FetchOrdersAsync`. 이미 있는 주문은 건너뜀. `NotSupportedException`은 실패로 세지 않음 |
| 2. 공급처 연결 | 항상 | `Imported`이고 `ProductId`가 없는 주문을 Listing으로 역추적 |
| 3. 자동 발주 | `AutoPurchase` | `Imported`+연결된 주문을 오래된 순으로: `RejectPurchase`(1건 한도 5만원·하루 30만원) → preflight → **DryRun이면 기록만** → `PurchaseAsync` |
| 4. 송장 반영 | `AutoUploadTracking` | `NeedsTrackingUpload`(송장 있고 미반영·미취소) 주문을 `IOrderFulfillmentProvider.UploadTrackingAsync`. `NO_SHIPMENT_BOX`/`NO_INVOICE`는 영구 실패로 분류해 실패 카운트에서 제외 |
| 5. CS 수집 | `AutoCollectCs` | `FetchCsTicketsAsync` → `CsTicket` 생성. 발주가 이미 나간 주문이면 `SupplierActionRequired=true`. **발주 전 취소 요청은 주문을 즉시 `Cancelled`** |

안전장치 ([AutomationPolicy.cs](../src/Tetragon.Domain/Ordering/AutomationPolicy.cs)): 기본값 `Enabled=false`, `DryRun=true`. `Errors`가 1건이라도 있으면 `RecordFailure` → 연속 3회면 `HaltedReason` 설정되어 스스로 멈춤 (`POST /cs/automation/resume` 또는 다시 켜기로 해제). `POST /cs/automation/run`은 스위치가 꺼져 있어도 한 바퀴 돌려볼 수 있습니다(한도·DryRun은 유지).

### 9.5 CS 티켓 ([CsTicket.cs](../src/Tetragon.Domain/Ordering/CsTicket.cs))

`Open → Resolved | Dismissed`. `SupplierActionRequired && !SupplierActionDone`이면 `Resolve()`가 예외를 던져 **공급처 조치를 먼저 체크하도록 강제**합니다. 마켓 × 티켓ID 유니크로 중복 수집 방지.

---

## 10. 점검·미리보기 서비스 (읽기 전용)

| 서비스 | 엔드포인트 | 하는 일 |
|---|---|---|
| `LinkPreviewService` | `POST /products/preview` | 링크 1개를 실제로 긁어 정규화·가격계산·`MarginHealth.Assess`(가장 나쁜 SKU 기준 적자/얇은마진 판정)·물류정보·기존 상품 여부·경고 목록을 **저장 없이** 반환 |
| `MarginHealth` | (Domain 정적) | `실수령 = 판매가 − 수수료`, `이익 = 실수령 − 원가(KRW환산) − 공급처배송비`, 손익분기 = `(원가+배송비)/(1−수수료율)`. 기준 마진 10% |
| `StockAudit` | `POST /audit/stock` | 공급처 재고 재조회 → 품절/재고부족/원가변동 목록 |
| `DuplicateAudit` | `GET /audit/duplicates` | 같은 (공급처, 원본ID) 상품이 둘 이상인지 + 아이템위너 위험 추정 (`listings` 생성자 매개변수 미사용 — 빌드 경고 CS9113) |
| `ShippingPlaceAudit` | `GET /settings/shipping-places/{market}` | 공급처별 출고지/반품지 코드 확보 현황 + WING 등록용 폼 값 |
| `PurchaseOrderSheetBuilder` | `GET /orders/{id}/purchase-sheet` | 무엇을·어디서·누구에게·얼마에 + 손실 경고 |

---

## 11. 저장소 — SQLite + JSON 컬럼

[TetragonDbContext.cs](../src/Tetragon.Infrastructure/Persistence/TetragonDbContext.cs)

- 테이블 16개: `Products`, `ScrapeJobs`, `CategoryCollectJobs`, `ShippingPlaceMappings`, `Wallets`, `WalletTransactions`, `RawProducts`, `PricingPolicies`, `PriceCalculations`, `ComplianceRules`, `ComplianceResults`, `Listings`, `Orders`, `CsTickets`, `AutomationPolicies`, `Credentials`
- 복합 값(이름/설명/이미지/옵션/Variant/가격/속성/Rule/Steps/SyncLogs 등)은 **전부 JSON 문자열 컬럼**. 변경 감지는 JSON 직렬화 비교. 즉 `Variants` 안의 재고 하나만 바뀌어도 컬럼 전체를 다시 씁니다.
- 모든 enum은 문자열 저장. `DateTimeOffset`은 SQLite가 정렬 못 해서 **binary(long) 변환** — PostgreSQL 전환 시 `ConfigureConventions`만 제거.
- `Json<T>` 변환기는 빈 문자열을 "값 없음"으로 읽고, `JsonList`는 NULL을 빈 목록으로 — 나중에 추가된 컬럼 때문에 기존 행이 터지는 것을 막기 위함 (diagnostics/REPORT.md E1 사건의 결과).
- 유니크: `Listings(ProductId, MarketCode)`, `Orders(MarketCode, MarketOrderId)`, `CsTickets(MarketCode, MarketTicketId)`, `ShippingPlaceMappings(MarketCode, AddressKey)`, `Wallets(TenantId)`, `AutomationPolicies(TenantId)`
- **자격증명은 `Credentials.SecretsJson`에 평문.** `PUT /settings/credentials`는 빈 값이면 기존 유지(마스킹 재저장 방지), `GET`은 키 이름만 반환.
- 환율: `open.er-api.com` 1시간 정적 캐시(프로세스 전역 Dictionary + SemaphoreSlim) → 실패 시 만료된 캐시 → 정적 폴백(CNY 195, USD 1400 …)

---

## 12. 플러그인 계층

### 12.1 계약 ([Tetragon.Plugin.Abstractions](../src/Tetragon.Plugin.Abstractions))

| 인터페이스 | 핵심 멤버 | 누가 구현 |
|---|---|---|
| `IPlugin` | `Code`, `DisplayName`, `Version`, **`IsLive`**(실연동인가), **`IsAvailable`**(지금 자격증명이 있어 쓸 수 있나, 기본 true) | 모든 플러그인 |
| `ISupplierPlugin` | `CanHandle(Uri)`, `CollectAsync(Uri, ScrapeContext)→RawProduct`, `CheckInventoryAsync(SourceRef)`, `TryGetSourceProductId(Uri)`(기본 null) | 공급처 6종 |
| `ICategoryCrawler` | `GetCategoriesAsync(parent)`, `CrawlAsync(request)→CrawledProductRef[]` (경량 참조만; 상세는 `CollectAsync`) | 도매꾹, 시뮬 |
| `ISupplierOrderPlugin` | `CheckCapabilityAsync()`, `PlaceOrderAsync(SupplierOrderRequest)` — 수령인은 **최종 구매자**, `OrderId`가 멱등키 | 도매꾹 |
| `IMarketplaceAdapter` | `RegisterAsync/UpdateAsync/DeleteAsync/UpdatePriceStockAsync/FetchOrdersAsync` — `MarketCredential`을 **호출 시점에** 주입 | 마켓 6종 |
| `IShippingPlaceProvider` | `EnsureOutboundPlaceAsync`, `EnsureReturnCenterAsync` → 코드 | 쿠팡만 |
| `IOrderFulfillmentProvider` | `UploadTrackingAsync(TrackingUpload)`, `FetchCsTicketsAsync(range)` | 쿠팡, 시뮬 마켓 |
| `IMarketExcelTemplate` | `Columns`, `BuildRows(ExcelRowContext)`, `UploadGuide` — 엑셀을 API와 **동등한 등록 경로**로 취급 | 4종 |
| `IAiProviderPlugin` | `Capabilities`(Translate/ProductNameSeo/OptionCleanup), `ExecuteAsync(AiRequest)→AiResult` | claude, simulated-ai |
| `ICredentialProvider` | `Get(scope,key)`, `HasKey` — **동기** (싱글턴 플러그인의 `IsAvailable`용) | `CachedCredentialProvider` |

예외 규약: `PermanentScrapeException`(재시도 무의미 → 즉시 DLQ) / `TransientScrapeException`(백오프 재시도) / `PluginCredentialException`.

**`LogisticsKeys`** ([Supplier.cs](../src/Tetragon.Plugin.Abstractions/Supplier.cs)) — 위탁판매 물류 속성의 표준 키(전부 한글 문자열: `출고지주소`, `반품지우편번호`, `반품배송비`, `최소구매수량`, `평균출고일`, `상세이미지사용허용`, `상세설명HTML` …). 공급처 플러그인이 `RawProduct.Attributes`에 이 키로 넣으면 `Product.Attributes → ListingPayload.Logistics → 어댑터/엑셀`까지 **타입 없이 문자열 dict로** 흘러갑니다.

**`ConsignmentLogistics`** ([Marketplace.cs](../src/Tetragon.Plugin.Abstractions/Marketplace.cs)) — 위 속성을 `FromAttributes`로 타입화한 값 객체. 로직이 많은 곳:
- `OutboundShippingDays = clamp(ceil(평균출고일 + 1), 1, 30)`, 모르면 3일
- `OutboundPlace` — 주소·우편번호가 **같은 곳에서 나온 짝**이 되도록 4단계 판정 (출고지 우편번호 있음 → 출고지·반품지가 같은 건물(`KoreanAddress.SamePlace`) → 반품지를 출고지로 → 주소만). 주석에 "출고지 주소에 반품지 우편번호를 붙여 보내 엉뚱한 출고지가 생성됐던" 이력.
- `ContactNumber` — 한 칸에 여러 번호가 온 값에서 유효한 하나만 (쿠팡 16자 제한)
- `IsOverseasPurchase = sourceCurrency != "KRW"` — 통화로 해외구매대행 여부를 판정

### 12.2 레지스트리 — 플러그인이 선택되는 규칙 ([Registries.cs](../src/Tetragon.Infrastructure/Plugins/Registries.cs))

| 레지스트리 | 규칙 |
|---|---|
| `SupplierPluginRegistry.Resolve(Uri)` | **`IsLive`인 것 중 첫 `CanHandle`** → 없으면 전체에서 첫 `CanHandle`(시뮬레이션이 최후 폴백). **`IsAvailable`은 보지 않음** — 타오바오 URL을 쿠키 없이 넣으면 타오바오 플러그인이 잡혀서 `PermanentScrapeException`으로 안내 |
| `Resolve(code)` / 마켓 / 크롤러 / 발주 | 코드 일치 |
| `AiRouter.Route(capability)` | `Capabilities ∋ capability && IsAvailable` → **`IsLive` 우선** → 첫 항목. 없으면 예외 |

등록 순서(Program.cs): AliExpress → Taobao → **Amazon(`Host.Contains("amazon.")`로 매우 넓음)** → 11st → Domeggook → Simulated.

### 12.3 공급처 ([Plugins.Suppliers](../src/Tetragon.Plugins.Suppliers))

| 코드 | IsAvailable | 방식 | 물류 Attributes | 크롤러 | 비고 |
|---|---|---|---|---|---|
| `domeggook` | `api_key` 있음 | 공식 OpenAPI (`getItemView` ver 4.4 / `getItemList` 4.1 / `getCategoryList` **1.0**) | **전부 채움** | O | 819줄. 오류도 HTTP 200 + `errors` 노드. 가격 `"1+4664\|50+4650"` 수량별 단가 파싱, `selectOpt`는 문자열로 한 번 더 감싸인 JSON(`data` 조합이 핵심, `hid=1` 제외), `desc.license.usable`일 때만 `desc.contents.item` 상세 HTML 수집, `TryGetSourceProductId`로 API 호출 전 중복 판정 |
| `11st` | 항상 true | 상세 페이지 JSON-LD | 브랜드만 | X | 옵션은 XHR이라 **단일 SKU**, 재고 `soldOut?0:999` |
| `aliexpress` | `app_key`+`app_secret` | 키 있으면 `aliexpress.ds.product.get`(HMAC-SHA256) / 없으면 og: 태그로 존재만 확인 후 **Permanent 실패 + 안내** | 없음 | X | `og:title` 빈 문자열 = 판매종료 |
| `taobao` | `cookie` 있음 | mtop h5 API, `MD5(token&ts&appKey&data)` 서명, **AppKey `12574478` 하드코딩** | 없음 | X | `CheckInventoryAsync`는 항상 `IsAvailable=true` **스텁** |
| `amazon` | **항상 true** | HTML 스크래핑, 가격 4전략(바이박스→JSON→최저 오퍼→심볼 없는 가격), 통화 미가정 | `ASIN`, `가격출처` | X | TLS 지문 차단으로 사실상 항상 캡차 → Permanent. `RawJson`에 원본 대신 4필드 요약 저장 |
| `simulated` | true | `mock/demo/example` 호스트, 상품ID 시드 기반 결정적 생성 | 일부 | O | `totalAvailable=137` 고정 |
| (발주) `DomeggookOrderPlugin` | — | `getOrderList`로 권한 확인(캐시) → 403이면 `Unavailable` + 신청 안내. `setOrder` POST는 **파라미터명 미검증**(주문 스코프 승인 전) | | | |

### 12.4 마켓 어댑터 ([Plugins.Markets](../src/Tetragon.Plugins.Markets))

| 코드 | IsLive | 인증 | 등록 | 부가 인터페이스 |
|---|---|---|---|---|
| `coupang` | true | HMAC-SHA256 `CEA` 헤더 (서명에 시각 포함 → 재시도마다 요청 재생성) | API | `IShippingPlaceProvider`, `IOrderFulfillmentProvider` |
| `smartstore` | true | OAuth2 client_credentials + `bcrypt(client_id_timestamp, salt=secret)`→base64 전자서명, 토큰 static 캐시 | API | — |
| `11st` | true | `openapikey` 헤더, **요청·응답 모두 XML** | API | — |
| `auction` / `gmarket` | **false** | 없음 | 모든 메서드가 `USE_EXCEL` 실패 반환, `FetchOrdersAsync`만 `NotSupportedException` throw | — |
| `mockmarket` | false | 없음 | static dict에 보관, 결정적 데모 주문·CS 생성 | `IOrderFulfillmentProvider` |

**쿠팡 어댑터가 하는 일** (partial class 4파일, 약 1,750줄 — 이 프로젝트에서 가장 복잡한 외부 연동)
1. **카테고리 자동 결정** (`CoupangCategory.cs`): 상품 매핑 → `default_category_code` → `POST /categorization/predict`. 실패 시 `NO_CATEGORY`.
2. **카테고리 메타로 필수 항목 자동 충족**: `MANDATORY` 구매옵션·검색옵션·고시정보를 조회(프로세스 수명 캐시) → 옵션값을 이름 부분일치/자유입력 속성에 배정, SELECT형은 허용값 내에서만, NUMBER형은 `값+usableUnits 단위`, 나머지 `"상세페이지 참조"`.
3. **등록 본문**: `deliveryMethod = IsOverseasPurchase ? AGENT_BUY : SEQUENCIAL`, 배송비 무료(판매가에 녹임), `returnCenterCode` 없으면 `NO_RETURN_CENTERCODE` + 주소 직접 기입, **1,000원 미만 판매가는 1,000원으로 올림**, `originalPrice = 판매가×1.2`, 대표이미지 1장, `requested=false`(수동 판매요청), `saleEndedAt=2099-01-01`.
4. **응답 적응 재시도**: "반품배송비는 0원 ~ 5,000원까지" 메시지에서 상한을 파싱해 1회 재시도. 403은 응답의 IP를 뽑아 "WING에 {ip} 등록" 안내. `0x1F` 첫 바이트면 gzip 힌트.
5. **출고지/반품지** (`CoupangShippingPlace.cs`): 목록 static 캐시(TTL 3분) → `FindMatch`(우편번호 AND 주소 → 주소 → 이름; "우편번호만 봤다가 4개 공급처가 같은 반품지로 묶였던" 사고 기록) → 생성(`addressType=JIBUN`, `remoteInfos=[]`) → 폴백은 **아무 배송지나 고르지 않고** 설정 기본값 없으면 실패.
6. **스로틀** (`CoupangThrottle.cs`): 동시 2, 간격 350ms, 빈 응답/5xx 3회 재시도.
7. **이행** (`CoupangFulfillment.cs`): 송장 `POST …/orders/invoices` (HTTP 200이어도 `data[].succeed` 건별 확인), 주문 `status=ACCEPT`, 수령인 전화는 **안심번호 우선**, CS는 취소(v4)·반품(v5) 별도 호출.

**스마트스토어**는 `ConsignmentLogistics`를 사실상 쓰지 않습니다 — 반품비 5,000/교환비 10,000, 원산지 `0200037`(중국)+`직수입`, 카테고리 폴백 `50000803` 등 하드코딩이고 `FetchOrdersAsync`가 `ShipTo`를 채우지 않아 **위탁 발주에 필요한 배송지가 없습니다**. **11번가**는 `addrSeq`/`rtngdDlvCn`(주소 순번)으로 출고지·반품지를 받고 `asDetail`에 공급사명+전화를 넣어 A/S가 공급처로 가게 합니다.

### 12.5 AI ([Plugins.Ai](../src/Tetragon.Plugins.Ai))

- `claude`: `POST api.anthropic.com/v1/messages`, **`model = "claude-opus-5"`**, `max_tokens 8000`, `output_config.format = json_schema {results: string[]}`로 입력과 1:1 대응 강제. 결과가 모자라면 원문으로 채움, `stop_reason == refusal` 처리, 예외는 전부 `AiResult.Fail`. Capabilities 3종 전부.
- `simulated-ai`: 중국어 상품용어 사전 60여 항목 치환 + 남은 한자 제거. **`Translate`, `OptionCleanup`만** — `ProductNameSeo`는 없어서 Claude 키가 없으면 `AiRouter`가 예외를 던지지만, `EnrichmentHandler`가 그 `InvalidOperationException`을 잡아 번역 결과를 유지하므로 파이프라인은 멈추지 않습니다.
- `PromptTemplates` v1.0.0 — Translate/SEO/OptionCleanup 시스템 프롬프트 3종. "운영에서는 테이블로 이관" 예정 주석.

### 12.6 엑셀 양식 ([ExcelTemplates.cs](../src/Tetragon.Plugins.Markets/ExcelTemplates.cs), [ExcelWorkbookService.cs](../src/Tetragon.Infrastructure/Excel/ExcelWorkbookService.cs))

| 템플릿 | MarketCode | 컬럼 | 특징 |
|---|---|---|---|
| ESM Plus | `esmplus` | 24 | `사이트구분` A/G/AG로 옥션+G마켓 동시. 주소를 문자열로 |
| 11번가 | `11st` | 22 | 발송지/반품지는 seq 코드 |
| 쿠팡 | `coupang` | 24 | **`배송방법 = AGENT_BUY`, `해외구매대행 = Y` 무조건** (어댑터는 통화로 분기) · 반품비가 공급처 값이 아닌 설정값 · 옵션행 판매가/옵션가에 둘 다 절대값 · 1,000원 하한 미적용 |
| 스마트스토어 | `smartstore` | 21 | 원산지 `0200037`, `직수입` |

공통: 옵션 ≤1이면 1행, 아니면 Variant마다 1행이고 **첫 행에만 이미지·상세설명**. 옵션가는 `Variant.Price − 대표가`(쿠팡 제외). `TemplateHelpers`가 출고지(`OutboundPlace.Address`)·반품지(`(우편번호) 주소`)·반품비·연락처·상세HTML(라이선스 허용 시) 폴백을 담당. ESM 어댑터 코드(`auction`/`gmarket`)와 템플릿 코드(`esmplus`)가 다릅니다 — `/excel/templates`와 `/plugins`를 짝지을 때 주의.

---

## 13. 프론트엔드 ([frontend/src](../frontend/src))

### 13.1 구조

- `main.tsx`: `QueryClient`(`refetchOnWindowFocus:false, retry:1, staleTime:3000`) → `BrowserRouter` → `App`. ErrorBoundary·Suspense·lazy 없음.
- `App.tsx`: 220px 사이드바 + 본문. **전역 상태는 React Query 캐시뿐**, 화면 간 연동은 `invalidateQueries(['jobs'|'orders'|'wallet'|'csTickets'|'products'|'policies'|'credentials'|'plugins'|'categoryJobs'])`로만.
- `shared/api.ts`(793줄): 모든 DTO 타입 + `request<T>`(`fetch('/api/v1'+path)`, 비2xx의 `{error}`를 `Error`로) + `downloadFile`(blob, `Content-Disposition` 파일명) + `uploadExcel`(multipart `file`) + `subscribePipeline`(EventSource).
- `shared/ui.tsx`: `Page`, `StatusBadge`(상품/리스팅/Job/컴플라이언스 17종 매핑), `PipelineStepper`, `Empty`, `ErrorBox`, `won/shortTime/shortDate`.
- API 베이스 `/api/v1`이 코드에 고정 — 프로덕션 `dist`는 `/api`를 5080으로 넘겨 줄 리버스 프록시가 필요합니다.

### 13.2 라우트 → 화면 → 호출

| 경로 | 화면 | 주요 호출 | 갱신 |
|---|---|---|---|
| `/dashboard` | KPI 4 + 파이프라인 단계/상태/마켓별 막대 | `GET /dashboard/summary` | **5초 폴링** |
| `/quick` | 빠른 등록 — URL 여러 줄 + 마켓 체크(`isLive`만, `isAvailable` 아니면 비활성) + 정책 | `POST /products/preview`(첫 URL만), `POST /products/quick-list` | SSE 80건 피드 |
| `/collect` | URL 수집 + Job 테이블(스텝퍼, 재시도 버튼) | `POST /products/collect`, `GET /jobs?limit=50`, `POST /jobs/{id}/retry` | **2초 폴링 + SSE 이벤트마다 invalidate** |
| `/categories` | 공급처 선택 → 1단계 드릴다운 목록(`isSelectable` 존중) → 미리보기 12건 → 수집 / 엑셀 왕복 | `/categories/*`, `GET /excel/categories/{s}`, `POST /excel/categories/import`, `GET /categories/jobs` | 2.5초 폴링 |
| `/products` | 필터 + 다중 선택 → 재고 동기화 / 마켓 등록 / 엑셀 내보내기(템플릿별 버튼) | `GET /products?pageSize=100`, `POST /inventory/check`, `POST /listings`, `POST /excel/listings/{m}` | 수동 (페이지네이션 UI 없음) |
| `/products/:id` | 기본정보·위탁물류 카드·가격 추적·상세이미지(라이선스 허용 시만 로드)·컴플라이언스·SKU·등록 현황, 이름 인라인 편집, 차단 해제 | `GET /products/{id}`, `/detail-html`, `PATCH`, `POST /resume` | |
| `/audit` | 재고 점검(mutation), 중복 상품, 아이템위너 위험 | `POST /audit/stock`, `GET /audit/duplicates` | 수동 |
| `/pricing` | Rule 카드 추가/↑↓/파라미터 편집 → 저장, 상품 골라 시뮬레이션 | `/pricing-policies/*`, `POST /{id}/simulate` | |
| `/listings` | 마켓·상태 필터 테이블 (읽기 전용) | `GET /listings` | 5초 폴링 |
| `/orders` | 주문 수집 버튼, 상태별 처리(발주서 토글 / 송장 입력) | `POST /orders/fetch`, `POST /{id}/tracking` | 수동 |
| (내장) `PurchaseSheet` | ①무엇·어디서 ②누구에게(한 줄 복사) ③수익 ④발주(preflight: 잔액·blockers·capability) → 자동 발주 / 수동 기록 | `GET /{id}/purchase-sheet`, `/purchase-preflight`, `POST /auto-purchase`, `POST /purchase` | 성공 시 `wallet`까지 invalidate |
| `/cs` | 자동화 패널(멈춤 배너·DryRun 배너·토글 6·한도 4·즉시 실행 결과) + 티켓 목록(공급처 조치 컬럼) | `/cs/automation*`, `/cs/tickets*` | 수동 |
| `/wallet` | 사용 가능/총잔액/예약, 입출금(프리셋 10/50/100만), 경고선, 거래 이력 | `/wallet/*` | 5초 폴링 |
| `/settings` | 자격증명 8스코프(password 입력, 등록됨 배지), 플러그인 상태표, 배송지 현황(쿠팡 고정), 금지어 CRUD | `/settings/*`, `/plugins`, `/compliance/rules` | |

### 13.3 실시간(SSE) 소비 방식

`subscribePipeline`은 `new EventSource('/api/v1/stream')` + `onmessage`만 씁니다 — `onerror/onopen` 없음, 연결 상태 표시 없음, 재연결은 브라우저 기본(약 3초)에 맡기고 **서버가 접속 시 최근 20건을 재전송**하는 것으로 공백을 메웁니다. `/collect`와 `/quick` 두 화면이 각자 연결을 엽니다. `/collect`는 SSE가 끊겨도 2초 폴링이 최종 상태를 보정합니다(`Collect.tsx:19` 주석).

---

## 14. 실제 API 목록 (코드 기준)

README의 표에는 없는 엔드포인트가 여럿 있어 코드(`MapGet/MapPost…`)에서 직접 뽑았습니다.

| 그룹 | 메서드·경로 | 비고 |
|---|---|---|
| Products | `POST /api/v1/products/collect` | 202 + jobIds |
| | `POST /api/v1/products/preview` | 저장 없는 링크 미리보기 (README 미기재) |
| | `POST /api/v1/products/quick-list` | 링크→수집→자동 등록 (README 미기재) |
| | `GET /api/v1/products` · `GET /{id}` · `PATCH /{id}` · `POST /{id}/resume` · `GET /{id}/raw` · `GET /{id}/detail-html` | |
| | `GET /api/v1/products/{id}/listing-payload` | 마켓 전송 직전 payload 확인 (README 미기재) |
| Jobs | `GET /api/v1/jobs` · `GET /{id}` · `POST /{id}/retry` | |
| Categories | `GET /suppliers` · `GET /{supplier}` · `POST /{supplier}/preview` · `POST /{supplier}/collect` · `GET /jobs` · `GET /jobs/{id}` | |
| Excel | `GET /templates` · `POST /listings/{market}` · `GET /categories/{supplier}` · `POST /categories/import` | |
| Pricing | `GET /pricing-policies/rules` · `GET` · `POST` · `PUT /{id}` · `POST /{id}/simulate` | |
| Listings | `POST /listings` · `GET /listings` · `POST /inventory/check` | |
| Compliance | `GET/POST /compliance/rules` · `DELETE /rules/{id}` | |
| Orders | `GET /orders` · `POST /fetch` · `GET /{id}/purchase-sheet` · `GET /{id}/purchase-preflight` · `POST /{id}/auto-purchase` · `POST /{id}/purchase` · `POST /{id}/tracking` | preflight/auto-purchase는 README 미기재 |
| Wallet | `GET /wallet` · `GET /transactions` · `POST /deposit` · `POST /withdraw` · `PUT /threshold` | README 미기재 |
| CS | `GET/PUT /cs/automation` · `POST /automation/resume` · `POST /automation/run` · `GET /tickets` · `POST /tickets/{id}/supplier-done` · `/resolve` · `/dismiss` | README 미기재 |
| Audit | `POST /audit/stock` · `GET /audit/duplicates` | README 미기재 |
| Settings | `GET/PUT /settings/credentials` · `GET /settings/shipping-places/{market}` | |
| Misc | `GET /dashboard/summary` · `GET /plugins` · `GET /stream`(SSE) · `GET /` → swagger | |

---

## 15. 코드 분석 중 눈에 띈 점 (동작에 영향 있는 것 위주)

운영·확장 시 알고 있어야 할 사실들입니다. 버그라기보다 **현재 구현의 경계**입니다.

1. **이벤트 큐가 메모리** — API를 재시작하면 진행 중이던 Job은 `Running` 상태로 DB에 남고 이벤트는 사라집니다. 화면에서 `retry`로 살려야 합니다. 지수 백오프 재시도도 `Task.Run` fire-and-forget이라 같은 제약.
2. **재시도는 수집 단계에만** — 번역·가격·컴플라이언스·등록 실패는 1회로 DLQ/Failed입니다 (등록은 `retry`가 등록만 다시 함).
3. **Enrichment는 국내 상품도 AI를 거침** — 도매꾹(ko-KR)도 `Translate`를 호출합니다. Claude 키가 등록돼 있으면 한국어→한국어 번역 호출이 발생합니다 (`AiRouter`가 Claude를 우선 선택하는지는 §12 참고).
4. **`ResumeBlockedProductUseCase`는 최근 500개 Job만 검색** — 아주 오래된 Blocked Job은 Product만 Ready가 되고 Job은 Blocked로 남을 수 있습니다.
5. **컴플라이언스 `MarketCode` 필드는 저장만 되고 필터링에 쓰이지 않음** — `ComplianceEngine`이 모든 Rule을 전 마켓에 적용합니다.
6. **`TariffVatRule`의 USD 기준 판정은 1,350원 고정 환산** — 실환율은 `exchange-rate` Rule만 씁니다.
7. **자격증명 평문 저장 + 읽기 경로 2개**(`ICredentialStore` / `CachedCredentialProvider`). 설정 저장 시 캐시 Invalidate는 하지만, 싱글턴 플러그인이 자격증명을 생성자에서 읽어 두는 구현이면 재기동이 필요할 수 있습니다.
8. **스키마 동기화가 자체 구현** — 컬럼 추가까지만. 모델에서 컬럼을 빼거나 타입을 바꾸면 기존 DB와 어긋납니다.
9. **`EventDispatcherWorker._processed`가 무한 누적** — 장기 실행 시 메모리가 EventId 수만큼 늘어납니다 (GUID 1개당 수십 바이트라 수십만 건은 문제없지만 비워지는 시점이 없음).
10. **CORS가 5173/4173 고정**, 인증 없음 — 로컬 단일 사용자 전제입니다.
11. **`DuplicateAudit`의 `listings` 매개변수 미사용** (빌드 경고 1건, diagnostics/REPORT.md §4).
12. **`AiRouter`만 `IsAvailable`을 거릅니다.** `IPlugin.IsAvailable` 주석은 "라우터가 후보에서 제외한다"고 하지만 `SupplierPluginRegistry.Resolve(Uri)`는 확인하지 않습니다. 결과적으로 자격증명 없는 공급처도 선택되고 `PermanentScrapeException` 안내로 끝나는데, 이건 의도된 UX로 보이지만 계약 문서와는 어긋납니다.
13. **`AmazonSupplierPlugin.CanHandle`이 `Host.Contains("amazon.")`** — 등록 순서상 11번가·도매꾹보다 앞이라, `amazon.`을 포함하는 다른 호스트를 선점할 수 있습니다.
14. **쿠팡 엑셀 양식만 `AGENT_BUY`/해외구매대행 `Y`를 무조건 넣습니다.** 같은 상품이 API 경로로는 `SEQUENCIAL`(국내)로 가는데 엑셀 경로로는 구매대행으로 나갑니다 — 도매꾹 상품을 엑셀로 내보낼 때 문제가 될 수 있습니다. 반품비도 이 템플릿만 공급처 값이 아닌 설정값을 씁니다.
15. **스마트스토어 어댑터는 위탁 물류를 쓰지 않습니다** — 반품/교환비·원산지·카테고리가 하드코딩이고 `FetchOrdersAsync`가 배송지를 채우지 않아, 이 마켓 주문은 자동 발주 preflight의 "구매자 배송지 없음"에 걸립니다.
16. **프론트 쿼리 키 충돌**: `Pricing.tsx:16`이 `['products','','']` 키에 `pageSize:30` 페처를, `Products.tsx:23`이 같은 키(필터 미입력 시)에 `pageSize:100` 페처를 씁니다. `staleTime:3000` 안에 두 화면을 오가면 상품 목록에 30건짜리 응답이 뜹니다.
17. **`index.css:231`의 `var(--muted)`는 정의되지 않은 변수** (`--text-dim`이 맞음). `table.kv th`(발주서·상품 상세 물류 카드)가 흐린 색 대신 본문 색으로 나옵니다.
18. 프론트 자잘한 것들: `/products?status=Ready` 링크는 `useSearchParams`를 쓰는 곳이 없어 필터가 적용되지 않음 · `api.purchaseOrder`는 호출부 없는 죽은 함수 · `quickList`의 `reuseExisting` 플래그를 화면이 보내지 않음 · `PipelineStepper`의 라벨 맵에 `ReadyToList`/`Listing`이 없어 영문 식별자가 노출될 수 있음 · `*` 라우트와 ErrorBoundary 없음 · `index.html`이 아직 `lang="en"`, `<title>frontend</title>`.
19. `diagnostics/REPORT.md`(2026-07-30)에 따르면 당시 데이터는 수집 성공 Job 912건, DLQ 10건(전부 외부 요인), 쿠팡 등록 실패 원인은 IP 미허용(403)·카테고리 추천 실패(`NO_CATEGORY`)·구 우편번호 등. 옛 파서로 수집된 약 860건의 옵션/물류 정보 재수집은 **미해결 과제**로 남아 있습니다.

---

## 부록 A. 요청 하나의 생애 (빠른 등록 예시)

```
[브라우저] POST /api/v1/products/quick-list {urls:[도매꾹링크], marketCodes:["coupang"]}
  └ QuickListUseCase
      ├ suppliers.Resolve(url) → DomeggookSupplierPlugin
      ├ TryGetSourceProductId → RawProducts 조회 → (없음)
      ├ ScrapeJob.Create(autoListMarkets=["coupang"]) 저장
      └ bus.Publish(ScrapeRequested)                              ← 202 응답은 여기서 끝
[EventDispatcherWorker 스레드]
  ScrapeRequestedHandler  : 도매꾹 API getItemView → RawProduct → Product(Normalized) + RawProductRecord
                            SSE {stage:Collecting}, {stage:Normalizing}
  EnrichmentHandler       : AiRouter(Translate) → Product(Enriched)      SSE {stage:Enriching}
  PricingHandler          : 기본 정책 6 Rule (국내라 배송비·관세 Skip) → Priced   SSE {stage:Pricing}
  ComplianceHandler       : 금지어 없음 → Ready → Job Completed → AutoList 있음 → Listing/Running
                            bus.Publish(ListingRequested)
  ListingRequestedHandler : ListingPayloadBuilder → ShippingPlaceResolver(쿠팡 출고지 코드 확보/재사용)
                            → CoupangAdapter.RegisterAsync (카테고리 예측·필수옵션·고시정보 자동 채움)
                            → Listing.Registered, Product.Listed, Job Completed   SSE {stage:Listing, state:Succeeded}
[브라우저] EventSource(/api/v1/stream)이 위 SSE를 받아 스텝퍼 갱신
```
