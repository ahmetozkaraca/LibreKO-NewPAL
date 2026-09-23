using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.Protocol;

public interface IItemMoveService
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ItemMoveService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IWorldPacketCoordinator worldPacketCoordinator,
    IItemInventoryRuleService itemInventoryRuleService,
    IItemEquipmentEffectService itemEquipmentEffectService,
    IUserNotificationService userNotificationService,
    ILogger<ItemMoveService> logger) : IItemMoveService
{
    private enum ItemMoveRequest : byte
    {
        Move = 1,
        Arrange = 2,
        ClientRefused = 3,
    }

    private readonly record struct MoveOutcome(
        bool Moved,
        int SourceItemIdBeforeMove,
        int DestinationItemIdBeforeMove,
        int SourceItemId,
        short SourceDurability,
        int DestinationItemId,
        short DestinationDurability);

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var requestType = (ItemMoveRequest)packet.ReadByte();
        if (requestType == ItemMoveRequest.ClientRefused)
        {
            logger.LogDebug("Rejected item move for {Name}: invalid request type {RequestType}", session.Name, requestType);
            await SendItemMoveResponseAsync(session, 0);
            return;
        }

        if (requestType == ItemMoveRequest.Arrange)
        {
            var arranged = session.WithLock(s =>
            {
                if (ItemTransfer.IsInventoryLocked(s))
                    return null;

                ArrangeBag(s);
                return ItemMovePacketMapper.BuildArrangedResponse(s);
            });

            if (arranged == null)
            {
                logger.LogDebug("Refused to arrange the bag for {Name}: busy", session.Name);
                await session.Client.SendPacket(ItemMovePacketMapper.BuildArrangeRefusedResponse());
                return;
            }

            logger.LogDebug("Arranged the bag for {Name}", session.Name);
            await session.Client.SendPacket(arranged);
            return;
        }

        if (requestType != ItemMoveRequest.Move)
        {
            logger.LogDebug("Rejected item move for {Name}: unknown request type {RequestType}", session.Name, requestType);
            await SendItemMoveResponseAsync(session, 0);
            return;
        }

        var directionByte = packet.ReadByte();
        if (!Enum.IsDefined(typeof(ItemMoveDirection), directionByte))
        {
            logger.LogDebug("Rejected item move for {Name}: invalid direction {Direction}", session.Name, directionByte);
            return;
        }

        var direction = (ItemMoveDirection)directionByte;
        var itemId = packet.ReadInt();
        var sourcePosition = packet.ReadByte();
        var destinationPosition = packet.ReadByte();
        var resolvedSourcePosition = sourcePosition;
        var resolvedDestinationPosition = destinationPosition;

        if (!TryResolveEquipmentPositions(
                itemInventoryRuleService,
                direction,
                ref resolvedSourcePosition,
                ref resolvedDestinationPosition))
        {
            logger.LogDebug(
                "Rejected item move for {Name}: could not resolve positions dir={Direction} src={Source} dst={Destination}",
                session.Name,
                direction,
                sourcePosition,
                destinationPosition);
            await SendItemMoveResponseAsync(session, 0);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null)
        {
            logger.LogDebug("Rejected item move for {Name}: item {ItemId} not found", session.Name, itemId);
            await SendItemMoveResponseAsync(session, 0);
            return;
        }

        var outcome = session.WithLock(s =>
        {
            if (ItemTransfer.IsInventoryLocked(s)
                || !itemInventoryRuleService.TryResolveMoveIndices(
                    s,
                    itemData,
                    direction,
                    resolvedSourcePosition,
                    resolvedDestinationPosition,
                    out var sourceIndex,
                    out var destinationIndex))
                return default;

            var sourceItem = s.Inventory[sourceIndex];
            var destinationItem = s.Inventory[destinationIndex];
            if (sourceItem.IsEmpty || sourceItem.ItemId != itemId)
                return default;

            var sourceItemIdBeforeMove = sourceItem.ItemId;
            var destinationItemIdBeforeMove = destinationItem.ItemId;
            if (destinationItem.IsEmpty)
                ItemTransfer.Move(sourceItem, destinationItem);
            else
                ItemTransfer.Swap(sourceItem, destinationItem);

            return new MoveOutcome(
                true,
                sourceItemIdBeforeMove,
                destinationItemIdBeforeMove,
                sourceItem.ItemId,
                sourceItem.Durability,
                destinationItem.ItemId,
                destinationItem.Durability);
        });

        if (!outcome.Moved)
        {
            logger.LogDebug(
                "Rejected item move for {Name}: dir={Direction} item={ItemId} src={Source}->{ResolvedSource} dst={Destination}->{ResolvedDestination} trading={Trading} merchanting={Merchanting} gathering={Gathering}",
                session.Name,
                direction,
                itemId,
                sourcePosition,
                resolvedSourcePosition,
                destinationPosition,
                resolvedDestinationPosition,
                session.Trade.IsTrading,
                session.Trade.IsMerchanting,
                session.IsGathering);
            await SendItemMoveResponseAsync(session, 0);
            return;
        }

        await itemEquipmentEffectService.ApplyMoveEffectsAsync(
            session,
            direction,
            outcome.SourceItemIdBeforeMove,
            outcome.DestinationItemIdBeforeMove);

        await SendItemMoveResponseAsync(session, 1);
        await userNotificationService.SendWeightChangeAsync(session);

        if (!itemInventoryRuleService.IsEquipmentChange(direction))
            return;

        switch (direction)
        {
            case ItemMoveDirection.InventoryToSlot:
                    await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                        session,
                        resolvedDestinationPosition,
                        outcome.DestinationItemId,
                        outcome.DestinationDurability);
                break;

            case ItemMoveDirection.SlotToInventory:
                await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                    session,
                    resolvedSourcePosition,
                    outcome.SourceItemId,
                    outcome.SourceDurability);
                break;

            case ItemMoveDirection.SlotToSlot:
                await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                    session,
                    resolvedSourcePosition,
                    outcome.SourceItemId,
                    outcome.SourceDurability);
                await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                    session,
                    resolvedDestinationPosition,
                    outcome.DestinationItemId,
                    outcome.DestinationDurability);
                break;

            case ItemMoveDirection.InventoryToCospre:
                if (itemInventoryRuleService.TryGetCospreVisualSlot(destinationPosition, out var equippedLookSlot))
                {
                    await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                        session,
                        equippedLookSlot,
                        outcome.DestinationItemId,
                        outcome.DestinationDurability);
                }

                break;

            case ItemMoveDirection.CospreToInventory:
                if (itemInventoryRuleService.TryGetCospreVisualSlot(sourcePosition, out var removedLookSlot))
                {
                    await worldPacketCoordinator.BroadcastUserLookChangeAsync(
                        session,
                        removedLookSlot,
                        outcome.SourceItemId,
                        outcome.SourceDurability);
                }

                break;
        }
    }

    private static void ArrangeBag(UserSession session)
    {
        var arranged = ItemTransfer.BagSnapshot(session.Inventory)
            .OrderByDescending(item => item.ItemId)
            .ToArray();

        for (var offset = 0; offset < arranged.Length; offset++)
            arranged[offset].WriteTo(session.Inventory[InventoryConstants.InventoryStart + offset]);
    }

    private static async Task SendItemMoveResponseAsync(UserSession session, byte subcommand)
    {
        await session.Client.SendPacket(ItemMovePacketMapper.BuildResponse(session, subcommand));
    }

    private static bool TryResolveEquipmentPositions(
        IItemInventoryRuleService itemInventoryRuleService,
        ItemMoveDirection direction,
        ref byte sourcePosition,
        ref byte destinationPosition)
    {
        return direction switch
        {
            ItemMoveDirection.InventoryToSlot => itemInventoryRuleService.TryResolveEquipmentPosition(destinationPosition, out destinationPosition),
            ItemMoveDirection.SlotToInventory => itemInventoryRuleService.TryResolveEquipmentPosition(sourcePosition, out sourcePosition),
            ItemMoveDirection.SlotToSlot => itemInventoryRuleService.TryResolveEquipmentPosition(sourcePosition, out sourcePosition)
                                && itemInventoryRuleService.TryResolveEquipmentPosition(destinationPosition, out destinationPosition),
            _ => true,
        };
    }
}
