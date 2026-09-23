using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

using static LibreKO.Game.Protocol.MerchantPacketConstants;

namespace LibreKO.Game.Protocol;

public interface IMerchantBuyingService
{
    Task OpenAsync(UserSession session);
    Task InsertAsync(UserSession session, Packet packet);
    Task ListAsync(UserSession session, Packet packet);
    Task BuyAsync(UserSession session, Packet packet);
    Task CloseAsync(UserSession session, bool broadcast);
}

public class MerchantBuyingService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    ILogger<MerchantBuyingService> logger) : IMerchantBuyingService
{
    public async Task OpenAsync(UserSession session)
    {
        var result =
            session.Hp <= 0 ? BuyingMerchantResult.WhileDead
            : session.Trade.IsTrading ? BuyingMerchantResult.WhileMerchanting
            : session.Trade.IsMerchanting || session.Trade.IsMerchantPreparing ? BuyingMerchantResult.WhileMerchanting
            : !IsBuyingMerchantZone(session) ? BuyingMerchantResult.NotAllowedHere
            : session.Level < MerchantPacketConstants.MinimumBuyingMerchantLevel ? BuyingMerchantResult.UnderLevelled
            : BuyingMerchantResult.Accepted;

        if (result == BuyingMerchantResult.Accepted)
        {
            session.WithLock(s =>
            {
                s.Trade.IsBuyingMerchantPreparing = true;
                ClearWanted(s);
            });
        }
        else
        {
            logger.LogDebug("Buying merchant open refused for {Name}: {Result}", session.Name, result);
        }

        await session.Client.SendPacket(MerchantPacketWriter.BuyOpenResult(result));
    }

    public async Task InsertAsync(UserSession session, Packet packet)
    {
        var wantedCount = packet.ReadByte();

        if (session.Hp <= 0
            || session.Trade.IsTrading
            || session.Trade.IsMerchanting
            || !session.Trade.IsBuyingMerchantPreparing
            || wantedCount == 0
            || wantedCount > MerchantPacketConstants.StallSlots)
        {
            await RefuseInsertAsync(session, BuyingMerchantResult.WrongStallSetup);
            return;
        }

        var wanted = new MerchantItem[MerchantPacketConstants.StallSlots];
        long totalCost = 0;

        for (var i = 0; i < wantedCount; i++)
        {
            var itemId = packet.ReadInt();
            var count = packet.ReadUShort();
            var price = packet.ReadInt();

            var itemData = gameDataService.GetItem(itemId);
            if (itemData == null || count == 0 || !IsSanePrice(price))
            {
                await RefuseInsertAsync(session, BuyingMerchantResult.WrongItemSetup);
                return;
            }

            var stack = itemData.Countable != 0 ? count : (ushort)1;
            totalCost += (long)price * stack;

            wanted[i] = new MerchantItem
            {
                ItemId = itemId,
                Count = stack,
                Price = price,
                Durability = itemData.Duration,
            };
        }

        var opened = session.WithLock(s =>
        {
            if (totalCost > s.Money)
                return false;

            for (var i = 0; i < wanted.Length; i++)
                s.Trade.BuyMerchantItems[i] = wanted[i] ?? new MerchantItem();

            s.Trade.MerchantState = MerchantMode.Buying;
            s.Trade.IsBuyingMerchantPreparing = true;
            s.Trade.MerchantTargetUserId = -1;
            return true;
        });

        if (!opened)
        {
            await RefuseInsertAsync(session, BuyingMerchantResult.SellerFundsTooLow);
            return;
        }

        logger.LogDebug("{Name} opened a buying stall wanting {Count} item kinds for up to {Cost} gold",
            session.Name, wantedCount, totalCost);

        await session.Client.SendPacket(
            MerchantPacketWriter.BuyInsertResult(BuyingMerchantResult.Accepted));

        await sessionManager.Regions.SendToRegion(
            session, MerchantPacketWriter.BuyingStallInserted(session.CharacterId, WantedItemIds(session)));
    }

    public async Task ListAsync(UserSession session, Packet packet)
    {
        var merchantId = packet.ReadInt();
        var merchant = sessionManager.GetByCharacterId(merchantId);

        if (merchant == null
            || merchant.CharacterId == session.CharacterId
            || !merchant.Trade.IsBuyingMerchant
            || session.Trade.IsMerchanting
            || session.Trade.IsTrading
            || !ExchangePacketConstants.IsWithinTradeRange(session, merchant))
        {
            session.Trade.MerchantTargetUserId = -1;
            return;
        }

        session.Trade.MerchantTargetUserId = merchant.CharacterId;

        var wanted = merchant.WithLock(m => m.Trade.BuyMerchantItems
            .Select(item => item != null && !item.IsEmpty
                ? new MerchantPacketWriter.StallItem(item.ItemId, item.Count, item.Durability, item.Price)
                : (MerchantPacketWriter.StallItem?)null)
            .ToList());

        await session.Client.SendPacket(MerchantPacketWriter.WantedList(merchant.CharacterId, wanted));
    }

    public async Task BuyAsync(UserSession session, Packet packet)
    {
        var sellerSlot = packet.ReadByte();
        var wantedSlot = packet.ReadByte();
        var stackSize = packet.ReadUShort();

        var merchant = sessionManager.GetByCharacterId(session.Trade.MerchantTargetUserId);
        var refusal = merchant == null || merchant.CharacterId == session.CharacterId
            ? BuyingMerchantResult.WrongStallSetup
            : BuyingMerchantResult.Accepted;

        Purchase? purchase = null;
        if (refusal == BuyingMerchantResult.Accepted)
        {
            UserSession.WithBoth(session, merchant!, (seller, owner) =>
            {
                refusal = Refusal(seller, owner, sellerSlot, wantedSlot, stackSize);
                if (refusal == BuyingMerchantResult.Accepted)
                    purchase = TryBuy(seller, owner, sellerSlot, wantedSlot, stackSize, out refusal);
            });
        }

        if (purchase == null)
        {
            logger.LogDebug("Sale to buying merchant refused for {Name}: {Result}", session.Name, refusal);
            await session.Client.SendPacket(MerchantPacketWriter.BuyPurchaseResult(refusal));
            return;
        }

        logger.LogInformation("{SellerName} sold item {ItemId} x{Count} to buying merchant {MerchantName} for {Price} gold",
            session.Name, purchase.Received.ItemId, stackSize, merchant!.Name, purchase.Price);

        await userNotificationService.SendStackChangeAsync(
            session, sellerSlot, purchase.SellerLeft.ItemId, purchase.SellerLeft.Count, purchase.SellerLeft.Durability);
        await userNotificationService.SendStackChangeAsync(
            merchant, (byte)(purchase.OwnerIndex - InventoryConstants.InventoryStart),
            purchase.Received.ItemId, purchase.Received.Count, purchase.Received.Durability, purchase.ReceivedIntoEmptySlot);

        await session.Client.SendPacket(MerchantPacketWriter.WantedItemSold(
            wantedSlot, purchase.WantedLeft, sellerSlot, purchase.SellerLeft.Count));
        await session.Client.SendPacket(
            MerchantPacketWriter.BuyPurchaseResult(BuyingMerchantResult.Accepted));

        await merchant.Client.SendPacket(MerchantPacketWriter.WantedItemBought(
            wantedSlot, purchase.WantedLeft, session.Name));

        await userNotificationService.SendGoldGainAsync(session, purchase.Price);
        await userNotificationService.SendGoldLossAsync(merchant, purchase.Price);
        await userNotificationService.SendWeightChangeAsync(session);
        await userNotificationService.SendWeightChangeAsync(merchant);

        if (purchase.StallEmptied)
            await CloseAsync(merchant, broadcast: true);
        else
            await sessionManager.Regions.SendToRegion(
                merchant, MerchantPacketWriter.BuyingStallInserted(merchant.CharacterId, WantedItemIds(merchant)));
    }

    public async Task CloseAsync(UserSession session, bool broadcast)
    {
        var closed = session.WithLock(s =>
        {
            if (!s.Trade.IsBuyingMerchant && !s.Trade.IsBuyingMerchantPreparing)
                return false;

            ClearWanted(s);
            s.Trade.IsBuyingMerchantPreparing = false;
            if (s.Trade.IsBuyingMerchant)
                s.Trade.MerchantState = MerchantMode.None;
            s.Trade.MerchantTargetUserId = -1;
            return true;
        });

        if (!closed || !broadcast)
            return;

        await sessionManager.Regions.SendToRegion(
            session, MerchantPacketWriter.BuyingStallClosed(session.CharacterId), excludeSender: false);
    }

    private BuyingMerchantResult Refusal(
        UserSession session, UserSession merchant, byte sellerSlot, byte wantedSlot, ushort stackSize)
    {
        if (!merchant.Trade.IsBuyingMerchant)
            return BuyingMerchantResult.WrongStallSetup;

        if (session.Hp <= 0)
            return BuyingMerchantResult.WhileDead;

        if (ItemTransfer.IsInventoryLocked(session))
            return BuyingMerchantResult.WhileMerchanting;

        if (!ExchangePacketConstants.IsWithinTradeRange(session, merchant))
            return BuyingMerchantResult.NotAllowedHere;

        if (sellerSlot >= InventoryConstants.HaveMax
            || wantedSlot >= MerchantPacketConstants.StallSlots
            || stackSize == 0)
            return BuyingMerchantResult.WrongPurchaseCount;

        var wantedItem = merchant.Trade.BuyMerchantItems[wantedSlot];
        if (wantedItem == null || wantedItem.IsEmpty || wantedItem.Count < stackSize)
            return BuyingMerchantResult.NoSuchItemWanted;

        var sellerItem = session.Inventory[InventoryConstants.InventoryStart + sellerSlot];
        if (sellerItem.IsEmpty || sellerItem.ItemId != wantedItem.ItemId || sellerItem.Count < stackSize)
            return BuyingMerchantResult.NoSuchItemWanted;

        var itemData = gameDataService.GetItem(wantedItem.ItemId);
        if (itemData == null)
            return BuyingMerchantResult.WrongItemSetup;

        if (!ItemTransfer.CanLeaveOwner(sellerItem, itemData))
            return BuyingMerchantResult.ItemNotSellable;

        if (itemData.Countable == 0 && stackSize != sellerItem.Count)
            return BuyingMerchantResult.WrongPurchaseCount;

        if (sellerItem.Durability < wantedItem.Durability)
            return BuyingMerchantResult.NeedsRepair;

        var price = (long)wantedItem.Price * stackSize;
        if (!IsSanePrice(price) || price > merchant.Money)
            return BuyingMerchantResult.BuyerFundsTooLow;

        if (!Coins.CanCredit(session.Money, price))
            return BuyingMerchantResult.OverMaxLimit;

        return BuyingMerchantResult.Accepted;
    }

    private Purchase? TryBuy(
        UserSession seller, UserSession owner, byte sellerSlot, byte wantedSlot, ushort stackSize,
        out BuyingMerchantResult refusal)
    {
        var wantedItem = owner.Trade.BuyMerchantItems[wantedSlot];
        var sellerItem = seller.Inventory[InventoryConstants.InventoryStart + sellerSlot];
        var stackable = gameDataService.GetItem(wantedItem.ItemId)?.Countable != 0;
        var ownerIndex = ItemTransfer.FindBagSlot(owner.Inventory, ItemStack.Of(sellerItem) with { Count = stackSize }, stackable);
        if (ownerIndex == ItemTransfer.NoSlot)
        {
            refusal = BuyingMerchantResult.InventoryFull;
            return null;
        }

        var price = (int)((long)wantedItem.Price * stackSize);
        var receivedIntoEmptySlot = owner.Inventory[ownerIndex].IsEmpty;
        ItemTransfer.Put(owner.Inventory[ownerIndex], ItemTransfer.Take(sellerItem, stackSize));
        owner.Money -= price;
        seller.Money += price;

        wantedItem.Count -= stackSize;
        if (wantedItem.Count == 0)
            owner.Trade.BuyMerchantItems[wantedSlot] = new MerchantItem();

        seller.RecalculateStatsWithBuffs(gameDataService);
        owner.RecalculateStatsWithBuffs(gameDataService);

        refusal = BuyingMerchantResult.Accepted;
        return new Purchase(
            price,
            ItemStack.Of(sellerItem),
            ItemStack.Of(owner.Inventory[ownerIndex]),
            ownerIndex,
            receivedIntoEmptySlot,
            wantedItem.Count,
            owner.Trade.BuyMerchantItems.All(entry => entry == null || entry.IsEmpty));
    }

    private sealed record Purchase(
        int Price,
        ItemStack SellerLeft,
        ItemStack Received,
        int OwnerIndex,
        bool ReceivedIntoEmptySlot,
        ushort WantedLeft,
        bool StallEmptied);

    private async Task RefuseInsertAsync(UserSession session, BuyingMerchantResult result)
    {
        logger.LogDebug("Buying merchant insert refused for {Name}: {Result}", session.Name, result);
        await session.Client.SendPacket(MerchantPacketWriter.BuyInsertResult(result));
        await CloseAsync(session, broadcast: false);
    }

    private static void ClearWanted(UserSession session)
    {
        for (var i = 0; i < session.Trade.BuyMerchantItems.Length; i++)
            session.Trade.BuyMerchantItems[i] = new MerchantItem();
    }

    private static List<int> WantedItemIds(UserSession session) =>
        session.Trade.BuyMerchantItems
            .Select(item => item != null && !item.IsEmpty ? item.ItemId : 0)
            .ToList();

    private static bool IsBuyingMerchantZone(UserSession session) =>
        (ZoneId)session.ZoneId is ZoneId.Moradon or ZoneId.Moradon2 or ZoneId.Moradon3
            or ZoneId.Moradon4 or ZoneId.Moradon5;
}
