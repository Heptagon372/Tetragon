using Tetragon.Application.Ports;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Services;

/// <summary>
/// 공급처별 배송지 등록 현황 점검.
///
/// 출고지는 API로 자동 생성되지만 <b>반품지는 쿠팡이 API 생성을 막아 두었다</b>
/// (v4는 굿스플로 정보 요구, v5는 500 — 택배사 코드를 바꿔도 동일).
/// 그래서 반품지만은 사람이 WING에 한 번 등록해야 한다.
///
/// 문제는 그걸 <b>상품 등록에 실패해야 알게 된다는 점</b>이다.
/// 이 점검은 등록 전에 "어느 공급처의 어떤 주소가 빠졌는지"를 한 번에 보여줘,
/// 몰아서 등록하고 이후로는 자동 매칭되게 한다.
/// </summary>
public sealed class ShippingPlaceAudit(
    IProductRepository products,
    IMarketplaceAdapterRegistry markets,
    ICredentialStore credentials)
{
    public async Task<ShippingPlaceAuditResult> RunAsync(string marketCode, CancellationToken ct)
    {
        var adapter = markets.Resolve(marketCode);
        if (adapter is not IShippingPlaceProvider provider)
            return new ShippingPlaceAuditResult
            {
                MarketCode = marketCode,
                Supported = false,
                Message = $"{marketCode}는 배송지 코드를 사전 등록할 필요가 없습니다 (주소를 직접 씁니다).",
            };

        var credential = await credentials.GetAsync($"market:{marketCode}", ct);

        // 상품에서 공급처별 물류 정보를 모은다 (같은 공급처는 하나로 묶는다)
        var (items, _) = await products.SearchAsync(null, null, 1, 2000, ct);
        var bySupplier = new Dictionary<string, ConsignmentLogistics>();
        foreach (var product in items)
        {
            var logistics = ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency);
            if (string.IsNullOrWhiteSpace(logistics.SupplierName)) continue;
            if (string.IsNullOrWhiteSpace(logistics.ReturnAddress)) continue;
            bySupplier.TryAdd(logistics.SupplierName, logistics);
        }

        var entries = new List<SupplierShippingStatus>();
        foreach (var (supplier, logistics) in bySupplier.OrderBy(x => x.Key))
        {
            // 실제로 해석해 본다 — 등록돼 있으면 코드가 나온다
            var outbound = await provider.EnsureOutboundPlaceAsync(logistics, credential, ct);
            var returnCenter = await provider.EnsureReturnCenterAsync(logistics, credential, ct);

            entries.Add(new SupplierShippingStatus
            {
                SupplierName = supplier,
                SupplierPhone = logistics.SupplierPhone,
                OutboundAddress = logistics.OutboundAddress,
                ReturnAddress = logistics.ReturnAddress,
                ReturnZipcode = logistics.ReturnZipcode,
                ReturnPhone = logistics.ReturnPhone ?? logistics.SupplierPhone,
                OutboundCode = outbound.Code,
                ReturnCenterCode = returnCenter.Code,
                OutboundReady = outbound.Success,
                ReturnReady = returnCenter.Success,
            });
        }

        return new ShippingPlaceAuditResult
        {
            MarketCode = marketCode,
            Supported = true,
            Suppliers = entries,
            Message = entries.Count == 0
                ? "수집된 상품에서 공급처 반품지를 찾지 못했습니다."
                : null,
        };
    }
}

public sealed record ShippingPlaceAuditResult
{
    public required string MarketCode { get; init; }
    public bool Supported { get; init; }
    public List<SupplierShippingStatus> Suppliers { get; init; } = [];
    public string? Message { get; init; }

    public int MissingReturnCount => Suppliers.Count(s => !s.ReturnReady);
}

public sealed record SupplierShippingStatus
{
    public required string SupplierName { get; init; }
    public string? SupplierPhone { get; init; }
    public string? OutboundAddress { get; init; }
    public string? ReturnAddress { get; init; }
    public string? ReturnZipcode { get; init; }
    public string? ReturnPhone { get; init; }
    public string? OutboundCode { get; init; }
    public string? ReturnCenterCode { get; init; }
    public bool OutboundReady { get; init; }
    public bool ReturnReady { get; init; }

    /// <summary>WING 반품지 등록 폼에 그대로 붙여넣을 수 있는 한 줄.</summary>
    public string RegisterLine =>
        string.Join(" / ", new[]
        {
            string.IsNullOrWhiteSpace(ReturnZipcode) ? ReturnAddress : $"({ReturnZipcode}) {ReturnAddress}",
            ReturnPhone,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ── WING '새 주소지 등록 → 반품지' 폼 필드에 1:1 대응 ──────────────
    // 주소를 기본/상세로 나눠 둬야 폼에 그대로 붙여넣을 수 있다.

    /// <summary>폼의 '주소지명'. 나중에 알아볼 수 있게 공급사명을 쓴다.</summary>
    public string FormPlaceName => $"[위탁] {SupplierName} 반품지";

    /// <summary>폼의 '주소' (도로명/지번 기본 주소).</summary>
    public string FormAddress => SplitReturnAddress().Address;

    /// <summary>폼의 '상세주소'.</summary>
    public string FormAddressDetail => SplitReturnAddress().Detail;

    /// <summary>폼의 '전화번호'.</summary>
    public string FormPhone => ReturnPhone ?? "";

    /// <summary>
    /// 반품 주소를 기본/상세로 나눈다.
    /// 괄호로 끝나는 법정동 표기 뒤를 상세주소로 본다
    /// (예: "경기 광주시 … 15-29 (샤론)" → 기본 "…15-29", 상세 "(샤론)").
    /// </summary>
    private (string Address, string Detail) SplitReturnAddress()
    {
        var full = (ReturnAddress ?? "").Trim();
        if (full.Length == 0) return ("", "");

        var opening = full.LastIndexOf('(');
        if (opening > 10)
            return (full[..opening].Trim(), full[opening..].Trim());

        // 괄호가 없으면 번지 뒤를 상세로 본다
        var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 4) return (full, "");
        return (string.Join(' ', parts[..4]), string.Join(' ', parts[4..]));
    }
}
