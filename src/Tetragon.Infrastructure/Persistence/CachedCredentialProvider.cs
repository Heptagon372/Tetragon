using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Infrastructure.Persistence;

/// <summary>
/// 플러그인용 동기 자격증명 조회 (싱글턴 + 인메모리 캐시).
/// 설정 저장 시 Invalidate()로 갱신한다. 프로덕션에서는 KMS 복호화 계층으로 교체.
/// </summary>
public sealed class CachedCredentialProvider(IServiceScopeFactory scopeFactory) : ICredentialProvider
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _cache = new();

    public string? Get(string scope, string key) => Load(scope).GetValueOrDefault(key);

    public bool HasKey(string scope, string key) =>
        Load(scope).TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    public void Invalidate(string scope) => _cache.TryRemove(scope, out _);

    private Dictionary<string, string> Load(string scope) =>
        _cache.GetOrAdd(scope, s =>
        {
            using var dbScope = scopeFactory.CreateScope();
            var db = dbScope.ServiceProvider.GetRequiredService<TetragonDbContext>();
            var entry = db.Credentials.AsNoTracking().FirstOrDefault(c => c.Scope == s);
            return entry is null
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, string>>(entry.SecretsJson) ?? [];
        });
}
