using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class WorldAuthorityTests : GameTestBase
{
    private const byte Moradon = (byte)ZoneId.Moradon;
    private const byte KarusHome = (byte)ZoneId.KarusCamp1;
    private const byte ElMoradHome = (byte)ZoneId.ElMoradCamp1;
    private const byte KarusEslant = (byte)ZoneId.KarusEslant1;
    private const byte RonarkLand = (byte)ZoneId.RonarkLand;
    private const byte NapiesGorge = (byte)ZoneId.NapiesGorge;
    private const byte Prison = (byte)ZoneId.Prison;

    private const int MapSize = 64;
    private const float UnitDistance = 4f;
    private const short ExitEvent = 1001;
    private const byte ZoneChangeEvent = 1;
    private const int ArrivalX = 150;
    private const int ArrivalZ = 160;

    private const short GateIndex = 4014;
    private const short GateGroup = 211;
    private const float GateX = 100f;
    private const float GateZ = 100f;
    private const byte WarpGateObject = 5;
    private const byte GateObject = 1;
    private const short FolkVillage = 2111;
    private const short LufersonCastle = 2113;
    private const short RonarkLandWarp = 2118;
    private const short ForeignGroupWarp = 2211;
    private const uint LufersonFee = 5000;

    private const float PositionScale = 10f;
    private const byte MoveEchoMove = 3;
    private const byte ZoneChangeTeleport = 3;
    private const byte ZoneChangeClientLoaded = 1;
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task ZoneExit_IntoAClosedWarZone_LeavesThePlayerWhereHeIs()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(KarusHome, NapiesGorge));
        var player = CreatePlayer(provider, 2001, KarusHome, AccountNation.Karus, level: 60);

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.ZoneId.Should().Be(KarusHome);
        player.Sent.Should().NotContain(packet => IsTeleport(packet));
    }

    [Fact]
    public async Task ZoneExit_IntoTheOpenWarZone_TakesThePlayerThere()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(KarusHome, NapiesGorge));
        provider.GetRequiredService<SessionManager>().Battle
            .OpenBattleZone(BattleZoneManager.NATION_BATTLE, NapiesGorge);
        var player = CreatePlayer(provider, 2002, KarusHome, AccountNation.Karus, level: 60);

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.ZoneId.Should().Be(NapiesGorge);
        player.Sent.Should().Contain(packet => IsTeleport(packet));
    }

    [Theory]
    [InlineData((byte)59, KarusHome)]
    [InlineData((byte)60, KarusEslant)]
    public async Task ZoneExit_IntoEslant_RequiresItsLevel(byte level, byte expectedZone)
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(KarusHome, KarusEslant));
        var player = CreatePlayer(provider, 2003, KarusHome, AccountNation.Karus, level);

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.ZoneId.Should().Be(expectedZone);
    }

    [Fact]
    public async Task ZoneExit_IntoTheEnemyHomeland_StaysShut()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(RonarkLand, KarusHome));
        var player = CreatePlayer(provider, 2004, RonarkLand, AccountNation.ElMorad, level: 80);

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.ZoneId.Should().Be(RonarkLand);
    }

    [Fact]
    public async Task ZoneExit_AGameMasterIsNotHeldByEntryRules()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(KarusHome, NapiesGorge));
        var player = CreatePlayer(provider, 2005, KarusHome, AccountNation.Karus, level: 1);
        player.Session.IsGM = true;

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.ZoneId.Should().Be(NapiesGorge);
    }

    [Fact]
    public async Task WarpList_AWarpNobodyOffered_IsRefusedAndCostsNothing()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2010, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await SelectWarpAsync(provider, player, FolkVillage);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX);
        LastWarpResult(player).Should().Be(WarpListPacketWriter.ResultNotQualified);
    }

    [Fact]
    public async Task WarpList_WalkingAwayFromTheGate_VoidsItsList()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2011, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        player.Session.X = GateX + 40;

        await SelectWarpAsync(provider, player, FolkVillage);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX + 40);
    }

    [Fact]
    public async Task WarpList_OnlyTheOpenedGatesDestinationsCanBeChosen()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2012, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        await SelectWarpAsync(provider, player, ForeignGroupWarp);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX);
    }

    [Fact]
    public async Task WarpList_ANoFeeIsTakenWhileAZoneChangeIsStillLoading()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2013, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        player.Session.IsWarping = true;
        await SelectWarpAsync(provider, player, LufersonCastle);

        player.Session.Money.Should().Be(10_000);
        player.Session.ZoneId.Should().Be(Moradon);
        LastWarpResult(player).Should().Be(WarpListPacketWriter.ResultNotQualified);
    }

    [Fact]
    public async Task WarpList_TheDeadCannotTravel()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2014, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        player.Session.Hp = 0;
        await SelectWarpAsync(provider, player, FolkVillage);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX);
    }

    [Fact]
    public async Task WarpList_AnOfferExpires()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2015, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        clock.Advance(TimeSpan.FromMinutes(5));
        await SelectWarpAsync(provider, player, FolkVillage);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX);
    }

    [Fact]
    public async Task WarpList_BelowTheDestinationsLevel_AnswersLevelTooLow()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2016, Moradon, AccountNation.Karus, level: 20, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        await SelectWarpAsync(provider, player, LufersonCastle);

        player.Session.ZoneId.Should().Be(Moradon);
        player.Session.Money.Should().Be(10_000);
        LastWarpResult(player).Should().Be(WarpListPacketWriter.ResultLevelTooLow);
    }

    [Fact]
    public async Task WarpList_WithoutNationalPoints_AnswersNoNationalPoints()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2017, Moradon, AccountNation.Karus, level: 75, x: GateX, z: GateZ);
        player.Session.Money = 100_000;
        player.Session.Loyalty = 0;

        await OpenGateAsync(provider, player);
        await SelectWarpAsync(provider, player, RonarkLandWarp);

        player.Session.ZoneId.Should().Be(Moradon);
        player.Session.Money.Should().Be(100_000);
        LastWarpResult(player).Should().Be(WarpListPacketWriter.ResultNoNationalPoints);
    }

    [Fact]
    public async Task WarpList_AQualifiedTravellerPaysOnceAndArrives()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2018, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        await SelectWarpAsync(provider, player, LufersonCastle);
        await SelectWarpAsync(provider, player, LufersonCastle);

        player.Session.ZoneId.Should().Be(KarusHome);
        player.Session.Money.Should().Be(10_000 - (int)LufersonFee);
    }

    [Fact]
    public async Task WarpList_AKeepersListOnlyWorksBesideTheKeeper()
    {
        const short keeperId = 950;
        var clock = new ManualClock();
        using var provider = CreateWorld(
            clock,
            gameData => gameData.GetNpc(keeperId, false).Returns(new NpcData { Id = keeperId, Group = (byte)(GateGroup / 10) }));
        UseMaps(provider, CreateMapManagerWithObjectEvent(
            Moradon,
            new ObjectEvent { Index = GateIndex, Type = WarpGateObject, ControlNpcId = GateGroup, PosX = 500, PosZ = 500 },
            new WarpInfo { WarpId = 211, Name = "Town", Fee = 100, Zone = Moradon, X = 30, Z = 40 }));
        var player = CreatePlayer(provider, 2019, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;
        var keeper = provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = keeperId, ZoneId = Moradon, X = GateX, Z = GateZ, MaxHp = 100, Hp = 100,
        });
        player.Session.Quest.EventNpcUniqueId = keeper.UniqueId;

        var request = new Packet(GameOpcodes.GS_WARP_LIST);
        request.WriteShort(keeperId);
        await World(provider).HandleWarpListAsync(player.Client, request);
        player.Sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP_LIST);

        player.Session.X = GateX + 40;
        await SelectWarpAsync(provider, player, 211);

        player.Session.Money.Should().Be(10_000);
        player.Session.X.Should().Be(GateX + 40);
    }

    [Fact]
    public async Task Corpse_AStrangerCannotLocateALivingPlayer()
    {
        using var provider = CreateWorld(new ManualClock());
        var seeker = CreatePlayer(provider, 2020, Moradon, AccountNation.Karus, level: 40);
        var target = CreatePlayer(provider, 2021, KarusHome, AccountNation.ElMorad, level: 40, x: 900, z: 900);

        await LocateCorpseAsync(provider, seeker, target.Session.CharacterId);

        seeker.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CORPSE);
    }

    [Fact]
    public async Task Corpse_AnswersForADeadPartyMemberInTheSameZone()
    {
        using var provider = CreateWorld(new ManualClock());
        var seeker = CreatePlayer(provider, 2022, Moradon, AccountNation.Karus, level: 40);
        var fallen = CreatePlayer(provider, 2023, Moradon, AccountNation.Karus, level: 40, x: 200, z: 210);
        PartyUp(provider, seeker.Session, fallen.Session);
        fallen.Session.Hp = 0;

        await LocateCorpseAsync(provider, seeker, fallen.Session.CharacterId);

        var answer = seeker.Sent.Single(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CORPSE);
        answer.ResetOffset();
        answer.ReadShort().Should().Be((short)fallen.Session.CharacterId);
        answer.ReadShort().Should().Be(2000);
        answer.ReadShort().Should().Be(2100);
    }

    [Fact]
    public async Task Corpse_ALivingPartyMemberOrOneInAnotherZoneStaysUnlocated()
    {
        using var provider = CreateWorld(new ManualClock());
        var seeker = CreatePlayer(provider, 2024, Moradon, AccountNation.Karus, level: 40);
        var alive = CreatePlayer(provider, 2025, Moradon, AccountNation.Karus, level: 40, x: 200, z: 210);
        var elsewhere = CreatePlayer(provider, 2026, KarusHome, AccountNation.Karus, level: 40, x: 200, z: 210);
        PartyUp(provider, seeker.Session, alive.Session, elsewhere.Session);
        elsewhere.Session.Hp = 0;

        await LocateCorpseAsync(provider, seeker, alive.Session.CharacterId);
        await LocateCorpseAsync(provider, seeker, elsewhere.Session.CharacterId);

        seeker.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CORPSE);
    }

    [Fact]
    public async Task Stealth_AnEnemyStopsReceivingTheHiddenPlayersMoves()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var (rogue, enemy, ally) = CreateStealthScene(provider);

        await provider.GetRequiredService<IStealthService>().HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        InOuts(enemy, rogue, InOutType.Out).Should().Be(1);

        enemy.Sent.Clear();
        ally.Sent.Clear();
        await StepAsync(provider, clock, rogue, 100.5f, 100);
        provider.GetRequiredService<MovementBroadcastService>().FlushMovers();

        enemy.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MOVE);
        ally.Sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MOVE);
    }

    [Fact]
    public async Task Stealth_TheNearbySnapshotLeavesOutAnUndetectedEnemy()
    {
        using var provider = CreateWorld(new ManualClock());
        var (rogue, enemy, ally) = CreateStealthScene(provider);
        await provider.GetRequiredService<IStealthService>().HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        enemy.Sent.Clear();

        var request = new Packet(GameOpcodes.GS_REQ_USERIN);
        request.WriteUShort(2);
        request.WriteInt(rogue.Session.CharacterId);
        request.WriteInt(ally.Session.CharacterId);
        await World(provider).HandleReqUserInAsync(enemy.Client, request);

        var snapshot = enemy.Sent.Single(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_REQ_USERIN);
        snapshot.ResetOffset();
        snapshot.ReadShort().Should().Be(1);
        snapshot.ReadByte();
        snapshot.ReadInt().Should().Be(ally.Session.CharacterId);
    }

    [Fact]
    public async Task Stealth_TheRegionListAndTheNearbyPanelLeaveOutAnUndetectedEnemy()
    {
        using var provider = CreateWorld(new ManualClock());
        var (rogue, enemy, _) = CreateStealthScene(provider);
        await provider.GetRequiredService<IStealthService>().HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        enemy.Sent.Clear();

        await provider.GetRequiredService<IWorldVisibilityService>().SendRegionUserListAsync(enemy.Session);
        var panel = new Packet(GameOpcodes.GS_USER_INFO);
        panel.WriteByte((byte)PlayerInspectSubOpcode.Nearby);
        await World(provider).HandleBottomUserListAsync(enemy.Client, panel);

        RegionListIds(enemy).Should().NotContain(rogue.Session.CharacterId);
        var nearby = enemy.Sent.Last(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_USER_INFO);
        nearby.ResetOffset();
        nearby.ReadByte();
        nearby.ReadByte();
        nearby.ReadShort();
        nearby.ReadByte();
        nearby.ReadShort().Should().Be(1, "only the ally is listed");
    }

    [Fact]
    public async Task Stealth_EndingStealthShowsThePlayerToTheEnemyAgain()
    {
        using var provider = CreateWorld(new ManualClock());
        var (rogue, enemy, _) = CreateStealthScene(provider);
        var stealth = provider.GetRequiredService<IStealthService>();
        await stealth.HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        enemy.Sent.Clear();

        await stealth.RevealAsync(rogue.Session, InvisibilityType.None);

        InOuts(enemy, rogue, InOutType.In).Should().Be(1);
    }

    [Fact]
    public async Task Stealth_SeeingTheInvisibleRevealsAndThenHidesTheEnemy()
    {
        using var provider = CreateWorld(new ManualClock());
        var (rogue, enemy, _) = CreateStealthScene(provider);
        var stealth = provider.GetRequiredService<IStealthService>();
        await stealth.HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        enemy.Sent.Clear();

        await stealth.GrantSightAsync(enemy.Session, 25);
        InOuts(enemy, rogue, InOutType.In).Should().Be(1);

        await stealth.ClearSightAsync(enemy.Session);
        InOuts(enemy, rogue, InOutType.Out).Should().Be(1);
    }

    [Fact]
    public async Task Stealth_AGameMasterStillSeesTheHidden()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var (rogue, enemy, _) = CreateStealthScene(provider);
        enemy.Session.IsGM = true;

        await provider.GetRequiredService<IStealthService>().HideAsync(rogue.Session, InvisibilityType.DispelOnAttack);
        await StepAsync(provider, clock, rogue, 100.5f, 100);
        provider.GetRequiredService<MovementBroadcastService>().FlushMovers();

        InOuts(enemy, rogue, InOutType.Out).Should().Be(0);
        enemy.Sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MOVE);
    }

    [Fact]
    public async Task Prison_WorksOnAPlayerWhoWithheldHisZoneChangeAcknowledgement()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var gm = CreatePlayer(provider, 2030, Moradon, AccountNation.Karus, level: 83);
        gm.Session.IsGM = true;
        var target = CreatePlayer(provider, 2031, Moradon, AccountNation.Karus, level: 40, x: 300, z: 300);
        await provider.GetRequiredService<IZoneTransitionService>().ChangeZoneAsync(target.Session, KarusHome, 400, 400);
        target.Session.IsWarping.Should().BeTrue();

        await provider.GetRequiredService<IAdminPacketCoordinator>()
            .HandleGmCommandAsync(gm.Session, $"+prison {target.Session.Name}");

        target.Session.ZoneId.Should().Be(Prison);
        LastNotice(gm).Should().Be($"{target.Session.Name} sent to prison.");
    }

    [Fact]
    public async Task Prison_ReportsAFailureInsteadOfClaimingSuccess()
    {
        using var provider = CreateWorld(
            new ManualClock(),
            gameData => gameData.ZoneInfoTable.Returns(new Dictionary<short, ZoneInfoData>
            {
                [Moradon] = new() { ZoneNo = Moradon, MapName = "Moradon" },
            }));
        var gm = CreatePlayer(provider, 2032, Moradon, AccountNation.Karus, level: 83);
        gm.Session.IsGM = true;
        var target = CreatePlayer(provider, 2033, Moradon, AccountNation.Karus, level: 40, x: 300, z: 300);

        await provider.GetRequiredService<IAdminPacketCoordinator>()
            .HandleGmCommandAsync(gm.Session, $"+prison {target.Session.Name}");

        target.Session.ZoneId.Should().Be(Moradon);
        LastNotice(gm).Should().Be($"Could not send {target.Session.Name} to prison.");
    }

    [Fact]
    public async Task ZoneChange_AnUnacknowledgedArrivalIsCompletedByTheServer()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(KarusHome, KarusEslant));
        var player = CreatePlayer(provider, 2034, KarusHome, AccountNation.Karus, level: 60);
        var watcher = CreatePlayer(provider, 2035, KarusEslant, AccountNation.Karus, level: 60, x: ArrivalX, z: ArrivalZ);

        await StepAsync(provider, clock, player, 100.5f, 100);
        player.Session.ZoneId.Should().Be(KarusEslant);
        player.Session.IsWarping.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(1));
        await provider.GetRequiredService<MovementBroadcastService>().SettleAsync();

        player.Session.IsWarping.Should().BeFalse();
        InOuts(watcher, player, InOutType.Warp).Should().Be(1);
    }

    [Fact]
    public async Task ZoneChange_TheArrivalSnapshotIsOnlyServedOncePerZoneChange()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2036, Moradon, AccountNation.Karus, level: 40);
        CreatePlayer(provider, 2037, Moradon, AccountNation.Karus, level: 40, x: 101, z: 100);

        await ZoneChangeStepAsync(provider, player, ZoneChangeClientLoaded);
        player.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_REQ_USERIN);

        await provider.GetRequiredService<IZoneTransitionService>().ChangeZoneAsync(player.Session, Moradon, 100, 100);
        await ZoneChangeStepAsync(provider, player, ZoneChangeClientLoaded);
        await ZoneChangeStepAsync(provider, player, ZoneChangeClientLoaded);

        player.Sent.Count(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_REQ_USERIN).Should().Be(1);
    }

    [Fact]
    public async Task Banish_SendsEveryoneInTheClosedBattleZoneHome()
    {
        using var provider = CreateWorld(new ManualClock());
        var karus = CreatePlayer(provider, 2038, NapiesGorge, AccountNation.Karus, level: 60);
        var elmorad = CreatePlayer(provider, 2039, NapiesGorge, AccountNation.ElMorad, level: 60, x: 120, z: 120);
        karus.Session.IsWarping = true;

        await provider.GetRequiredService<EventSchedulerService>().BanishFromBattleZonesAsync();

        karus.Session.ZoneId.Should().Be(KarusHome);
        elmorad.Session.ZoneId.Should().Be(ElMoradHome);
        karus.Sent.Should().Contain(packet => IsTeleport(packet));
        elmorad.Sent.Should().Contain(packet => IsTeleport(packet));
    }

    [Fact]
    public async Task Move_StandsASittingPlayerUpForEveryone()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var player = CreatePlayer(provider, 2040, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2041, Moradon, AccountNation.Karus, level: 40, x: 101, z: 100);
        await ChangeStateAsync(provider, player, (byte)StateChangeType.Pose, (byte)UserPoseState.Sitting);
        watcher.Sent.Clear();

        await StepAsync(provider, clock, player, 100.5f, 100);

        player.Session.IsSitting.Should().BeFalse();
        PosesOf(watcher, player).Should().Equal((int)UserPoseState.Standing);
    }

    [Fact]
    public async Task Home_IsRefusedUnderTheNoRecallCurse()
    {
        using var provider = CreateWorld(new ManualClock(), WithMoradonStart);
        var player = CreatePlayer(provider, 2042, Moradon, AccountNation.Karus, level: 40);
        player.Session.CanTeleport = false;

        await World(provider).HandleHomeAsync(player.Client);

        player.Session.X.Should().Be(100);
        player.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        player.Sent.Should().ContainSingle(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT);
    }

    [Fact]
    public async Task Home_IsRefusedWhileAZoneChangeIsLoading()
    {
        using var provider = CreateWorld(new ManualClock(), WithMoradonStart);
        var player = CreatePlayer(provider, 2043, Moradon, AccountNation.Karus, level: 40);
        player.Session.IsWarping = true;

        await World(provider).HandleHomeAsync(player.Client);

        player.Session.X.Should().Be(100);
    }

    [Fact]
    public async Task Home_CannotBeSpammed()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock, WithMoradonStart);
        var player = CreatePlayer(provider, 2044, Moradon, AccountNation.Karus, level: 40);

        await World(provider).HandleHomeAsync(player.Client);
        player.Session.X.Should().Be(816);

        player.Session.X = 100;
        await World(provider).HandleHomeAsync(player.Client);
        player.Session.X.Should().Be(100);

        clock.Advance(TimeSpan.FromMinutes(1));
        await World(provider).HandleHomeAsync(player.Client);
        player.Session.X.Should().Be(816);
    }

    [Theory]
    [InlineData((byte)StateChangeType.Abnormal, 5000)]
    [InlineData((byte)StateChangeType.Visibility, 1)]
    [InlineData((byte)StateChangeType.Transformation, 107550)]
    [InlineData((byte)StateChangeType.Stealth, 1)]
    public async Task StateChange_AServerOwnedStateIsRefusedAndScored(byte type, int value)
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2045, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2046, Moradon, AccountNation.ElMorad, level: 40, x: 101, z: 100);

        await ChangeStateAsync(provider, player, type, value);

        watcher.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_STATE_CHANGE);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(player.Client.Id)
            .Should().Be(ViolationMonitor.InvalidRequestWeight);
    }

    [Theory]
    [InlineData((byte)StateChangeType.NeedParty, 1)]
    [InlineData((byte)StateChangeType.Action, 11)]
    [InlineData((byte)StateChangeType.Pose, (int)UserPoseState.Dead)]
    [InlineData((byte)StateChangeType.CombatStance, 7)]
    public async Task StateChange_AStateTheClientDoesNotDrawIsDroppedWithoutPenalty(byte type, int value)
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2049, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2146, Moradon, AccountNation.ElMorad, level: 40, x: 101, z: 100);

        await ChangeStateAsync(provider, player, type, value);

        watcher.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_STATE_CHANGE);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(player.Client.Id).Should().Be(0);
    }

    [Fact]
    public async Task StateChange_TheDeadCannotStandUp()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2047, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2048, Moradon, AccountNation.ElMorad, level: 40, x: 101, z: 100);
        player.Session.Hp = 0;

        await ChangeStateAsync(provider, player, (byte)StateChangeType.Pose, (byte)UserPoseState.Standing);

        watcher.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_STATE_CHANGE);
    }

    [Fact]
    public async Task RegionBoundary_OscillatingAcrossItDoesNotFloodTheNeighbours()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var mover = CreatePlayer(provider, 2050, Moradon, AccountNation.Karus, level: 40, x: 47.8f, z: 10);
        var distant = CreatePlayer(provider, 2051, Moradon, AccountNation.Karus, level: 40, x: 140, z: 10);

        for (var step = 0; step < 10; step++)
            await StepAsync(provider, clock, mover, step % 2 == 0 ? 48.2f : 47.8f, 10);

        (InOuts(distant, mover, InOutType.In) + InOuts(distant, mover, InOutType.Out))
            .Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task RegionBoundary_ACrossingThatWaitedOutTheCooldownIsSettledByTheServer()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var mover = CreatePlayer(provider, 2052, Moradon, AccountNation.Karus, level: 40, x: 47.8f, z: 10);
        var distant = CreatePlayer(provider, 2053, Moradon, AccountNation.Karus, level: 40, x: 140, z: 10);

        await StepAsync(provider, clock, mover, 48.2f, 10);
        await StepAsync(provider, clock, mover, 47.8f, 10);
        mover.Session.RegionX.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        await provider.GetRequiredService<MovementBroadcastService>().SettleAsync();

        mover.Session.RegionX.Should().Be(0);
        InOuts(distant, mover, InOutType.Out).Should().Be(1);
    }

    [Fact]
    public async Task Move_TheClaimedHeightIsKeptNearTheGround()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, CreateMapManagerWithTiles(Moradon, MapSize, UnitDistance));
        var player = CreatePlayer(provider, 2054, Moradon, AccountNation.Karus, level: 40);

        await StepAsync(provider, clock, player, 100.5f, 100, y: 6000);

        player.Session.Y.Should().BeLessThanOrEqualTo(
            new LibreKO.Game.Configuration.TravelCheckSettings().MaxHeightAboveGround);
        player.Session.MoveOldWillY.Should().Be((ushort)(player.Session.Y * PositionScale));
    }

    [Fact]
    public async Task Move_TheMoverIsNotSentItsOwnMove()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        var player = CreatePlayer(provider, 2055, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2056, Moradon, AccountNation.Karus, level: 40, x: 101, z: 100);

        await StepAsync(provider, clock, player, 100.5f, 100);
        provider.GetRequiredService<MovementBroadcastService>().FlushMovers();

        player.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MOVE);
        watcher.Sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MOVE);
    }

    [Fact]
    public async Task ObjectEvent_AGateEventOnlyMovesTheGateBoundToIt()
    {
        const short gateObject = 1300;
        using var provider = CreateWorld(new ManualClock());
        UseMaps(provider, CreateMapManagerWithObjectEvent(
            NapiesGorge,
            new ObjectEvent { Index = gateObject, Type = GateObject, Belong = 1, PosX = 100, PosZ = 100 }));
        var regions = provider.GetRequiredService<SessionManager>().Regions;
        var gate = regions.SpawnNpc(new NpcInstance
        {
            NpcId = gateObject, ZoneId = NapiesGorge, X = 100, Z = 100, MaxHp = 100, Hp = 100,
            Nation = EntityNation.Karus,
        });
        var decoy = regions.SpawnNpc(new NpcInstance
        {
            NpcId = 1301, ZoneId = NapiesGorge, X = 900, Z = 900, MaxHp = 100, Hp = 100,
            Nation = EntityNation.Karus,
        });
        var player = CreatePlayer(provider, 2057, NapiesGorge, AccountNation.Karus, level: 60);

        var packet = new Packet(GameOpcodes.GS_OBJECT_EVENT);
        packet.WriteShort(gateObject);
        packet.WriteInt(decoy.UniqueId);
        await World(provider).HandleObjectEventAsync(player.Client, packet);

        decoy.GateOpen.Should().BeFalse();
        gate.GateOpen.Should().BeTrue();
    }

    [Fact]
    public async Task SpeedHackCheck_CannotPullThePlayerBackToAnEarlierSpot()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2058, Moradon, AccountNation.Karus, level: 40);
        var speedCheck = provider.GetRequiredService<IInGameOpcodeRouter>().Resolve(GameOpcodes.GS_SPEEDHACK_CHECK);
        speedCheck.Should().NotBeNull();

        await speedCheck!(player.Client, new Packet(GameOpcodes.GS_SPEEDHACK_CHECK));
        player.Session.X = 220;
        await speedCheck(player.Client, new Packet(GameOpcodes.GS_SPEEDHACK_CHECK));

        player.Session.X.Should().Be(220);
        player.Sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
    }

    [Fact]
    public async Task Relocations_FromManyThreads_LeaveThePlayerInExactlyOneRegion()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2059, Moradon, AccountNation.Karus, level: 40);
        var transitions = provider.GetRequiredService<IZoneTransitionService>();
        var world = provider.GetRequiredService<IWorldMovementService>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (index, _) =>
        {
            if (index % 2 == 0)
                await transitions.ChangeZoneAsync(player.Session, index % 4 == 0 ? Moradon : KarusHome, 100 + index, 100);
            else
                await world.WarpAsync(player.Session, (ushort)((300 + index) * PositionScale), 1000);
        });

        RegistrationsOf(provider, player.Session).Should().Be(1);
        player.Session.RegisteredRegionKey.Should().NotBe(RegionManager.NoRegionKey);
    }

    [Fact]
    public async Task ZoneExit_ARefusedGateSaysWhyOnceAndRetriesAfterAPause()
    {
        var clock = new ManualClock();
        using var provider = CreateWorld(clock);
        UseMaps(provider, ZoneExitMap(RonarkLand, KarusHome));
        var player = CreatePlayer(provider, 2060, RonarkLand, AccountNation.ElMorad, level: 80);

        await StepAsync(provider, clock, player, 100.5f, 100);
        await StepAsync(provider, clock, player, 101, 100);

        Notices(player).Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(WorldMovementService.ZoneGateRetrySeconds));
        await StepAsync(provider, clock, player, 101.5f, 100);

        Notices(player).Should().Be(2);
        player.Session.ZoneId.Should().Be(RonarkLand);
    }

    [Fact]
    public async Task WarpList_AMerchantCannotLeaveThroughAGate()
    {
        using var provider = CreateWorld(new ManualClock());
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2061, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        player.Session.Trade.MerchantState = MerchantMode.Selling;
        await SelectWarpAsync(provider, player, LufersonCastle);

        player.Session.ZoneId.Should().Be(Moradon);
        player.Session.Money.Should().Be(10_000);
    }

    [Fact]
    public async Task WarpList_ATraderCannotHopAcrossTheZone()
    {
        using var provider = CreateWorld(new ManualClock());
        UseMaps(provider, MoradonGateMap());
        var player = CreatePlayer(provider, 2062, Moradon, AccountNation.Karus, level: 40, x: GateX, z: GateZ);
        player.Session.Money = 10_000;

        await OpenGateAsync(provider, player);
        player.Session.Trade.ExchangeUser = 2063;
        player.Session.Trade.ExchangeStarted = true;
        await SelectWarpAsync(provider, player, FolkVillage);

        player.Session.X.Should().Be(GateX);
        player.Session.Money.Should().Be(10_000);
        LastWarpResult(player).Should().Be(WarpListPacketWriter.ResultNotQualified);
    }

    [Fact]
    public async Task InstanceEntry_ThePlayersLeftBehindSeeHimGo()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2064, Moradon, AccountNation.Karus, level: 40);
        var watcher = CreatePlayer(provider, 2065, Moradon, AccountNation.Karus, level: 40, x: 101, z: 100);
        var rooms = provider.GetRequiredService<InstanceRoomRegistry>();
        var room = rooms.Open(KarusHome, set: 1, TimeSpan.FromMinutes(1));

        rooms.Join(room, player.Session);
        await provider.GetRequiredService<IZoneTransitionService>().ChangeZoneAsync(player.Session, KarusHome, 100, 100);

        InOuts(watcher, player, InOutType.Out).Should().Be(1);
    }

    [Fact]
    public void Reach_PlayersInDifferentRoomsAreNeverInRange()
    {
        using var provider = CreateWorld(new ManualClock());
        var player = CreatePlayer(provider, 2066, Moradon, AccountNation.Karus, level: 40);
        var other = CreatePlayer(provider, 2067, Moradon, AccountNation.Karus, level: 40, x: 101, z: 100);
        var npc = new NpcInstance { ZoneId = Moradon, X = 101, Z = 100, Room = 1 };

        Reach.Within(player.Session, other.Session, UnitDistance).Should().BeTrue();

        other.Session.Room = 1;

        Reach.Within(player.Session, other.Session, UnitDistance).Should().BeFalse();
        Reach.CanInteract(player.Session, npc).Should().BeFalse();
        Reach.CanInteract(other.Session, npc).Should().BeTrue();
    }

    [Fact]
    public async Task ObjectEvent_AGateInAnotherRoomIsLeftAlone()
    {
        const short gateObject = 1300;
        using var provider = CreateWorld(new ManualClock());
        UseMaps(provider, CreateMapManagerWithObjectEvent(
            NapiesGorge,
            new ObjectEvent { Index = gateObject, Type = GateObject, Belong = 1, PosX = 100, PosZ = 100 }));
        var regions = provider.GetRequiredService<SessionManager>().Regions;
        var roomGate = regions.SpawnNpc(new NpcInstance
        {
            NpcId = gateObject, ZoneId = NapiesGorge, X = 100, Z = 100, MaxHp = 100, Hp = 100,
            Nation = EntityNation.Karus, Room = 1,
        });
        var player = CreatePlayer(provider, 2068, NapiesGorge, AccountNation.Karus, level: 60);

        var packet = new Packet(GameOpcodes.GS_OBJECT_EVENT);
        packet.WriteShort(gateObject);
        packet.WriteInt(roomGate.UniqueId);
        await World(provider).HandleObjectEventAsync(player.Client, packet);

        roomGate.GateOpen.Should().BeFalse();
    }

    private static int Notices(Player player)
        => player.Sent.Count(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT);

    private sealed record Player(UserSession Session, IClient Client, PacketLog Sent);

    private sealed class PacketLog : List<Packet>
    {
        private readonly Lock _sync = new();

        public void AddSafely(Packet packet)
        {
            using var scope = _sync.EnterScope();
            Add(packet);
        }
    }

    private static ServiceProvider CreateWorld(ManualClock clock, Action<IGameDataService>? gameData = null)
        => CreateProvider(
            _ => { },
            gameData,
            configureServices: services => services.AddSingleton<TimeProvider>(clock));

    private static void WithMoradonStart(IGameDataService gameData)
        => gameData.GetStartPosition((short)Moradon).Returns(new StartPositionData
        {
            ZoneId = Moradon,
            KarusX = 816,
            KarusZ = 532,
            ElmoradX = 816,
            ElmoradZ = 532,
        });

    private static Player CreatePlayer(
        ServiceProvider provider, int characterId, byte zoneId, AccountNation nation, byte level,
        float x = 100, float z = 100)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.AccountId.Returns(characterId + 1);
        client.CharacterId.Returns(characterId);
        var sent = new PacketLog();
        client.SendPacket(Arg.Do<Packet>(sent.AddSafely), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = $"Traveller{characterId}";
        session.Nation = nation;
        session.Level = level;
        session.Loyalty = 100;
        session.ZoneId = zoneId;
        session.X = x;
        session.Z = z;
        session.MaxHp = 100;
        session.Hp = 100;
        sessionManager.Regions.AddToRegion(session);
        return new Player(session, client, sent);
    }

    private static (Player Rogue, Player Enemy, Player Ally) CreateStealthScene(ServiceProvider provider)
        => (CreatePlayer(provider, 2100, Moradon, AccountNation.Karus, level: 60),
            CreatePlayer(provider, 2101, Moradon, AccountNation.ElMorad, level: 60, x: 102, z: 100),
            CreatePlayer(provider, 2102, Moradon, AccountNation.Karus, level: 60, x: 103, z: 100));

    private static void PartyUp(ServiceProvider provider, UserSession leader, params UserSession[] members)
    {
        var party = provider.GetRequiredService<SessionManager>().Parties.CreateParty((short)leader.CharacterId);
        leader.PartyIndex = party.Index;
        leader.IsPartyLeader = true;
        for (var index = 0; index < members.Length; index++)
        {
            party.MemberIds[index + 1] = (short)members[index].CharacterId;
            members[index].PartyIndex = party.Index;
        }
    }

    private static void UseMaps(ServiceProvider provider, MapManager maps)
        => provider.GetRequiredService<SessionManager>().Maps = maps;

    private static MapManager ZoneExitMap(byte zoneId, byte targetZone)
    {
        var maps = CreateMapManagerWithTiles(zoneId, MapSize, UnitDistance, ExitEvent);
        var events = (Dictionary<short, Dictionary<short, GameEventData>>)typeof(MapManager)
            .GetField("_zoneEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(maps)!;
        events[zoneId] = new Dictionary<short, GameEventData>
        {
            [ExitEvent] = new()
            {
                ZoneNum = zoneId,
                EventNum = ExitEvent,
                Type = ZoneChangeEvent,
                Exec1 = targetZone,
                Exec2 = ArrivalX,
                Exec3 = ArrivalZ,
            },
        };
        return maps;
    }

    private static MapManager MoradonGateMap() => CreateMapManagerWithObjectEvent(
        Moradon,
        new ObjectEvent
        {
            Index = GateIndex, Type = WarpGateObject, ControlNpcId = GateGroup, Belong = 1, PosX = GateX, PosZ = GateZ,
        },
        new WarpInfo { WarpId = FolkVillage, Name = "Folk Village", Fee = 3000, Zone = Moradon, Nation = 1, X = 411, Z = 525 },
        new WarpInfo { WarpId = LufersonCastle, Name = "Luferson Castle", Fee = LufersonFee, Zone = KarusHome, Nation = 1, X = 437, Z = 1627 },
        new WarpInfo { WarpId = RonarkLandWarp, Name = "Ronark Land", Fee = 17000, Zone = RonarkLand, Nation = 1, X = 1375, Z = 1098 },
        new WarpInfo { WarpId = ForeignGroupWarp, Name = "Elsewhere", Fee = 100, Zone = Moradon, Nation = 1, X = 700, Z = 700 });

    private static IWorldPacketCoordinator World(ServiceProvider provider)
        => provider.GetRequiredService<IWorldPacketCoordinator>();

    private static Task StepAsync(
        ServiceProvider provider, ManualClock clock, Player player, float x, float z, ushort y = 0)
    {
        clock.Advance(MoveInterval);
        var wireX = (ushort)MathF.Round(x * PositionScale);
        var wireZ = (ushort)MathF.Round(z * PositionScale);
        var packet = new Packet(GameOpcodes.GS_MOVE);
        packet.WriteUShort(wireX);
        packet.WriteUShort(wireZ);
        packet.WriteUShort(y);
        packet.WriteShort(0);
        packet.WriteByte(MoveEchoMove);
        packet.WriteUShort(wireX);
        packet.WriteUShort(wireZ);
        packet.WriteUShort(y);
        packet.ResetOffset();
        return World(provider).HandleMoveAsync(player.Client, packet);
    }

    private static Task OpenGateAsync(ServiceProvider provider, Player player)
    {
        var packet = new Packet(GameOpcodes.GS_OBJECT_EVENT);
        packet.WriteShort(GateIndex);
        packet.WriteInt(GateGroup);
        return World(provider).HandleObjectEventAsync(player.Client, packet);
    }

    private static Task SelectWarpAsync(ServiceProvider provider, Player player, short warpId)
    {
        var packet = new Packet(GameOpcodes.GS_WARP_LIST);
        packet.WriteShort(GateGroup);
        packet.WriteShort(warpId);
        return World(provider).HandleWarpListAsync(player.Client, packet);
    }

    private static Task LocateCorpseAsync(ServiceProvider provider, Player seeker, int characterId)
    {
        var packet = new Packet(GameOpcodes.GS_CORPSE);
        packet.WriteShort((short)characterId);
        return provider.GetRequiredService<IMiscPacketCoordinator>().HandleCorpseAsync(seeker.Client, packet);
    }

    private static Task ChangeStateAsync(ServiceProvider provider, Player player, byte type, int value)
    {
        var packet = new Packet(GameOpcodes.GS_STATE_CHANGE);
        packet.WriteByte(type);
        packet.WriteInt(value);
        return World(provider).HandleStateChangeAsync(player.Client, packet);
    }

    private static Task ZoneChangeStepAsync(ServiceProvider provider, Player player, byte step)
    {
        var packet = new Packet(GameOpcodes.GS_ZONE_CHANGE);
        packet.WriteByte(step);
        return World(provider).HandleZoneChangeAsync(player.Client, packet);
    }

    private static bool IsTeleport(Packet packet)
        => packet.GetOpcode() == (byte)GameOpcodes.GS_ZONE_CHANGE && packet.GetBytes()[1] == ZoneChangeTeleport;

    private static byte? LastWarpResult(Player player)
    {
        var results = player.Sent
            .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP_LIST)
            .Select(packet => packet.GetBytes())
            .Where(body => body[1] == (byte)WarpListSubOpcode.Result)
            .ToList();
        return results.Count == 0 ? null : results[^1][2];
    }

    private static int InOuts(Player viewer, Player subject, InOutType type)
        => viewer.Sent
            .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_USER_INOUT)
            .Select(packet => packet.GetBytes())
            .Count(body => body[1] == (byte)type && BitConverter.ToInt32(body, 3) == subject.Session.CharacterId);

    private static List<int> PosesOf(Player viewer, Player subject)
        => viewer.Sent
            .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_STATE_CHANGE)
            .Select(packet => packet.GetBytes())
            .Where(body => BitConverter.ToInt32(body, 1) == subject.Session.CharacterId
                && body[5] == (byte)StateChangeType.Pose)
            .Select(body => BitConverter.ToInt32(body, 6))
            .ToList();

    private static List<int> RegionListIds(Player viewer)
    {
        var ids = new List<int>();
        foreach (var body in viewer.Sent
            .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_REGIONCHANGE)
            .Select(packet => packet.GetBytes())
            .Where(body => body[1] == (byte)RegionListStage.List))
        {
            var count = BitConverter.ToInt16(body, 2);
            for (var index = 0; index < count; index++)
                ids.Add(BitConverter.ToInt32(body, 4 + index * sizeof(int)));
        }

        return ids;
    }

    private static string LastNotice(Player player)
    {
        var notice = player.Sent.Last(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT);
        notice.ResetOffset();
        notice.ReadByte();
        notice.ReadByte();
        notice.ReadInt();
        notice.ReadSByteString();
        return notice.ReadString();
    }

    private static int RegistrationsOf(ServiceProvider provider, UserSession session)
    {
        var regions = (System.Collections.IDictionary)typeof(RegionManager)
            .GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider.GetRequiredService<SessionManager>().Regions)!;
        var count = 0;
        foreach (var region in regions.Values)
        {
            if (((ConcurrentDictionary<int, UserSession>)region!).Values.Contains(session))
                count++;
        }

        return count;
    }
}
