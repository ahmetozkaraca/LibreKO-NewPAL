using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IBeautyShopPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class BeautyShopPacketCoordinator(SessionManager sessionManager) : IBeautyShopPacketCoordinator
{
    private const byte Restyle = 1;
    private const int HeaderBytes = sizeof(byte) + sizeof(byte);
    private const int AppearanceBytes = sizeof(byte) + sizeof(int);

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        await client.SendPacket(PreGamePacketWriter.ChangeHairResult(TryRestyle(session, packet)
            ? PreGamePacketWriter.ChangeHairSucceeded
            : PreGamePacketWriter.ChangeHairFailed));
    }

    private bool TryRestyle(UserSession session, Packet packet)
    {
        if (packet.RemainingBytes < HeaderBytes || packet.ReadByte() != Restyle)
            return false;

        var name = packet.ReadSByteString();
        if (packet.RemainingBytes < AppearanceBytes)
            return false;

        var face = packet.ReadByte();
        var hair = packet.ReadInt();
        if (!string.Equals(name, session.Name, StringComparison.Ordinal)
            || !CharacterRules.IsValidAppearance(face, hair)
            || session.Hp <= 0
            || NpcDialogContext.ActiveNpc(sessionManager, session)?.NpcId != NpcData.MakeupArtist)
            return false;

        session.WithLock(s =>
        {
            s.Face = face;
            s.Hair = hair;
        });
        return true;
    }
}
