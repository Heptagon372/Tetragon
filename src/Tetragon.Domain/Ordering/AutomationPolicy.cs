using Tetragon.SharedKernel;

namespace Tetragon.Domain.Ordering;

/// <summary>
/// 주문 자동 이행 정책.
///
/// 이 기능은 <b>사람 확인 없이 실제로 돈을 쓴다</b>. 그래서 기본값은 전부 안전한 쪽이다:
/// 자동화는 꺼져 있고, 켜도 처음에는 시험 모드로 돈이 나가지 않는다.
///
/// 한도를 두는 이유는 잘못될 때 손실을 끊기 위해서다. 가격 파싱이 어긋나거나
/// 공급처가 값을 잘못 주면 자동화는 그걸 의심하지 않고 계속 산다.
///   - <see cref="MaxOrderAmount"/> : 한 건이 이보다 비싸면 사람에게 넘긴다
///   - <see cref="DailyLimit"/>     : 하루 총 지출 상한
///   - <see cref="ConsecutiveFailureLimit"/> : 연달아 실패하면 스스로 멈춘다
///     (같은 원인으로 100번 실패하는 것을 막는다)
/// </summary>
public sealed class AutomationPolicy : Entity<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;

    /// <summary>자동 이행 전체 스위치. 꺼져 있으면 아무것도 자동으로 하지 않는다.</summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// 시험 모드. 켜져 있으면 발주 직전까지 전부 실행하되 <b>실제 결제는 하지 않는다</b>.
    /// 무엇이 어떻게 처리될지 먼저 확인하고 실전으로 넘어가기 위한 단계다.
    /// </summary>
    public bool DryRun { get; private set; } = true;

    /// <summary>주문 수집만 자동으로 하고 발주는 사람이 누르게 할 수도 있다.</summary>
    public bool AutoCollectOrders { get; private set; } = true;
    public bool AutoPurchase { get; private set; } = true;
    public bool AutoUploadTracking { get; private set; } = true;
    public bool AutoCollectCs { get; private set; } = true;

    /// <summary>한 건 발주 상한. 이보다 비싼 주문은 자동으로 사지 않는다.</summary>
    public decimal MaxOrderAmount { get; private set; } = 50_000m;
    /// <summary>하루 자동 발주 총액 상한.</summary>
    public decimal DailyLimit { get; private set; } = 300_000m;
    /// <summary>연속 실패 허용 횟수. 넘으면 자동화를 스스로 끈다.</summary>
    public int ConsecutiveFailureLimit { get; private set; } = 3;

    /// <summary>실행 주기(분).</summary>
    public int IntervalMinutes { get; private set; } = 10;

    // ── 실행 상태 ────────────────────────────────────────────────────────
    public int ConsecutiveFailures { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public string? LastRunSummary { get; private set; }
    /// <summary>자동으로 멈춘 이유. 값이 있으면 사람이 확인하고 다시 켜야 한다.</summary>
    public string? HaltedReason { get; private set; }

    /// <summary>오늘 자동 발주로 쓴 금액 (날짜가 바뀌면 초기화).</summary>
    public decimal SpentToday { get; private set; }
    public DateOnly SpentDate { get; private set; } = DateOnly.FromDateTime(DateTime.Today);

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private AutomationPolicy() { }

    public static AutomationPolicy CreateDefault(string tenantId) =>
        new() { Id = Guid.NewGuid(), TenantId = tenantId };

    /// <summary>지금 자동으로 무언가 해도 되는 상태인가.</summary>
    public bool CanRun => Enabled && HaltedReason is null;

    /// <summary>
    /// 이 금액을 자동으로 써도 되는지 판단한다.
    /// 안 되는 이유를 문자열로 돌려주고, 괜찮으면 null.
    /// </summary>
    public string? RejectPurchase(decimal amount, DateOnly today)
    {
        if (!AutoPurchase) return "자동 발주가 꺼져 있습니다.";
        if (amount > MaxOrderAmount)
            return $"1건 한도({MaxOrderAmount:N0}원)를 넘습니다 ({amount:N0}원) — 사람이 직접 확인하세요.";

        var spent = SpentDate == today ? SpentToday : 0m;
        if (spent + amount > DailyLimit)
            return $"하루 한도({DailyLimit:N0}원)를 넘습니다 (오늘 {spent:N0}원 사용, 이번 건 {amount:N0}원).";

        return null;
    }

    public void RecordSpend(decimal amount, DateOnly today)
    {
        if (SpentDate != today) { SpentDate = today; SpentToday = 0m; }
        SpentToday += amount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordSuccess(string summary)
    {
        ConsecutiveFailures = 0;
        LastRunAt = DateTimeOffset.UtcNow;
        LastRunSummary = Truncate(summary);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>실패 누적. 한도를 넘으면 스스로 멈춘다 — 같은 오류를 반복하지 않기 위해서다.</summary>
    public void RecordFailure(string summary)
    {
        ConsecutiveFailures++;
        LastRunAt = DateTimeOffset.UtcNow;
        LastRunSummary = Truncate(summary);
        if (ConsecutiveFailures >= ConsecutiveFailureLimit)
            HaltedReason =
                $"연속 {ConsecutiveFailures}회 실패로 자동화를 멈췄습니다. 마지막 오류: {Truncate(summary, 200)}";
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>사람이 원인을 확인하고 다시 켠다.</summary>
    public void Resume()
    {
        HaltedReason = null;
        ConsecutiveFailures = 0;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Configure(
        bool? enabled = null, bool? dryRun = null,
        bool? autoCollectOrders = null, bool? autoPurchase = null,
        bool? autoUploadTracking = null, bool? autoCollectCs = null,
        decimal? maxOrderAmount = null, decimal? dailyLimit = null,
        int? consecutiveFailureLimit = null, int? intervalMinutes = null)
    {
        if (enabled is { } e)
        {
            Enabled = e;
            // 다시 켤 때는 멈춤 사유를 함께 푼다 — 켰는데 안 도는 상황을 막는다
            if (e) { HaltedReason = null; ConsecutiveFailures = 0; }
        }
        if (dryRun is { } d) DryRun = d;
        if (autoCollectOrders is { } ac) AutoCollectOrders = ac;
        if (autoPurchase is { } ap) AutoPurchase = ap;
        if (autoUploadTracking is { } at) AutoUploadTracking = at;
        if (autoCollectCs is { } acs) AutoCollectCs = acs;
        if (maxOrderAmount is { } m && m > 0) MaxOrderAmount = m;
        if (dailyLimit is { } dl && dl > 0) DailyLimit = dl;
        if (consecutiveFailureLimit is { } c && c > 0) ConsecutiveFailureLimit = c;
        if (intervalMinutes is { } i && i > 0) IntervalMinutes = Math.Clamp(i, 1, 24 * 60);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string Truncate(string s, int max = 1000) => s.Length <= max ? s : s[..max];
}
