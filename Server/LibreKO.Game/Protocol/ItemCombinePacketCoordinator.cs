using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IItemCombinePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ItemCombinePacketCoordinator(
    SessionManager sessionManager,
    ILogger<ItemCombinePacketCoordinator> logger) : IItemCombinePacketCoordinator
{
    private const byte ItemCombineSubList = 1;
    private const byte ItemCombineSubCombine = 2;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = packet.ReadByte();
        switch (sub)
        {
            case ItemCombineSubList:
                await SendListAsync(session);
                break;
            case ItemCombineSubCombine:
                await HandleCombineAsync(session, packet);
                break;
        }
    }

    private async Task SendListAsync(UserSession session)
    {
        await session.Client.SendPacket(
            ItemCombinePacketWriter.RecipeList(ItemCombineSubList, []));
    }

    private async Task HandleCombineAsync(UserSession session, Packet packet)
    {
        var recipeId = packet.RemainingBytes >= sizeof(int) ? packet.ReadInt() : 0;
        logger.LogDebug("{Name} asked to combine recipe {Recipe}, which the server does not craft", session.Name, recipeId);

        await session.Client.SendPacket(ItemCombinePacketWriter.Result(
            ItemCombineSubCombine, ItemCombinePacketWriter.Failed, ItemCombinePacketWriter.NoItemId));
    }
}
