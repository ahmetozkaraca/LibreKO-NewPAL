using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LibreKO.Game.Tests;

public class ChatRoomLogoutTests : GameTestBase
{
    private const byte Create = 2;
    private const byte Join = 3;
    private const byte Say = 5;
    private const byte SayBroadcast = 6;
    private const int HostId = 1320;
    private const int GuestId = 1321;

    [Fact]
    public async Task LoggingOutLeavesTheChatRoom()
    {
        using var provider = CreateProvider(_ => { });
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var rooms = provider.GetRequiredService<IChatRoomPacketCoordinator>();
        var (host, hostSent) = Online(sessionManager, HostId, "Host");
        var (guest, _) = Online(sessionManager, GuestId, "Guest");

        await rooms.HandleAsync(host.Client, Named(Create, "Lounge"));
        var created = hostSent.Last(p => p.GetOpcode() == (byte)GameOpcodes.GS_CHATROOM);
        created.ResetOffset();
        created.ReadByte();
        created.ReadByte();
        var roomId = created.ReadInt();
        var join = new Packet(GameOpcodes.GS_CHATROOM);
        join.WriteByte(Join);
        join.WriteInt(roomId);
        await rooms.HandleAsync(guest.Client, join);

        await provider.GetRequiredService<ISessionTerminationService>().LogoutAsync(guest.Client);
        var (_, returnedSent) = Online(sessionManager, GuestId, "Guest");
        await rooms.HandleAsync(host.Client, Named(Say, "anyone here?"));

        returnedSent.Should().NotContain(p => p.GetOpcode() == (byte)GameOpcodes.GS_CHATROOM
            && p.GetData()[0] == SayBroadcast);
    }

    private static Packet Named(byte sub, string text)
    {
        var packet = new Packet(GameOpcodes.GS_CHATROOM);
        packet.WriteByte(sub);
        packet.WriteSByteString(text);
        return packet;
    }

    private static (UserSession Session, List<Packet> Sent) Online(SessionManager sessionManager, int characterId, string name)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = sessionManager.CreateSession(client, characterId, characterId);
        session.Name = name;
        session.Hp = 1;
        return (session, sent);
    }
}
