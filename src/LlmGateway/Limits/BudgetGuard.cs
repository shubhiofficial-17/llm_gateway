using System.Globalization;
using LlmGateway.Auth;
using LlmGateway.Telemetry;
using LlmGateway.Usage;
using Microsoft.Extensions.Options;

namespace LlmGateway.Limits;

/// <summary>Estimated cost held against a team's monthly budget while its request runs.</summary>
public sealed record BudgetReservation(string TeamId, string Month, string Id, long ReservedMicros)
{
    public static readonly BudgetReservation None = new("", "", "", 0);
}

/// <summary>
/// Monthly spend budgets. Like the token limit, each request reserves its worst-case cost before it
/// is sent and settles to the real cost afterwards, so parallel requests can't overspend.
/// Unlike rate limits this fails closed: if the budget can't be checked, the request is refused,
/// because it protects money. See ADR 0007.
/// </summary>
public sealed class BudgetGuard(
    IBudgetStore store,
    IUsageStore ledger,
    IOptionsMonitor<BudgetOptions> options,
    TimeProvider time,
    GatewayTelemetry telemetry,
    ILogger<BudgetGuard> logger)
{
    /// <summary>
    /// Reserves <paramref name="estimateMicros"/> against the team's budget for the current month.
    /// Returns null if the request was refused; the error response has then been written.
    /// </summary>
    public async Task<BudgetReservation?> TryReserveAsync(HttpContext ctx, Team team, long estimateMicros)
    {
        var limit = team.MonthlyBudgetMicros ?? (long)(options.CurrentValue.DefaultMonthlyBudgetUsd * 1_000_000m);
        if (limit <= 0 || estimateMicros <= 0)
        {
            return BudgetReservation.None; // no budget, or a free model
        }

        var now = time.GetUtcNow();
        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var reservationId = Guid.NewGuid().ToString("N");
        var ttl = TimeSpan.FromSeconds(options.CurrentValue.ReservationTtlSeconds);

        BudgetDecision decision;
        try
        {
            decision = await store.TryReserveAsync(team.Id, month, limit, estimateMicros, reservationId, ttl);
            if (decision.Status == BudgetStatus.NeedsSeed)
            {
                // First request this month, or Redis lost its data: rebuild "used" from the Postgres ledger.
                var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
                var spent = await ledger.GetSummaryAsync(team.Id, monthStart, now.AddSeconds(1), ctx.RequestAborted);
                await store.SeedAsync(team.Id, month, spent.CostMicros);
                decision = await store.TryReserveAsync(team.Id, month, limit, estimateMicros, reservationId, ttl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Budget check failed for team {TeamId}; refusing the request", team.Id);
            telemetry.LimitRejected(team.Id, "budget_unavailable");
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status503ServiceUnavailable,
                "Spend budgets can't be checked right now, so the request was not sent. Try again shortly.",
                "gateway_error", "budget_unavailable");
            return null;
        }

        ctx.Response.Headers["x-gateway-budget-remaining-usd"] =
            Pricing.MicrosToUsd(Math.Max(0, decision.RemainingMicros)).ToString(CultureInfo.InvariantCulture);

        if (decision.Status == BudgetStatus.Reserved)
        {
            return new BudgetReservation(team.Id, month, reservationId, estimateMicros);
        }

        // 402 rather than 429: retrying won't help until the budget is raised or the month rolls over.
        telemetry.LimitRejected(team.Id, "monthly_budget");
        await OpenAiError.WriteAsync(ctx, StatusCodes.Status402PaymentRequired,
            $"Team '{team.Id}' has reached its monthly budget of ${Pricing.MicrosToUsd(limit)}. " +
            $"This request could cost up to ${Pricing.MicrosToUsd(estimateMicros)}.",
            "insufficient_quota", "budget_exceeded");
        return null;
    }

    /// <summary>Swaps the reservation for the real cost. If this fails the reservation expires on its own.</summary>
    public async Task SettleAsync(BudgetReservation reservation, long actualMicros)
    {
        if (reservation == BudgetReservation.None)
        {
            return;
        }

        try
        {
            await store.SettleAsync(reservation.TeamId, reservation.Month, reservation.Id, reservation.ReservedMicros, actualMicros);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not settle budget reservation {Id} for team {TeamId}", reservation.Id, reservation.TeamId);
        }
    }
}
