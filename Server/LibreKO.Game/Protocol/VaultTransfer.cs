using LibreKO.Common.Domain.Entities.GameData;

namespace LibreKO.Game.Protocol;

internal static class VaultTransfer
{
    public static bool Fits(ItemData itemData, ItemSlot source, ItemSlot destination, int count) =>
        destination.IsEmpty
        || (destination.ItemId == source.ItemId
            && itemData.Countable != 0
            && destination.Flag == source.Flag
            && destination.ExpiresAt == source.ExpiresAt
            && destination.Count + count <= InventoryConstants.MaxStackCount);

    public static void Move(ItemSlot source, ItemSlot destination, int count)
    {
        if (destination.IsEmpty)
        {
            destination.ItemId = source.ItemId;
            destination.Durability = source.Durability;
            destination.Flag = source.Flag;
            destination.ExpiresAt = source.ExpiresAt;
        }

        destination.Count = (ushort)(destination.Count + count);
        source.Count = (ushort)(source.Count - count);
        if (source.Count == 0)
            source.Clear();
    }
}
