using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class WarEventAuthorityTests : GameTestBase
{
    private const byte SubMapEventResult = 2;
    private const byte SubEventResult = 3;
    private const byte SubMaxUser = 4;
    private const byte ClaimedGatekeeperType = 7;
    private const byte DeclareWinner = 2;
    private const byte WarZone = BattleZoneManager.ZONE_BATTLE1;
    private const byte OtherWarZone = BattleZoneManager.ZONE_BATTLE2;
    private const int KarusGatekeeper = 21041;
    private const int KarusGarrisonCaptain1 = 21042;
    private const int KarusGarrisonCaptain2 = 21045;
    private const int OrdinaryMonster = 750;

    [Fact]
    public async Task AClientCannotDeclareWarNpcKillsOrWinTheWar()
    {
        using var provider = CreateWarProvider();
        var battle = OpenWar(provider, WarZone);
        var (_, client, _) = CreatePlayer(provider, 3000, AccountNation.ElMorad);
        var events = provider.GetRequiredService<IEventSystemsPacketCoordinator>();

        for (var claim = 0; claim < BattleZoneManager.NPC_KILL_VICTORY_COUNT; claim++)
            await events.HandleBattleEventAsync(client, BattleEvent(SubEventResult, 0));

        battle.Victory.Should().Be(0);
        battle.KilledKarusNpc.Should().Be(0);
        battle.KilledElmoNpc.Should().Be(0);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().BeGreaterThanOrEqualTo(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task AClientCannotClaimWarderOrGatekeeperLoyalty()
    {
        using var provider = CreateWarProvider();
        OpenWar(provider, WarZone);
        var (session, client, _) = CreatePlayer(provider, 3001, AccountNation.ElMorad);
        var events = provider.GetRequiredService<IEventSystemsPacketCoordinator>();

        await events.HandleBattleEventAsync(client, BattleEvent(SubMaxUser, ClaimedGatekeeperType));

        session.Loyalty.Should().Be(0);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task AClientCannotOpenTheEnemyHomeland()
    {
        using var provider = CreateWarProvider();
        var battle = OpenWar(provider, WarZone);
        var (_, client, _) = CreatePlayer(provider, 3002, AccountNation.ElMorad);
        var events = provider.GetRequiredService<IEventSystemsPacketCoordinator>();

        await events.HandleBattleEventAsync(client, BattleEvent(SubMapEventResult, (byte)AccountNation.Karus));

        battle.KarusOpenFlag.Should().BeFalse();
        battle.ElmoradOpenFlag.Should().BeFalse();
    }

    [Fact]
    public async Task KillingTheEnemyWarNpcsPaysLoyaltyAndWinsTheWar()
    {
        using var provider = CreateWarProvider();
        var battle = OpenWar(provider, WarZone);
        var (killer, _, sent) = CreatePlayer(provider, 3003, AccountNation.ElMorad);

        await KillAsync(provider, killer, KarusGatekeeper, WarZone);
        killer.Loyalty.Should().Be(BattleZoneManager.GATEKEEPER_KILL_LOYALTY);
        battle.Victory.Should().Be(0);

        await KillAsync(provider, killer, KarusGarrisonCaptain1, WarZone);
        await KillAsync(provider, killer, KarusGarrisonCaptain2, WarZone);

        killer.Loyalty.Should().Be(BattleZoneManager.GATEKEEPER_KILL_LOYALTY + 2 * BattleZoneManager.WARDER_KILL_LOYALTY);
        battle.KilledKarusNpc.Should().Be(BattleZoneManager.NPC_KILL_VICTORY_COUNT);
        battle.Victory.Should().Be((byte)AccountNation.ElMorad);
        battle.KarusOpenFlag.Should().BeTrue();
        sent.Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_BATTLE_EVENT)
            .Select(packet => packet.GetBytes())
            .Should().ContainSingle(bytes => bytes[1] == SubEventResult && bytes[2] == DeclareWinner
                && bytes[3] == (byte)AccountNation.ElMorad);
    }

    [Fact]
    public async Task WarNpcsCountOnlyWhileTheWarRunsInTheirZone()
    {
        using var provider = CreateWarProvider();
        var (killer, _, _) = CreatePlayer(provider, 3004, AccountNation.ElMorad);
        var battle = provider.GetRequiredService<SessionManager>().Battle;

        await KillAsync(provider, killer, KarusGatekeeper, WarZone);
        OpenWar(provider, OtherWarZone);
        await KillAsync(provider, killer, KarusGatekeeper, WarZone);

        killer.Loyalty.Should().Be(0);
        battle.KilledKarusNpc.Should().Be(0);
    }

    [Fact]
    public async Task ANationScoresNothingForItsOwnWarNpcsOrForOrdinaryMonsters()
    {
        using var provider = CreateWarProvider();
        var battle = OpenWar(provider, WarZone);
        var (karus, _, _) = CreatePlayer(provider, 3005, AccountNation.Karus);
        var (elmorad, _, _) = CreatePlayer(provider, 3006, AccountNation.ElMorad);

        await KillAsync(provider, karus, KarusGatekeeper, WarZone);
        await KillAsync(provider, elmorad, OrdinaryMonster, WarZone);

        karus.Loyalty.Should().Be(0);
        elmorad.Loyalty.Should().Be(0);
        battle.KilledKarusNpc.Should().Be(0);
        battle.KilledElmoNpc.Should().Be(0);
    }

    private static ServiceProvider CreateWarProvider()
        => CreateProvider(
            _ => { },
            gameData => gameData.NpcPositions.Returns(new List<NpcPosData>
            {
                WarNpc(KarusGatekeeper, BattleZoneManager.SPECIAL_KARUS_GATEKEEPER),
                WarNpc(KarusGarrisonCaptain1, BattleZoneManager.SPECIAL_KARUS_WARDER1),
                WarNpc(KarusGarrisonCaptain2, BattleZoneManager.SPECIAL_KARUS_WARDER2),
                WarNpc(OrdinaryMonster, 0),
            }),
            configureServices: services => services.AddSingleton<TimeProvider>(new ManualClock()));

    private static NpcPosData WarNpc(int npcId, short specialType) => new()
    {
        ZoneId = WarZone,
        NpcId = npcId,
        SpecialType = specialType,
        NumNPC = 1,
    };

    private static BattleZoneManager OpenWar(ServiceProvider provider, byte zone)
    {
        var battle = provider.GetRequiredService<SessionManager>().Battle;
        battle.OpenBattleZone(BattleZoneManager.NATION_BATTLE, zone).Should().BeTrue();
        return battle;
    }

    private static async Task KillAsync(ServiceProvider provider, UserSession killer, int npcId, byte zone)
    {
        var npc = provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = npcId,
            Name = "War NPC",
            ZoneId = zone,
            X = killer.X,
            Z = killer.Z,
            MaxHp = 100,
            Hp = 0,
            NpcType = NpcData.TypeGuard,
            Nation = EntityNation.Karus,
        });

        await provider.GetRequiredService<ICombatLifecycleService>().HandleNpcDeathAsync(npc, killer);
    }

    private static Packet BattleEvent(byte subOpcode, byte value)
    {
        var packet = new Packet(GameOpcodes.GS_BATTLE_EVENT);
        packet.WriteByte(subOpcode);
        packet.WriteByte(value);
        packet.ResetOffset();
        return packet;
    }

    private static (UserSession Session, IClient Client, List<Packet> Sent) CreatePlayer(
        ServiceProvider provider, int characterId, AccountNation nation)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(packet => sent.Add(packet)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = $"Soldier{characterId}";
        session.Nation = nation;
        session.ZoneId = WarZone;
        session.X = 100;
        session.Z = 100;
        session.MaxHp = 100;
        session.Hp = 100;
        sessionManager.Regions.AddToRegion(session);
        return (session, client, sent);
    }
}
