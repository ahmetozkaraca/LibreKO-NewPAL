using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IClanPremiumPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ClanPremiumPacketCoordinator(SessionManager sessionManager) : IClanPremiumPacketCoordinator
{
    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < sizeof(byte)
            || packet.ReadByte() != KnightsBroadcastBuilders.ClanPremiumQuery)
            return;

        var active = session.KnightsId > 0 && sessionManager.Knights.GetClan(session.KnightsId)?.HasPremium == true;
        await client.SendPacket(ClanBroadcastPacketWriter.ClanPremium(
            KnightsBroadcastBuilders.ClanPremiumQuery,
            active ? KnightsBroadcastBuilders.ClanPremiumActive : KnightsBroadcastBuilders.ClanPremiumInactive));
    }
}
