namespace Tetragon.Plugin.Abstractions;

/// <summary>URL/코드 → 공급처 플러그인 해석 (Factory, 설계서 5.1).</summary>
public interface ISupplierPluginRegistry
{
    ISupplierPlugin? Resolve(Uri url);
    ISupplierPlugin? Resolve(string supplierCode);
    IReadOnlyList<ISupplierPlugin> All { get; }
}

/// <summary>마켓 코드 → 어댑터 해석.</summary>
public interface IMarketplaceAdapterRegistry
{
    IMarketplaceAdapter? Resolve(string marketCode);
    IReadOnlyList<IMarketplaceAdapter> All { get; }
}

/// <summary>공급처 코드 → 카테고리 크롤러 해석. 지원하는 공급처만 등록된다.</summary>
public interface ICategoryCrawlerRegistry
{
    ICategoryCrawler? Resolve(string supplierCode);
    IReadOnlyList<ICategoryCrawler> All { get; }
}
