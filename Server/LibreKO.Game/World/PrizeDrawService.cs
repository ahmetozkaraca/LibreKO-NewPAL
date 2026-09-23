using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;

namespace LibreKO.Game.World;

public interface IRewardRandom
{
    int Next(int maxExclusive);
}

public sealed class RewardRandom : IRewardRandom
{
    public int Next(int maxExclusive) => Random.Shared.Next(maxExclusive);
}

public interface IPrizeDrawService
{
    IReadOnlyList<RewardPrizeData> EligiblePrizes(PrizePool pool, byte level);
    RewardPrizeData Pick(IReadOnlyList<RewardPrizeData> prizes);
}

public sealed class PrizeDrawService(IGameDataService gameData, IRewardRandom random) : IPrizeDrawService
{
    public IReadOnlyList<RewardPrizeData> EligiblePrizes(PrizePool pool, byte level) =>
        gameData.RewardPrizesByPool[pool]
            .Where(prize => prize.Weight > 0 && prize.Count > 0 && prize.AcceptsLevel(level) && IsReportable(pool, prize.Kind))
            .OrderBy(prize => prize.Id)
            .ToList();

    public RewardPrizeData Pick(IReadOnlyList<RewardPrizeData> prizes)
    {
        var roll = random.Next(prizes.Sum(prize => prize.Weight));
        foreach (var prize in prizes)
        {
            if (roll < prize.Weight)
                return prize;

            roll -= prize.Weight;
        }

        return prizes[^1];
    }

    private static bool IsReportable(PrizePool pool, RewardKind kind) =>
        kind == RewardKind.Gold || (kind == RewardKind.Item && pool != PrizePool.Genie);
}
