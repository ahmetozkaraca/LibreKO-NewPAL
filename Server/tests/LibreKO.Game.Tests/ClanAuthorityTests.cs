using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class ClanAuthorityTests : GameTestBase
{
    private const short Heroes = 77;
    private const short Villains = 88;
    private const byte ChiefFame = 1;
    private const byte PromotedFlag = 2;
    private const byte TopGrade = 1;
    private const byte TicketPurchase = 1;
    private const byte NormalPurchase = 0;
    private const short KeepCape = -1;
    private const int Crimson = 0x0000FF;
    private const int PaintCost = 36_000;

    [Fact]
    public async Task AnOfficerCannotDraftSomeoneWhoNeverApplied()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 800, "HeroChief", Heroes, ChiefFame);
        var bystander = CreatePlayer(sessionManager, 801, "Bystander", 0, 0);

        await Knights(provider).HandleProcessAsync(chief.Client, Admit(bystander));

        bystander.KnightsId.Should().Be(0);
        sessionManager.Knights.GetClan(Heroes)!.Members.Should().Be(1);
    }

    [Fact]
    public async Task AnApplicationLetsOnlyTheChosenClanAdmit()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var heroChief = CreatePlayer(sessionManager, 802, "HeroChief", Heroes, ChiefFame);
        var villainChief = CreatePlayer(sessionManager, 803, "VillainChief", Villains, ChiefFame);
        var applicant = CreatePlayer(sessionManager, 804, "Applicant", 0, 0);

        await Knights(provider).HandleProcessAsync(applicant.Client, Apply(Heroes));
        await Knights(provider).HandleProcessAsync(villainChief.Client, Admit(applicant));

        applicant.KnightsId.Should().Be(0);

        await Knights(provider).HandleProcessAsync(heroChief.Client, Admit(applicant));

        applicant.KnightsId.Should().Be(Heroes);
        sessionManager.Knights.GetClan(Heroes)!.Members.Should().Be(2);
    }

    [Fact]
    public async Task AnApplicationExpires()
    {
        using var provider = CreateGuildHall(out var clock);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 805, "HeroChief", Heroes, ChiefFame);
        var applicant = CreatePlayer(sessionManager, 806, "Applicant", 0, 0);

        await Knights(provider).HandleProcessAsync(applicant.Client, Apply(Heroes));
        clock.Advance(TimeSpan.FromHours(1));
        await Knights(provider).HandleProcessAsync(chief.Client, Admit(applicant));

        applicant.KnightsId.Should().Be(0);
    }

    [Fact]
    public async Task ARejectionOnlyReachesAnApplicant()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 807, "HeroChief", Heroes, ChiefFame);
        var bystander = CreatePlayer(sessionManager, 808, "Bystander", 0, 0);

        await Knights(provider).HandleProcessAsync(chief.Client, Reject(bystander));

        await bystander.Client.DidNotReceive().SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PaintingTheCapeAlwaysCostsClanPoints()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 809, "HeroChief", Heroes, ChiefFame);
        TalkToCapeMerchant(sessionManager, chief);

        await provider.GetRequiredService<IKnightsCapePacketCoordinator>()
            .HandleAsync(chief.Client, CapeRequest(TicketPurchase, KeepCape, Crimson));

        var clan = sessionManager.Knights.GetClan(Heroes)!;
        (clan.CapeR, clan.CapeG, clan.CapeB).Should().Be(((byte)0, (byte)0, (byte)0));
    }

    [Fact]
    public async Task TheCapeIsPaintedOnlyAtTheCapeMerchant()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 810, "HeroChief", Heroes, ChiefFame);
        var clan = sessionManager.Knights.GetClan(Heroes)!;
        clan.ClanPointFund = PaintCost;
        var capes = provider.GetRequiredService<IKnightsCapePacketCoordinator>();

        await capes.HandleAsync(chief.Client, CapeRequest(NormalPurchase, KeepCape, Crimson));

        clan.CapeR.Should().Be(0);
        clan.ClanPointFund.Should().Be(PaintCost);

        TalkToCapeMerchant(sessionManager, chief);
        await capes.HandleAsync(chief.Client, CapeRequest(NormalPurchase, KeepCape, Crimson));

        clan.CapeR.Should().Be(0xFF);
        clan.ClanPointFund.Should().Be(0);
    }

    private static IKnightsPacketCoordinator Knights(ServiceProvider provider)
        => provider.GetRequiredService<IKnightsPacketCoordinator>();

    private static ServiceProvider CreateGuildHall(out ManualClock clock)
    {
        var manualClock = new ManualClock();
        clock = manualClock;
        var provider = CreateProvider(
            db =>
            {
                db.Set<KnightsEntity>().Add(Clan(Heroes, "Heroes", "HeroChief"));
                db.Set<KnightsEntity>().Add(Clan(Villains, "Villains", "VillainChief"));
            },
            configureServices: services => services.AddSingleton<TimeProvider>(manualClock));

        var knights = provider.GetRequiredService<SessionManager>().Knights;
        knights.AddClan(Heroes, Clan(Heroes, "Heroes", "HeroChief"));
        knights.AddClan(Villains, Clan(Villains, "Villains", "VillainChief"));
        return provider;
    }

    private static KnightsEntity Clan(short id, string name, string chief) => new()
    {
        Id = id,
        Name = name,
        Chief = chief,
        Nation = (byte)AccountNation.Karus,
        Flag = PromotedFlag,
        Grade = TopGrade,
        Members = 1,
    };

    private static void TalkToCapeMerchant(SessionManager sessionManager, UserSession session)
    {
        session.Quest.EventNpcUniqueId = sessionManager.Regions.SpawnNpc(new NpcInstance
        {
            NpcId = 16031,
            NpcType = NpcData.TypeClanCape,
            ZoneId = session.ZoneId,
            X = session.X,
            Z = session.Z,
            MaxHp = 1,
            Hp = 1,
        }).UniqueId;
    }

    private static Packet Apply(short clanId)
    {
        var packet = new Packet(GameOpcodes.GS_KNIGHTS_PROCESS);
        packet.WriteByte((byte)KnightsSubOpcode.Join);
        packet.WriteShort(clanId);
        return packet;
    }

    private static Packet Admit(UserSession applicant) => Named(KnightsSubOpcode.Admit, applicant.Name);

    private static Packet Reject(UserSession applicant) => Named(KnightsSubOpcode.Reject, applicant.Name);

    private static Packet Named(KnightsSubOpcode subOpcode, string name)
    {
        var packet = new Packet(GameOpcodes.GS_KNIGHTS_PROCESS);
        packet.WriteByte((byte)subOpcode);
        packet.WriteString(name);
        return packet;
    }

    private static Packet CapeRequest(byte opcode, short capeId, int colour)
    {
        var packet = new Packet(GameOpcodes.GS_CAPE);
        packet.WriteByte(opcode);
        packet.WriteShort(capeId);
        packet.WriteInt(colour);
        return packet;
    }

    private static UserSession CreatePlayer(
        SessionManager sessionManager, int characterId, string name, short clanId, byte fame)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = name;
        session.Nation = AccountNation.Karus;
        session.Level = 60;
        session.ZoneId = (byte)ZoneId.Moradon;
        session.X = 100;
        session.Z = 100;
        session.MaxHp = 100;
        session.Hp = 100;
        session.KnightsId = clanId;
        session.KnightsFame = fame;
        sessionManager.Regions.AddToRegion(session);
        return session;
    }
}
