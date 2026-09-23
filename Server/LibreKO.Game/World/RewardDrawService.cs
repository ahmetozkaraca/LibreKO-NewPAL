using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public readonly record struct RewardDrawResult(RewardOutcome Outcome, RewardKind Kind, int ItemId, int Count)
{
    public const int NoPrize = 0;

    public int PrizeItemId => Outcome == RewardOutcome.Succeeded && Kind == RewardKind.Item ? ItemId : NoPrize;
    public int PrizeGold => Outcome == RewardOutcome.Succeeded && Kind == RewardKind.Gold ? Count : NoPrize;

    public static RewardDrawResult Failed(RewardOutcome outcome) => new(outcome, RewardKind.Gold, NoPrize, NoPrize);
}

public interface IRewardDrawService
{
    Task<int> OpenRouletteAsync(UserSession session);
    Task<RewardDrawResult> SpinRouletteAsync(UserSession session);
    Task<IReadOnlyList<RoulettePrizeRecord>> RouletteHistoryAsync(UserSession session);
    Task<bool> IsDailyRewardAvailableAsync(UserSession session, PrizePool pool);
    Task<RewardDrawResult> ClaimDailyRewardAsync(UserSession session, PrizePool pool);
}

public sealed class RewardDrawService(
    IRewardStateService states,
    IRewardGrantService grants,
    IPrizeDrawService prizes,
    IViolationMonitor violations,
    TimeProvider time,
    ILogger<RewardDrawService> logger) : IRewardDrawService
{
    public const int SpinCost = 1;

    private const int NoCoins = 0;

    private static readonly RewardDrawResult Unavailable = RewardDrawResult.Failed(RewardOutcome.Unavailable);

    public async Task<int> OpenRouletteAsync(UserSession session)
    {
        if (!await states.EnsureLoadedAsync(session))
            return NoCoins;

        return session.WithLock(s =>
        {
            var state = s.Rewards;
            if (state.EventCoins >= SpinCost)
                state.OfferedDraws.Add(PrizePool.Roulette);
            else
                state.OfferedDraws.Remove(PrizePool.Roulette);

            return state.EventCoins;
        });
    }

    public Task<RewardDrawResult> SpinRouletteAsync(UserSession session) =>
        states.RunExclusiveAsync(session, Unavailable, state => SpinAsync(session, state));

    public async Task<IReadOnlyList<RoulettePrizeRecord>> RouletteHistoryAsync(UserSession session)
    {
        if (!await states.EnsureLoadedAsync(session))
            return [];

        return session.WithLock(s => s.Rewards.RecentSpins.ToList());
    }

    public async Task<bool> IsDailyRewardAvailableAsync(UserSession session, PrizePool pool)
    {
        if (!await states.EnsureLoadedAsync(session))
            return false;

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        return session.WithLock(s =>
        {
            var state = s.Rewards;
            var available = !state.HasClaimed(pool, today) && prizes.EligiblePrizes(pool, s.Level).Count > 0;
            if (available)
                state.OfferedDraws.Add(pool);
            else
                state.OfferedDraws.Remove(pool);

            return available;
        });
    }

    public Task<RewardDrawResult> ClaimDailyRewardAsync(UserSession session, PrizePool pool) =>
        states.RunExclusiveAsync(session, Unavailable, state => ClaimDailyAsync(session, state, pool));

    private async Task<RewardDrawResult> SpinAsync(UserSession session, RewardState state)
    {
        var now = time.GetUtcNow().UtcDateTime;
        RewardPrizeData? prize = null;
        RewardGrant? grant = null;

        var verdict = session.WithLock(s =>
        {
            if (state.EventCoins < SpinCost)
                return RewardVerdict.Violated(state.OfferedDraws.Contains(PrizePool.Roulette)
                    ? ViolationKind.InvalidState
                    : ViolationKind.ForgedEvent);

            var eligible = prizes.EligiblePrizes(PrizePool.Roulette, s.Level);
            if (eligible.Count == 0)
                return new RewardVerdict(RewardOutcome.Unavailable, null);

            var drawn = Draw(s, eligible, out prize, out grant);
            if (drawn.Outcome == RewardOutcome.Succeeded)
                state.EventCoins -= SpinCost;

            return drawn;
        });

        if (!Settle(session, verdict, PrizePool.Roulette))
            return RewardDrawResult.Failed(verdict.Outcome);

        var spin = new RouletteSpin
        {
            CharacterId = session.CharacterId,
            Kind = prize!.Kind,
            ItemId = prize.ItemId,
            Count = prize.Count,
            SpunAt = now,
        };

        if (!await states.SpendEventCoinsAsync(spin, SpinCost))
        {
            session.WithLock(s =>
            {
                grants.Revert(s, grant!);
                state.EventCoins += SpinCost;
            });
            logger.LogWarning("{Name} could not record a roulette spin; the prize was withdrawn", session.Name);
            return Unavailable;
        }

        session.WithLock(_ => state.RecordSpin(new RoulettePrizeRecord(prize.Kind, prize.ItemId, prize.Count, now)));
        await grants.NotifyAsync(session, grant!);
        logger.LogInformation("{Name} spun the roulette and won prize {PrizeId}", session.Name, prize.Id);
        return new RewardDrawResult(RewardOutcome.Succeeded, prize.Kind, prize.ItemId, prize.Count);
    }

    private async Task<RewardDrawResult> ClaimDailyAsync(UserSession session, RewardState state, PrizePool pool)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        RewardPrizeData? prize = null;
        RewardGrant? grant = null;
        DateOnly? previousClaim = null;

        var verdict = session.WithLock(s =>
        {
            if (state.HasClaimed(pool, today))
                return RewardVerdict.Violated(ViolationKind.InvalidState);

            var eligible = prizes.EligiblePrizes(pool, s.Level);
            if (eligible.Count == 0)
                return RewardVerdict.Violated(state.OfferedDraws.Contains(pool) || !IsStatusGated(pool)
                    ? ViolationKind.InvalidState
                    : ViolationKind.ForgedEvent);

            var drawn = Draw(s, eligible, out prize, out grant);
            if (drawn.Outcome == RewardOutcome.Succeeded)
            {
                previousClaim = state.DailyClaims.TryGetValue(pool, out var claimedOn) ? claimedOn : null;
                state.DailyClaims[pool] = today;
            }

            return drawn;
        });

        if (!Settle(session, verdict, pool))
            return RewardDrawResult.Failed(verdict.Outcome);

        var claim = new DailyRewardClaim
        {
            AccountId = session.AccountId,
            Pool = pool,
            Day = today,
            CharacterId = session.CharacterId,
            Kind = prize!.Kind,
            ItemId = prize.ItemId,
            Count = prize.Count,
            ClaimedAt = now,
        };

        if (!await states.ClaimDailyRewardAsync(claim))
        {
            session.WithLock(s =>
            {
                grants.Revert(s, grant!);
                if (previousClaim is { } day)
                    state.DailyClaims[pool] = day;
                else
                    state.DailyClaims.Remove(pool);
            });
            logger.LogWarning("{Name} could not record the {Pool} reward for {Day}; the prize was withdrawn",
                session.Name, pool, today);
            return Unavailable;
        }

        session.WithLock(_ => state.OfferedDraws.Remove(pool));
        await grants.NotifyAsync(session, grant!);
        logger.LogInformation("{Name} collected the {Pool} reward for {Day}: prize {PrizeId}",
            session.Name, pool, today, prize.Id);
        return new RewardDrawResult(RewardOutcome.Succeeded, prize.Kind, prize.ItemId, prize.Count);
    }

    private RewardVerdict Draw(UserSession session, IReadOnlyList<RewardPrizeData> eligible,
        out RewardPrizeData? prize, out RewardGrant? grant)
    {
        prize = null;
        grant = null;
        foreach (var candidate in eligible)
        {
            var plan = grants.Plan(session, [], [LineOf(candidate)]);
            if (!plan.IsReady)
                return RewardVerdict.Blocked(plan.Status);
        }

        prize = prizes.Pick(eligible);
        grant = grants.Plan(session, [], [LineOf(prize)]);
        grants.Apply(session, grant);
        return RewardVerdict.Succeeded;
    }

    private bool Settle(UserSession session, RewardVerdict verdict, PrizePool pool)
    {
        if (verdict.Violation is { } violation)
            violations.Report(session, violation, $"{pool} draw refused");
        else if (verdict.Outcome == RewardOutcome.Unavailable)
            logger.LogWarning("The {Pool} prize table could not serve {Name}; check its prizes", pool, session.Name);

        return verdict.Outcome == RewardOutcome.Succeeded;
    }

    // The stock genie window enables its claim button before the status reply arrives.
    private static bool IsStatusGated(PrizePool pool) => pool != PrizePool.Genie;

    private static RewardLine LineOf(RewardPrizeData prize) => new(prize.Kind, prize.ItemId, prize.Count);
}
