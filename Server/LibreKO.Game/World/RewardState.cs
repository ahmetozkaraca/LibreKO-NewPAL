using LibreKO.Common.Enums;

namespace LibreKO.Game.World;

public readonly record struct RewardQuestKey(int QuestId, DateOnly PeriodStart);

public readonly record struct RoulettePrizeRecord(RewardKind Kind, int ItemId, int Count, DateTime SpunAt);

public sealed class RewardQuestProgress
{
    public int Kills { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }

    public bool IsAccepted => AcceptedAt != null;
    public bool IsClaimed => ClaimedAt != null;
}

public sealed class RewardState
{
    public const int RecentSpinCapacity = 20;

    private volatile bool _loaded;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool IsLoaded
    {
        get => _loaded;
        set => _loaded = value;
    }

    public Dictionary<RewardQuestKey, RewardQuestProgress> Quests { get; } = [];
    public HashSet<RewardQuestKey> DirtyQuests { get; } = [];
    public int EventCoins { get; set; }
    public List<RoulettePrizeRecord> RecentSpins { get; } = [];
    public Dictionary<PrizePool, DateOnly> DailyClaims { get; } = [];
    public HashSet<int> OfferedAccepts { get; } = [];
    public Dictionary<QuestBoard, HashSet<int>> OfferedClaims { get; } = [];
    public HashSet<PrizePool> OfferedDraws { get; } = [];
    public HashSet<PrizePool> CollectedDraws { get; } = [];

    public RewardQuestProgress? ProgressOf(RewardQuestKey key) => Quests.GetValueOrDefault(key);

    public RewardQuestProgress Track(RewardQuestKey key)
    {
        if (!Quests.TryGetValue(key, out var progress))
        {
            progress = new RewardQuestProgress();
            Quests[key] = progress;
        }

        return progress;
    }

    public HashSet<int> OfferedClaimsOn(QuestBoard board)
    {
        if (!OfferedClaims.TryGetValue(board, out var offered))
        {
            offered = [];
            OfferedClaims[board] = offered;
        }

        return offered;
    }

    public bool HasClaimed(PrizePool pool, DateOnly day) =>
        DailyClaims.TryGetValue(pool, out var claimedOn) && claimedOn == day;

    public void RecordSpin(RoulettePrizeRecord spin)
    {
        RecentSpins.Insert(0, spin);
        if (RecentSpins.Count > RecentSpinCapacity)
            RecentSpins.RemoveAt(RecentSpins.Count - 1);
    }
}
