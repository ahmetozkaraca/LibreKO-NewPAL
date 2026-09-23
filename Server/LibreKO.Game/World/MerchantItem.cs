using LibreKO.Common.Domain.Entities.GameData;

namespace LibreKO.Game.World;

public class MerchantItem
{
    public int ItemId { get; set; }
    public short Durability { get; set; }
    public ushort Count { get; set; }
    public int Price { get; set; }
    public byte OriginalSlot { get; set; }
    public byte Flag { get; set; }
    public long ExpiresAt { get; set; }

    public bool IsEmpty => ItemId == 0;

    public bool IsStillHeldIn(ItemSlot slot) =>
        !IsEmpty
        && slot.ItemId == ItemId
        && slot.Flag == Flag
        && slot.ExpiresAt == ExpiresAt
        && slot.Count >= Count;
}
