using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface ILootPacketCoordinator
{
    Task HandleItemDropAsync(IClient client, Packet packet);
    Task HandleBundleOpenAsync(IClient client, Packet packet);
    Task HandleItemGetAsync(IClient client, Packet packet);
}

public class LootPacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    ICollectionRaceService collectionRaceService,
    ILogger<LootPacketCoordinator> logger) : ILootPacketCoordinator
{
    private const int DropRequestBytes = sizeof(byte) + sizeof(int) + sizeof(ushort);
    private const int OpenRequestBytes = sizeof(int);
    private const int GetRequestBytes = sizeof(int) + sizeof(int) + sizeof(ushort);

    public async Task HandleItemDropAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < DropRequestBytes)
            return;

        var pos = packet.ReadByte();
        var itemId = packet.ReadInt();
        var count = packet.ReadUShort();
        var itemData = gameDataService.GetItem(itemId);
        if (pos >= InventoryConstants.HaveMax || count == 0 || itemData == null || ItemTransfer.IsInventoryLocked(session))
        {
            await session.Client.SendPacket(ItemDropPacketWriter.Refused(session.CharacterId));
            return;
        }

        var absPos = InventoryConstants.InventoryStart + pos;

        var dropped = session.WithLock(s =>
        {
            var slot = s.Inventory[absPos];
            if (slot.ItemId != itemId
                || slot.Count < count
                || (itemData.Countable == 0 && count != slot.Count)
                || !ItemTransfer.CanLeaveOwner(slot, itemData))
                return (ItemStack?)null;

            var stack = ItemTransfer.Take(slot, count);
            s.RecalculateStatsWithBuffs(gameDataService);
            return stack;
        });

        if (dropped == null)
        {
            await session.Client.SendPacket(ItemDropPacketWriter.Refused(session.CharacterId));
            return;
        }

        var bundle = sessionManager.Regions.CreateBundle(session.X, session.Z, session.Y);
        bundle.ZoneId = session.ZoneId;
        bundle.OwnerCharId = session.CharacterId;
        bundle.Items.Add(LootItem.Of(dropped.Value));
        logger.LogDebug("{Name} dropped item {ItemId} x{Count}", session.Name, itemId, count);

        await session.Client.SendPacket(ItemDropPacketWriter.Dropped(session.CharacterId, bundle.BundleId, hasItems: true));

        await userNotificationService.SendWeightChangeAsync(session);
    }

    public async Task HandleBundleOpenAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < OpenRequestBytes)
            return;

        var bundleId = packet.ReadInt();
        var bundle = sessionManager.Regions.GetBundle(bundleId);
        if (bundle == null)
        {
            await session.Client.SendPacket(new BundleOpenPacketWriter { BundleId = bundleId }.Build());
            return;
        }

        if (!bundle.IsWithinReachOf(session)
            || !bundle.CanLoot(session.CharacterId, session.IsInParty ? session.PartyIndex : -1, DateTime.UtcNow.Ticks))
        {
            await session.Client.SendPacket(BundleOpenPacketWriter.Refusal(bundleId));
            return;
        }

        var snapshot = bundle.SnapshotItems();

        var writer = new BundleOpenPacketWriter { BundleId = bundleId };
        for (var index = 0; index < snapshot.Count; index++)
            writer.Add(snapshot[index].ItemId, snapshot[index].Count);

        await session.Client.SendPacket(writer.Build());
    }

    public async Task HandleItemGetAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < GetRequestBytes)
            return;

        var bundleId = packet.ReadInt();
        var itemId = packet.ReadInt();
        var slotId = packet.ReadUShort();
        var bundle = sessionManager.Regions.GetBundle(bundleId);
        if (bundle == null
            || !bundle.IsWithinReachOf(session)
            || !bundle.CanLoot(session.CharacterId, session.IsInParty ? session.PartyIndex : -1, DateTime.UtcNow.Ticks)
            || ItemTransfer.IsInventoryLocked(session))
        {
            await SendItemGetErrorAsync(session);
            return;
        }

        if (itemId == InventoryConstants.ItemGold)
        {
            var looted = session.WithLock(s =>
            {
                if (!bundle.TryClaimSlot(slotId, itemId, item => Coins.CanCredit(s.Money, item.Count), out var claimed)
                    || claimed == null)
                    return ((ushort Count, int Money)?)null;

                s.Money = Coins.Credit(s.Money, claimed.Count);
                return (claimed.Count, s.Money);
            });

            if (looted == null)
            {
                await SendItemGetErrorAsync(session);
                return;
            }

            ForgetIfEmpty(bundle);
            await session.Client.SendPacket(ItemGetPacketWriter.LootedGold(
                bundleId, itemId, looted.Value.Count, looted.Value.Money, slotId));
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null)
        {
            await SendItemGetErrorAsync(session);
            return;
        }

        var stackable = itemData.Countable != 0;
        var picked = session.WithLock(s =>
        {
            var destination = ItemTransfer.NoSlot;
            var roomless = false;
            if (!bundle.TryClaimSlot(slotId, itemId, item =>
                {
                    destination = ItemTransfer.FindBagSlot(s.Inventory, item.Landed(itemData.Duration), stackable);
                    roomless = destination == ItemTransfer.NoSlot;
                    return !roomless;
                }, out var claimed) || claimed == null)
                return Pickup.Refused(roomless ? ItemGetPacketWriter.ResultNoSlot : ItemGetPacketWriter.ResultError);

            var landed = s.Inventory[destination];
            ItemTransfer.Put(landed, claimed.Landed(itemData.Duration));
            s.RecalculateStatsWithBuffs(gameDataService);
            return new Pickup(ItemGetPacketWriter.ResultSuccess, destination, landed.Count, s.Money);
        });

        if (picked.Result != ItemGetPacketWriter.ResultSuccess)
        {
            await session.Client.SendPacket(ItemGetPacketWriter.Failed(picked.Result));
            return;
        }

        ForgetIfEmpty(bundle);
        logger.LogDebug("{Name} picked up item {ItemId} into slot {Slot}", session.Name, itemId, picked.Index);

        var success = ItemGetPacketWriter.Looted(
            bundleId,
            (byte)(picked.Index - InventoryConstants.InventoryStart),
            itemId,
            picked.Count,
            picked.Money,
            slotId);
        await session.Client.SendPacket(success);

        await userNotificationService.SendWeightChangeAsync(session);
        await collectionRaceService.HandleItemGainAsync(session, itemId);
    }

    private void ForgetIfEmpty(LootBundle bundle)
    {
        if (bundle.IsEmpty())
            sessionManager.Regions.RemoveBundle(bundle.BundleId);
    }

    private static async Task SendItemGetErrorAsync(UserSession session)
    {
        var result = ItemGetPacketWriter.Failed(ItemGetPacketWriter.ResultError);
        await session.Client.SendPacket(result);
    }

    private readonly record struct Pickup(byte Result, int Index, ushort Count, int Money)
    {
        public static Pickup Refused(byte result) => new(result, ItemTransfer.NoSlot, 0, 0);
    }
}
