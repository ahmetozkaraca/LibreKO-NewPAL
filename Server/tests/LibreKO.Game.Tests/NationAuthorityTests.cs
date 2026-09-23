using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Common.Infrastructure.Persistence.Seed.Entities;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class NationAuthorityTests : GameTestBase
{
    private const byte KingElection = 1;
    private const byte KingTax = 3;
    private const byte KingEvent = 4;
    private const byte ElectionPoll = 4;
    private const byte PollCastVote = 2;
    private const byte ElectionTypeElection = 3;
    private const byte CandidateListType = 4;
    private const byte TaxScepter = 7;
    private const byte EventPrize = 3;
    private const int KingScepter = 910074311;
    private const byte VoterLevel = 60;
    private const byte ElectionNominate = 2;
    private const byte ElectionTypeNomination = 1;
    private const byte SenatorListType = 3;
    private const byte TaxCollect = 2;
    private const int TerritoryTax = 1_000;
    private const short SenatorClan = 31;
    private const short OutsiderClan = 32;
    private const short FirstNomineeClan = 33;
    private const short SecondNomineeClan = 34;
    private const int RacingSenators = 8;

    private const byte SiegeDelosNpc = 4;
    private const byte DelosCollectFunds = 2;
    private const byte DelosMoradonTariff = 4;
    private const short CastleClan = 70;
    private const byte ClanChief = 1;
    private const int DungeonCharge = 5_000;
    private const short StartingTariff = 10;
    private const ushort NewTariff = 15;
    private const ushort ExcessiveTariff = 21;
    private const byte Delos = (byte)ZoneId.Delos;

    [Fact]
    public async Task AnAccountCastsOneVoteWhicheverCharacterItUses()
    {
        var kingData = new KingSystemData { Nation = (byte)AccountNation.Karus, Type = ElectionTypeElection };
        using var provider = CreateProvider(
            db =>
            {
                db.Add(kingData);
                db.KingElectionList.Add(new KingElectionList
                {
                    Nation = (byte)AccountNation.Karus, Type = CandidateListType, Name = "Candidate",
                });
            },
            gameData => gameData.KingSystemTable.Returns(new Dictionary<byte, KingSystemData>
            {
                [(byte)AccountNation.Karus] = kingData,
            }));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();

        var (first, firstClient, _) = CreatePlayer(sessionManager, 900, accountId: 77, AccountNation.Karus);
        await nation.HandleKingAsync(firstClient, Vote("Candidate"));
        sessionManager.RemoveSession(first);

        var (_, secondClient, _) = CreatePlayer(sessionManager, 901, accountId: 77, AccountNation.Karus);
        await nation.HandleKingAsync(secondClient, Vote("Candidate"));

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.KingBallotBox.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void AnEmptyThroneCrownsNobody()
    {
        using var provider = CreateProvider(_ => { });
        var kings = provider.GetRequiredService<IKingSystemRuntimeService>();
        var (session, _, _) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 902, 78, AccountNation.Karus);
        session.Name = string.Empty;

        kings.IsKing(session, new KingSystemData { Nation = (byte)AccountNation.Karus, KingName = string.Empty })
            .Should().BeFalse();
    }

    [Fact]
    public void AKingNameOnlyCrownsItsOwnNation()
    {
        using var provider = CreateProvider(_ => { });
        var kings = provider.GetRequiredService<IKingSystemRuntimeService>();
        var (session, _, _) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 903, 79, AccountNation.ElMorad);
        session.Name = "Ruler";

        kings.IsKing(session, new KingSystemData { Nation = (byte)AccountNation.Karus, KingName = "Ruler" })
            .Should().BeFalse();
    }

    [Fact]
    public void TheShippedSeedCrownsNobody()
    {
        new KingSystemSeed().GetSeedData().Should().OnlyContain(row => row.KingName.Length == 0);
    }

    [Fact]
    public async Task AKingWithoutACharacterOfThatNationIsDismissed()
    {
        var phantom = new KingSystemData { Nation = (byte)AccountNation.Karus, KingName = "NxWiLe" };
        var reigning = new KingSystemData { Nation = (byte)AccountNation.ElMorad, KingName = "Crowned" };
        using var provider = CreateProvider(db =>
        {
            db.AddRange(phantom, reigning);
            db.Accounts.Add(new Account { Id = 501, Login = "crowned", Password = "x", Nation = AccountNation.ElMorad });
            db.Characters.Add(new Character { AccountId = 501, Name = "Crowned", MapId = 1 });
        });
        var kings = provider.GetRequiredService<IKingSystemRuntimeService>();

        (await kings.DismissUnknownKingAsync(phantom)).Should().BeTrue();
        (await kings.DismissUnknownKingAsync(reigning)).Should().BeFalse();

        phantom.KingName.Should().BeEmpty();
        reigning.KingName.Should().Be("Crowned");
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.KingSystem.SingleAsync(row => row.Nation == (byte)AccountNation.Karus)).KingName.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyASenatorNominatesAndOnlyOnce()
    {
        var kingData = new KingSystemData { Nation = (byte)AccountNation.Karus, Type = ElectionTypeNomination };
        using var provider = CreateProvider(
            db =>
            {
                db.Add(kingData);
                db.KingElectionList.Add(new KingElectionList
                {
                    Nation = (byte)AccountNation.Karus, Type = SenatorListType, Name = "Senator", Knights = SenatorClan,
                });
            },
            gameData => gameData.KingSystemTable.Returns(new Dictionary<byte, KingSystemData>
            {
                [(byte)AccountNation.Karus] = kingData,
            }));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();
        var senator = CreateChief(sessionManager, 930, "Senator", SenatorClan);
        var outsider = CreateChief(sessionManager, 931, "Outsider", OutsiderClan);
        CreateChief(sessionManager, 932, "FirstNominee", FirstNomineeClan);
        CreateChief(sessionManager, 933, "SecondNominee", SecondNomineeClan);

        await nation.HandleKingAsync(outsider.Client, Nominate("FirstNominee"));
        (await CandidatesAsync(provider)).Should().BeEmpty();

        await nation.HandleKingAsync(senator.Client, Nominate("FirstNominee"));
        await nation.HandleKingAsync(senator.Client, Nominate("SecondNominee"));

        (await CandidatesAsync(provider)).Should().Equal("FirstNominee");
    }

    [Fact]
    public async Task SimultaneousNominationsOfOnePlayerListHimOnce()
    {
        var kingData = new KingSystemData { Nation = (byte)AccountNation.Karus, Type = ElectionTypeNomination };
        var senators = Enumerable.Range(0, RacingSenators).Select(index => $"Senator{index}").ToList();
        using var provider = CreateProvider(
            db =>
            {
                db.Add(kingData);
                foreach (var name in senators)
                {
                    db.KingElectionList.Add(new KingElectionList
                    {
                        Nation = (byte)AccountNation.Karus, Type = SenatorListType, Name = name, Knights = SenatorClan,
                    });
                }
            },
            gameData => gameData.KingSystemTable.Returns(new Dictionary<byte, KingSystemData>
            {
                [(byte)AccountNation.Karus] = kingData,
            }));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();
        var clients = senators.Select((name, index) => CreateChief(sessionManager, 940 + index, name, SenatorClan).Client).ToList();
        CreateChief(sessionManager, 960, "FirstNominee", FirstNomineeClan);

        await Task.WhenAll(clients.Select(client => Task.Run(() => nation.HandleKingAsync(client, Nominate("FirstNominee")))));

        (await CandidatesAsync(provider)).Should().Equal("FirstNominee");
    }

    [Fact]
    public async Task TaxCollectionNeverOverflowsTheKingsPurse()
    {
        using var provider = CreateKingdom(out var king, out var client);
        var kingData = provider.GetRequiredService<IKingSystemRuntimeService>().GetKingData(AccountNation.Karus)!;
        kingData.TerritoryTax = TerritoryTax;
        king.Money = ExchangePacketConstants.CoinMax - 10;

        await provider.GetRequiredService<INationSystemsPacketCoordinator>()
            .HandleKingAsync(client, KingPacket(KingTax, TaxCollect));

        king.Money.Should().Be(ExchangePacketConstants.CoinMax - 10);
        kingData.TerritoryTax.Should().Be(TerritoryTax);

        king.Money = 0;
        await provider.GetRequiredService<INationSystemsPacketCoordinator>()
            .HandleKingAsync(client, KingPacket(KingTax, TaxCollect));

        king.Money.Should().Be(TerritoryTax);
        kingData.TerritoryTax.Should().Be(0);
    }

    [Fact]
    public async Task TheKingCannotConjureASecondScepter()
    {
        using var provider = CreateKingdom(out var king, out var client);
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();

        await nation.HandleKingAsync(client, KingPacket(KingTax, TaxScepter));
        await nation.HandleKingAsync(client, KingPacket(KingTax, TaxScepter));

        king.Inventory.Count(slot => slot.ItemId == KingScepter).Should().Be(1);
    }

    [Fact]
    public async Task APrizeNeverOverflowsTheWinnersPurse()
    {
        using var provider = CreateKingdom(out _, out var client);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var (winner, _, _) = CreatePlayer(sessionManager, 911, 91, AccountNation.Karus);
        winner.Name = "Winner";
        winner.Money = ExchangePacketConstants.CoinMax - 10;
        var treasury = provider.GetRequiredService<IKingSystemRuntimeService>()
            .GetKingData(AccountNation.Karus)!.NationalTreasury;

        var prize = KingPacket(KingEvent, EventPrize);
        prize.WriteSByteString("Winner");
        prize.WriteInt(1_000);
        await provider.GetRequiredService<INationSystemsPacketCoordinator>().HandleKingAsync(client, prize);

        winner.Money.Should().Be(ExchangePacketConstants.CoinMax - 10);
        provider.GetRequiredService<IKingSystemRuntimeService>()
            .GetKingData(AccountNation.Karus)!.NationalTreasury.Should().Be(treasury);
    }

    [Fact]
    public async Task OnlyTheCastleLordAtTheCastleManagerCollectsTheDungeonCharge()
    {
        var siege = new SiegeWarfareData { CastleIndex = 1, MasterKnights = CastleClan, DungeonCharge = DungeonCharge };
        using var provider = CreateProvider(_ => { }, gameData => gameData.SiegeWarfare.Returns(siege));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();
        var (stranger, strangerClient, _) = CreatePlayer(sessionManager, 920, 92, AccountNation.Karus);
        var (lord, lordClient, _) = CreatePlayer(sessionManager, 921, 93, AccountNation.Karus);
        lord.KnightsId = CastleClan;
        lord.KnightsFame = ClanChief;

        TalkToCastleManager(sessionManager, stranger);
        await nation.HandleSiegeAsync(strangerClient, Siege(DelosCollectFunds));
        await nation.HandleSiegeAsync(lordClient, Siege(DelosCollectFunds));

        stranger.Money.Should().Be(0);
        lord.Money.Should().Be(0);
        siege.DungeonCharge.Should().Be(DungeonCharge);

        TalkToCastleManager(sessionManager, lord);
        await nation.HandleSiegeAsync(lordClient, Siege(DelosCollectFunds));

        lord.Money.Should().Be(DungeonCharge);
        siege.DungeonCharge.Should().Be(0);
    }

    [Fact]
    public async Task SiegeFundsNeverOverflowTheCastleLordsPurse()
    {
        var siege = new SiegeWarfareData { CastleIndex = 1, MasterKnights = CastleClan, DungeonCharge = DungeonCharge };
        using var provider = CreateProvider(_ => { }, gameData => gameData.SiegeWarfare.Returns(siege));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var (lord, lordClient, _) = CreatePlayer(sessionManager, 922, 94, AccountNation.Karus);
        lord.KnightsId = CastleClan;
        lord.KnightsFame = ClanChief;
        lord.Money = ExchangePacketConstants.CoinMax - 10;
        TalkToCastleManager(sessionManager, lord);

        await provider.GetRequiredService<INationSystemsPacketCoordinator>()
            .HandleSiegeAsync(lordClient, Siege(DelosCollectFunds));

        lord.Money.Should().Be(ExchangePacketConstants.CoinMax - 10);
        siege.DungeonCharge.Should().Be(DungeonCharge);
    }

    [Fact]
    public async Task OnlyTheCastleLordSetsASaneTariff()
    {
        var siege = new SiegeWarfareData { CastleIndex = 1, MasterKnights = CastleClan, MoradonTariff = StartingTariff };
        using var provider = CreateProvider(_ => { }, gameData => gameData.SiegeWarfare.Returns(siege));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var nation = provider.GetRequiredService<INationSystemsPacketCoordinator>();
        var (stranger, strangerClient, _) = CreatePlayer(sessionManager, 923, 95, AccountNation.Karus);
        var (lord, lordClient, _) = CreatePlayer(sessionManager, 924, 96, AccountNation.Karus);
        lord.KnightsId = CastleClan;
        lord.KnightsFame = ClanChief;
        TalkToCastleManager(sessionManager, stranger);
        TalkToCastleManager(sessionManager, lord);

        await nation.HandleSiegeAsync(strangerClient, Siege(DelosMoradonTariff, NewTariff));
        siege.MoradonTariff.Should().Be(StartingTariff);

        await nation.HandleSiegeAsync(lordClient, Siege(DelosMoradonTariff, ExcessiveTariff));
        siege.MoradonTariff.Should().Be(StartingTariff);

        await nation.HandleSiegeAsync(lordClient, Siege(DelosMoradonTariff, NewTariff));
        siege.MoradonTariff.Should().Be((short)NewTariff);
    }

    private static ServiceProvider CreateKingdom(out UserSession king, out IClient client)
    {
        var kingData = new KingSystemData
        {
            Nation = (byte)AccountNation.Karus, KingName = "Ruler", NationalTreasury = 500_000_000,
        };
        var provider = CreateProvider(
            db => db.Add(kingData),
            gameData =>
            {
                gameData.KingSystemTable.Returns(new Dictionary<byte, KingSystemData>
                {
                    [(byte)AccountNation.Karus] = kingData,
                });
                gameData.GetItem(KingScepter).Returns(new ItemData { Num = KingScepter });
            });
        (king, client, _) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 910, 90, AccountNation.Karus);
        king.Name = "Ruler";
        return provider;
    }

    private static void TalkToCastleManager(SessionManager sessionManager, UserSession session)
    {
        session.Quest.EventNpcUniqueId = sessionManager.Regions.SpawnNpc(new NpcInstance
        {
            NpcId = 522,
            NpcType = NpcData.TypeCastleManager,
            ZoneId = session.ZoneId,
            X = session.X,
            Z = session.Z,
            MaxHp = 1,
            Hp = 1,
        }).UniqueId;
    }

    private static UserSession CreateChief(SessionManager sessionManager, int characterId, string name, short clanId)
    {
        var (chief, _, _) = CreatePlayer(sessionManager, characterId, characterId, AccountNation.Karus);
        chief.Name = name;
        chief.KnightsId = clanId;
        chief.KnightsFame = ClanChief;
        return chief;
    }

    private static async Task<List<string>> CandidatesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().KingElectionList
            .Where(entry => entry.Type == CandidateListType)
            .Select(entry => entry.Name)
            .ToListAsync();
    }

    private static Packet Nominate(string nominee)
    {
        var packet = new Packet(GameOpcodes.GS_KING);
        packet.WriteByte(KingElection);
        packet.WriteByte(ElectionNominate);
        packet.WriteSByteString(nominee);
        return packet;
    }

    private static Packet Vote(string candidate)
    {
        var packet = new Packet(GameOpcodes.GS_KING);
        packet.WriteByte(KingElection);
        packet.WriteByte(ElectionPoll);
        packet.WriteByte(PollCastVote);
        packet.WriteSByteString(candidate);
        return packet;
    }

    private static Packet KingPacket(byte main, byte sub)
    {
        var packet = new Packet(GameOpcodes.GS_KING);
        packet.WriteByte(main);
        packet.WriteByte(sub);
        return packet;
    }

    private static Packet Siege(byte sub, ushort tariff = 0)
    {
        var packet = new Packet(GameOpcodes.GS_SIEGE);
        packet.WriteByte(SiegeDelosNpc);
        packet.WriteByte(sub);
        packet.WriteUShort(tariff);
        return packet;
    }

    private static (UserSession Session, IClient Client, List<Packet> Sent) CreatePlayer(
        SessionManager sessionManager, int characterId, int accountId, AccountNation nation)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(packet => sent.Add(packet)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = sessionManager.CreateSession(client, characterId, accountId);
        session.Name = $"Citizen{characterId}";
        session.Nation = nation;
        session.Level = VoterLevel;
        session.ZoneId = Delos;
        session.X = 100;
        session.Z = 100;
        session.MaxHp = 100;
        session.Hp = 100;
        sessionManager.Regions.AddToRegion(session);
        return (session, client, sent);
    }
}
