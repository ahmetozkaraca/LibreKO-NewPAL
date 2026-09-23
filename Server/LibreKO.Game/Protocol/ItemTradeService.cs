using LibreKO.Common.Enums;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;

using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IItemTradeService
{
    Task HandleRepairAsync(IClient client, Packet packet);
    Task HandleTradeAsync(IClient client, Packet packet);
}

public class ItemTradeService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    IViolationMonitor violationMonitor) : IItemTradeService
{
    private const byte TradeBuy = 1;
    private const byte TradeSell = 2;
    private const byte TradeMove = 3;
    private const byte RepairEquipped = 1;
    private const byte RepairInBag = 2;
    private const int NoSellingGroup = 0;
    private const int LoyaltyMerchantSellingGroup = 249000;
    private const int MaxTradeLines = InventoryConstants.HaveMax;
    private const int SaleTypeFull = 1;
    private const int SellPriceDivisor = 6;
    private const int ItemBaseIdStep = 1000;
    private const int PercentBase = 100;
    private const int RepairPriceOffset = 10;
    private const double RepairPriceScale = 10000.0;
    private const double RepairPriceExponent = 0.75;

    public async Task HandleRepairAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        if (session.Hp <= 0)
        {
            await SendRepairResponseAsync(session, ItemRepairResult.Failed);
            return;
        }

        var positionType = packet.ReadByte();
        var slot = packet.ReadByte();
        var npcId = packet.ReadInt();
        var itemId = packet.ReadInt();

        var npc = sessionManager.Regions.GetNpc(npcId);
        if (npc == null || !npc.IsAlive || npc.NpcType != NpcData.TypeRepairMerchant
            || !Reach.CanInteract(session, npc) || ItemTransfer.IsInventoryLocked(session))
        {
            await SendRepairResponseAsync(session, ItemRepairResult.Failed);
            return;
        }

        int absolutePosition;
        if (positionType == RepairEquipped && slot < InventoryConstants.SlotMax)
            absolutePosition = slot;
        else if (positionType == RepairInBag && slot < InventoryConstants.HaveMax)
            absolutePosition = InventoryConstants.InventoryStart + slot;
        else
        {
            await SendRepairResponseAsync(session, ItemRepairResult.Failed);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null || itemData.Duration <= 1)
        {
            await SendRepairResponseAsync(session, ItemRepairResult.Failed);
            return;
        }

        var outcome = session.WithLock(s =>
        {
            var item = s.Inventory[absolutePosition];
            if (item.ItemId != itemId)
                return (Success: false, Money: 0);

            var quantity = itemData.Duration - item.Durability;
            if (quantity <= 0)
                return (Success: false, Money: 0);

            var repairDiscount = gameDataService.GetPremiumProperty(s.PremiumType, PremiumPropertyType.RepairDiscount);
            var repairCost = RepairCost(itemData, quantity, repairDiscount);
            if (s.Money < repairCost)
                return (Success: false, Money: 0);

            s.Money -= (int)repairCost;
            item.Durability = itemData.Duration;
            return (Success: true, Money: s.Money);
        });

        if (!outcome.Success)
        {
            await SendRepairResponseAsync(session, ItemRepairResult.Failed);
            return;
        }

        await client.SendPacket(ItemRepairPacketWriter.Repaired(outcome.Money));
    }

    public async Task HandleTradeAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        if (session.Hp <= 0)
        {
            await SendItemTradeErrorAsync(session, ItemTradeRefusal.CannotTrade);
            return;
        }

        var type = packet.ReadByte();
        if (type is not (TradeBuy or TradeSell))
        {
            if (type == TradeMove)
                violationMonitor.Report(session, ViolationKind.InvalidRequest, "sent the shop bag swap the client never sends");
            await SendItemTradeErrorAsync(session, ItemTradeRefusal.CannotTrade);
            return;
        }

        var sellingGroup = packet.ReadInt();
        var npcId = packet.ReadInt();
        var lineCount = packet.ReadByte();
        if (lineCount == 0 || lineCount > MaxTradeLines)
        {
            await SendItemTradeErrorAsync(session, ItemTradeRefusal.CannotTrade);
            return;
        }

        var entries = new List<NpcTradeEntry>(lineCount);
        for (var index = 0; index < lineCount; index++)
        {
            var tradeItemId = packet.ReadInt();
            var tradePosition = packet.ReadByte();
            var count = packet.ReadUShort();
            byte line = 0;
            byte listIndex = 0;
            if (type == TradeBuy)
            {
                line = packet.ReadByte();
                listIndex = packet.ReadByte();
            }

            entries.Add(new NpcTradeEntry(tradeItemId, tradePosition, count, line, listIndex));
        }

        var npc = sessionManager.Regions.GetNpc(npcId);
        if (npc == null || !npc.IsAlive || !IsShopkeeper(npc, sellingGroup)
            || !Reach.CanInteract(session, npc) || ItemTransfer.IsInventoryLocked(session))
        {
            await SendItemTradeErrorAsync(session, ItemTradeRefusal.CannotTrade);
            return;
        }

        var outcome = type == TradeBuy ? Buy(session, sellingGroup, entries) : Sell(session, entries);
        if (outcome.Refusal != ItemTradeRefusal.None)
        {
            await SendItemTradeErrorAsync(session, outcome.Refusal);
            return;
        }

        await userNotificationService.SendWeightChangeAsync(session);
        await client.SendPacket(ItemTradePacketWriter.Traded(outcome.Balance, outcome.Price, outcome.LoyaltyGroup));
    }

    private TradeOutcome Buy(UserSession session, int sellingGroup, List<NpcTradeEntry> entries)
    {
        var loyaltyMerchant = sellingGroup == LoyaltyMerchantSellingGroup;
        var lines = new List<(NpcTradeEntry Entry, ItemData Data, long UnitPrice)>(entries.Count);
        var positions = new HashSet<byte>();
        foreach (var entry in entries)
        {
            var listed = gameDataService.GetSellingGroupItem(sellingGroup, entry.Line, entry.Index);
            if (listed == null || listed.ItemId != entry.ItemId)
            {
                violationMonitor.Report(session, ViolationKind.InvalidRequest,
                    $"asked selling group {sellingGroup} for item {entry.ItemId} at line {entry.Line} index {entry.Index}");
                return TradeOutcome.Refused(ItemTradeRefusal.CannotTrade);
            }

            var itemData = gameDataService.GetItem(entry.ItemId);
            var unitPrice = itemData == null ? 0 : UnitBuyPrice(itemData, loyaltyMerchant);
            if (itemData == null
                || unitPrice <= 0
                || entry.Position >= InventoryConstants.HaveMax
                || !positions.Add(entry.Position)
                || entry.Count == 0
                || entry.Count > InventoryConstants.MaxStackCount
                || (itemData.Countable == 0 && entry.Count != 1))
                return TradeOutcome.Refused(ItemTradeRefusal.CannotTrade);

            lines.Add((entry, itemData, unitPrice));
        }

        return session.WithLock(s =>
        {
            long total = 0;
            long weight = s.Stats.ItemWeight;
            foreach (var (entry, itemData, unitPrice) in lines)
            {
                if (!ItemTransfer.CanPut(BagSlot(s, entry.Position), Purchased(entry, itemData), itemData.Countable != 0))
                    return TradeOutcome.Refused(ItemTradeRefusal.InventoryFull);

                total += unitPrice * entry.Count;
                weight += (long)itemData.Weight * entry.Count;
            }

            if (total > (loyaltyMerchant ? s.Loyalty : s.Money))
                return TradeOutcome.Refused(ItemTradeRefusal.NotEnoughCoins);

            if (weight > s.Stats.MaxWeight)
                return TradeOutcome.Refused(ItemTradeRefusal.InventoryFull);

            foreach (var (entry, itemData, _) in lines)
                ItemTransfer.Put(BagSlot(s, entry.Position), Purchased(entry, itemData));

            if (loyaltyMerchant)
                s.Loyalty -= (int)total;
            else
                s.Money -= (int)total;

            s.RecalculateStatsWithBuffs(gameDataService);
            return new TradeOutcome(
                ItemTradeRefusal.None,
                loyaltyMerchant ? s.Loyalty : s.Money,
                (int)total,
                loyaltyMerchant ? lines[^1].Data.SellingGroup : null);
        });
    }

    private TradeOutcome Sell(UserSession session, List<NpcTradeEntry> entries)
    {
        var lines = new List<(NpcTradeEntry Entry, ItemData Data)>(entries.Count);
        var positions = new HashSet<byte>();
        foreach (var entry in entries)
        {
            var itemData = gameDataService.GetItem(entry.ItemId);
            if (itemData == null
                || entry.Position >= InventoryConstants.HaveMax
                || !positions.Add(entry.Position)
                || entry.Count == 0)
                return TradeOutcome.Refused(ItemTradeRefusal.CannotTrade);

            lines.Add((entry, itemData));
        }

        return session.WithLock(s =>
        {
            var sellBonus = gameDataService.GetPremiumProperty(s.PremiumType, PremiumPropertyType.ItemSell);
            long total = 0;
            foreach (var (entry, itemData) in lines)
            {
                var slot = BagSlot(s, entry.Position);
                if (slot.ItemId != entry.ItemId
                    || entry.Count > slot.Count
                    || (itemData.Countable == 0 && entry.Count != slot.Count)
                    || !ItemTransfer.CanLeaveOwner(slot, itemData))
                    return TradeOutcome.Refused(ItemTradeRefusal.CannotTrade);

                total += SalePrice(itemData, entry.Count, sellBonus);
            }

            if (!Coins.CanCredit(s.Money, total))
                return TradeOutcome.Refused(ItemTradeRefusal.CannotTrade);

            foreach (var (entry, _) in lines)
                ItemTransfer.Take(BagSlot(s, entry.Position), entry.Count);

            s.Money += (int)total;
            s.RecalculateStatsWithBuffs(gameDataService);
            return new TradeOutcome(ItemTradeRefusal.None, s.Money, (int)total, null);
        });
    }

    private bool IsShopkeeper(NpcInstance npc, int sellingGroup) =>
        npc.SellingGroup != NoSellingGroup
        && npc.SellingGroup == sellingGroup
        && gameDataService.HasSellingGroup(sellingGroup);

    private static long UnitBuyPrice(ItemData itemData, bool loyaltyMerchant) =>
        loyaltyMerchant ? itemData.NpBuyPrice : itemData.BuyPrice;

    private static ItemStack Purchased(NpcTradeEntry entry, ItemData itemData) =>
        ItemStack.Fresh(entry.ItemId, itemData.Duration, entry.Count);

    private static ItemSlot BagSlot(UserSession session, byte position) =>
        session.Inventory[InventoryConstants.InventoryStart + position];

    private long SalePrice(ItemData itemData, ushort count, int sellBonusPercent)
    {
        var fullPrice = SaleTypeOf(itemData) == SaleTypeFull;
        long unitPrice = fullPrice ? itemData.BuyPrice : itemData.BuyPrice / SellPriceDivisor;
        if (unitPrice < 1)
            return 0;

        var price = unitPrice * count;
        return !fullPrice && sellBonusPercent > 0 ? price * (PercentBase + sellBonusPercent) / PercentBase : price;
    }

    private int SaleTypeOf(ItemData itemData)
    {
        var baseItem = gameDataService.GetItem(itemData.Num / ItemBaseIdStep * ItemBaseIdStep);
        return baseItem?.SellPrice ?? itemData.SellPrice;
    }

    private static long RepairCost(ItemData itemData, int missingDurability, int discountPercent)
    {
        var cost = ((itemData.BuyPrice - RepairPriceOffset) / RepairPriceScale
                + Math.Pow(itemData.BuyPrice, RepairPriceExponent))
            * missingDurability / itemData.Duration;
        var charged = (long)Math.Clamp(cost, 0, ExchangePacketConstants.CoinMax);
        return discountPercent > 0 ? charged * (PercentBase - discountPercent) / PercentBase : charged;
    }

    private static async Task SendRepairResponseAsync(UserSession session, ItemRepairResult result)
    {
        await session.Client.SendPacket(
            ItemRepairPacketWriter.Completed(result, session.Money));
    }

    private static async Task SendItemTradeErrorAsync(UserSession session, ItemTradeRefusal reason)
    {
        await session.Client.SendPacket(ItemTradePacketWriter.Failed(reason));
    }

    private readonly record struct NpcTradeEntry(int ItemId, byte Position, ushort Count, byte Line, byte Index);

    private readonly record struct TradeOutcome(ItemTradeRefusal Refusal, int Balance, int Price, byte? LoyaltyGroup)
    {
        public static TradeOutcome Refused(ItemTradeRefusal refusal) => new(refusal, 0, 0, null);
    }
}
