using Tetragon.Domain.Attention;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Imports;
using Tetragon.Domain.Listings;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Pricing;
using Tetragon.Domain.Sourcing;
using Tetragon.Domain.Treasury;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Ports;

// ─── 이벤트 버스 (RabbitMQ 대응 포트 — 현재 구현: 인메모리 Channels) ────

public interface IEventBus
{
    /// <summary>통합 이벤트 발행. 라우팅 키는 타입에서 유도된다.</summary>
    Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default);
}

/// <summary>통합 이벤트 소비자. DI에 등록하면 디스패처가 라우팅한다.</summary>
public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken ct);
}

// ─── 리포지토리 (설계서 ADR-006: 테넌트 컨텍스트 추상화) ────────────────

public interface IProductRepository
{
    Task<Product?> FindAsync(Guid id, CancellationToken ct);
    Task AddAsync(Product product, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task<(IReadOnlyList<Product> Items, int Total)> SearchAsync(
        string? status, string? keyword, int page, int pageSize, CancellationToken ct);
    Task<Dictionary<string, int>> CountByStatusAsync(CancellationToken ct);
}

public interface IScrapeJobRepository
{
    Task<ScrapeJob?> FindAsync(Guid id, CancellationToken ct);
    Task AddAsync(ScrapeJob job, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task<IReadOnlyList<ScrapeJob>> RecentAsync(int limit, CancellationToken ct);
}

public interface IRawProductRepository
{
    Task AddAsync(RawProductRecord record, CancellationToken ct);
    Task<RawProductRecord?> FindByProductAsync(Guid productId, CancellationToken ct);
    /// <summary>공급처 + 원본 상품번호로 이미 수집한 상품을 찾는다 (링크 재등록 시 중복 방지).</summary>
    Task<RawProductRecord?> FindBySourceAsync(string supplierCode, string sourceProductId, CancellationToken ct);
}

public interface IWalletRepository
{
    /// <summary>테넌트의 금고. 없으면 만든다 (금고는 항상 하나 존재해야 한다).</summary>
    Task<Wallet> GetOrCreateAsync(CancellationToken ct);
    /// <summary>금고 상태와 거래 이력을 함께 저장한다 — 둘이 어긋나면 잔액을 설명할 수 없다.</summary>
    Task SaveAsync(Wallet wallet, WalletTransaction transaction, CancellationToken ct);
    Task<IReadOnlyList<WalletTransaction>> RecentTransactionsAsync(int limit, CancellationToken ct);
    /// <summary>금액 변동 없이 설정만 바뀐 경우 (경고선 등).</summary>
    Task SaveAsync(CancellationToken ct);
}

public interface IShippingPlaceMappingRepository
{
    Task<ShippingPlaceMapping?> FindAsync(string marketCode, string addressKey, CancellationToken ct);
    Task AddAsync(ShippingPlaceMapping mapping, CancellationToken ct);
    Task<IReadOnlyList<ShippingPlaceMapping>> AllAsync(CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface ICategoryCollectJobRepository
{
    Task<CategoryCollectJob?> FindAsync(Guid id, CancellationToken ct);
    Task AddAsync(CategoryCollectJob job, CancellationToken ct);
    Task<IReadOnlyList<CategoryCollectJob>> RecentAsync(int limit, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    /// <summary>이미 수집한 상품인지 확인 (공급처 + 원본 상품 ID 기준 중복 제거).</summary>
    Task<HashSet<string>> ExistingSourceIdsAsync(string supplierCode, IEnumerable<string> sourceIds, CancellationToken ct);
}

public interface IPricingPolicyRepository
{
    Task<PricingPolicy?> FindAsync(Guid id, CancellationToken ct);
    Task<PricingPolicy?> FindDefaultAsync(CancellationToken ct);
    Task<IReadOnlyList<PricingPolicy>> AllAsync(CancellationToken ct);
    Task AddAsync(PricingPolicy policy, CancellationToken ct);
    Task AddCalculationsAsync(IEnumerable<PriceCalculation> calculations, CancellationToken ct);
    Task<IReadOnlyList<PriceCalculation>> CalculationsForProductAsync(Guid productId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IComplianceRepository
{
    Task<IReadOnlyList<ComplianceRule>> RulesAsync(CancellationToken ct);
    Task AddRuleAsync(ComplianceRule rule, CancellationToken ct);
    Task RemoveRuleAsync(Guid ruleId, CancellationToken ct);
    Task AddResultAsync(ComplianceResult result, CancellationToken ct);
    Task<ComplianceResult?> LatestResultAsync(Guid productId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IListingRepository
{
    Task<Listing?> FindAsync(Guid id, CancellationToken ct);
    Task<Listing?> FindByProductAndMarketAsync(Guid productId, string marketCode, CancellationToken ct);
    /// <summary>마켓 상품번호로 역추적 — 주문을 원본 상품·공급처에 연결할 때 쓴다.</summary>
    Task<Listing?> FindByMarketItemAsync(string marketCode, string marketItemId, CancellationToken ct);
    Task<IReadOnlyList<Listing>> SearchAsync(string? marketCode, string? status, CancellationToken ct);
    Task<IReadOnlyList<Listing>> ByProductAsync(Guid productId, CancellationToken ct);
    Task AddAsync(Listing listing, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface ICsTicketRepository
{
    Task<CsTicket?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>같은 요청을 두 번 담지 않기 위한 확인.</summary>
    Task<bool> ExistsAsync(string marketCode, string marketTicketId, CancellationToken ct);
    Task<IReadOnlyList<CsTicket>> SearchAsync(string? status, string? kind, CancellationToken ct);
    Task AddAsync(CsTicket ticket, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IAutomationPolicyRepository
{
    Task<AutomationPolicy> GetOrCreateAsync(CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IAttentionRepository
{
    Task<AttentionItem?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>같은 사실이 이미 열려 있는가 (08 §1.3 ③ — dedup의 실체).</summary>
    Task<AttentionItem?> FindActiveAsync(string kind, string dedupKey, CancellationToken ct);
    Task<IReadOnlyList<AttentionItem>> FindManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    /// <summary>목록. 항상 impact_krw 내림차순 — 이 정렬이 "줄 세우기"의 전부다.</summary>
    Task<IReadOnlyList<AttentionItem>> QueryAsync(
        AttentionState? state, string? kind, bool dueOnly, int limit, CancellationToken ct);
    Task<IReadOnlyList<AttentionItem>> ActiveByKindAsync(string kind, CancellationToken ct);
    /// <summary>일일 상한(08 §1.3 ②) 계산용 — 오늘 새로 열린 항목 수.</summary>
    Task<int> CountOpenedSinceAsync(string kind, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<AttentionKindSummary>> SummaryAsync(CancellationToken ct);
    Task AddAsync(AttentionItem item, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

/// <summary>kind별 집계 — 화면 상단의 "무엇이 몇 건 · 얼마가 걸려 있나".</summary>
public sealed record AttentionKindSummary(string Kind, int Count, decimal ImpactKrw);

public interface IOrderRepository
{
    Task<Order?> FindAsync(Guid id, CancellationToken ct);
    Task<Order?> FindByMarketOrderAsync(string marketCode, string marketOrderId, CancellationToken ct);
    Task<IReadOnlyList<Order>> SearchAsync(string? status, CancellationToken ct);
    Task AddAsync(Order order, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface IImportProfileRepository
{
    /// <summary>지문이 정확히 일치할 때만 찾는다 — 비슷한 양식을 갖다 쓰면 틀린 숫자가 조용히 들어온다.</summary>
    Task<ImportProfile?> FindAsync(string channel, string purpose, string fingerprint, CancellationToken ct);
    Task<IReadOnlyList<ImportProfile>> ListAsync(string? channel, string? purpose, CancellationToken ct);
    Task AddAsync(ImportProfile profile, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

// ─── 외부 서비스 포트 ───────────────────────────────────────────────────

/// <summary>환율 Provider (설계서 5.4 — 캐시 1시간).</summary>
public interface IExchangeRateProvider
{
    Task<decimal> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken ct);
}

/// <summary>테넌트별 자격증명 저장소 (마켓 API 키, AI 키, 공급처 쿠키).</summary>
public interface ICredentialStore
{
    Task<MarketCredential> GetAsync(string scope, CancellationToken ct);
    Task SetAsync(string scope, Dictionary<string, string> secrets, CancellationToken ct);
    /// <summary>키 이름 목록만 반환 (값은 마스킹).</summary>
    Task<Dictionary<string, List<string>>> ListScopesAsync(CancellationToken ct);
}

/// <summary>실시간 파이프라인 이벤트를 UI(SSE)로 브로드캐스트.</summary>
public interface IPipelineNotifier
{
    void Notify(PipelineNotification notification);
    IAsyncEnumerable<PipelineNotification> SubscribeAsync(CancellationToken ct);
}

public sealed record PipelineNotification(
    Guid JobId, string Stage, string State, Guid? ProductId, string? Message, DateTimeOffset At);
