using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IWarehousePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class WarehousePacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    ILogger<WarehousePacketCoordinator> logger) : IWarehousePacketCoordinator
{
    private const int WarehousePageSize = 24;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var sub = (WarehouseSubOpcode)packet.ReadByte();
        logger.LogDebug("Warehouse operation {Sub} by {Name}", sub, session.Name);

        switch (sub)
        {
            case WarehouseSubOpcode.Open:
                await OpenAsync(session);
                break;

            case WarehouseSubOpcode.Input:
                await InputAsync(session, packet);
                break;

            case WarehouseSubOpcode.Output:
                await OutputAsync(session, packet);
                break;

            case WarehouseSubOpcode.Move:
                await MoveAsync(session, packet);
                break;

            case WarehouseSubOpcode.InventoryMove:
                await InventoryMoveAsync(session, packet);
                break;
        }
    }

    private static async Task OpenAsync(UserSession session)
    {
        var slots = new List<ItemSlot>(UserSession.WarehouseMax);
        for (var i = 0; i < UserSession.WarehouseMax; i++)
            slots.Add(session.Warehouse[i]);

        await session.Client.SendPacket(WarehousePacketWriter.Contents(
            WarehouseSubOpcode.Open, session.WarehouseMoney, slots));
    }

    private async Task InputAsync(UserSession session, Packet packet)
    {
        var npcId = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        if (!CanUseWarehouse(session, npcId))
        {
            await SendResultAsync(session, WarehouseSubOpcode.Input, false);
            return;
        }

        if (itemId == InventoryConstants.ItemGold)
        {
            var goldOk = session.WithLock(s =>
            {
                if (count <= 0 || count > s.Money || !Coins.CanCredit(s.WarehouseMoney, count))
                    return false;
                s.Money -= count;
                s.WarehouseMoney += count;
                return true;
            });
            await SendResultAsync(session, WarehouseSubOpcode.Input, goldOk);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        var realDst = page * WarehousePageSize + dstPos;
        if (itemData == null
            || srcPos >= InventoryConstants.HaveMax
            || dstPos >= WarehousePageSize
            || realDst >= UserSession.WarehouseMax
            || !IsTransferableCount(itemData, count))
        {
            await SendResultAsync(session, WarehouseSubOpcode.Input, false);
            return;
        }

        var absSrc = InventoryConstants.InventoryStart + srcPos;
        var success = session.WithLock(s =>
        {
            var source = s.Inventory[absSrc];
            if (!Holds(source, itemId, itemData, count) || !ItemTransfer.CanLeaveOwner(source, itemData))
                return false;

            if (!Transfer(source, s.Warehouse[realDst], itemData, count))
                return false;

            s.RecalculateStatsWithBuffs(gameDataService);
            return true;
        });

        await SendResultAsync(session, WarehouseSubOpcode.Input, success);
        if (success)
            await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task OutputAsync(UserSession session, Packet packet)
    {
        var npcId = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        if (!CanUseWarehouse(session, npcId))
        {
            await SendResultAsync(session, WarehouseSubOpcode.Output, false);
            return;
        }

        if (itemId == InventoryConstants.ItemGold)
        {
            var goldOk = session.WithLock(s =>
            {
                if (count <= 0 || count > s.WarehouseMoney || !Coins.CanCredit(s.Money, count))
                    return false;
                s.WarehouseMoney -= count;
                s.Money += count;
                return true;
            });
            await SendResultAsync(session, WarehouseSubOpcode.Output, goldOk);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        var realSrc = page * WarehousePageSize + srcPos;
        if (itemData == null
            || srcPos >= WarehousePageSize
            || realSrc >= UserSession.WarehouseMax
            || dstPos >= InventoryConstants.HaveMax
            || !IsTransferableCount(itemData, count))
        {
            await SendResultAsync(session, WarehouseSubOpcode.Output, false);
            return;
        }

        var absDst = InventoryConstants.InventoryStart + dstPos;
        var success = session.WithLock(s =>
        {
            var source = s.Warehouse[realSrc];
            if (!Holds(source, itemId, itemData, count) || !Transfer(source, s.Inventory[absDst], itemData, count))
                return false;

            s.RecalculateStatsWithBuffs(gameDataService);
            return true;
        });

        await SendResultAsync(session, WarehouseSubOpcode.Output, success);
        if (success)
            await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task MoveAsync(UserSession session, Packet packet)
    {
        var npcId = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        var realSrc = page * WarehousePageSize + srcPos;
        var realDst = page * WarehousePageSize + dstPos;
        if (!CanUseWarehouse(session, npcId)
            || srcPos >= WarehousePageSize
            || dstPos >= WarehousePageSize
            || realSrc >= UserSession.WarehouseMax
            || realDst >= UserSession.WarehouseMax)
        {
            await SendResultAsync(session, WarehouseSubOpcode.Move, false);
            return;
        }

        var moved = session.WithLock(s => TryMove(s.Warehouse[realSrc], s.Warehouse[realDst], itemId));

        await SendResultAsync(session, WarehouseSubOpcode.Move, moved);
    }

    private async Task InventoryMoveAsync(UserSession session, Packet packet)
    {
        var npcId = packet.ReadInt();
        var itemId = packet.ReadInt();
        _ = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        if (!CanUseWarehouse(session, npcId)
            || srcPos >= InventoryConstants.HaveMax
            || dstPos >= InventoryConstants.HaveMax)
        {
            await SendResultAsync(session, WarehouseSubOpcode.InventoryMove, false);
            return;
        }

        var absSrc = InventoryConstants.InventoryStart + srcPos;
        var absDst = InventoryConstants.InventoryStart + dstPos;
        var moved = session.WithLock(s => TryMove(s.Inventory[absSrc], s.Inventory[absDst], itemId));

        await SendResultAsync(session, WarehouseSubOpcode.InventoryMove, moved);
    }

    private bool CanUseWarehouse(UserSession session, int npcId) =>
        !ItemTransfer.IsInventoryLocked(session)
        && sessionManager.Regions.GetNpc(npcId) is { IsAlive: true, NpcType: NpcData.TypeWarehouse } npc
        && Reach.CanInteract(session, npc);

    private static bool IsTransferableCount(ItemData itemData, int count) =>
        count > 0
        && count <= InventoryConstants.MaxStackCount
        && (itemData.Countable != 0 || count == 1);

    private static bool Holds(ItemSlot source, int itemId, ItemData itemData, int count) =>
        source.ItemId == itemId
        && source.Count >= count
        && (itemData.Countable != 0 || source.Count == count);

    private static bool Transfer(ItemSlot source, ItemSlot destination, ItemData itemData, int count)
    {
        var moving = ItemStack.Of(source) with { Count = (ushort)count };
        if (!ItemTransfer.CanPut(destination, moving, itemData.Countable != 0))
            return false;

        ItemTransfer.Put(destination, ItemTransfer.Take(source, (ushort)count));
        return true;
    }

    private static bool TryMove(ItemSlot source, ItemSlot destination, int itemId)
    {
        if (source.IsEmpty || source.ItemId != itemId || !destination.IsEmpty)
            return false;

        ItemTransfer.Move(source, destination);
        return true;
    }

    private static Task SendResultAsync(UserSession session, WarehouseSubOpcode sub, bool succeeded) =>
        session.Client.SendPacket(WarehousePacketWriter.Result(sub, succeeded));
}
