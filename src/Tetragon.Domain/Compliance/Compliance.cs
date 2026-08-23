using Tetragon.SharedKernel;

namespace Tetragon.Domain.Compliance;

/// <summary>금지어 Rule Set — 마켓별로 다르게 구성 가능 (설계서 5.5).</summary>
public sealed class ComplianceRule : Entity<Guid>
{
    public string TenantId { get; set; } = Tenant.Default;
    /// <summary>적용 마켓 코드. null = 전 마켓 공통.</summary>
    public string? MarketCode { get; set; }
    public string Keyword { get; set; } = "";
    public ComplianceSeverity Severity { get; set; } = ComplianceSeverity.Block;
    public string? Reason { get; set; }

    public static ComplianceRule Create(string keyword, ComplianceSeverity severity, string? reason = null, string? marketCode = null)
        => new() { Id = Guid.NewGuid(), Keyword = keyword, Severity = severity, Reason = reason, MarketCode = marketCode };
}

public enum ComplianceSeverity { Warn, Block }

/// <summary>검사 결과 (Pass / Warn / Block 3단계, 설계서 5.5).</summary>
public sealed class ComplianceResult
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = Tenant.Default;
    public Guid ProductId { get; set; }
    public ComplianceVerdict Verdict { get; set; }
    public List<ComplianceHit> Hits { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ComplianceVerdict { Pass, Warn, Block }

public sealed record ComplianceHit(string Keyword, string Field, ComplianceSeverity Severity, string? Reason);

/// <summary>
/// Aho-Corasick 오토마타 — 상품명/상세/옵션 일괄 금지어 검사 (설계서 5.5, 수백만 건 대비 O(n)).
/// </summary>
public sealed class AhoCorasickMatcher
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children = [];
        public Node? Fail;
        public List<string> Outputs = [];
    }

    private readonly Node _root = new();
    private bool _built;

    public void AddKeyword(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;
        var node = _root;
        foreach (var ch in keyword.ToLowerInvariant())
        {
            if (!node.Children.TryGetValue(ch, out var next))
                node.Children[ch] = next = new Node();
            node = next;
        }
        node.Outputs.Add(keyword);
        _built = false;
    }

    public void Build()
    {
        var queue = new Queue<Node>();
        foreach (var child in _root.Children.Values)
        {
            child.Fail = _root;
            queue.Enqueue(child);
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (ch, child) in current.Children)
            {
                var fail = current.Fail;
                while (fail is not null && !fail.Children.ContainsKey(ch)) fail = fail.Fail;
                child.Fail = fail?.Children.GetValueOrDefault(ch) ?? _root;
                child.Outputs.AddRange(child.Fail.Outputs);
                queue.Enqueue(child);
            }
        }
        _built = true;
    }

    /// <summary>텍스트에서 매칭된 키워드 집합 반환.</summary>
    public IReadOnlySet<string> Match(string text)
    {
        if (!_built) Build();
        var found = new HashSet<string>();
        var node = _root;
        foreach (var ch in text.ToLowerInvariant())
        {
            while (node != _root && !node.Children.ContainsKey(ch)) node = node.Fail!;
            node = node.Children.GetValueOrDefault(ch) ?? _root;
            foreach (var output in node.Outputs) found.Add(output);
        }
        return found;
    }
}
