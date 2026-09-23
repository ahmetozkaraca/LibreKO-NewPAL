using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public readonly record struct RewardLine(RewardKind Kind, int ItemId, int Count);

public readonly record struct ItemRequirement(int ItemId, int Count);

public enum RewardGrantStatus : byte
{
    Ready,
    InventoryLocked,
    MissingItems,
    InventoryFull,
    TooHeavy,
    PurseFull,
    UnknownItem,
}

public sealed class RewardGrant
{
    private static readonly IReadOnlyDictionary<int, ItemStack> NoChanges = new Dictionary<int, ItemStack>();

    internal RewardGrant(RewardGrantStatus status)
    {
        Status = status;
        Planned = NoChanges;
    }

    internal RewardGrant(IReadOnlyDictionary<int, ItemStack> planned, int gold, int eventCoins, long experience)
    {
        Status = RewardGrantStatus.Ready;
        Planned = planned;
        Gold = gold;
        EventCoins = eventCoins;
        Experience = experience;
    }

    public RewardGrantStatus Status { get; }
    public int Gold { get; }
    public int EventCoins { get; }
    public long Experience { get; }
    public bool IsReady => Status == RewardGrantStatus.Ready;

    internal IReadOnlyDictionary<int, ItemStack> Planned { get; }
    internal SlotLedger Ledger { get; } = new();
    internal int CoinsAdded { get; set; }
}

public interface IRewardGrantService
{
    int CountItem(UserSession session, int itemId);
    RewardGrant Plan(UserSession session, IReadOnlyList<ItemRequirement> consumed, IReadOnlyList<RewardLine> granted);
    void Apply(UserSession session, RewardGrant grant);
    bool Revert(UserSession session, RewardGrant grant);
    Task NotifyAsync(UserSession session, RewardGrant grant);
}

public sealed class RewardGrantService(
    IGameDataService gameData,
    IUserNotificationService userNotification,
    IPlayerProgressionService playerProgression,
    ILogger<RewardGrantService> logger) : IRewardGrantService
{
    private const int GridStart = InventoryConstants.InventoryStart;
    private const int GridEnd = InventoryConstants.InventoryStart + InventoryConstants.HaveMax;

    public int CountItem(UserSession session, int itemId)
    {
        var total = 0;
        for (var index = InventoryConstants.InventoryStart; index < session.Inventory.Length; index++)
        {
            if (session.Inventory[index].ItemId == itemId && IsConsumable(session.Inventory[index]))
                total += session.Inventory[index].Count;
        }

        return total;
    }

    public RewardGrant Plan(UserSession session, IReadOnlyList<ItemRequirement> consumed, IReadOnlyList<RewardLine> granted)
    {
        var touchesInventory = consumed.Count > 0 || granted.Any(line => line.Kind == RewardKind.Item);
        if (touchesInventory && session.Trade.LocksInventory)
            return new RewardGrant(RewardGrantStatus.InventoryLocked);

        var slots = session.Inventory.Select(ItemStack.Of).ToArray();
        var touched = new HashSet<int>();
        long weight = 0;
        foreach (var requirement in consumed.Where(requirement => requirement.Count > 0))
        {
            var item = gameData.GetItem(requirement.ItemId);
            if (item == null)
                return new RewardGrant(RewardGrantStatus.UnknownItem);
            if (!Take(session, slots, requirement, touched))
                return new RewardGrant(RewardGrantStatus.MissingItems);

            weight -= (long)item.Weight * requirement.Count;
        }

        long gold = 0;
        long coins = 0;
        long experience = 0;
        foreach (var line in granted.Where(line => line.Count > 0))
        {
            if (line.Kind == RewardKind.Gold)
                gold += line.Count;
            else if (line.Kind == RewardKind.EventCoins)
                coins += line.Count;
            else if (line.Kind == RewardKind.Experience)
                experience += line.Count;
            else if (line.Kind == RewardKind.Item)
            {
                var item = gameData.GetItem(line.ItemId);
                if (item == null)
                    return new RewardGrant(RewardGrantStatus.UnknownItem);
                if (!Place(slots, item, line.Count, touched))
                    return new RewardGrant(RewardGrantStatus.InventoryFull);

                weight += (long)item.Weight * line.Count;
            }
        }

        if (weight > 0 && session.Stats.ItemWeight + weight > session.Stats.MaxWeight)
            return new RewardGrant(RewardGrantStatus.TooHeavy);
        if (!Coins.CanCredit(session.Money, gold))
            return new RewardGrant(RewardGrantStatus.PurseFull);

        var planned = touched.ToDictionary(index => index, index => slots[index]);
        return new RewardGrant(planned, (int)gold, (int)Math.Min(coins, int.MaxValue), experience);
    }

    public void Apply(UserSession session, RewardGrant grant)
    {
        foreach (var (index, stack) in grant.Planned)
        {
            var slot = session.Inventory[index];
            grant.Ledger.Touch(slot, ItemTransfer.Bag(session.Inventory));
            stack.WriteTo(slot);
        }

        grant.Ledger.Settle();

        session.Money += grant.Gold;
        var coins = (int)Math.Min((long)session.Rewards.EventCoins + grant.EventCoins, int.MaxValue);
        grant.CoinsAdded = coins - session.Rewards.EventCoins;
        session.Rewards.EventCoins = coins;

        if (grant.Planned.Count > 0)
            session.RecalculateStatsWithBuffs(gameData);
    }

    public bool Revert(UserSession session, RewardGrant grant)
    {
        var reverted = grant.Ledger.Revert(gameData);
        if (!reverted)
            logger.LogError("The reward items of {Name} could not be taken back", session.Name);

        session.Money = Math.Max(0, session.Money - grant.Gold);
        session.Rewards.EventCoins = Math.Max(0, session.Rewards.EventCoins - grant.CoinsAdded);

        if (grant.Planned.Count > 0)
            session.RecalculateStatsWithBuffs(gameData);
        return reverted;
    }

    public async Task NotifyAsync(UserSession session, RewardGrant grant)
    {
        foreach (var (index, stack) in grant.Planned)
        {
            var isNewItem = !stack.IsEmpty && stack.ItemId != grant.Ledger.Before(session.Inventory[index]).ItemId;
            await userNotification.SendStackChangeAsync(
                session, (byte)index, stack.ItemId, stack.Count, stack.Durability, isNewItem);
        }

        if (grant.Planned.Count > 0)
            await userNotification.SendWeightChangeAsync(session);
        if (grant.Gold > 0)
            await userNotification.SendGoldGainAsync(session, grant.Gold);
        if (grant.Experience > 0)
            await playerProgression.AwardExperienceAsync(session, grant.Experience);
    }

    private static bool IsConsumable(ItemSlot slot) => slot.IsTradable && !slot.Expires;

    private static bool Take(UserSession session, ItemStack[] slots, ItemRequirement requirement, HashSet<int> touched)
    {
        var remaining = requirement.Count;
        for (var index = InventoryConstants.InventoryStart; index < slots.Length && remaining > 0; index++)
        {
            if (slots[index].ItemId != requirement.ItemId || !IsConsumable(session.Inventory[index]))
                continue;

            var taken = Math.Min(slots[index].Count, remaining);
            slots[index] = taken == slots[index].Count
                ? default
                : slots[index] with { Count = (ushort)(slots[index].Count - taken) };
            remaining -= taken;
            touched.Add(index);
        }

        return remaining == 0;
    }

    private static bool Place(ItemStack[] slots, ItemData item, int count, HashSet<int> touched)
    {
        var stackable = item.Countable != 0;
        if (stackable && count > InventoryConstants.MaxStackCount)
            return false;

        var perSlot = stackable ? count : ItemTransfer.SingleItem;
        var placements = stackable ? ItemTransfer.SingleItem : count;
        for (var placed = 0; placed < placements; placed++)
        {
            var incoming = ItemStack.Fresh(item.Num, item.Duration, (ushort)perSlot);
            var position = ItemTransfer.FindSlot(slots[GridStart..GridEnd], incoming, stackable);
            if (position == ItemTransfer.NoSlot)
                return false;

            var index = GridStart + position;
            slots[index] = ItemTransfer.Merge(slots[index], incoming);
            touched.Add(index);
        }

        return true;
    }
}
