using LibreKO.Common.Enums;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

using static LibreKO.Game.Protocol.MerchantPacketConstants;

namespace LibreKO.Game.Protocol;

public interface IMerchantListingService
{
    Task AddItemAsync(UserSession session, Packet packet);
    Task CancelItemAsync(UserSession session, Packet packet);
    Task ListItemsAsync(UserSession session, Packet packet);
    Task BuyItemAsync(UserSession session, Packet packet);
}

public class MerchantListingService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    IMerchantLifecycleService merchantLifecycleService,
    ILogger<MerchantListingService> logger) : IMerchantListingService
{
    public async Task AddItemAsync(UserSession session, Packet packet)
    {
        var itemId = packet.ReadInt();
        var count = packet.ReadUShort();
        var price = packet.ReadInt();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        var itemData = gameDataService.GetItem(itemId);
        var absPos = InventoryConstants.InventoryStart + srcPos;
        var refusal =
            itemData == null ? "no such item"
            : !session.Trade.IsSellingMerchantPreparing || session.Trade.IsMerchanting ? "the stall setup is not open"
            : session.Trade.IsTrading || session.IsGathering ? "busy"
            : srcPos >= InventoryConstants.HaveMax ? "source slot out of range"
            : dstPos >= session.Trade.MerchantItems.Length ? "stall slot out of range"
            : ItemTransfer.IsNoTradeItem(itemId) ? "item cannot be traded"
            : !IsSanePrice(price) ? "price out of range"
            : !IsSanePrice((long)price * count) ? "total price out of range"
            : count == 0 ? "count is zero"
            : itemData.Countable == 0 && count != 1 ? "not stackable but count is not 1"
            : null;

        refusal ??= session.WithLock(s =>
        {
            if (s.Trade.MerchantItems[dstPos] is { IsEmpty: false })
                return "stall slot already taken";

            if (s.Trade.MerchantItems.Any(item => item is { IsEmpty: false } && item.OriginalSlot == absPos))
                return "that bag slot is already listed";

            var held = s.Inventory[absPos];
            if (held.ItemId != itemId)
                return $"slot {absPos} holds {held.ItemId}, not {itemId}";
            if (held.Count < count)
                return $"slot {absPos} holds {held.Count}, fewer than {count}";
            if (!ItemTransfer.CanLeaveOwner(held, itemData))
                return $"item {itemId} ({held.State}) may not change hands";

            s.Trade.MerchantItems[dstPos] = new MerchantItem
            {
                ItemId = itemId,
                Durability = held.Durability,
                Count = count,
                Price = price,
                OriginalSlot = (byte)absPos,
                Flag = held.Flag,
                ExpiresAt = held.ExpiresAt,
            };
            return null;
        });

        if (refusal != null)
        {
            logger.LogDebug(
                "Merchant add refused for {Name}: {Reason} (item {ItemId} x{Count} at {SrcPos} -> stall {DstPos}, price {Price})",
                session.Name, refusal, itemId, count, srcPos, dstPos, price);

            await session.Client.SendPacket(
                MerchantPacketWriter.Refused(MerchantSubOpcode.ItemAdd, MerchantResult.CannotTrade));
            return;
        }

        await session.Client.SendPacket(MerchantPacketWriter.ItemAdded(
            MerchantSubOpcode.ItemAdd, itemId, count,
            session.Trade.MerchantItems[dstPos].Durability, price, srcPos, dstPos));
    }

    public async Task CancelItemAsync(UserSession session, Packet packet)
    {
        var slotIndex = packet.ReadByte();

        var cancelled = slotIndex < session.Trade.MerchantItems.Length
            && !session.Trade.IsMerchanting
            && session.WithLock(s =>
            {
                var merchantItem = s.Trade.MerchantItems[slotIndex];
                if (merchantItem == null || merchantItem.IsEmpty)
                    return false;

                s.Trade.MerchantItems[slotIndex] = new MerchantItem();
                return true;
            });

        if (!cancelled)
        {
            await session.Client.SendPacket(
                MerchantPacketWriter.Result(MerchantSubOpcode.ItemCancel, MerchantPacketWriter.Failed));
            return;
        }

        await session.Client.SendPacket(MerchantPacketWriter.ItemCancelled(
            MerchantSubOpcode.ItemCancel, slotIndex));
    }

    public async Task ListItemsAsync(UserSession session, Packet packet)
    {
        var targetId = packet.ReadInt();
        var merchant = sessionManager.GetByCharacterId(targetId);
        if (merchant == null
            || merchant.CharacterId == session.CharacterId
            || !merchant.Trade.IsSellingMerchant
            || !ExchangePacketConstants.IsWithinTradeRange(session, merchant))
        {
            session.Trade.MerchantTargetUserId = -1;
            return;
        }

        session.Trade.MerchantTargetUserId = merchant.CharacterId;
        logger.LogDebug("{Name} browsing merchant shop of {MerchantName}", session.Name, merchant.Name);

        var stall = merchant.WithLock(m => m.Trade.MerchantItems
            .Select(item => item != null && !item.IsEmpty
                ? new MerchantPacketWriter.StallItem(item.ItemId, item.Count, item.Durability, item.Price)
                : (MerchantPacketWriter.StallItem?)null)
            .ToList());

        await session.Client.SendPacket(MerchantPacketWriter.StallContents(
            MerchantSubOpcode.ItemList, targetId, stall));
    }

    private Task RefuseBuyAsync(UserSession session) =>
        session.Client.SendPacket(
            MerchantPacketWriter.Refused(MerchantSubOpcode.ItemBuy, MerchantResult.CannotTrade));

    public async Task BuyItemAsync(UserSession session, Packet packet)
    {
        var itemId = packet.ReadInt();
        var count = packet.ReadUShort();
        var merchantSlot = packet.ReadByte();
        _ = packet.ReadByte();

        var itemData = gameDataService.GetItem(itemId);
        if (merchantSlot >= session.Trade.MerchantItems.Length
            || count == 0
            || itemData == null
            || (itemData.Countable == 0 && count != 1)
            || ItemTransfer.IsInventoryLocked(session))
        {
            await RefuseBuyAsync(session);
            return;
        }

        var merchant = sessionManager.GetByCharacterId(session.Trade.MerchantTargetUserId);
        if (merchant == null
            || merchant.CharacterId == session.CharacterId
            || !merchant.Trade.IsSellingMerchant
            || !ExchangePacketConstants.IsWithinTradeRange(session, merchant))
        {
            session.Trade.MerchantTargetUserId = -1;
            await RefuseBuyAsync(session);
            return;
        }

        Sale? sale = null;
        UserSession.WithBoth(session, merchant, (buyer, seller) =>
            sale = TrySell(buyer, seller, merchantSlot, itemId, count, itemData));

        if (sale == null)
        {
            logger.LogDebug("Merchant buy refused for {Name}: {ItemId} x{Count} from stall slot {Slot} of {MerchantName}",
                session.Name, itemId, count, merchantSlot, merchant.Name);
            await RefuseBuyAsync(session);
            return;
        }

        logger.LogInformation("{BuyerName} bought item {ItemId} x{Count} from {SellerName} for {Cost} gold",
            session.Name, itemId, count, merchant.Name, sale.Cost);

        var buyResult = MerchantPacketWriter.ItemBought(
            MerchantSubOpcode.ItemBuy, itemId, sale.Remaining, merchantSlot,
            (byte)(sale.BuyerIndex - InventoryConstants.InventoryStart));
        await session.Client.SendPacket(buyResult);
        await userNotificationService.SendGoldLossAsync(session, sale.Cost);
        await userNotificationService.SendGoldGainAsync(merchant, sale.Cost);
        await userNotificationService.SendWeightChangeAsync(session);
        await userNotificationService.SendStackChangeAsync(
            merchant, (byte)sale.SellerIndex, sale.SellerSlot.ItemId, sale.SellerSlot.Count, sale.SellerSlot.Durability);

        var soldNotify = MerchantPacketWriter.ItemSold(
            MerchantSubOpcode.ItemPurchased, itemId, session.Name);
        await merchant.Client.SendPacket(soldNotify);

        if (sale.StallEmptied)
            await merchantLifecycleService.CloseAsync(merchant, MerchantInOut.StallClosed);
    }

    private Sale? TrySell(UserSession buyer, UserSession seller, byte merchantSlot, int itemId, ushort count, ItemData itemData)
    {
        if (!seller.Trade.IsSellingMerchant)
            return null;

        var listed = seller.Trade.MerchantItems[merchantSlot];
        if (listed == null || listed.IsEmpty || listed.ItemId != itemId || listed.Count < count)
            return null;

        var sellerSlot = seller.Inventory[listed.OriginalSlot];
        if (!listed.IsStillHeldIn(sellerSlot) || !ItemTransfer.CanLeaveOwner(sellerSlot, itemData))
            return null;

        var cost = (long)listed.Price * count;
        if (!IsSanePrice(cost) || cost > buyer.Money || !Coins.CanCredit(seller.Money, cost))
            return null;

        var stackable = itemData.Countable != 0;
        var buyerIndex = ItemTransfer.FindBagSlot(buyer.Inventory, ItemStack.Of(sellerSlot) with { Count = count }, stackable);
        if (buyerIndex == ItemTransfer.NoSlot)
            return null;

        ItemTransfer.Put(buyer.Inventory[buyerIndex], ItemTransfer.Take(sellerSlot, count));
        buyer.Money -= (int)cost;
        seller.Money += (int)cost;

        listed.Count -= count;
        if (listed.Count == 0)
            seller.Trade.MerchantItems[merchantSlot] = new MerchantItem();

        buyer.RecalculateStatsWithBuffs(gameDataService);
        seller.RecalculateStatsWithBuffs(gameDataService);

        return new Sale(
            (int)cost,
            listed.Count,
            buyerIndex,
            listed.OriginalSlot,
            ItemStack.Of(sellerSlot),
            seller.Trade.MerchantItems.All(entry => entry == null || entry.IsEmpty));
    }

    private sealed record Sale(int Cost, ushort Remaining, int BuyerIndex, int SellerIndex, ItemStack SellerSlot, bool StallEmptied);
}
