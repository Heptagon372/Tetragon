using Tetragon.SharedKernel;

namespace Tetragon.Domain.Attention;

/// <summary>
/// 운영자가 봐야 하는 사실 한 건 (v2 확장 08 §1).
///
/// 유한한 자원은 등록 슬롯이 아니라 <b>운영자의 주의</b>다.
/// 신규 기능(성과·정산·소싱·콘텐츠)이 각자 알림을 보내면 1인 운영자는
/// 알림 피로로 전부 무시하게 된다. 그래서 주의를 여기 한 곳에 모으고 줄을 세운다.
///
/// 설계의 전부는 세 줄이다:
///   ① <see cref="ImpactKrw"/> 없이는 만들 수 없다 — 근거를 못 적는 항목은 사람 시간을 쓸 가치가 없다
///   ② 하루 새로 열리는 항목에 상한이 있다 — 넘으면 버리지 않고 집계 항목 하나로 접는다
///   ③ <see cref="DedupKey"/>는 "같은 사실"의 정의다 — 매일 재검출돼도 항목은 하나다
///
/// 사실이 사라지면 <see cref="ResolveAuto"/>로 저절로 닫힌다.
/// 사람이 손대지 않아도 닫히는 항목이 있어야 목록이 신뢰를 얻는다.
/// </summary>
public sealed class AttentionItem : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;

    /// <summary>무슨 종류의 사실인가 (<see cref="AttentionKinds"/>).</summary>
    public string Kind { get; private set; } = "";

    /// <summary>이 사실이 걸려 있는 대상 — 'product'|'listing'|'order'|'candidate'|'settlement_line'|'work_item'|'system'.</summary>
    public string SubjectType { get; private set; } = "";
    public string SubjectId { get; private set; } = "";

    public AttentionSeverity Severity { get; private set; } = AttentionSeverity.Warn;

    /// <summary>이걸 처리하면 얼마가 걸려 있나 (월 환산, 원). 목록 정렬의 기준.</summary>
    public decimal ImpactKrw { get; private set; }

    /// <summary>그 숫자의 근거 문장. 사람이 읽는다 — "최근 30일 주문 4건 × 개당 손실 1,240원 → 월 4,960원".</summary>
    public string ImpactBasis { get; private set; } = "";

    public string Title { get; private set; } = "";
    public Dictionary<string, string> Detail { get; private set; } = [];

    /// <summary>제안 조치 (<see cref="AttentionActions"/>). 화면이 버튼을 고를 때 쓴다.</summary>
    public string? SuggestedAction { get; private set; }

    public AttentionState State { get; private set; } = AttentionState.Open;
    public DateTimeOffset? SnoozeUntil { get; private set; }

    /// <summary>"같은 사실"의 정의. (테넌트 × kind × dedupKey)가 살아 있는 항목 중 유일하다.</summary>
    public string DedupKey { get; private set; } = "";

    public DateTimeOffset FirstSeenAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; private set; } = DateTimeOffset.UtcNow;
    public int SeenCount { get; private set; } = 1;

    public DateTimeOffset? ResolvedAt { get; private set; }
    /// <summary>'human' | 'auto'. 저절로 닫힌 것과 사람이 닫은 것을 구분한다.</summary>
    public string? ResolvedBy { get; private set; }
    public string? ResolutionNote { get; private set; }

    private AttentionItem() { }

    public static AttentionItem Open(
        string tenantId, string kind, string subjectType, string subjectId, string dedupKey,
        string title, decimal impactKrw, string impactBasis,
        AttentionSeverity severity, string? suggestedAction, Dictionary<string, string> detail)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = kind,
            SubjectType = subjectType,
            SubjectId = subjectId,
            DedupKey = dedupKey,
            Title = title,
            ImpactKrw = impactKrw,
            ImpactBasis = impactBasis,
            Severity = severity,
            SuggestedAction = suggestedAction,
            Detail = detail,
        };

    /// <summary>
    /// 같은 사실이 다시 검출됐다. 항목을 새로 만들지 않고 흔적만 남긴다.
    /// 영향 금액은 최신값으로 갱신한다 — 손실은 시간이 갈수록 커지므로 옛날 숫자로 줄을 세우면 안 된다.
    /// </summary>
    public void Touch(decimal impactKrw, string impactBasis, string title, AttentionSeverity severity)
    {
        LastSeenAt = DateTimeOffset.UtcNow;
        SeenCount++;
        ImpactKrw = impactKrw;
        ImpactBasis = impactBasis;
        Title = title;
        Severity = severity;
    }

    /// <summary>지금은 못 본다. 기한이 지나면 다시 목록에 올라온다.</summary>
    public void Snooze(int days)
    {
        if (days < 1) throw new ArgumentException("연기는 최소 1일입니다.", nameof(days));
        State = AttentionState.Snoozed;
        SnoozeUntil = DateTimeOffset.UtcNow.AddDays(days);
    }

    /// <summary>사람이 처리했다.</summary>
    public void Done(string? note) => Close(AttentionState.Done, "human", note);

    /// <summary>사람이 "볼 필요 없다"고 판단했다.</summary>
    public void Dismiss(string? note) => Close(AttentionState.Dismissed, "human", note);

    /// <summary>사실이 사라졌다 — 사람이 손대지 않아도 닫는다.</summary>
    public void ResolveAuto(string note) => Close(AttentionState.Done, "auto", note);

    /// <summary>일일 상한을 넘어 집계 항목으로 접혔다. 버린 것이 아니라 묶은 것이다.</summary>
    public void FoldInto(string foldTitle) =>
        Close(AttentionState.Dismissed, "auto", $"집계 항목으로 접힘 — {foldTitle}");

    private void Close(AttentionState state, string by, string? note)
    {
        State = state;
        ResolvedAt = DateTimeOffset.UtcNow;
        ResolvedBy = by;
        ResolutionNote = note;
        SnoozeUntil = null;
    }

    /// <summary>아직 살아 있는가 (dedup 유일성이 걸리는 범위).</summary>
    public bool IsActive => State is AttentionState.Open or AttentionState.Snoozed;

    /// <summary>지금 목록에 보여야 하는가. 연기 기한이 지난 항목은 다시 올라온다.</summary>
    public bool IsDue(DateTimeOffset now) =>
        State == AttentionState.Open || (State == AttentionState.Snoozed && SnoozeUntil <= now);
}

public enum AttentionSeverity { Info, Warn, Critical }

public enum AttentionState { Open, Snoozed, Done, Dismissed }

/// <summary>
/// kind 목록 (08 §1.4). 문자열을 직접 쓰지 않는 이유는 오타가 dedup을 조용히 깨기 때문이다 —
/// kind가 하나 틀리면 같은 사실이 두 항목으로 열리고, 자동 닫기는 영원히 실패한다.
/// </summary>
public static class AttentionKinds
{
    /// <summary>역마진 — 팔수록 손해인 리스팅 (09 리프라이싱 평가).</summary>
    public const string MarginLoss = "margin.loss";
    /// <summary>기준 마진 미달 (09).</summary>
    public const string MarginThin = "margin.thin";
    /// <summary>아이템위너 상실 (09 위너 감시).</summary>
    public const string WinnerLost = "winner.lost";
    /// <summary>성과가 죽은 리스팅 — 정리 후보 (09 롤업).</summary>
    public const string PerfDead = "perf.dead";
    /// <summary>정산 라인이 주문과 안 맞음 (10 대사).</summary>
    public const string SettleUnmatched = "settle.unmatched";
    /// <summary>확정이익과 추정이익의 차이 (10).</summary>
    public const string SettleGap = "settle.gap";
    /// <summary>발주 사가가 Disputed — 돈을 잠그지도 풀지도 못하는 상태 (v2 §2.4).</summary>
    public const string SagaDisputed = "saga.disputed";
    /// <summary>작업이 죽었다 (v2 §2.1).</summary>
    public const string WorkDead = "work.dead";
    /// <summary>소싱 후보 승인 대기 (11).</summary>
    public const string SourceCandidate = "source.candidate";
    /// <summary>AI 생성 콘텐츠가 검증에서 반려됨 (12).</summary>
    public const string ContentRejected = "content.rejected";
    /// <summary>필수 속성이 없어 등록이 막힘 — 쿠팡 브랜드/GTIN (12 속성 추출).</summary>
    public const string RegulationBlocked = "regulation.blocked";
    /// <summary>일일 상한을 넘은 항목들을 묶은 집계 항목 (08 §1.3 ②).</summary>
    public const string Folded = "attention.folded";

    // ── 지금 코드가 이미 알고 있는 사실 (08 §1.1 "현재 코드" 행) ──

    /// <summary>금고 잔액이 경고선 아래 — 발주가 멈추면 미출고 페널티가 쌓인다.</summary>
    public const string WalletLow = "wallet.low";
    /// <summary>발주 후의 취소·반품인데 공급처 조치가 남았다 — 방치하면 산 물건이 그대로 나간다.</summary>
    public const string CsSupplierAction = "cs.supplier_action";
}

/// <summary>suggested_action 값 — 화면이 어떤 버튼을 띄울지 고르는 힌트.</summary>
public static class AttentionActions
{
    public const string Reprice = "reprice";
    public const string Prune = "prune";
    public const string Purchase = "purchase";
    public const string Recontent = "recontent";
    public const string Verify = "verify";
    public const string Settle = "settle";
    public const string None = "none";
}
