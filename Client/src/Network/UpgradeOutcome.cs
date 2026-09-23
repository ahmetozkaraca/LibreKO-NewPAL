using System;
using System.Collections.Generic;
using LibreKO.Domain;

namespace LibreKO.Network;

public static class UpgradeOutcome
{
    public const byte Failed = 0;
    public const byte Succeeded = 1;

    public static bool Rolled(byte result) => result is Succeeded or Failed;

    public static Dictionary<int, ItemSlot> Changes(
        ItemSlot[]? inventory, byte result, UpgradeSlotResult[] slots, Func<int, short> fullDurability)
    {
        var changes = new Dictionary<int, ItemSlot>();
        if (!Rolled(result) || slots.Length == 0)
            return changes;

        var origin = slots[0];
        if (InBag(origin.Position))
        {
            int abs = InventoryConstants.InventoryStart + origin.Position;
            var item = Current(inventory, changes, abs);
            if (origin.ItemId == 0)
                item = default;
            else if (result == Succeeded)
                item = new ItemSlot { ItemId = origin.ItemId, Count = 1, Durability = fullDurability(origin.ItemId) };
            else
                item.ItemId = origin.ItemId;
            changes[abs] = item;
        }

        for (int i = 1; i < slots.Length; i++)
        {
            var material = slots[i];
            if (!InBag(material.Position))
                continue;

            int abs = InventoryConstants.InventoryStart + material.Position;
            var current = Current(inventory, changes, abs);
            if (current.ItemId == 0 || current.ItemId != material.ItemId)
                continue;

            if (current.Count > 1)
                current.Count--;
            else
                current = default;
            changes[abs] = current;
        }

        return changes;
    }

    private static bool InBag(int position) => position >= 0 && position < InventoryConstants.HaveMax;

    private static ItemSlot Current(ItemSlot[]? inventory, Dictionary<int, ItemSlot> changes, int abs)
        => changes.TryGetValue(abs, out var changed) ? changed
            : inventory != null && abs < inventory.Length ? inventory[abs] : default;
}
