using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Treasury;

namespace Tetragon.Api.Endpoints;

public static class WalletEndpoints
{
    public static void MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/wallet").WithTags("Wallet");

        group.MapGet("/", async (IWalletRepository wallets, CancellationToken ct) =>
        {
            var wallet = await wallets.GetOrCreateAsync(ct);
            return Results.Ok(WalletDto(wallet));
        })
        .WithSummary("금고 잔액 조회");

        group.MapGet("/transactions", async (
            int? limit, IWalletRepository wallets, CancellationToken ct) =>
        {
            var items = await wallets.RecentTransactionsAsync(Math.Clamp(limit ?? 50, 1, 500), ct);
            return Results.Ok(new
            {
                items = items.Select(t => new
                {
                    id = t.Id,
                    type = t.Type.ToString(),
                    typeLabel = TypeLabel(t.Type),
                    amount = t.Amount,
                    balanceAfter = t.BalanceAfter,
                    orderId = t.OrderId,
                    memo = t.Memo,
                    occurredAt = t.OccurredAt,
                }),
            });
        })
        .WithSummary("금고 입출금 이력");

        group.MapPost("/deposit", async (
            WalletAmountRequest request, IWalletRepository wallets, CancellationToken ct) =>
        {
            var wallet = await wallets.GetOrCreateAsync(ct);
            try
            {
                var transaction = wallet.Deposit(request.Amount, request.Memo ?? "충전");
                await wallets.SaveAsync(wallet, transaction, ct);
                return Results.Ok(WalletDto(wallet));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("금고 충전");

        group.MapPost("/withdraw", async (
            WalletAmountRequest request, IWalletRepository wallets, CancellationToken ct) =>
        {
            var wallet = await wallets.GetOrCreateAsync(ct);
            try
            {
                var transaction = wallet.Withdraw(request.Amount, request.Memo ?? "출금");
                await wallets.SaveAsync(wallet, transaction, ct);
                return Results.Ok(WalletDto(wallet));
            }
            catch (InsufficientFundsException ex)
            {
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    required = ex.Required,
                    available = ex.Available,
                    shortfall = ex.Shortfall,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("금고 출금");

        group.MapPut("/threshold", async (
            WalletThresholdRequest request, IWalletRepository wallets, CancellationToken ct) =>
        {
            var wallet = await wallets.GetOrCreateAsync(ct);
            wallet.SetLowBalanceThreshold(request.Threshold);
            // 금액 변동이 아니므로 이력 없이 상태만 저장한다
            await wallets.SaveAsync(ct);
            return Results.Ok(WalletDto(wallet));
        })
        .WithSummary("잔액 경고선 설정");
    }

    private static object WalletDto(Wallet w) => new
    {
        id = w.Id,
        currency = w.Currency,
        balance = w.Balance,
        reserved = w.Reserved,
        available = w.Available,
        lowBalanceThreshold = w.LowBalanceThreshold,
        isLow = w.IsLow,
        updatedAt = w.UpdatedAt,
    };

    private static string TypeLabel(WalletTransactionType type) => type switch
    {
        WalletTransactionType.Deposit => "충전",
        WalletTransactionType.Withdraw => "출금",
        WalletTransactionType.Reserve => "발주 예약",
        WalletTransactionType.Purchase => "발주 결제",
        WalletTransactionType.Release => "예약 취소",
        _ => type.ToString(),
    };
}
