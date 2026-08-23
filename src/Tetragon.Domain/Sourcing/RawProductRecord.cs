using Tetragon.SharedKernel;

namespace Tetragon.Domain.Sourcing;

/// <summary>
/// 스크래핑 원본 보존 레코드 (설계서 §4: MongoDB raw_products 대응 — 여기선 SQLite JSON 컬럼).
/// 재처리·디버깅·감사 용도로 원본 JSON을 그대로 보존한다.
/// </summary>
public sealed class RawProductRecord : Entity<Guid>
{
    public string TenantId { get; set; } = Tenant.Default;
    public string SupplierCode { get; set; } = "";
    public string SourceProductId { get; set; } = "";
    public string Url { get; set; } = "";
    public Guid? ProductId { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    public static RawProductRecord Create(string tenantId, string supplierCode, string sourceProductId, string url, string rawJson, Guid? productId)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SupplierCode = supplierCode,
            SourceProductId = sourceProductId,
            Url = url,
            RawJson = rawJson,
            ProductId = productId,
        };
}
