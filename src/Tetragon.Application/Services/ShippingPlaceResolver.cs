using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Listings;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// 위탁판매 출고지/반품지 해석기.
///
/// 공급처 주소를 마켓에 등록하고 코드를 받아 캐시한다.
/// 같은 공급처의 상품 수천 개를 등록해도 마켓에는 출고지가 한 번만 만들어진다.
///
/// 실패해도 등록 자체를 막지는 않는다 — 어댑터가 판매자 기본값으로 폴백하도록
/// null을 돌려주고, 사유는 로그와 매핑 레코드에 남긴다.
/// </summary>
public sealed class ShippingPlaceResolver(
    IShippingPlaceMappingRepository mappings,
    ILogger<ShippingPlaceResolver> logger)
{
    public async Task<ShippingPlaceResolution> ResolveAsync(
        IMarketplaceAdapter adapter,
        ConsignmentLogistics logistics,
        string supplierCode,
        MarketCredential credential,
        CancellationToken ct)
    {
        // 마켓이 사전 등록을 요구하지 않으면 어댑터가 주소를 직접 쓴다
        if (adapter is not IShippingPlaceProvider provider) return ShippingPlaceResolution.NotRequired();
        if (!logistics.IsComplete)
        {
            logger.LogDebug("공급처 물류 정보 부족 — 판매자 기본 출고지/반품지를 사용합니다 ({Supplier})", supplierCode);
            return ShippingPlaceResolution.NotRequired();
        }

        // 캐시 키는 실제로 등록할 주소로 만든다.
        // 원본 출고지 주소로 키를 만들면, 출고지가 반품지로 대체된 경우
        // 같은 주소가 서로 다른 키로 두 번 등록된다.
        var addressKey = ShippingPlaceMapping.BuildAddressKey(
            logistics.OutboundPlace.Address, logistics.ReturnAddress);

        var mapping = await mappings.FindAsync(adapter.Code, addressKey, ct);
        if (mapping?.IsResolved == true)
            return ShippingPlaceResolution.Resolved(mapping.OutboundPlaceCode!, mapping.ReturnCenterCode!);

        if (mapping is null)
        {
            mapping = ShippingPlaceMapping.Create(
                Tenant.Default, adapter.Code, supplierCode, addressKey,
                logistics.OutboundPlace.Address, logistics.ReturnAddress);
            await mappings.AddAsync(mapping, ct);
        }

        // 이미 확보한 코드는 다시 만들지 않는다 (한쪽만 성공했던 경우)
        var outbound = mapping.OutboundPlaceCode;
        var returnCode = mapping.ReturnCenterCode;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(outbound))
        {
            var result = await provider.EnsureOutboundPlaceAsync(logistics, credential, ct);
            if (result.Success) outbound = result.Code;
            else errors.Add(result.Error ?? "출고지 등록 실패");
        }

        if (string.IsNullOrWhiteSpace(returnCode))
        {
            var result = await provider.EnsureReturnCenterAsync(logistics, credential, ct);
            if (result.Success) returnCode = result.Code;
            else errors.Add(result.Error ?? "반품지 등록 실패");
        }

        if (errors.Count > 0)
        {
            mapping.Fail(string.Join(" / ", errors));
            logger.LogWarning(
                "{Market} 출고지/반품지 등록 실패 — 판매자 기본값으로 폴백합니다: {Errors}",
                adapter.Code, string.Join(" / ", errors));
        }
        else
        {
            logger.LogInformation(
                "{Market} 위탁 출고지/반품지 확보: {Supplier} → 출고={Outbound}, 반품={Return}",
                adapter.Code, logistics.SupplierName ?? supplierCode, outbound, returnCode);
        }

        mapping.SetCodesPreservingError(outbound, returnCode, errors.Count > 0);
        await mappings.SaveAsync(ct);

        // 코드를 못 얻었으면 그대로 등록하지 않는다.
        // 빈 코드로 보내면 쿠팡이 "반품지센터코드를 입력하세요"라는 원문 오류만 돌려주고,
        // 정작 무엇을 해야 하는지(어느 주소를 WING에 등록해야 하는지)는 사라진다.
        return ShippingPlaceResolution.Of(outbound, returnCode, errors);
    }
}

/// <summary>
/// 출고지/반품지 해석 결과.
///
/// 코드를 못 얻은 경우를 '조용한 null'이 아니라 사유와 함께 돌려준다.
/// 그래야 등록을 막고 사용자에게 할 일을 알려줄 수 있다.
/// </summary>
public sealed record ShippingPlaceResolution
{
    public string? OutboundCode { get; init; }
    public string? ReturnCode { get; init; }
    /// <summary>이 마켓은 사전 등록된 코드를 요구하지 않는다 (엑셀·11번가 등).</summary>
    public bool CodesNotRequired { get; init; }
    /// <summary>해석 실패 사유. 있으면 등록을 진행하지 않는다.</summary>
    public List<string> Problems { get; init; } = [];

    public bool CanProceed => CodesNotRequired || Problems.Count == 0;

    public static ShippingPlaceResolution NotRequired() => new() { CodesNotRequired = true };

    public static ShippingPlaceResolution Resolved(string outbound, string returnCode) =>
        new() { OutboundCode = outbound, ReturnCode = returnCode };

    public static ShippingPlaceResolution Of(string? outbound, string? returnCode, List<string> errors)
    {
        // 반품지는 코드가 없어도 어댑터가 NO_RETURN_CENTERCODE + 주소로 처리하므로 막지 않는다.
        // 출고지는 대체 수단이 없어 없으면 진행할 수 없다.
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(outbound))
        {
            problems.AddRange(errors.Where(e => e.Contains("출고지", StringComparison.Ordinal)));
            if (problems.Count == 0) problems.Add("쿠팡 출고지 코드를 확보하지 못했습니다.");
        }

        return new ShippingPlaceResolution
        {
            OutboundCode = outbound,
            ReturnCode = returnCode,
            Problems = problems,
        };
    }
}
