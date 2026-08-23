using Tetragon.Plugin.Abstractions;

namespace Tetragon.Infrastructure.Plugins;

/// <summary>DI에 등록된 공급처 플러그인을 URL/코드로 해석 (설계서 5.1 Factory).</summary>
public sealed class SupplierPluginRegistry(IEnumerable<ISupplierPlugin> plugins) : ISupplierPluginRegistry
{
    private readonly List<ISupplierPlugin> _plugins = plugins.ToList();

    public IReadOnlyList<ISupplierPlugin> All => _plugins;

    public ISupplierPlugin? Resolve(Uri url) =>
        // 실연동 플러그인 우선, 시뮬레이션은 마지막 폴백
        _plugins.Where(p => p.IsLive).FirstOrDefault(p => p.CanHandle(url))
        ?? _plugins.FirstOrDefault(p => p.CanHandle(url));

    public ISupplierPlugin? Resolve(string supplierCode) =>
        _plugins.FirstOrDefault(p => p.Code.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>DI에 등록된 카테고리 크롤러를 공급처 코드로 해석.</summary>
public sealed class CategoryCrawlerRegistry(IEnumerable<ICategoryCrawler> crawlers) : ICategoryCrawlerRegistry
{
    private readonly List<ICategoryCrawler> _crawlers = crawlers.ToList();

    public IReadOnlyList<ICategoryCrawler> All => _crawlers;

    public ICategoryCrawler? Resolve(string supplierCode) =>
        _crawlers.FirstOrDefault(c => c.SupplierCode.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>DI에 등록된 재고 스캐너를 공급처 코드로 해석.</summary>
public sealed class StockScannerRegistry(IEnumerable<IStockScanner> scanners) : IStockScannerRegistry
{
    private readonly List<IStockScanner> _scanners = scanners.ToList();

    public IReadOnlyList<IStockScanner> All => _scanners;

    public IStockScanner? Resolve(string supplierCode) =>
        _scanners.FirstOrDefault(s => s.SupplierCode.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>DI에 등록된 발주 플러그인을 공급처 코드로 해석.</summary>
public sealed class SupplierOrderPluginRegistry(IEnumerable<ISupplierOrderPlugin> plugins) : ISupplierOrderPluginRegistry
{
    private readonly List<ISupplierOrderPlugin> _plugins = plugins.ToList();

    public IReadOnlyList<ISupplierOrderPlugin> All => _plugins;

    public ISupplierOrderPlugin? Resolve(string supplierCode) =>
        _plugins.FirstOrDefault(p => p.SupplierCode.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>DI에 등록된 마켓 어댑터를 코드로 해석.</summary>
public sealed class MarketplaceAdapterRegistry(IEnumerable<IMarketplaceAdapter> adapters) : IMarketplaceAdapterRegistry
{
    private readonly List<IMarketplaceAdapter> _adapters = adapters.ToList();

    public IReadOnlyList<IMarketplaceAdapter> All => _adapters;

    public IMarketplaceAdapter? Resolve(string marketCode) =>
        _adapters.FirstOrDefault(a => a.Code.Equals(marketCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// AI Provider 라우터 (설계서 5.3 Strategy).
/// 능력을 지원하는 실연동 Provider 우선, 없으면 시뮬레이션 폴백.
/// </summary>
public sealed class AiRouter(IEnumerable<IAiProviderPlugin> providers) : IAiRouter
{
    private readonly List<IAiProviderPlugin> _providers = providers.ToList();

    public IAiProviderPlugin Route(AiCapability capability, string tenantId)
    {
        // 사용 가능한 것만 후보로 두고, 실연동 Provider를 우선한다.
        // (키 없는 실연동 플러그인은 IsAvailable=false로 제외 → 시뮬레이션 폴백)
        return _providers
            .Where(p => p.Capabilities.Contains(capability) && p.IsAvailable)
            .OrderByDescending(p => p.IsLive)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"'{capability}' 능력을 가진 AI Provider가 없습니다.");
    }
}
