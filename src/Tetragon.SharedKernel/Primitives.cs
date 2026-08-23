namespace Tetragon.SharedKernel;

/// <summary>모든 엔티티의 베이스. Id 동등성.</summary>
public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected set; } = default!;

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other && GetType() == other.GetType() && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}

/// <summary>애그리거트 루트. 도메인 이벤트를 수집했다가 커밋 시점에 발행한다.</summary>
public abstract class AggregateRoot<TId> : Entity<TId> where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent @event) => _domainEvents.Add(@event);
    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }
}

/// <summary>도메인 이벤트 마커.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 컨텍스트 간 통합 이벤트 Envelope (설계서 6.2).
/// CorrelationId = 파이프라인(Job) 추적, CausationId = 직전 이벤트.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string TenantId { get; init; } = Tenant.Default;
    public Guid CorrelationId { get; init; }
    public Guid? CausationId { get; init; }
    public int Version { get; init; } = 1;
}

/// <summary>멀티테넌시 자리표시자 — 단일 테넌트로 시작, Row 격리 구조는 유지.</summary>
public static class Tenant
{
    public const string Default = "default";
}
