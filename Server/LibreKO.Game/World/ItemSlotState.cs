using LibreKO.Common.Domain.Entities.GameData;

namespace LibreKO.Game.World;

public readonly record struct ItemSlotState(int ItemId, short Durability, ushort Count, byte Flag, long ExpiresAt)
{
    public static ItemSlotState Of(ItemSlot slot) =>
        new(slot.ItemId, slot.Durability, slot.Count, slot.Flag, slot.ExpiresAt);

    public void RestoreTo(ItemSlot slot)
    {
        slot.ItemId = ItemId;
        slot.Durability = Durability;
        slot.Count = Count;
        slot.Flag = Flag;
        slot.ExpiresAt = ExpiresAt;
    }
}
