using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol;

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

internal readonly record struct RewardSlotChange(int Index, int ItemId, int Delta, short Durability);

internal readonly record struct RewardSlotImage(int Index, int ItemId, short Durability, ushort Count, byte Flag, long ExpiresAt);

public sealed class RewardGrant
{
    private static readonly IReadOnlyList<RewardSlotChange> NoChanges = [];

    internal RewardGrant(RewardGrantStatus status)
    {
        Status = status;
        Changes = NoChanges;
    }

    internal RewardGrant(IReadOnlyList<RewardSlotChange> changes, int gold, int eventCoins, long experience)
    {
        Status = RewardGrantStatus.Ready;
        Changes = changes;
        Gold = gold;
        EventCoins = eventCoins;
        Experience = experience;
    }

    public RewardGrantStatus Status { get; }
    public int Gold { get; }
    public int EventCoins { get; }
    public long Experience { get; }
    public bool IsReady => Status == RewardGrantStatus.Ready;

    internal IReadOnlyList<RewardSlotChange> Changes { get; }
    internal Dictionary<int, RewardSlotImage> Originals { get; } = [];
    internal List<RewardSlotImage> Updated { get; } = [];
    internal int CoinsAdded { get; set; }
}

public interface IRewardGrantService
{
    int CountItem(UserSession session, int itemId);
    RewardGrant Plan(UserSession session, IReadOnlyList<ItemRequirement> consumed, IReadOnlyList<RewardLine> granted);
    void Apply(UserSession session, RewardGrant grant);
    void Revert(UserSession session, RewardGrant grant);
    Task NotifyAsync(UserSession session, RewardGrant grant);
}

public sealed class RewardGrantService(
    IGameDataService gameData,
    IUserNotificationService userNotification,
    IPlayerProgressionService playerProgression) : IRewardGrantService
{
    private const int EmptyItemId = 0;
    private const int NoSlot = -1;
    private const int SingleUnit = 1;
    private const int GridStart = InventoryConstants.InventoryStart;
    private const int GridEnd = InventoryConstants.InventoryStart + InventoryConstants.HaveMax;

    public int CountItem(UserSession session, int itemId)
    {
        var total = 0;
        for (var index = InventoryConstants.InventoryStart; index < session.Inventory.Length; index++)
        {
            if (session.Inventory[index].ItemId == itemId)
                total += session.Inventory[index].Count;
        }

        return total;
    }

    public RewardGrant Plan(UserSession session, IReadOnlyList<ItemRequirement> consumed, IReadOnlyList<RewardLine> granted)
    {
        var touchesInventory = consumed.Count > 0 || granted.Any(line => line.Kind == RewardKind.Item);
        if (touchesInventory && session.Trade.LocksInventory)
            return new RewardGrant(RewardGrantStatus.InventoryLocked);

        var ids = new int[session.Inventory.Length];
        var counts = new int[session.Inventory.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            ids[index] = session.Inventory[index].ItemId;
            counts[index] = session.Inventory[index].Count;
        }

        var changes = new List<RewardSlotChange>();
        long weight = 0;
        foreach (var requirement in consumed.Where(requirement => requirement.Count > 0))
        {
            var item = gameData.GetItem(requirement.ItemId);
            if (item == null)
                return new RewardGrant(RewardGrantStatus.UnknownItem);
            if (!Take(ids, counts, requirement, changes))
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
                if (!Place(ids, counts, item, line.Count, changes))
                    return new RewardGrant(RewardGrantStatus.InventoryFull);

                weight += (long)item.Weight * line.Count;
            }
        }

        if (weight > 0 && session.Stats.ItemWeight + weight > session.Stats.MaxWeight)
            return new RewardGrant(RewardGrantStatus.TooHeavy);
        if (session.Money + gold > ExchangePacketConstants.CoinMax)
            return new RewardGrant(RewardGrantStatus.PurseFull);

        return new RewardGrant(changes, (int)gold, (int)Math.Min(coins, int.MaxValue), experience);
    }

    public void Apply(UserSession session, RewardGrant grant)
    {
        foreach (var change in grant.Changes)
        {
            var slot = session.Inventory[change.Index];
            grant.Originals.TryAdd(change.Index, Image(change.Index, slot));

            if (slot.IsEmpty)
            {
                slot.Clear();
                slot.ItemId = change.ItemId;
                slot.Durability = change.Durability;
            }

            slot.Count = (ushort)(slot.Count + change.Delta);
            if (slot.Count == 0)
                slot.Clear();
        }

        session.Money += grant.Gold;
        var coins = (int)Math.Min((long)session.Rewards.EventCoins + grant.EventCoins, int.MaxValue);
        grant.CoinsAdded = coins - session.Rewards.EventCoins;
        session.Rewards.EventCoins = coins;

        foreach (var index in grant.Originals.Keys)
            grant.Updated.Add(Image(index, session.Inventory[index]));

        if (grant.Changes.Count > 0)
            session.RecalculateStatsWithBuffs(gameData);
    }

    public void Revert(UserSession session, RewardGrant grant)
    {
        foreach (var original in grant.Originals.Values)
        {
            var slot = session.Inventory[original.Index];
            slot.ItemId = original.ItemId;
            slot.Durability = original.Durability;
            slot.Count = original.Count;
            slot.Flag = original.Flag;
            slot.ExpiresAt = original.ExpiresAt;
        }

        session.Money = Math.Max(0, session.Money - grant.Gold);
        session.Rewards.EventCoins = Math.Max(0, session.Rewards.EventCoins - grant.CoinsAdded);

        if (grant.Changes.Count > 0)
            session.RecalculateStatsWithBuffs(gameData);
    }

    public async Task NotifyAsync(UserSession session, RewardGrant grant)
    {
        foreach (var slot in grant.Updated)
        {
            var isNewItem = slot.ItemId != EmptyItemId && slot.ItemId != grant.Originals[slot.Index].ItemId;
            await userNotification.SendStackChangeAsync(
                session, (byte)slot.Index, slot.ItemId, slot.Count, slot.Durability, isNewItem);
        }

        if (grant.Updated.Count > 0)
            await userNotification.SendWeightChangeAsync(session);
        if (grant.Gold > 0)
            await userNotification.SendGoldGainAsync(session, grant.Gold);
        if (grant.Experience > 0)
            await playerProgression.AwardExperienceAsync(session, grant.Experience);
    }

    private static bool Take(int[] ids, int[] counts, ItemRequirement requirement, List<RewardSlotChange> changes)
    {
        var remaining = requirement.Count;
        for (var index = InventoryConstants.InventoryStart; index < ids.Length && remaining > 0; index++)
        {
            if (ids[index] != requirement.ItemId || counts[index] <= 0)
                continue;

            var taken = Math.Min(counts[index], remaining);
            counts[index] -= taken;
            if (counts[index] == 0)
                ids[index] = EmptyItemId;

            remaining -= taken;
            changes.Add(new RewardSlotChange(index, requirement.ItemId, -taken, default));
        }

        return remaining == 0;
    }

    private static bool Place(int[] ids, int[] counts, ItemData item, int count, List<RewardSlotChange> changes)
    {
        if (item.Countable != 0)
        {
            if (count > InventoryConstants.MaxStackCount)
                return false;

            var slot = FindStack(ids, counts, item.Num, count);
            if (slot == NoSlot)
                slot = FindEmpty(ids);
            if (slot == NoSlot)
                return false;

            ids[slot] = item.Num;
            counts[slot] += count;
            changes.Add(new RewardSlotChange(slot, item.Num, count, item.Duration));
            return true;
        }

        for (var unit = 0; unit < count; unit++)
        {
            var slot = FindEmpty(ids);
            if (slot == NoSlot)
                return false;

            ids[slot] = item.Num;
            counts[slot] = SingleUnit;
            changes.Add(new RewardSlotChange(slot, item.Num, SingleUnit, item.Duration));
        }

        return true;
    }

    private static int FindStack(int[] ids, int[] counts, int itemId, int count)
    {
        for (var index = GridStart; index < GridEnd; index++)
        {
            if (ids[index] == itemId && counts[index] + count <= InventoryConstants.MaxStackCount)
                return index;
        }

        return NoSlot;
    }

    private static int FindEmpty(int[] ids)
    {
        for (var index = GridStart; index < GridEnd; index++)
        {
            if (ids[index] == EmptyItemId)
                return index;
        }

        return NoSlot;
    }

    private static RewardSlotImage Image(int index, ItemSlot slot) =>
        new(index, slot.ItemId, slot.Durability, slot.Count, slot.Flag, slot.ExpiresAt);
}
