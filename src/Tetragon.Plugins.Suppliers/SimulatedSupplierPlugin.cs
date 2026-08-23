using System.Text.Json;
using System.Text.RegularExpressions;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 시뮬레이션 공급처 — API 키/쿠키 없이 전체 파이프라인을 검증하기 위한 플러그인.
/// mock.shop / demo.tetragon 도메인 URL만 처리한다 (실제 URL을 가짜 데이터로 덮지 않도록).
/// </summary>
public sealed partial class SimulatedSupplierPlugin : ISupplierPlugin, ICategoryCrawler
{
    [GeneratedRegex(@"/item/(\d+)\.html")]
    private static partial Regex UrlItemIdRegex();

    public string Code => "simulated";
    public string SupplierCode => Code;
    public string DisplayName => "시뮬레이션 공급처";
    public string Version => "1.1.0";
    public bool IsLive => false;

    private static readonly string[] Titles =
    [
        "2024新款韩版宽松显瘦连衣裙夏季气质长裙", "北欧风格简约陶瓷马克杯创意咖啡杯",
        "无线蓝牙耳机运动跑步双耳入耳式降噪", "夏季新款男士休闲短袖T恤纯棉圆领",
        "便携式折叠收纳箱大容量整理箱家用", "儿童益智积木玩具拼装大颗粒早教",
        "韩国ins风透明手机壳硅胶防摔保护套", "厨房多功能切菜神器不锈钢刨丝器",
    ];

    private static readonly (string Group, string[] Values)[] OptionPresets =
    [
        ("颜色", ["白色", "黑色", "粉色", "蓝色"]),
        ("尺码", ["S", "M", "L", "XL"]),
    ];

    public bool CanHandle(Uri url) =>
        url.Host.Contains("mock", StringComparison.OrdinalIgnoreCase)
        || url.Host.Contains("demo", StringComparison.OrdinalIgnoreCase)
        || url.Host.Contains("example", StringComparison.OrdinalIgnoreCase);

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(400, 1200), ct); // 네트워크 지연 시뮬레이션

        // 상품 ID는 URL에서 읽는다. 카테고리 목록이 URL에 심어둔 ID와 반드시 일치해야
        // 중복 제거(raw_products의 공급처+원본ID)가 성립한다 — 실제 플러그인과 같은 규약.
        var productId = UrlItemIdRegex().Match(productUrl.AbsolutePath) is { Success: true } m
            ? m.Groups[1].Value
            : (Math.Abs(productUrl.ToString().GetHashCode()) % 900000000 + 100000000).ToString();

        // 나머지 데이터는 상품 ID 기반 결정적 생성 (같은 상품 → 같은 내용)
        var seed = Math.Abs(productId.GetHashCode());
        var random = new Random(seed);
        var title = Titles[seed % Titles.Length];
        var basePrice = random.Next(15, 300) + 0.9m;

        var groups = new List<RawOptionGroup>();
        var groupCount = random.Next(1, 3);
        for (var g = 0; g < groupCount; g++)
        {
            var (groupName, values) = OptionPresets[g];
            var count = random.Next(2, values.Length + 1);
            groups.Add(new RawOptionGroup(groupName,
                values.Take(count).Select((v, i) => new RawOptionValue($"{g}00{i}", v, null)).ToList()));
        }

        // 옵션 조합 → SKU
        var variants = new List<RawVariant>();
        var combos = groups.Aggregate(new List<Dictionary<string, string>> { new() }, (acc, group) =>
            acc.SelectMany(combo => group.Values.Select(v =>
                new Dictionary<string, string>(combo) { [group.Name] = v.Id })).ToList());
        var index = 0;
        foreach (var combo in combos)
        {
            variants.Add(new RawVariant
            {
                SourceSkuId = $"{productId}-{index++}",
                OptionValueIds = combo,
                Price = basePrice + random.Next(0, 5) * 2,
                Stock = random.Next(0, 200),
            });
        }

        var imageCount = random.Next(3, 6);
        var images = Enumerable.Range(0, imageCount)
            .Select(i => $"https://picsum.photos/seed/{productId}-{i}/600/600")
            .ToList();

        var raw = new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = productId,
            Url = productUrl.ToString(),
            Title = title,
            TitleLocale = "zh-CN",
            Description = $"{title}。高品质材料，舒适耐用。工厂直销，支持批发。",
            ImageUrls = images,
            CategoryPath = ["女装", "连衣裙"],
            OptionGroups = groups,
            Variants = variants,
            Currency = "CNY",
            BasePrice = basePrice,
            Attributes = new Dictionary<string, string> { ["브랜드"] = "노브랜드", ["원산지"] = "중국" },
        };
        return raw with { RawJson = JsonSerializer.Serialize(raw) };
    }

    public async Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        // 10% 확률로 품절 시뮬레이션
        var isAvailable = Random.Shared.Next(10) > 0;
        return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = isAvailable };
    }

    // ── 카테고리 크롤링 (API 키 없이 카테고리 수집을 검증하기 위함) ──────

    private static readonly SupplierCategory[] Categories =
    [
        new("100", "여성의류", null, true, "여성의류"),
        new("100100", "원피스", "100", false, "여성의류 > 원피스"),
        new("100200", "티셔츠", "100", false, "여성의류 > 티셔츠"),
        new("200", "생활용품", null, true, "생활용품"),
        new("200100", "주방용품", "200", false, "생활용품 > 주방용품"),
        new("200200", "수납정리", "200", false, "생활용품 > 수납정리"),
        new("300", "디지털/가전", null, true, "디지털/가전"),
        new("300100", "이어폰/헤드폰", "300", false, "디지털/가전 > 이어폰/헤드폰"),
        new("300200", "폰악세서리", "300", false, "디지털/가전 > 폰악세서리"),
    ];

    public Task<IReadOnlyList<SupplierCategory>> GetCategoriesAsync(string? parentCode, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SupplierCategory>>(
            Categories.Where(c => c.ParentCode == parentCode).ToList());

    public async Task<CategoryCrawlPage> CrawlAsync(CategoryCrawlRequest request, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 700), ct);

        // 카테고리/키워드마다 결정적인 상품 목록을 만든다 (같은 조건 → 같은 결과)
        var seed = $"{request.CategoryCode}|{request.Keyword}".GetHashCode();
        var random = new Random(seed + request.Page);
        const int totalAvailable = 137;   // 이 카테고리의 전체 상품 수 (가짜)

        var startIndex = (request.Page - 1) * request.PageSize;
        var count = Math.Clamp(totalAvailable - startIndex, 0, request.PageSize);
        if (count <= 0)
            return new CategoryCrawlPage { Items = [], Page = request.Page, TotalCount = totalAvailable, HasMore = false };

        var items = Enumerable.Range(0, count).Select(i =>
        {
            var itemId = 500_000_000 + Math.Abs(seed % 1_000_000) + startIndex + i;
            return new CrawledProductRef
            {
                SourceProductId = itemId.ToString(),
                // 개별 수집은 CollectAsync가 처리하므로 이 플러그인이 다룰 수 있는 URL을 준다
                Url = $"https://mock.shop/item/{itemId}.html",
                Title = Titles[(startIndex + i) % Titles.Length],
                Price = random.Next(15, 300) + 0.9m,
                Currency = "CNY",
                ThumbnailUrl = $"https://picsum.photos/seed/{itemId}/200/200",
            };
        }).ToList();

        return new CategoryCrawlPage
        {
            Items = items,
            Page = request.Page,
            TotalCount = totalAvailable,
            HasMore = startIndex + count < totalAvailable,
        };
    }
}
