using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IItemExchangePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ItemExchangePacketCoordinator(
    SessionManager sessionManager,
    ILogger<ItemExchangePacketCoordinator> logger) : IItemExchangePacketCoordinator
{
    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = (ItemExchangeSubOpcode)packet.ReadByte();
        switch (sub)
        {
            case ItemExchangeSubOpcode.List:
                await SendListAsync(session);
                break;
            case ItemExchangeSubOpcode.Exchange:
                await HandleExchangeAsync(session, packet);
                break;
        }
    }

    private async Task SendListAsync(UserSession session)
    {
        await session.Client.SendPacket(ItemExchangePacketWriter.RecipeList(ItemExchangeSubOpcode.List, []));
    }

    private async Task HandleExchangeAsync(UserSession session, Packet packet)
    {
        var recipeId = packet.RemainingBytes >= sizeof(int) ? packet.ReadInt() : 0;
        logger.LogDebug("{Name} asked to exchange recipe {Recipe}, which the server does not exchange", session.Name, recipeId);

        await session.Client.SendPacket(ItemExchangePacketWriter.Result(
            ItemExchangeSubOpcode.Exchange, ItemExchangePacketWriter.Failed, ItemExchangePacketWriter.NoItemId));
    }
}
