using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IItemRemoveService
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ItemRemoveService(
    SessionManager sessionManager,
    IUserNotificationService userNotificationService,
    IItemInventoryRuleService itemInventoryRuleService,
    IItemEquipmentEffectService itemEquipmentEffectService,
    ILogger<ItemRemoveService> logger) : IItemRemoveService
{
    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var type = packet.ReadByte();
        var position = packet.ReadByte();
        var itemId = packet.ReadInt();

        if (type == 1
            && !itemInventoryRuleService.TryResolveEquipmentPosition(position, out position))
        {
            await SendItemRemoveResponseAsync(session, 0);
            return;
        }

        if (!itemInventoryRuleService.TryResolveRemoveIndex(type, position, out var absolutePosition, out var affectsEquipment))
        {
            await SendItemRemoveResponseAsync(session, 0);
            return;
        }

        var removed = session.WithLock(s =>
        {
            var item = s.Inventory[absolutePosition];
            if (ItemTransfer.IsInventoryLocked(s) || item.IsEmpty || item.ItemId != itemId || item.State == ItemFlag.Sealed)
                return false;

            item.Clear();
            return true;
        });

        if (!removed)
        {
            await SendItemRemoveResponseAsync(session, 0);
            return;
        }

        var removedItemId = itemId;
        logger.LogDebug("{Name} removed item {ItemId} from slot {Slot}", session.Name, removedItemId, absolutePosition);

        if (affectsEquipment)
            await itemEquipmentEffectService.ApplyRemovalEffectsAsync(session, removedItemId);

        await userNotificationService.SendWeightChangeAsync(session);
        await SendItemRemoveResponseAsync(session, ItemRemoveResult.Removed);
    }

    private static async Task SendItemRemoveResponseAsync(UserSession session, ItemRemoveResult result)
    {
        var writer = result == ItemRemoveResult.Removed
            ? ItemRemovePacketWriter.Removed()
            : ItemRemovePacketWriter.Failed();
        await session.Client.SendPacket(writer);
    }
}
