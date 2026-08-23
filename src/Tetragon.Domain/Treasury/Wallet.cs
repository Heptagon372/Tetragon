using Tetragon.SharedKernel;

namespace Tetragon.Domain.Treasury;

/// <summary>
/// 발주 자금 금고.
///
/// 위탁판매의 자금 흐름은 한 방향이다:
///   구매자 결제(마켓) → 정산까지 수일~수주 대기
///   그 사이에 우리는 공급처에 **먼저 돈을 내고** 발주해야 한다.
/// 즉 마켓 정산금이 들어오기 전에 쓸 운전자금이 필요하고, 그걸 담는 곳이 금고다.
///
/// 잔액이 모자라면 발주가 막히고 주문이 지연되므로,
/// 발주 직전에 반드시 잔액을 확인하고 부족하면 경고한다.
///
/// 동시성: 여러 주문이 동시에 발주되면 같은 잔액을 두 번 쓸 수 있다.
/// 그래서 '차감'이 아니라 '예약(Hold) → 확정(Capture)' 2단계로 처리한다.
/// </summary>
public sealed class Wallet : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string Currency { get; private set; } = "KRW";

    /// <summary>충전된 총액에서 확정 지출을 뺀 값. 예약분은 아직 빠지지 않았다.</summary>
    public decimal Balance { get; private set; }

    /// <summary>발주 진행 중이라 묶여 있는 금액. 실제로 쓸 수 있는 돈은 Balance − Reserved.</summary>
    public decimal Reserved { get; private set; }

    /// <summary>지금 당장 발주에 쓸 수 있는 금액.</summary>
    public decimal Available => Balance - Reserved;

    /// <summary>이 금액 아래로 떨어지면 경고한다 (0이면 경고 없음).</summary>
    public decimal LowBalanceThreshold { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private Wallet() { }

    public static Wallet Create(string tenantId, string currency = "KRW") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Currency = currency,
        LowBalanceThreshold = 100_000m,
    };

    /// <summary>금고에 돈을 넣는다.</summary>
    public WalletTransaction Deposit(decimal amount, string? memo)
    {
        if (amount <= 0) throw new ArgumentException("충전 금액은 0보다 커야 합니다.");
        Balance += amount;
        Touch();
        return WalletTransaction.Of(Id, WalletTransactionType.Deposit, amount, Balance, memo);
    }

    /// <summary>금고에서 돈을 뺀다 (오입금 정정·출금).</summary>
    public WalletTransaction Withdraw(decimal amount, string? memo)
    {
        if (amount <= 0) throw new ArgumentException("출금 금액은 0보다 커야 합니다.");
        if (amount > Available)
            throw new InsufficientFundsException(amount, Available, Currency);
        Balance -= amount;
        Touch();
        return WalletTransaction.Of(Id, WalletTransactionType.Withdraw, -amount, Balance, memo);
    }

    /// <summary>
    /// 발주에 쓸 금액을 잡아 둔다. 아직 실제로 나간 돈은 아니다.
    /// 발주가 확정되면 Capture, 취소되면 Release 한다.
    /// </summary>
    public WalletTransaction Reserve(decimal amount, Guid orderId, string? memo)
    {
        if (amount <= 0) throw new ArgumentException("예약 금액은 0보다 커야 합니다.");
        if (amount > Available)
            throw new InsufficientFundsException(amount, Available, Currency);
        Reserved += amount;
        Touch();
        return WalletTransaction.Of(Id, WalletTransactionType.Reserve, -amount, Balance, memo, orderId);
    }

    /// <summary>예약을 실제 지출로 확정한다 (공급처에 결제 완료).</summary>
    public WalletTransaction Capture(decimal reservedAmount, decimal actualAmount, Guid orderId, string? memo)
    {
        if (reservedAmount > Reserved) reservedAmount = Reserved;   // 이력 불일치 방어
        Reserved -= reservedAmount;
        Balance -= actualAmount;
        Touch();
        return WalletTransaction.Of(Id, WalletTransactionType.Purchase, -actualAmount, Balance, memo, orderId);
    }

    /// <summary>발주가 취소돼 예약을 푼다.</summary>
    public WalletTransaction Release(decimal amount, Guid orderId, string? memo)
    {
        if (amount > Reserved) amount = Reserved;
        Reserved -= amount;
        Touch();
        return WalletTransaction.Of(Id, WalletTransactionType.Release, amount, Balance, memo, orderId);
    }

    public void SetLowBalanceThreshold(decimal threshold)
    {
        LowBalanceThreshold = Math.Max(threshold, 0);
        Touch();
    }

    /// <summary>경고를 띄워야 하는 상태인지.</summary>
    public bool IsLow => LowBalanceThreshold > 0 && Available < LowBalanceThreshold;

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

/// <summary>잔액이 모자랄 때 던진다. 금액을 담아 UI가 얼마가 부족한지 보여줄 수 있게 한다.</summary>
public sealed class InsufficientFundsException(decimal required, decimal available, string currency)
    : InvalidOperationException(
        $"금고 잔액이 부족합니다. 필요 {required:N0}{currency} / 사용 가능 {available:N0}{currency} " +
        $"(부족분 {required - available:N0}{currency})")
{
    public decimal Required { get; } = required;
    public decimal Available { get; } = available;
    public decimal Shortfall { get; } = required - available;
}

/// <summary>금고 입출금 이력. 잔액이 왜 이 값인지 항상 되짚을 수 있어야 한다.</summary>
public sealed class WalletTransaction : Entity<Guid>
{
    public Guid WalletId { get; private set; }
    public WalletTransactionType Type { get; private set; }
    /// <summary>부호 있는 금액 (입금 +, 지출 −).</summary>
    public decimal Amount { get; private set; }
    /// <summary>이 거래 직후의 잔액.</summary>
    public decimal BalanceAfter { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? Memo { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; } = DateTimeOffset.UtcNow;

    private WalletTransaction() { }

    internal static WalletTransaction Of(
        Guid walletId, WalletTransactionType type, decimal amount,
        decimal balanceAfter, string? memo, Guid? orderId = null) => new()
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Type = type,
            Amount = amount,
            BalanceAfter = balanceAfter,
            OrderId = orderId,
            Memo = memo,
        };
}

public enum WalletTransactionType
{
    Deposit,    // 충전
    Withdraw,   // 출금
    Reserve,    // 발주 예약 (아직 안 나감)
    Purchase,   // 발주 확정 지출
    Release,    // 예약 취소
}
