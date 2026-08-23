using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Tetragon.Application.Services;
using Tetragon.Domain.Pricing;
using Tetragon.SharedKernel;

namespace Tetragon.Infrastructure.Persistence;

/// <summary>첫 실행 시 스키마 생성 + 기본 정책/금지어 시드.</summary>
public static partial class DbSeeder
{
    public static async Task InitializeAsync(TetragonDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await SyncSchemaAsync(db, ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        if (!await db.PricingPolicies.AnyAsync(ct))
        {
            db.PricingPolicies.Add(PricingPolicy.CreateDefaultPreset(Tenant.Default));
            await db.SaveChangesAsync(ct);
        }

        if (!await db.ComplianceRules.AnyAsync(ct))
        {
            db.ComplianceRules.AddRange(ComplianceEngine.DefaultSeedRules());
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// EnsureCreated는 DB가 이미 있으면 아무것도 하지 않으므로,
    /// 모델이 바뀌어도 기존 DB에는 반영되지 않는다.
    /// 누락된 테이블/인덱스를 만들고, 기존 테이블에 새로 생긴 컬럼을 추가한다.
    /// 기존 데이터는 건드리지 않는다.
    ///
    /// 한계: 컬럼 삭제·타입 변경·제약 변경은 처리하지 못한다.
    /// 운영 단계에서는 EF Core 마이그레이션으로 전환해야 한다.
    /// </summary>
    private static async Task SyncSchemaAsync(TetragonDbContext db, CancellationToken ct)
    {
        await CreateMissingTablesAsync(db, ct);
        await AddMissingColumnsAsync(db, ct);
    }

    private static async Task CreateMissingTablesAsync(TetragonDbContext db, CancellationToken ct)
    {
        var script = db.Database.GenerateCreateScript();
        var statements = script
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase));

        foreach (var statement in statements)
        {
            var idempotent = CreateTableRegex().Replace(statement, "CREATE TABLE IF NOT EXISTS ");
            idempotent = CreateIndexRegex().Replace(idempotent, "CREATE $1INDEX IF NOT EXISTS ");
            if (idempotent.Contains("IF NOT EXISTS", StringComparison.OrdinalIgnoreCase) is false) continue;

            try
            {
                await db.Database.ExecuteSqlRawAsync(idempotent, ct);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // 이미 존재하는 스키마 요소 — 무시하고 계속 진행한다
            }
        }
    }

    /// <summary>
    /// 모델이 기대하는 컬럼과 실제 테이블의 컬럼을 비교해 누락분을 ALTER TABLE로 추가한다.
    /// 엔티티에 속성을 추가했을 때 "no such column" 오류가 나는 것을 막는다.
    /// </summary>
    private static async Task AddMissingColumnsAsync(TetragonDbContext db, CancellationToken ct)
    {
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (string.IsNullOrWhiteSpace(tableName)) continue;

            var existing = await GetExistingColumnsAsync(db, tableName, ct);
            if (existing.Count == 0) continue;   // 테이블이 없다 — 위에서 이미 만들어졌어야 한다

            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (string.IsNullOrWhiteSpace(columnName)) continue;

                var columnType = property.GetColumnType() ?? "TEXT";

                if (!existing.Contains(columnName))
                {
                    // SQLite는 NOT NULL 컬럼을 기본값 없이 추가할 수 없다.
                    // 기존 행에 채울 값이 없으므로 nullable로 추가한다.
                    var sql = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType}";
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(sql, ct);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    {
                        // 이미 있거나 추가할 수 없는 컬럼 — 건너뛴다
                        continue;
                    }
                }

                // 컬럼을 nullable로 붙이면 기존 행은 NULL이 된다.
                // 모델이 NOT NULL로 아는 속성이면 EF가 값을 읽는 순간
                // "The data is NULL at ordinal N"으로 터진다 — 그 테이블을 읽는 화면 전체가 죽는다.
                // (실제로 ScrapeJobs.AutoListMarkets를 추가했다가 /jobs와 대시보드가 500을 냈다.)
                // 새로 추가했든 예전에 추가했든, NULL이 남아 있으면 여기서 메운다.
                if (!property.IsNullable)
                    await BackfillNullsAsync(db, tableName, columnName, columnType, ct);
            }
        }
    }

    /// <summary>NOT NULL로 취급되는 컬럼에 남은 NULL을 타입에 맞는 빈 값으로 채운다.</summary>
    private static async Task BackfillNullsAsync(
        TetragonDbContext db, string tableName, string columnName, string columnType, CancellationToken ct)
    {
        // TEXT는 빈 문자열, 숫자는 0. JSON 컨버터들은 빈 문자열을 "값 없음"으로 읽도록 되어 있다.
        var literal = columnType.Contains("INT", StringComparison.OrdinalIgnoreCase)
            || columnType.Contains("REAL", StringComparison.OrdinalIgnoreCase)
            || columnType.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase)
            || columnType.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase)
                ? "0"
                : "''";

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{tableName}\" SET \"{columnName}\" = {literal} WHERE \"{columnName}\" IS NULL", ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // 채울 수 없으면 그대로 둔다 — 읽을 때 드러난다
        }
    }

    private static async Task<HashSet<string>> GetExistingColumnsAsync(
        TetragonDbContext db, string tableName, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        // PRAGMA는 파라미터 바인딩을 지원하지 않는다. 테이블명은 모델에서 온 값이라 안전하다.
        command.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));   // 1 = name

        return columns;
    }

    [GeneratedRegex(@"^CREATE TABLE\s+", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableRegex();
    [GeneratedRegex(@"^CREATE (UNIQUE )?INDEX\s+", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndexRegex();
}
