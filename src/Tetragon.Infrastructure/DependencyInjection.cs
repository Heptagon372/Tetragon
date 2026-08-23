using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tetragon.Application.Pipeline;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Application.Services.Import;
using Tetragon.Application.UseCases;
using Tetragon.Domain.Events;
using Tetragon.Domain.Pricing;
using Tetragon.Domain.Pricing.Rules;
using Tetragon.Infrastructure.External;
using Tetragon.Infrastructure.Messaging;
using Tetragon.Infrastructure.Persistence;
using Tetragon.Infrastructure.Plugins;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Tetragon 코어(Application + Infrastructure) 등록. 플러그인은 호스트에서 별도 등록.</summary>
    public static IServiceCollection AddTetragonCore(this IServiceCollection services, string sqliteConnectionString)
    {
        // ── 저장소 ──
        services.AddDbContext<TetragonDbContext>(options => options.UseSqlite(sqliteConnectionString));
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IScrapeJobRepository, ScrapeJobRepository>();
        services.AddScoped<IRawProductRepository, RawProductRepository>();
        services.AddScoped<ICategoryCollectJobRepository, CategoryCollectJobRepository>();
        services.AddScoped<IShippingPlaceMappingRepository, ShippingPlaceMappingRepository>();
        services.AddScoped<IWalletRepository, WalletRepository>();
        services.AddScoped<ShippingPlaceResolver>();
        services.AddScoped<PurchaseOrderSheetBuilder>();
        services.AddScoped<SupplierPurchaseService>();
        services.AddScoped<ShippingPlaceAudit>();
        services.AddScoped<StockAudit>();
        services.AddScoped<DuplicateAudit>();
        services.AddScoped<IPricingPolicyRepository, PricingPolicyRepository>();
        services.AddScoped<IComplianceRepository, ComplianceRepository>();
        services.AddScoped<IListingRepository, ListingRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<ICsTicketRepository, CsTicketRepository>();
        services.AddScoped<IAutomationPolicyRepository, AutomationPolicyRepository>();
        services.AddScoped<FulfillmentAutomation>();
        services.AddScoped<ICredentialStore, CredentialStore>();
        // 주의 원장 (확장 08 §1) — 09~12가 전부 여기에 쓴다
        services.AddScoped<IAttentionRepository, AttentionRepository>();
        services.AddSingleton<AttentionPolicy>();
        services.AddScoped<AttentionLedger>();
        services.AddScoped<AttentionScan>();
        // CSV 임포트 프레임 (확장 08 §3) — 09 성과 / 10 정산이 공유한다
        services.AddScoped<IImportProfileRepository, ImportProfileRepository>();
        services.AddScoped<ImportProfileStore>();
        services.AddScoped<CsvImporter>();

        // ── 이벤트버스 + 디스패처 ──
        services.AddSingleton<InMemoryEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
        services.AddHostedService<EventDispatcherWorker>();
        services.AddSingleton<PipelineNotifier>();
        services.AddSingleton<IPipelineNotifier>(sp => sp.GetRequiredService<PipelineNotifier>());

        // ── 외부 서비스 포트 ──
        services.AddHttpClient("exchange-rate", c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddSingleton<IExchangeRateProvider, OpenErApiExchangeRateProvider>();

        // ── 도메인/애플리케이션 서비스 ──
        services.AddScoped<ProductNormalizer>();
        services.AddScoped<PricingEngine>();
        services.AddScoped<ComplianceEngine>();
        services.AddScoped<ListingPayloadBuilder>();
        services.AddScoped<JobProgress>();
        services.AddScoped<CollectProductsUseCase>();
        services.AddScoped<CollectCategoryUseCase>();
        services.AddScoped<QuickListUseCase>();
        services.AddScoped<LinkPreviewService>();
        services.AddScoped<RequestListingUseCase>();
        services.AddScoped<ResumeBlockedProductUseCase>();

        // ── 가격 Rule (새 Rule = 여기에 한 줄 추가, 기존 수정 금지 — OCP) ──
        services.AddSingleton<IPriceRule, ExchangeRateRule>();
        services.AddSingleton<IPriceRule, IntlShippingRule>();
        services.AddSingleton<IPriceRule, TariffVatRule>();
        services.AddSingleton<IPriceRule, MarginRule>();
        services.AddSingleton<IPriceRule, MarketFeeRule>();
        services.AddSingleton<IPriceRule, PsychRoundingRule>();

        // ── 파이프라인 이벤트 핸들러 ──
        services.AddScoped<IIntegrationEventHandler<ScrapeRequested>, ScrapeRequestedHandler>();
        services.AddScoped<IIntegrationEventHandler<CategoryCrawlRequested>, CategoryCrawlHandler>();
        services.AddScoped<IIntegrationEventHandler<ProductNormalized>, EnrichmentHandler>();
        services.AddScoped<IIntegrationEventHandler<ProductEnriched>, PricingHandler>();
        services.AddScoped<IIntegrationEventHandler<PriceCalculated>, ComplianceHandler>();
        services.AddScoped<IIntegrationEventHandler<ListingRequested>, ListingRequestedHandler>();
        services.AddScoped<IIntegrationEventHandler<InventoryCheckRequested>, InventoryCheckHandler>();
        services.AddScoped<IIntegrationEventHandler<StockChanged>, StockChangedHandler>();

        // ── 플러그인 레지스트리 ──
        services.AddSingleton<CachedCredentialProvider>();
        services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<CachedCredentialProvider>());
        // 봇 차단(TLS 지문/JS 챌린지) 사이트용 브라우저 fetch. 사이드카 미설정이면 IsAvailable=false.
        services.AddSingleton<IBrowserFetcher, HttpBrowserFetcher>();
        services.AddSingleton<ISupplierPluginRegistry, SupplierPluginRegistry>();
        services.AddSingleton<IMarketplaceAdapterRegistry, MarketplaceAdapterRegistry>();
        services.AddSingleton<ICategoryCrawlerRegistry, CategoryCrawlerRegistry>();
        services.AddSingleton<ISupplierOrderPluginRegistry, SupplierOrderPluginRegistry>();

        // ── 엑셀 (마켓별 양식은 호스트에서 IMarketExcelTemplate으로 등록) ──
        services.AddSingleton<Excel.ExcelWorkbookService>();
        services.AddSingleton<IAiRouter, AiRouter>();

        return services;
    }
}
