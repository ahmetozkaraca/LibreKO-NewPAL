using FluentAssertions;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class InGameAdmissionTests : GameTestBase
{
    private const int CharacterId = 4100;
    private const int OtherCharacterId = 4200;
    private const byte Zone = 21;
    private const byte OtherZone = 1;
    private const float Position = 100;
    private const string Message = "hello";

    [Fact]
    public async Task ChatFromASessionThatIsClosingIsDropped()
    {
        using var provider = CreateProvider(_ => { }, configureServices: services => services.AddSingleton<TimeProvider>(new ManualClock()));
        var handler = provider.GetRequiredService<IPacketHandler>();
        var (leaving, leavingClient, leavingSent) = CreatePlayer(provider, CharacterId, Zone);
        var (_, stayingClient, stayingSent) = CreatePlayer(provider, OtherCharacterId, OtherZone);
        leaving.TryBeginClosing().Should().BeTrue();

        await handler.HandlePacket(leavingClient, ChatRequest());
        await handler.HandlePacket(stayingClient, ChatRequest());

        leavingSent.Should().BeEmpty("a player who is leaving the world must not keep talking in it");
        stayingSent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChatIsRateLimitedByThePacketGuard()
    {
        using var provider = CreateProvider(_ => { }, configureServices: services => services.AddSingleton<TimeProvider>(new ManualClock()));
        var (_, client, _) = CreatePlayer(provider, CharacterId, Zone);
        var handler = provider.GetRequiredService<IPacketHandler>();
        var limit = OpcodePolicies.LimitOf(GameOpcodes.GS_CHAT);

        for (var i = 0; i <= limit.Capacity; i++)
            await handler.HandlePacket(client, ChatRequest());

        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.RateLimitWeight);
    }

    private static (UserSession Session, IClient Client, List<Packet> Sent) CreatePlayer(ServiceProvider provider, int characterId, byte zone)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.AccountId.Returns(characterId + 1);
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = $"Speaker{characterId}";
        session.ZoneId = zone;
        session.X = Position;
        session.Z = Position;
        sessionManager.Regions.AddToRegion(session);
        return (session, client, sent);
    }

    private static Packet ChatRequest()
    {
        var packet = new Packet(GameOpcodes.GS_CHAT);
        packet.WriteByte((byte)ChatType.General);
        packet.WriteString(Message);
        packet.ResetOffset();
        return packet;
    }
}
