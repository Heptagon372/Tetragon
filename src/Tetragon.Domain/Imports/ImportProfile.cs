using Tetragon.SharedKernel;

namespace Tetragon.Domain.Imports;

/// <summary>
/// CSV 컬럼 매핑 프로파일 (확장 08 §3.2).
///
/// 마켓은 CSV 컬럼명을 예고 없이 바꾼다. 매핑을 코드에 박으면 그때마다 배포해야 한다.
/// 그래서 <b>헤더 지문(sha256)으로 프로파일을 찾고, 없으면 파싱을 거부한다.</b>
///
/// 추측으로 파싱하지 않는 것이 핵심이다 —
/// 잘못 매핑된 정산 데이터는 틀린 이익을 확신 있게 보여주는데,
/// 그게 데이터가 없는 것보다 나쁘다.
/// </summary>
public sealed class ImportProfile : Entity<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    /// <summary>'coupang' | 'smartstore' | '11st' …</summary>
    public string Channel { get; private set; } = "";
    /// <summary><see cref="ImportPurposes"/> — 'perf' | 'settlement'.</summary>
    public string Purpose { get; private set; } = "";
    /// <summary>정규화된 헤더 배열의 sha256. 컬럼명이 하나라도 바뀌면 달라진다.</summary>
    public string HeaderFingerprint { get; private set; } = "";
    /// <summary>정규화된 원본 컬럼명 → 표준 필드명. 예: {"노출수":"impressions"}.</summary>
    public Dictionary<string, string> ColumnMap { get; private set; } = [];
    /// <summary>지문을 만든 원본 헤더 그대로. 나중에 사람이 "이게 무슨 양식이었지"를 볼 때 쓴다.</summary>
    public List<string> SampleHeader { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    /// <summary>'seed' | 'human'. 사람이 확정한 매핑인지 기본 제공인지 구분한다.</summary>
    public string CreatedBy { get; private set; } = "human";

    private ImportProfile() { }

    public static ImportProfile Create(
        string channel, string purpose, string fingerprint,
        Dictionary<string, string> columnMap, List<string> sampleHeader, string createdBy)
        => new()
        {
            Id = Guid.NewGuid(),
            Channel = channel,
            Purpose = purpose,
            HeaderFingerprint = fingerprint,
            ColumnMap = columnMap,
            SampleHeader = sampleHeader,
            CreatedBy = createdBy,
        };

    public void Remap(Dictionary<string, string> columnMap)
    {
        ColumnMap = columnMap;
        CreatedBy = "human";
    }
}

public static class ImportPurposes
{
    /// <summary>마켓 판매자센터의 상품별 성과 (노출·클릭·주문) → 09 <c>listing_perf</c>.</summary>
    public const string Performance = "perf";
    /// <summary>정산 내역 → 10 <c>settlement_lines</c>.</summary>
    public const string Settlement = "settlement";

    public static bool IsKnown(string purpose) => purpose is Performance or Settlement;
}
