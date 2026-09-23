using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;

namespace LibreKO.Game.World;

public readonly record struct ItemStack(int ItemId, short Durability, ushort Count, byte Flag, long ExpiresAt)
{
    public const long NoExpiry = 0;

    public bool IsEmpty => ItemId == 0;

    public static ItemStack Of(ItemSlot slot) =>
        new(slot.ItemId, slot.Durability, slot.Count, slot.Flag, slot.ExpiresAt);

    public static ItemStack Fresh(int itemId, short durability, ushort count) =>
        new(itemId, durability, count, (byte)ItemFlag.Unsealed, NoExpiry);

    public bool SharesIdentityWith(ItemStack other) =>
        ItemId == other.ItemId && Flag == other.Flag && ExpiresAt == other.ExpiresAt;

    public void WriteTo(ItemSlot slot)
    {
        slot.ItemId = ItemId;
        slot.Durability = Durability;
        slot.Count = Count;
        slot.Flag = Flag;
        slot.ExpiresAt = ExpiresAt;
    }

    public ItemSlot ToSlot()
    {
        var slot = new ItemSlot();
        WriteTo(slot);
        return slot;
    }
}
