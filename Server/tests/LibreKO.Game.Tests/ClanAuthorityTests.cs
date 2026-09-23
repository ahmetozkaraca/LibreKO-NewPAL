using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
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
    private const byte TraineeFame = 5;
    private const int Donation = 1_000;
    private const int Departures = 10;
    private const int FoundingFee = KnightsPacketConstants.ClanCoinRequirement;

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
    public async Task TheChiefHearsBackOnEveryRejection()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 850, "HeroChief", Heroes, ChiefFame);
        var applicant = CreatePlayer(sessionManager, 851, "Applicant", 0, 0);

        await Knights(provider).HandleProcessAsync(chief.Client, Reject(applicant));
        await Knights(provider).HandleProcessAsync(applicant.Client, Apply(Heroes));
        await Knights(provider).HandleProcessAsync(chief.Client, Reject(applicant));

        MembershipResults(chief, KnightsSubOpcode.Reject).Should().Equal(
            (byte)KnightsResult.NoSuchUser, (byte)KnightsResult.Succeeded);
        MembershipResults(applicant, KnightsSubOpcode.Reject).Should().Equal((byte)KnightsResult.UserDeclined);
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

    [Fact]
    public async Task ResendingTheCurrentDyeIsNotChargedAsARepaint()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 852, "HeroChief", Heroes, ChiefFame);
        var clan = sessionManager.Knights.GetClan(Heroes)!;
        clan.CapeR = (byte)Crimson;
        clan.ClanPointFund = PaintCost;
        TalkToCapeMerchant(sessionManager, chief);

        await provider.GetRequiredService<IKnightsCapePacketCoordinator>()
            .HandleAsync(chief.Client, CapeRequest(NormalPurchase, KeepCape, Crimson));

        clan.ClanPointFund.Should().Be(PaintCost);
    }

    [Fact]
    public async Task AClanNeverHearsFromAnApplicantOfTheOtherNation()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 811, "HeroChief", Heroes, ChiefFame);
        var foreigner = CreatePlayer(sessionManager, 812, "Foreigner", 0, 0);
        foreigner.Nation = AccountNation.ElMorad;

        await Knights(provider).HandleProcessAsync(foreigner.Client, Apply(Heroes));
        await chief.Client.DidNotReceive().SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>());

        foreigner.Nation = AccountNation.Karus;
        await Knights(provider).HandleProcessAsync(chief.Client, Admit(foreigner));

        foreigner.KnightsId.Should().Be(0);
        sessionManager.Knights.GetClan(Heroes)!.Members.Should().Be(1);
    }

    [Fact]
    public async Task AMemberWhoLeftCannotBeKickedOutOfASecondSeat()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var chief = CreatePlayer(sessionManager, 813, "HeroChief", Heroes, ChiefFame);
        var member = CreatePlayer(sessionManager, 814, "Member", Heroes, TraineeFame);
        var clan = sessionManager.Knights.GetClan(Heroes)!;
        clan.Members = 2;

        await Knights(provider).HandleProcessAsync(member.Client, Withdraw());
        await Knights(provider).HandleProcessAsync(chief.Client, Named(KnightsSubOpcode.Remove, member.Name));

        member.KnightsId.Should().Be(0);
        clan.Members.Should().Be(1);
    }

    [Fact]
    public void OnlyOneOfTwoSimultaneousDeparturesClaimsTheMember()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var runtime = provider.GetRequiredService<IKnightsRuntimeService>();
        var member = CreatePlayer(sessionManager, 815, "Member", Heroes, TraineeFame);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            member.KnightsId = Heroes;
            var claims = new bool[2];
            Parallel.Invoke(
                () => claims[0] = runtime.TryLeaveClan(member, Heroes),
                () => claims[1] = runtime.TryLeaveClan(member, Heroes));

            claims.Count(claimed => claimed).Should().Be(1);
            member.KnightsId.Should().Be(0);
        }
    }

    [Fact]
    public async Task SimultaneousDeparturesEachTakeTheirOwnDonationFromTheFund()
    {
        using var provider = CreateGuildHall(out _);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var clan = sessionManager.Knights.GetClan(Heroes)!;
        clan.Members = Departures + 1;
        clan.ClanPointFund = Departures * Donation;
        var members = Enumerable.Range(0, Departures).Select(index =>
        {
            var member = CreatePlayer(sessionManager, 820 + index, $"Donor{index}", Heroes, TraineeFame);
            member.KnightsPoints = Donation;
            return member;
        }).ToList();

        await Task.WhenAll(members.Select(member =>
            Task.Run(() => Knights(provider).HandleProcessAsync(member.Client, Withdraw()))));

        clan.ClanPointFund.Should().Be(0);
        clan.Members.Should().Be(1);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("Bad	Name")]
    [InlineData("X")]
    public async Task AClanNameOutsideTheNameRulesIsRefusedForFree(string name)
    {
        using var provider = CreateGuildHall(out _);
        var founder = CreatePlayer(provider.GetRequiredService<SessionManager>(), 830, "Founder", 0, 0);
        founder.Money = FoundingFee;

        await Knights(provider).HandleProcessAsync(founder.Client, Named(KnightsSubOpcode.Create, name));

        founder.KnightsId.Should().Be(0);
        founder.Money.Should().Be(FoundingFee);
    }

    [Fact]
    public async Task AClanThatCannotBeSavedRefundsItsFoundingFee()
    {
        var repository = Substitute.For<IKnightsRepository>();
        repository.CreateAsync(Arg.Any<KnightsEntity>()).Returns(Task.FromException(new InvalidOperationException()));
        using var provider = CreateProvider(_ => { }, configureServices: services => services.AddScoped(_ => repository));
        var founder = CreatePlayer(provider.GetRequiredService<SessionManager>(), 831, "Founder", 0, 0);
        founder.Money = FoundingFee;

        await Knights(provider).HandleProcessAsync(founder.Client, Named(KnightsSubOpcode.Create, "Founders"));

        MembershipResults(founder, KnightsSubOpcode.Create).Should().Equal((byte)KnightsCreateResult.TryAgainLater);
        founder.Money.Should().Be(FoundingFee);
        founder.KnightsId.Should().Be(0);
    }

    private static List<byte> MembershipResults(UserSession session, KnightsSubOpcode sub) =>
        session.Client.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IClient.SendPacket))
            .Select(call => (Packet)call.GetArguments()[0]!)
            .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_KNIGHTS_PROCESS && packet.GetData()[0] == (byte)sub)
            .Select(packet => packet.GetData()[1])
            .ToList();

    private static Packet Withdraw()
    {
        var packet = new Packet(GameOpcodes.GS_KNIGHTS_PROCESS);
        packet.WriteByte((byte)KnightsSubOpcode.Withdraw);
        return packet;
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
