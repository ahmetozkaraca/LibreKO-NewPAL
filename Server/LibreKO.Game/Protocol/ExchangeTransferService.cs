using LibreKO.Common.Enums;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IExchangeTransferService
{
    Task AddAsync(UserSession session, Packet packet);
    Task DecideAsync(UserSession session);
}

public class ExchangeTransferService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    IExchangeLifecycleService exchangeLifecycleService,
    ILogger<ExchangeTransferService> logger) : IExchangeTransferService
{
    public async Task AddAsync(UserSession session, Packet packet)
    {
        if (!session.Trade.IsTrading)
            return;

        var target = sessionManager.GetByCharacterId(session.Trade.ExchangeUser);
        if (target == null || target.Hp <= 0 || session.Hp <= 0
            || !ExchangePacketConstants.IsWithinTradeRange(session, target))
        {
            await exchangeLifecycleService.CancelAsync(session);
            return;
        }

        var pos = packet.ReadByte();
        var itemId = packet.ReadInt();
        var count = packet.ReadInt();

        var isGold = itemId == InventoryConstants.ItemGold;
        var itemData = isGold ? null : gameDataService.GetItem(itemId);
        if (count <= 0
            || (!isGold && (itemData == null || pos >= InventoryConstants.HaveMax || count > InventoryConstants.MaxStackCount)))
        {
            await SendAddFailAsync(session);
            return;
        }

        var partners = true;
        ExchangeItem? offered = null;
        UserSession.WithBoth(session, target, (me, partner) =>
        {
            partners = ExchangePacketConstants.ArePartners(me, partner);
            if (!partners || me.Trade.ExchangeOk || ExchangePacketConstants.IsBusyElsewhere(me))
                return;

            offered = isGold ? EscrowGold(me, count) : EscrowItem(me, pos, itemId, (ushort)count, itemData!);
        });

        if (!partners)
        {
            await exchangeLifecycleService.CancelAsync(session);
            return;
        }

        if (offered == null)
        {
            await SendAddFailAsync(session);
            return;
        }

        await session.Client.SendPacket(ExchangePacketWriter.Result(
            ExchangePacketConstants.ExchangeAdd, ExchangePacketWriter.Succeeded));

        await target.Client.SendPacket(ExchangePacketWriter.ItemOffered(
            ExchangePacketConstants.ExchangeOtherAdd, itemId, count, offered.Durability));
    }

    public async Task DecideAsync(UserSession session)
    {
        if (!session.Trade.IsTrading)
            return;

        var target = sessionManager.GetByCharacterId(session.Trade.ExchangeUser);
        if (target == null || target.Hp <= 0 || session.Hp <= 0 || !ExchangePacketConstants.IsWithinTradeRange(session, target))
        {
            await exchangeLifecycleService.CancelAsync(session);
            return;
        }

        var outcome = DecideOutcome.NotPartners;
        Delivered? toSession = null;
        Delivered? toTarget = null;

        UserSession.WithBoth(session, target, (sa, sb) =>
        {
            if (!ExchangePacketConstants.ArePartners(sa, sb))
                return;

            if (!sb.Trade.ExchangeOk)
            {
                sa.Trade.ExchangeOk = true;
                outcome = DecideOutcome.Wait;
                return;
            }

            var forSession = PlanDelivery(sa, sb);
            var forTarget = PlanDelivery(sb, sa);
            if (forSession == null || forTarget == null)
            {
                LogUnreturned(sa, sa.InitExchange(false));
                LogUnreturned(sb, sb.InitExchange(false));
                outcome = DecideOutcome.Fail;
                return;
            }

            toSession = Deliver(sa, forSession);
            toTarget = Deliver(sb, forTarget);

            sa.CompleteExchange();
            sb.CompleteExchange();
            sa.RecalculateStatsWithBuffs(gameDataService);
            sb.RecalculateStatsWithBuffs(gameDataService);
            outcome = DecideOutcome.Done;
        });

        switch (outcome)
        {
            case DecideOutcome.NotPartners:
                await exchangeLifecycleService.CancelAsync(session);
                break;

            case DecideOutcome.Wait:
                await target.Client.SendPacket(
                    ExchangePacketWriter.Sub(ExchangePacketConstants.ExchangeOtherDecide));
                break;

            case DecideOutcome.Fail:
                var fail = ExchangePacketWriter.Result(
                    ExchangePacketConstants.ExchangeDone, ExchangePacketWriter.Failed);
                await session.Client.SendPacket(fail);
                await target.Client.SendPacket(fail);
                break;

            case DecideOutcome.Done:
                logger.LogInformation("Exchange completed between {Name} ({ItemCount} items) and {TargetName} ({TargetItemCount} items)",
                    session.Name, toTarget!.Items.Count, target.Name, toSession!.Items.Count);

                await session.Client.SendPacket(ExchangePacketWriter.Completed(
                    ExchangePacketConstants.ExchangeDone, toSession.Money, toSession.Items));
                await target.Client.SendPacket(ExchangePacketWriter.Completed(
                    ExchangePacketConstants.ExchangeDone, toTarget.Money, toTarget.Items));

                await userNotificationService.SendWeightChangeAsync(session);
                await userNotificationService.SendWeightChangeAsync(target);
                break;
        }
    }

    private enum DecideOutcome { NotPartners, Wait, Fail, Done }

    private sealed record Delivery(List<(ExchangeItem Item, int Index)> Placements, long Gold);

    private sealed record Delivered(int Money, List<ExchangePacketWriter.TransferredItem> Items);

    private static ExchangeItem? EscrowGold(UserSession session, int count)
    {
        if (count > session.Money)
            return null;

        session.Money -= count;
        var gold = session.Trade.ExchangeItemList.Find(entry => entry.IsGold);
        if (gold == null)
            session.Trade.ExchangeItemList.Add(gold = new ExchangeItem { ItemId = InventoryConstants.ItemGold });

        gold.Count += count;
        return gold;
    }

    private static ExchangeItem? EscrowItem(UserSession session, byte position, int itemId, ushort count, ItemData itemData)
    {
        var index = InventoryConstants.InventoryStart + position;
        var slot = session.Inventory[index];
        var stackable = itemData.Countable != 0;
        var offeredItems = session.Trade.ExchangeItemList.Count(entry => !entry.IsGold);
        if (slot.ItemId != itemId
            || count > slot.Count
            || (!stackable && count != slot.Count)
            || !ItemTransfer.CanLeaveOwner(slot, itemData)
            || offeredItems >= ExchangePacketConstants.MaxOfferedItems)
            return null;

        var escrowed = ExchangeItem.Escrowed(ItemTransfer.Take(slot, count), (byte)index, stackable);
        session.Trade.ExchangeItemList.Add(escrowed);
        return escrowed;
    }

    private Delivery? PlanDelivery(UserSession receiver, UserSession giver)
    {
        var bag = ItemTransfer.BagSnapshot(receiver.Inventory);
        var placements = new List<(ExchangeItem Item, int Index)>();
        long gold = 0;
        long weight = receiver.Stats.ItemWeight;

        foreach (var item in giver.Trade.ExchangeItemList)
        {
            if (item.IsGold)
            {
                gold += item.Count;
                continue;
            }

            var itemData = gameDataService.GetItem(item.ItemId);
            if (itemData == null)
                return null;

            weight += (long)itemData.Weight * item.Count;
            var position = ItemTransfer.FindSlot(bag, item.Stack, item.Stackable);
            if (position == ItemTransfer.NoSlot)
                return null;

            bag[position] = ItemTransfer.Merge(bag[position], item.Stack);
            placements.Add((item, InventoryConstants.InventoryStart + position));
        }

        if ((placements.Count > 0 && weight > receiver.Stats.MaxWeight) || !Coins.CanCredit(receiver.Money, gold))
            return null;

        return new Delivery(placements, gold);
    }

    private static Delivered Deliver(UserSession receiver, Delivery delivery)
    {
        foreach (var (item, index) in delivery.Placements)
        {
            ItemTransfer.Put(receiver.Inventory[index], item.Stack);
            item.DstPos = (byte)(index - InventoryConstants.InventoryStart);
        }

        receiver.Money = Coins.Credit(receiver.Money, delivery.Gold);

        var transferred = delivery.Placements
            .Select(placement =>
            {
                var slot = receiver.Inventory[placement.Index];
                return new ExchangePacketWriter.TransferredItem(
                    placement.Item.DstPos, slot.ItemId, slot.Count, slot.Durability);
            })
            .ToList();

        return new Delivered(receiver.Money, transferred);
    }

    private void LogUnreturned(UserSession session, IReadOnlyList<ExchangeItem> unreturned)
    {
        if (unreturned.Count > 0)
            logger.LogError("Could not return {Count} escrowed items to {Name}", unreturned.Count, session.Name);
    }

    internal static bool IsTradableItem(ItemData? itemData, int itemId, byte pos)
        => itemData != null
        && pos < InventoryConstants.HaveMax
        && itemData.Race != ExchangePacketConstants.RaceUntradeable
        && !ItemTransfer.IsNoTradeItem(itemId);

    private static async Task SendAddFailAsync(UserSession session)
    {
        await session.Client.SendPacket(ExchangePacketWriter.Result(
            ExchangePacketConstants.ExchangeAdd, ExchangePacketWriter.Failed));
    }
}
