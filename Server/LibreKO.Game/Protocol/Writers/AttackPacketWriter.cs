using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Game.Protocol.Writers;

public enum AttackResult : byte
{
    Failed = 0,
    Succeeded = 1,
    TargetDead = 2,
}

public sealed class AttackPacketWriter
{
    public const byte TypeMelee = 1;

    public const byte NoCritical = 0;

    public static Packet Create(AttackResult result, int attackerId, int targetId)
    {
        var packet = new Packet(GameOpcodes.GS_ATTACK);
        packet.WriteByte(TypeMelee);
        packet.WriteByte((byte)result);
        packet.WriteInt(attackerId);
        packet.WriteInt(targetId);
        packet.WriteByte(NoCritical);
        return packet;
    }
}
