using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol;

namespace LibreKO.Game.World;

public static class ItemTransfer
{
    public const int NoSlot = -1;
    public const ushort SingleItem = 1;

    public static bool IsInventoryLocked(UserSession session) =>
        session.Trade.LocksInventory || session.IsGathering;

    public static bool IsNoTradeItem(int itemId) =>
        itemId is >= ExchangePacketConstants.ItemNoTrade and < ExchangePacketConstants.ItemNoTradeMax;

    public static bool CanLeaveOwner(ItemSlot slot, ItemData? itemData) =>
        IsTransferable(slot, itemData)
        && slot.IsTradable
        && !slot.Expires;

    public static bool CanEnterAccountVault(ItemSlot slot, ItemData? itemData) =>
        IsTransferable(slot, itemData)
        && (slot.IsTradable || slot.State == ItemFlag.Rented);

    private static bool IsTransferable(ItemSlot slot, ItemData? itemData) =>
        itemData != null
        && !slot.IsEmpty
        && itemData.Race != ExchangePacketConstants.RaceUntradeable
        && !IsNoTradeItem(slot.ItemId);

    public static bool IsStackable(ItemData? itemData) => itemData is { Countable: not 0 };

    public static IReadOnlyList<ItemSlot> Bag(ItemSlot[] inventory) =>
        new ArraySegment<ItemSlot>(inventory, InventoryConstants.InventoryStart, InventoryConstants.HaveMax);

    public static bool IsBagIndex(int index) =>
        index >= InventoryConstants.InventoryStart
        && index < InventoryConstants.InventoryStart + InventoryConstants.HaveMax;

    public static bool CanPut(ItemStack occupant, ItemStack incoming, bool stackable) =>
        occupant.IsEmpty
        || (stackable
            && occupant.SharesIdentityWith(incoming)
            && occupant.Count + incoming.Count <= InventoryConstants.MaxStackCount);

    public static bool CanPut(ItemSlot slot, ItemStack incoming, bool stackable) =>
        CanPut(ItemStack.Of(slot), incoming, stackable);

    public static ItemStack Merge(ItemStack occupant, ItemStack incoming) =>
        occupant.IsEmpty ? incoming : occupant with { Count = (ushort)(occupant.Count + incoming.Count) };

    public static void Put(ItemSlot slot, ItemStack incoming) =>
        Merge(ItemStack.Of(slot), incoming).WriteTo(slot);

    public static ItemStack Take(ItemSlot slot, ushort count)
    {
        var taken = ItemStack.Of(slot) with { Count = count };
        if (count >= slot.Count)
            slot.Clear();
        else
            slot.Count -= count;
        return taken;
    }

    public static bool IsTransferableCount(ItemData itemData, int count) =>
        count > 0
        && count <= InventoryConstants.MaxStackCount
        && (itemData.Countable != 0 || count == 1);

    public static bool Holds(ItemSlot source, int itemId, ItemData itemData, int count) =>
        source.ItemId == itemId
        && source.Count >= count
        && (itemData.Countable != 0 || source.Count == count);

    public static bool TryMove(ItemSlot source, ItemSlot destination, int itemId)
    {
        if (source.IsEmpty || source.ItemId != itemId || !destination.IsEmpty)
            return false;

        Move(source, destination);
        return true;
    }

    public static bool TryTransfer(ItemSlot source, ItemSlot destination, ushort count, bool stackable)
    {
        if (count == 0 || source.Count < count || !CanPut(destination, ItemStack.Of(source) with { Count = count }, stackable))
            return false;

        Put(destination, Take(source, count));
        return true;
    }

    public static void Move(ItemSlot source, ItemSlot destination)
    {
        ItemStack.Of(source).WriteTo(destination);
        source.Clear();
    }

    public static void Swap(ItemSlot first, ItemSlot second)
    {
        var held = ItemStack.Of(first);
        ItemStack.Of(second).WriteTo(first);
        held.WriteTo(second);
    }

    public static ItemStack[] BagSnapshot(ItemSlot[] inventory)
    {
        var bag = new ItemStack[InventoryConstants.HaveMax];
        for (var position = 0; position < bag.Length; position++)
            bag[position] = ItemStack.Of(inventory[InventoryConstants.InventoryStart + position]);
        return bag;
    }

    public static int FindSlot(ItemStack[] slots, ItemStack incoming, bool stackable)
    {
        if (stackable)
        {
            for (var index = 0; index < slots.Length; index++)
            {
                if (!slots[index].IsEmpty && CanPut(slots[index], incoming, stackable))
                    return index;
            }
        }

        return Array.FindIndex(slots, slot => slot.IsEmpty);
    }

    public static int FindBagSlot(ItemSlot[] inventory, ItemStack incoming, bool stackable)
    {
        var position = FindSlot(BagSnapshot(inventory), incoming, stackable);
        return position == NoSlot ? NoSlot : InventoryConstants.InventoryStart + position;
    }
}
