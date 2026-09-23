using LibreKO.Common.Domain.Entities.GameData;

namespace LibreKO.Game.World;

public class ExchangeItem
{
    public int ItemId { get; set; }
    public short Durability { get; set; }
    public int Count { get; set; }
    public byte Flag { get; set; }
    public long ExpiresAt { get; set; }
    public bool Stackable { get; set; }
    public byte SrcPos { get; set; }
    public byte DstPos { get; set; }

    public bool IsGold => ItemId == InventoryConstants.ItemGold;

    public ItemStack Stack => new(ItemId, Durability, (ushort)Count, Flag, ExpiresAt);

    public static ExchangeItem Escrowed(ItemStack stack, byte sourceIndex, bool stackable) => new()
    {
        ItemId = stack.ItemId,
        Durability = stack.Durability,
        Count = stack.Count,
        Flag = stack.Flag,
        ExpiresAt = stack.ExpiresAt,
        Stackable = stackable,
        SrcPos = sourceIndex,
    };
}
