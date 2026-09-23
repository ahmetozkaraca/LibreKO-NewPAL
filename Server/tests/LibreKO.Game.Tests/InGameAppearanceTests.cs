using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LibreKO.Game.Tests;

public class InGameAppearanceTests : GameTestBase
{
    private const byte Restyle = 1;
    private const byte OldFace = 1;
    private const byte NewFace = 2;
    private const int OldHair = 1;
    private const int NewHair = 3;
    private const short PremiumClan = 41;
    private const float StandX = 100;
    private const float StandZ = 100;

    [Fact]
    public async Task KellyRestylesACharacterInTheWorld()
    {
        using var provider = CreateProvider(_ => { });
        var (session, sent) = Player(provider);
        TalkToKelly(provider, session);

        await provider.GetRequiredService<IBeautyShopPacketCoordinator>().HandleAsync(session.Client, Request(session.Name));

        (session.Face, session.Hair).Should().Be((NewFace, NewHair));
        Reply(sent, GameOpcodes.GS_CHANGE_HAIR).Should().Be(PreGamePacketWriter.ChangeHairSucceeded);
    }

    [Fact]
    public async Task ARestyleAwayFromKellyIsRefusedWithAReply()
    {
        using var provider = CreateProvider(_ => { });
        var (session, sent) = Player(provider);

        await provider.GetRequiredService<IBeautyShopPacketCoordinator>().HandleAsync(session.Client, Request(session.Name));

        (session.Face, session.Hair).Should().Be((OldFace, OldHair));
        Reply(sent, GameOpcodes.GS_CHANGE_HAIR).Should().Be(PreGamePacketWriter.ChangeHairFailed);
    }

    [Fact]
    public async Task AClanPremiumQueryIsAnswered()
    {
        using var provider = CreateProvider(_ => { });
        var (session, sent) = Player(provider);
        session.KnightsId = PremiumClan;
        provider.GetRequiredService<SessionManager>().Knights.AddClan(PremiumClan, new KnightsEntity
        {
            Id = PremiumClan,
            Name = "Premium",
            PremiumExpiry = DateTime.UtcNow.AddDays(1),
        });
        var query = new Packet(GameOpcodes.GS_CLAN_PREMIUM);
        query.WriteByte(KnightsBroadcastBuilders.ClanPremiumQuery);

        await provider.GetRequiredService<IClanPremiumPacketCoordinator>().HandleAsync(session.Client, query);

        Reply(sent, GameOpcodes.GS_CLAN_PREMIUM, KnightsBroadcastBuilders.ClanPremiumQuery)
            .Should().Be(KnightsBroadcastBuilders.ClanPremiumActive);
    }

    private static Packet Request(string name)
    {
        var packet = new Packet(GameOpcodes.GS_CHANGE_HAIR);
        packet.WriteByte(Restyle);
        packet.WriteSByteString(name);
        packet.WriteByte(NewFace);
        packet.WriteInt(NewHair);
        return packet;
    }

    private static byte Reply(List<Packet> sent, GameOpcodes opcode, byte? sub = null)
    {
        var packet = sent.Last(p => p.GetOpcode() == (byte)opcode);
        packet.ResetOffset();
        if (sub != null)
            packet.ReadByte().Should().Be(sub.Value);
        return packet.ReadByte();
    }

    private static void TalkToKelly(ServiceProvider provider, UserSession session)
    {
        session.Quest.EventNpcUniqueId = provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = NpcData.MakeupArtist,
            NpcType = NpcData.TypeTalk,
            ZoneId = session.ZoneId,
            X = session.X,
            Z = session.Z,
            MaxHp = 1,
            Hp = 1,
        }).UniqueId;
    }

    private static (UserSession Session, List<Packet> Sent) Player(ServiceProvider provider)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId: 940, accountId: 941);
        session.Name = "Stylish";
        session.Nation = AccountNation.Karus;
        session.ZoneId = (byte)ZoneId.Moradon;
        session.X = StandX;
        session.Z = StandZ;
        session.MaxHp = 100;
        session.Hp = 100;
        session.Face = OldFace;
        session.Hair = OldHair;
        sessionManager.Regions.AddToRegion(session);
        return (session, sent);
    }
}
