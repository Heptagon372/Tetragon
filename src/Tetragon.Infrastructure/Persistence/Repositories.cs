using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tetragon.Application.Ports;
using Tetragon.Domain.Attention;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Imports;
using Tetragon.Domain.Listings;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Pricing;
using Tetragon.Domain.Sourcing;
using Tetragon.Domain.Treasury;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Infrastructure.Persistence;

public sealed class ProductRepository(TetragonDbContext db) : IProductRepository
{
    public Task<Product?> FindAsync(Guid id, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(Product product, CancellationToken ct) =>
        await db.Products.AddAsync(product, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<(IReadOnlyList<Product>, int)> SearchAsync(
        string? status, string? keyword, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Products.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProductStatus>(status, true, out var s))
            query = query.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(keyword))
            // Name은 JSON 컬럼 — SQLite LIKE로 검색 (프로덕션: Elasticsearch 조회 모델, ADR-004)
            query = query.Where(p => EF.Property<string>(p, "Name").Contains(keyword));

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task<Dictionary<string, int>> CountByStatusAsync(CancellationToken ct) =>
        await db.Products.GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status.ToString(), x => x.Count, ct);
}

public sealed class ScrapeJobRepository(TetragonDbContext db) : IScrapeJobRepository
{
    public Task<ScrapeJob?> FindAsync(Guid id, CancellationToken ct) =>
        db.ScrapeJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task AddAsync(ScrapeJob job, CancellationToken ct) =>
        await db.ScrapeJobs.AddAsync(job, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<ScrapeJob>> RecentAsync(int limit, CancellationToken ct) =>
        await db.ScrapeJobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToListAsync(ct);
}

public sealed class WalletRepository(TetragonDbContext db) : IWalletRepository
{
    public async Task<Wallet> GetOrCreateAsync(CancellationToken ct)
    {
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.TenantId == Tenant.Default, ct);
        if (wallet is not null) return wallet;

        wallet = Wallet.Create(Tenant.Default);
        await db.Wallets.AddAsync(wallet, ct);
        await db.SaveChangesAsync(ct);
        return wallet;
    }

    /// <summary>잔액과 이력은 한 트랜잭션으로 저장한다 — 어긋나면 잔액을 설명할 수 없다.</summary>
    public async Task SaveAsync(Wallet wallet, WalletTransaction transaction, CancellationToken ct)
    {
        await db.WalletTransactions.AddAsync(transaction, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WalletTransaction>> RecentTransactionsAsync(int limit, CancellationToken ct) =>
        await db.WalletTransactions.OrderByDescending(t => t.OccurredAt).Take(limit).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class ShippingPlaceMappingRepository(TetragonDbContext db) : IShippingPlaceMappingRepository
{
    public Task<ShippingPlaceMapping?> FindAsync(string marketCode, string addressKey, CancellationToken ct) =>
        db.ShippingPlaceMappings
            .FirstOrDefaultAsync(m => m.MarketCode == marketCode && m.AddressKey == addressKey, ct);

    public async Task AddAsync(ShippingPlaceMapping mapping, CancellationToken ct) =>
        await db.ShippingPlaceMappings.AddAsync(mapping, ct);

    public async Task<IReadOnlyList<ShippingPlaceMapping>> AllAsync(CancellationToken ct) =>
        await db.ShippingPlaceMappings.OrderByDescending(m => m.UpdatedAt).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class CategoryCollectJobRepository(TetragonDbContext db) : ICategoryCollectJobRepository
{
    public Task<CategoryCollectJob?> FindAsync(Guid id, CancellationToken ct) =>
        db.CategoryCollectJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task AddAsync(CategoryCollectJob job, CancellationToken ct) =>
        await db.CategoryCollectJobs.AddAsync(job, ct);

    public async Task<IReadOnlyList<CategoryCollectJob>> RecentAsync(int limit, CancellationToken ct) =>
        await db.CategoryCollectJobs.OrderByDescending(j => j.CreatedAt).Take(limit).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>이미 수집한 원본 상품 ID 집합 — raw_products가 수집 이력의 단일 진실 원천.</summary>
    public async Task<HashSet<string>> ExistingSourceIdsAsync(
        string supplierCode, IEnumerable<string> sourceIds, CancellationToken ct)
    {
        var ids = sourceIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var found = await db.RawProducts
            .Where(r => r.SupplierCode == supplierCode && ids.Contains(r.SourceProductId))
            .Select(r => r.SourceProductId)
            .ToListAsync(ct);
        return found.ToHashSet();
    }
}

public sealed class RawProductRepository(TetragonDbContext db) : IRawProductRepository
{
    public async Task AddAsync(RawProductRecord record, CancellationToken ct)
    {
        await db.RawProducts.AddAsync(record, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task<RawProductRecord?> FindByProductAsync(Guid productId, CancellationToken ct) =>
        db.RawProducts.FirstOrDefaultAsync(r => r.ProductId == productId, ct);

    // 같은 상품을 여러 번 수집했다면 가장 최근 것이 정확하다.
    public Task<RawProductRecord?> FindBySourceAsync(
        string supplierCode, string sourceProductId, CancellationToken ct) =>
        db.RawProducts
            .Where(r => r.SupplierCode == supplierCode && r.SourceProductId == sourceProductId)
            .OrderByDescending(r => r.CollectedAt)
            .FirstOrDefaultAsync(ct);
}

public sealed class PricingPolicyRepository(TetragonDbContext db) : IPricingPolicyRepository
{
    public Task<PricingPolicy?> FindAsync(Guid id, CancellationToken ct) =>
        db.PricingPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<PricingPolicy?> FindDefaultAsync(CancellationToken ct) =>
        db.PricingPolicies.FirstOrDefaultAsync(p => p.IsDefault, ct);

    public async Task<IReadOnlyList<PricingPolicy>> AllAsync(CancellationToken ct) =>
        await db.PricingPolicies.OrderByDescending(p => p.IsDefault).ThenBy(p => p.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(PricingPolicy policy, CancellationToken ct) =>
        await db.PricingPolicies.AddAsync(policy, ct);

    public async Task AddCalculationsAsync(IEnumerable<PriceCalculation> calculations, CancellationToken ct) =>
        await db.PriceCalculations.AddRangeAsync(calculations, ct);

    public async Task<IReadOnlyList<PriceCalculation>> CalculationsForProductAsync(Guid productId, CancellationToken ct) =>
        await db.PriceCalculations.Where(c => c.ProductId == productId)
            .OrderByDescending(c => c.CalculatedAt).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class ComplianceRepository(TetragonDbContext db) : IComplianceRepository
{
    public async Task<IReadOnlyList<ComplianceRule>> RulesAsync(CancellationToken ct) =>
        await db.ComplianceRules.ToListAsync(ct);

    public async Task AddRuleAsync(ComplianceRule rule, CancellationToken ct) =>
        await db.ComplianceRules.AddAsync(rule, ct);

    public async Task RemoveRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var rule = await db.ComplianceRules.FindAsync([ruleId], ct);
        if (rule is not null) db.ComplianceRules.Remove(rule);
    }

    public async Task AddResultAsync(ComplianceResult result, CancellationToken ct) =>
        await db.ComplianceResults.AddAsync(result, ct);

    public Task<ComplianceResult?> LatestResultAsync(Guid productId, CancellationToken ct) =>
        db.ComplianceResults.Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.CheckedAt).FirstOrDefaultAsync(ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class ListingRepository(TetragonDbContext db) : IListingRepository
{
    public Task<Listing?> FindAsync(Guid id, CancellationToken ct) =>
        db.Listings.FirstOrDefaultAsync(l => l.Id == id, ct);

    public Task<Listing?> FindByProductAndMarketAsync(Guid productId, string marketCode, CancellationToken ct) =>
        db.Listings.FirstOrDefaultAsync(l => l.ProductId == productId && l.MarketCode == marketCode, ct);

    public Task<Listing?> FindByMarketItemAsync(string marketCode, string marketItemId, CancellationToken ct) =>
        db.Listings.FirstOrDefaultAsync(l => l.MarketCode == marketCode && l.MarketItemId == marketItemId, ct);

    public async Task<IReadOnlyList<Listing>> SearchAsync(string? marketCode, string? status, CancellationToken ct)
    {
        var query = db.Listings.AsQueryable();
        if (!string.IsNullOrWhiteSpace(marketCode))
            query = query.Where(l => l.MarketCode == marketCode);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ListingStatus>(status, true, out var s))
            query = query.Where(l => l.Status == s);
        return await query.OrderByDescending(l => l.UpdatedAt).Take(500).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Listing>> ByProductAsync(Guid productId, CancellationToken ct) =>
        await db.Listings.Where(l => l.ProductId == productId).ToListAsync(ct);

    public async Task AddAsync(Listing listing, CancellationToken ct) =>
        await db.Listings.AddAsync(listing, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class OrderRepository(TetragonDbContext db) : IOrderRepository
{
    public Task<Order?> FindAsync(Guid id, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<Order?> FindByMarketOrderAsync(string marketCode, string marketOrderId, CancellationToken ct) =>
        db.Orders.FirstOrDefaultAsync(o => o.MarketCode == marketCode && o.MarketOrderId == marketOrderId, ct);

    public async Task<IReadOnlyList<Order>> SearchAsync(string? status, CancellationToken ct)
    {
        var query = db.Orders.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, true, out var s))
            query = query.Where(o => o.Status == s);
        return await query.OrderByDescending(o => o.OrderedAt).Take(500).ToListAsync(ct);
    }

    public async Task AddAsync(Order order, CancellationToken ct) =>
        await db.Orders.AddAsync(order, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class CsTicketRepository(TetragonDbContext db) : ICsTicketRepository
{
    public Task<CsTicket?> FindAsync(Guid id, CancellationToken ct) =>
        db.CsTickets.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<bool> ExistsAsync(string marketCode, string marketTicketId, CancellationToken ct) =>
        db.CsTickets.AnyAsync(t => t.MarketCode == marketCode && t.MarketTicketId == marketTicketId, ct);

    public async Task<IReadOnlyList<CsTicket>> SearchAsync(string? status, string? kind, CancellationToken ct)
    {
        var query = db.CsTickets.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<CsStatus>(status, true, out var s))
            query = query.Where(t => t.Status == s);
        if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<CsKind>(kind, true, out var k))
            query = query.Where(t => t.Kind == k);
        return await query.OrderByDescending(t => t.RequestedAt).Take(500).ToListAsync(ct);
    }

    public async Task AddAsync(CsTicket ticket, CancellationToken ct) =>
        await db.CsTickets.AddAsync(ticket, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class AutomationPolicyRepository(TetragonDbContext db) : IAutomationPolicyRepository
{
    public async Task<AutomationPolicy> GetOrCreateAsync(CancellationToken ct)
    {
        var policy = await db.AutomationPolicies.FirstOrDefaultAsync(p => p.TenantId == Tenant.Default, ct);
        if (policy is not null) return policy;

        policy = AutomationPolicy.CreateDefault(Tenant.Default);
        await db.AutomationPolicies.AddAsync(policy, ct);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class AttentionRepository(TetragonDbContext db) : IAttentionRepository
{
    public Task<AttentionItem?> FindAsync(Guid id, CancellationToken ct) =>
        db.AttentionItems.FirstOrDefaultAsync(a => a.Id == id, ct);

    /// <summary>
    /// 살아 있는(open·snoozed) 항목 중에서만 찾는다.
    /// 닫힌 항목까지 보면 "한 번 처리한 사실"이 영원히 다시 안 열린다 —
    /// 역마진이 고쳐졌다가 다시 생기면 다시 알려야 한다.
    /// </summary>
    public Task<AttentionItem?> FindActiveAsync(string kind, string dedupKey, CancellationToken ct) =>
        db.AttentionItems.FirstOrDefaultAsync(
            a => a.TenantId == Tenant.Default && a.Kind == kind && a.DedupKey == dedupKey
                 && (a.State == AttentionState.Open || a.State == AttentionState.Snoozed), ct);

    public async Task<IReadOnlyList<AttentionItem>> FindManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.AttentionItems.Where(a => ids.Contains(a.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<AttentionItem>> QueryAsync(
        AttentionState? state, string? kind, bool dueOnly, int limit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var query = db.AttentionItems.Where(a => a.TenantId == Tenant.Default);

        if (state is not null) query = query.Where(a => a.State == state);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(a => a.Kind == kind);
        // 연기 기한이 지난 항목은 다시 목록에 올라온다 — 스윕 워커 없이 조회 시점에 판정한다
        if (dueOnly)
            query = query.Where(a => a.State == AttentionState.Open
                || (a.State == AttentionState.Snoozed && a.SnoozeUntil != null && a.SnoozeUntil <= now));

        return await query
            .OrderByDescending(a => a.ImpactKrw)
            .ThenByDescending(a => a.LastSeenAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AttentionItem>> ActiveByKindAsync(string kind, CancellationToken ct)
    {
        var items = await db.AttentionItems
            .Where(a => a.TenantId == Tenant.Default && a.Kind == kind
                        && (a.State == AttentionState.Open || a.State == AttentionState.Snoozed))
            .ToListAsync(ct);
        // 한 kind의 살아 있는 항목은 일일 상한에 묶여 있어 많아야 수백 건이다 — 메모리에서 정렬한다
        return [.. items.OrderByDescending(a => a.ImpactKrw)];
    }

    public Task<int> CountOpenedSinceAsync(string kind, DateTimeOffset since, CancellationToken ct) =>
        db.AttentionItems.CountAsync(
            a => a.TenantId == Tenant.Default && a.Kind == kind && a.FirstSeenAt >= since, ct);

    /// <summary>
    /// 집계는 <b>지금 볼 것</b> 기준이다 — 기본 목록과 같은 집합이어야 배지 숫자와 화면이 어긋나지 않는다.
    /// (SQLite는 decimal에 SUM을 못 걸어 메모리에서 합산한다. 살아 있는 항목은 상한에 묶여 있어 양이 작다.)
    /// </summary>
    public async Task<IReadOnlyList<AttentionKindSummary>> SummaryAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await db.AttentionItems
            .Where(a => a.TenantId == Tenant.Default
                        && (a.State == AttentionState.Open
                            || (a.State == AttentionState.Snoozed
                                && a.SnoozeUntil != null && a.SnoozeUntil <= now)))
            .Select(a => new { a.Kind, a.ImpactKrw })
            .ToListAsync(ct);

        return [.. items
            .GroupBy(a => a.Kind)
            .Select(g => new AttentionKindSummary(g.Key, g.Count(), g.Sum(a => a.ImpactKrw)))
            .OrderByDescending(s => s.ImpactKrw)];
    }

    public async Task AddAsync(AttentionItem item, CancellationToken ct) =>
        await db.AttentionItems.AddAsync(item, ct);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class ImportProfileRepository(TetragonDbContext db) : IImportProfileRepository
{
    public Task<ImportProfile?> FindAsync(
        string channel, string purpose, string fingerprint, CancellationToken ct) =>
        db.ImportProfiles.FirstOrDefaultAsync(
            p => p.TenantId == Tenant.Default && p.Channel == channel
                 && p.Purpose == purpose && p.HeaderFingerprint == fingerprint, ct);

    public async Task<IReadOnlyList<ImportProfile>> ListAsync(
        string? channel, string? purpose, CancellationToken ct)
    {
        var query = db.ImportProfiles.Where(p => p.TenantId == Tenant.Default);
        if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(p => p.Channel == channel);
        if (!string.IsNullOrWhiteSpace(purpose)) query = query.Where(p => p.Purpose == purpose);
        return await query.OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
    }

    public async Task AddAsync(ImportProfile profile, CancellationToken ct) =>
        await db.ImportProfiles.AddAsync(profile, ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var profile = await db.ImportProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return false;
        db.ImportProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class CredentialStore(TetragonDbContext db) : ICredentialStore
{
    public async Task<MarketCredential> GetAsync(string scope, CancellationToken ct)
    {
        var entry = await db.Credentials.FindAsync([scope], ct);
        var secrets = entry is null
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(entry.SecretsJson) ?? [];
        return new MarketCredential { Secrets = secrets };
    }

    public async Task SetAsync(string scope, Dictionary<string, string> secrets, CancellationToken ct)
    {
        var entry = await db.Credentials.FindAsync([scope], ct);
        if (entry is null)
        {
            entry = new CredentialEntry { Scope = scope };
            await db.Credentials.AddAsync(entry, ct);
        }

        // 빈 값은 기존 유지, 값이 있으면 갱신 (마스킹된 재저장 방지)
        var existing = JsonSerializer.Deserialize<Dictionary<string, string>>(entry.SecretsJson) ?? [];
        foreach (var (key, value) in secrets)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            existing[key] = value.Trim();
        }
        entry.SecretsJson = JsonSerializer.Serialize(existing);
        entry.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<string, List<string>>> ListScopesAsync(CancellationToken ct)
    {
        var entries = await db.Credentials.ToListAsync(ct);
        return entries.ToDictionary(
            e => e.Scope,
            e => (JsonSerializer.Deserialize<Dictionary<string, string>>(e.SecretsJson) ?? []).Keys.ToList());
    }
}
