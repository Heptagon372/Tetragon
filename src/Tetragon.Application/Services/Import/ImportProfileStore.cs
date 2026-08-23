using Tetragon.Application.Ports;
using Tetragon.Domain.Imports;

namespace Tetragon.Application.Services.Import;

/// <summary>
/// 헤더 지문 → 컬럼 매핑 프로파일 (확장 08 §3.2).
///
/// 마켓이 컬럼명을 바꾸면 지문이 달라지고, 지문이 달라지면 임포트가 <b>거부된다.</b>
/// 그게 정상 동작이다 — 바뀐 양식을 옛 매핑으로 읽으면 틀린 숫자가 조용히 들어온다.
/// 사람이 새 매핑을 확정하면 프로파일이 하나 더 생기고, 옛 프로파일은 그대로 남는다
/// (과거 파일을 다시 올릴 수 있어야 대사 분쟁에 답할 수 있다).
/// </summary>
public sealed class ImportProfileStore(IImportProfileRepository repo)
{
    public Task<ImportProfile?> FindAsync(
        string channel, string purpose, string fingerprint, CancellationToken ct) =>
        repo.FindAsync(channel, purpose, fingerprint, ct);

    public Task<IReadOnlyList<ImportProfile>> ListAsync(
        string? channel, string? purpose, CancellationToken ct) =>
        repo.ListAsync(channel, purpose, ct);

    /// <summary>
    /// 사람이 확정한 매핑을 저장한다.
    /// 필수 필드가 하나라도 안 붙어 있으면 거절한다 — 반쯤 매핑된 프로파일은
    /// 임포트 시점에야 터지고, 그때는 이미 파일을 올린 뒤다.
    /// </summary>
    public async Task<ImportProfile> SaveAsync(
        string channel, string purpose, string fingerprint,
        Dictionary<string, string> columnMap, List<string> sampleHeader, CancellationToken ct)
    {
        if (!ImportPurposes.IsKnown(purpose))
            throw new ArgumentException($"알 수 없는 임포트 목적: {purpose}", nameof(purpose));

        var fields = ImportFieldCatalog.For(purpose);
        var known = fields.Select(f => f.Name).ToHashSet();

        var unknown = columnMap.Values.Where(v => !known.Contains(v)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"알 수 없는 표준 필드: {string.Join(", ", unknown)}");

        var duplicated = columnMap.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicated.Count > 0)
            throw new ArgumentException($"한 필드에 컬럼이 둘 이상 붙었습니다: {string.Join(", ", duplicated)}");

        var missing = fields.Where(f => f.Required && !columnMap.ContainsValue(f.Name)).Select(f => f.Label).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"필수 컬럼을 지정하세요: {string.Join(", ", missing)}");

        var existing = await repo.FindAsync(channel, purpose, fingerprint, ct);
        if (existing is not null)
        {
            existing.Remap(columnMap);
            await repo.SaveAsync(ct);
            return existing;
        }

        var profile = ImportProfile.Create(channel, purpose, fingerprint, columnMap, sampleHeader, "human");
        await repo.AddAsync(profile, ct);
        await repo.SaveAsync(ct);
        return profile;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => repo.DeleteAsync(id, ct);
}
