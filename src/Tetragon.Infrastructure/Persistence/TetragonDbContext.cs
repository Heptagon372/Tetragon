using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Tetragon.Domain.Attention;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Imports;
using Tetragon.Domain.Listings;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Pricing;
using Tetragon.Domain.Sourcing;
using Tetragon.Domain.Treasury;
using Tetragon.SharedKernel;

namespace Tetragon.Infrastructure.Persistence;

/// <summary>
/// SQLite 기반 저장소. 설계서의 PostgreSQL(관계형) + MongoDB(원본 JSON)를
/// 로컬 환경에 맞게 SQLite + JSON 컬럼으로 대체 — Repository 포트 뒤에 격리되어 교체 가능.
/// </summary>
public sealed class TetragonDbContext(DbContextOptions<TetragonDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ScrapeJob> ScrapeJobs => Set<ScrapeJob>();
    public DbSet<CategoryCollectJob> CategoryCollectJobs => Set<CategoryCollectJob>();
    public DbSet<ShippingPlaceMapping> ShippingPlaceMappings => Set<ShippingPlaceMapping>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<RawProductRecord> RawProducts => Set<RawProductRecord>();
    public DbSet<PricingPolicy> PricingPolicies => Set<PricingPolicy>();
    public DbSet<PriceCalculation> PriceCalculations => Set<PriceCalculation>();
    public DbSet<ComplianceRule> ComplianceRules => Set<ComplianceRule>();
    public DbSet<ComplianceResult> ComplianceResults => Set<ComplianceResult>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<CsTicket> CsTickets => Set<CsTicket>();
    public DbSet<AutomationPolicy> AutomationPolicies => Set<AutomationPolicy>();
    public DbSet<CredentialEntry> Credentials => Set<CredentialEntry>();
    public DbSet<AttentionItem> AttentionItems => Set<AttentionItem>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// SQLite는 DateTimeOffset을 ORDER BY에 쓸 수 없다.
    /// 정렬 가능한 이진 표현으로 전역 변환한다 (PostgreSQL 전환 시 이 설정만 제거).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.TenantId);
            b.Property(p => p.Status).HasConversion<string>();
            b.HasIndex(p => p.Status);
            Json(b.Property(p => p.Source));
            Json(b.Property(p => p.Name));
            Json(b.Property(p => p.Description));
            Json(b.Property(p => p.SourceCategory));
            Json(b.Property(p => p.Images));
            Json(b.Property(p => p.OptionGroups));
            Json(b.Property(p => p.Variants));
            Json(b.Property(p => p.BasePrice));
            Json(b.Property(p => p.Attributes));
            b.Ignore(p => p.DomainEvents);
        });

        modelBuilder.Entity<ScrapeJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.Stage).HasConversion<string>();
            b.Property(j => j.State).HasConversion<string>();
            b.HasIndex(j => j.CreatedAt);
            JsonList(b.Property(j => j.AutoListMarkets));
            b.Ignore(j => j.DomainEvents);
            b.Ignore(j => j.HasAutoList);
        });

        modelBuilder.Entity<Wallet>(b =>
        {
            b.HasKey(w => w.Id);
            b.HasIndex(w => w.TenantId).IsUnique();
            b.Ignore(w => w.DomainEvents);
            b.Ignore(w => w.Available);
            b.Ignore(w => w.IsLow);
        });

        modelBuilder.Entity<WalletTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.WalletId);
            b.HasIndex(t => t.OccurredAt);
            b.Property(t => t.Type).HasConversion<string>();
        });

        modelBuilder.Entity<ShippingPlaceMapping>(b =>
        {
            b.HasKey(m => m.Id);
            // (마켓 × 주소) 조합당 하나만 — 같은 공급처 상품이 수천 개여도 출고지는 한 번만 만든다
            b.HasIndex(m => new { m.MarketCode, m.AddressKey }).IsUnique();
            b.Ignore(m => m.IsResolved);
        });

        modelBuilder.Entity<CategoryCollectJob>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.State).HasConversion<string>();
            b.HasIndex(j => j.CreatedAt);
            b.Ignore(j => j.DomainEvents);
            b.Ignore(j => j.ReachedLimit);
        });

        modelBuilder.Entity<RawProductRecord>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.ProductId);
            // 카테고리 수집의 중복 제거에 쓰인다
            b.HasIndex(r => new { r.SupplierCode, r.SourceProductId });
        });

        modelBuilder.Entity<PricingPolicy>(b =>
        {
            b.HasKey(p => p.Id);
            Json(b.Property(p => p.Rules));
            b.Ignore(p => p.DomainEvents);
        });

        modelBuilder.Entity<PriceCalculation>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.ProductId);
            Json(b.Property(c => c.SourceCost));
            Json(b.Property(c => c.FinalPrice));
            Json(b.Property(c => c.Steps));
        });

        modelBuilder.Entity<ComplianceRule>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Severity).HasConversion<string>();
        });

        modelBuilder.Entity<ComplianceResult>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.ProductId);
            b.Property(r => r.Verdict).HasConversion<string>();
            Json(b.Property(r => r.Hits));
        });

        modelBuilder.Entity<Listing>(b =>
        {
            b.HasKey(l => l.Id);
            b.HasIndex(l => new { l.ProductId, l.MarketCode }).IsUnique();
            b.Property(l => l.Status).HasConversion<string>();
            Json(b.Property(l => l.ListedPrice));
            Json(b.Property(l => l.SyncLogs));
            b.Ignore(l => l.DomainEvents);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(o => o.Id);
            b.HasIndex(o => new { o.MarketCode, o.MarketOrderId }).IsUnique();
            b.Property(o => o.Status).HasConversion<string>();
            Json(b.Property(o => o.PaidAmount));
            // 위탁판매 발주 금액 (공급처 원가·실지불액)
            JsonNullable(b.Property(o => o.SupplierUnitCost));
            JsonNullable(b.Property(o => o.SupplierPaidAmount));
            b.HasIndex(o => o.ProductId);
            b.Ignore(o => o.DomainEvents);
            b.Ignore(o => o.EstimatedMargin);
        });

        modelBuilder.Entity<CsTicket>(b =>
        {
            b.HasKey(t => t.Id);
            // 같은 CS 요청을 두 번 담지 않는다
            b.HasIndex(t => new { t.MarketCode, t.MarketTicketId }).IsUnique();
            b.HasIndex(t => t.Status);
            b.Property(t => t.Kind).HasConversion<string>();
            b.Property(t => t.Status).HasConversion<string>();
            b.Ignore(t => t.DomainEvents);
            b.Ignore(t => t.NeedsAttention);
        });

        modelBuilder.Entity<AutomationPolicy>(b =>
        {
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.TenantId).IsUnique();
            b.Ignore(p => p.CanRun);
            // DateOnly는 SQLite에서 기본 지원되지 않아 문자열로 저장한다
            b.Property(p => p.SpentDate).HasConversion(
                v => v.ToString("yyyy-MM-dd"),
                v => DateOnly.ParseExact(v, "yyyy-MM-dd"));
        });

        modelBuilder.Entity<AttentionItem>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Severity).HasConversion<string>();
            b.Property(a => a.State).HasConversion<string>();
            // SQLite는 decimal을 ORDER BY에 쓸 수 없다. 목록의 정렬 기준이 바로 이 값이므로
            // REAL로 저장한다 (08 §1.2 스키마도 impact_krw를 REAL로 둔다).
            b.Property(a => a.ImpactKrw).HasConversion<double>();
            // 목록은 항상 영향 금액 내림차순이다 — 이 정렬이 "줄 세우기"의 전부다
            b.HasIndex(a => new { a.State, a.ImpactKrw });
            b.HasIndex(a => new { a.SubjectType, a.SubjectId });
            // "같은 사실"의 유일성. SQLite 부분 인덱스라 닫힌 항목은 제약을 받지 않는다 —
            // 고쳐졌다 다시 생긴 사실은 다시 열려야 한다
            b.HasIndex(a => new { a.TenantId, a.Kind, a.DedupKey })
                .IsUnique()
                .HasFilter("\"State\" IN ('Open','Snoozed')");
            Json(b.Property(a => a.Detail));
            b.Ignore(a => a.DomainEvents);
            b.Ignore(a => a.IsActive);
        });

        modelBuilder.Entity<ImportProfile>(b =>
        {
            b.HasKey(p => p.Id);
            // (채널 × 목적 × 지문)당 하나. 양식이 바뀌면 지문이 달라져 새 프로파일이 생긴다
            b.HasIndex(p => new { p.Channel, p.Purpose, p.HeaderFingerprint }).IsUnique();
            Json(b.Property(p => p.ColumnMap));
            Json(b.Property(p => p.SampleHeader));
        });

        modelBuilder.Entity<CredentialEntry>(b =>
        {
            b.HasKey(c => c.Scope);
        });
    }

    /// <summary>
    /// 복합 타입을 JSON 컬럼으로 변환 + JSON 스냅샷 비교로 변경 감지.
    ///
    /// 빈 문자열은 "값 없음"으로 읽는다 — 나중에 추가된 컬럼을 스키마 동기화가
    /// 빈 값으로 메우기 때문에, 그걸 역직렬화하려다 터지면 안 된다.
    /// </summary>
    private static void Json<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> property)
    {
        property.HasConversion(
            new ValueConverter<T, string>(
                v => JsonSerializer.Serialize(v, JsonOpts),
                v => string.IsNullOrWhiteSpace(v) ? default! : JsonSerializer.Deserialize<T>(v, JsonOpts)!),
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts),
                v => JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts)!));
    }

    /// <summary>
    /// 문자열 목록용 JSON 컬럼.
    /// 나중에 추가된 컬럼은 기존 행에서 NULL이므로, 역직렬화 전에 빈 목록으로 바꾼다.
    /// (일반 <see cref="Json{T}"/>를 쓰면 그 행을 읽는 순간 터진다.)
    /// </summary>
    private static void JsonList(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<IReadOnlyList<string>> property)
    {
        property.HasConversion(
            new ValueConverter<IReadOnlyList<string>, string>(
                v => JsonSerializer.Serialize(v, JsonOpts),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, JsonOpts) ?? new List<string>()),
            new ValueComparer<IReadOnlyList<string>>(
                (a, b) => a!.SequenceEqual(b!),
                v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                v => v.ToList()));
    }

    /// <summary>
    /// nullable 복합 타입용. null을 문자열 "null"이 아니라 DB NULL로 저장해
    /// "값 없음"과 "빈 값"이 구분되게 한다.
    /// </summary>
    private static void JsonNullable<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T?> property)
        where T : struct
    {
        property.HasConversion(
            new ValueConverter<T?, string?>(
                v => v == null ? null : JsonSerializer.Serialize(v.Value, JsonOpts),
                v => v == null ? null : JsonSerializer.Deserialize<T>(v, JsonOpts)),
            new ValueComparer<T?>(
                (a, b) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(b, JsonOpts),
                v => v == null ? 0 : JsonSerializer.Serialize(v, JsonOpts).GetHashCode(),
                v => v));
    }
}

/// <summary>테넌트별 자격증명 (설계서 5.6 — 프로덕션에선 KMS/Envelope 암호화로 교체).</summary>
public sealed class CredentialEntry
{
    public string Scope { get; set; } = ""; // "market:smartstore", "supplier:taobao", "ai:claude"
    public string TenantId { get; set; } = Tenant.Default;
    public string SecretsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
