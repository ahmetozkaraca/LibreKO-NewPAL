using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Tests;

internal static class MagicCastRequests
{
    private const int PayloadSlots = 7;

    public static Packet Build(MagicProcessOpcode opcode, int skillId, int casterId, int targetId, params int[] data)
    {
        var packet = new Packet(GameOpcodes.GS_MAGIC_PROCESS);
        packet.WriteByte((byte)opcode);
        packet.WriteInt(skillId);
        packet.WriteInt(casterId);
        packet.WriteInt(targetId);
        for (var slot = 0; slot < PayloadSlots; slot++)
            packet.WriteInt(slot < data.Length ? data[slot] : 0);
        return packet;
    }

    public static Task SendAsync(
        this IMagicPacketCoordinator coordinator, IClient client, MagicProcessOpcode opcode, int skillId,
        int casterId, int targetId, params int[] data) =>
        coordinator.HandleAsync(client, Build(opcode, skillId, casterId, targetId, data));

    public static async Task CastAsync(
        this IMagicPacketCoordinator coordinator, IClient client, int skillId, int casterId, int targetId,
        params int[] data)
    {
        await coordinator.SendAsync(client, MagicProcessOpcode.Casting, skillId, casterId, targetId, data);
        await coordinator.SendAsync(client, MagicProcessOpcode.Effecting, skillId, casterId, targetId, data);
    }
}
