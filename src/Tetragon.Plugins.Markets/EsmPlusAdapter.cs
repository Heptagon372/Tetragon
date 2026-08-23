using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 옥션 / G마켓 (ESM Plus) 어댑터.
///
/// 중요: ESM Plus는 일반 판매자에게 상품등록 REST API를 공개하지 않는다.
/// 공식 경로는 두 가지뿐이다.
///   1) ESM Plus 웹에서 대량등록 엑셀 업로드  ← 대부분의 셀러가 쓰는 방법
///   2) ESM Plus 제휴 솔루션사 API (별도 계약·승인 필요)
///
/// 따라서 이 어댑터는 API 호출을 흉내내지 않고, 엑셀 내보내기로 안내한다.
/// 엑셀은 EsmPlusExcelTemplate이 ESM Plus 대량등록 양식에 맞춰 생성한다.
/// 계약된 솔루션사 API가 있다면 이 어댑터를 교체하면 된다.
/// </summary>
public abstract class EsmPlusAdapterBase : IMarketplaceAdapter
{
    public abstract string Code { get; }
    public abstract string DisplayName { get; }
    public string Version => "1.0.0";
    /// <summary>실연동이 아니라 엑셀 경로임을 명확히 한다.</summary>
    public bool IsLive => false;
    public bool IsAvailable => true;

    private const string Guidance =
        "옥션·G마켓은 ESM Plus 대량등록 엑셀로 등록합니다. " +
        "상품 목록에서 '엑셀 내보내기 → ESM Plus'를 선택해 파일을 받은 뒤 " +
        "ESM Plus(esmplus.com) → 상품등록 → 대량등록에서 업로드하세요.";

    public Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential cred, CancellationToken ct) =>
        Task.FromResult(ListingResult.Fail("USE_EXCEL", Guidance));

    public Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential cred, CancellationToken ct) =>
        Task.FromResult(ListingResult.Fail("USE_EXCEL", Guidance));

    public Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential cred, CancellationToken ct) =>
        Task.FromResult(ListingResult.Fail("USE_EXCEL",
            "ESM Plus에서 직접 상품을 삭제하세요."));

    public Task<ListingResult> UpdatePriceStockAsync(string marketItemId, Money price, int stock, MarketCredential cred, CancellationToken ct) =>
        Task.FromResult(ListingResult.Fail("USE_EXCEL",
            "가격·재고 수정은 ESM Plus 대량수정 엑셀을 사용하세요."));

    public Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential cred, CancellationToken ct) =>
        throw new NotSupportedException(
            "ESM Plus 주문 수집은 제휴 솔루션사 API가 필요합니다. " +
            "ESM Plus에서 주문 엑셀을 내려받아 확인하세요.");
}

/// <summary>옥션 (ESM Plus).</summary>
public sealed class AuctionAdapter : EsmPlusAdapterBase
{
    public override string Code => "auction";
    public override string DisplayName => "옥션 (ESM Plus)";
}

/// <summary>G마켓 (ESM Plus).</summary>
public sealed class GmarketAdapter : EsmPlusAdapterBase
{
    public override string Code => "gmarket";
    public override string DisplayName => "G마켓 (ESM Plus)";
}
