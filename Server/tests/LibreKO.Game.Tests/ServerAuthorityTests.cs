using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class ServerAuthorityTests : GameTestBase
{
    private const byte MoveEchoMove = 3;
    private const float PositionScale = 10f;
    private const byte Zone = 21;
    private const float RunStepMeters = 0.6f;
    private const int FullSpeedSteps = 30;
    private const byte FrozenSpeedAmount = 1;
    private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task GuardedRouter_RefusesAWarpRequestFromAPlayer()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, _) = CreatePlayer(provider, 1000, x: 100, z: 100);

        await RouteAsync(provider, client, WarpRequest(900, 900));

        session.X.Should().Be(100);
        session.Z.Should().Be(100);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.ServerOnlyOpcodeWeight);
    }

    [Fact]
    public async Task GuardedRouter_LetsAGameMasterWarp()
    {
        using var provider = CreateHarness(new ManualClock());
        var (session, client, _) = CreatePlayer(provider, 1001, x: 100, z: 100);
        session.IsGM = true;

        await RouteAsync(provider, client, WarpRequest(900, 900));

        session.X.Should().Be(90);
        session.Z.Should().Be(90);
    }

    [Fact]
    public void PacketGuard_NeverDropsASitToggleMashedAsFastAsAHandCan()
    {
        const int PressesPerSecond = 10;
        const int SecondsMashed = 30;
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (_, client, _) = CreatePlayer(provider, 1003, x: 100, z: 100);
        var guard = provider.GetRequiredService<IPacketGuard>();

        for (var press = 0; press < PressesPerSecond * SecondsMashed; press++)
        {
            guard.Admit(client, GameOpcodes.GS_STATE_CHANGE).Should().BeTrue();
            clock.Advance(TimeSpan.FromSeconds(1.0 / PressesPerSecond));
        }
    }

    [Fact]
    public void PacketGuard_DropsPacketsAboveTheOpcodeLimitUntilTheBucketRefills()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (_, client, _) = CreatePlayer(provider, 1002, x: 100, z: 100);
        var guard = provider.GetRequiredService<IPacketGuard>();
        var limit = OpcodePolicies.LimitOf(GameOpcodes.GS_DATASAVE);

        for (var i = 0; i < limit.Capacity; i++)
            guard.Admit(client, GameOpcodes.GS_DATASAVE).Should().BeTrue();

        guard.Admit(client, GameOpcodes.GS_DATASAVE).Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(1 / limit.RefillPerSecond));
        guard.Admit(client, GameOpcodes.GS_DATASAVE).Should().BeTrue();
    }

    [Fact]
    public void ViolationMonitor_DisconnectsOnceTheScoreReachesTheLimit()
    {
        using var provider = CreateHarness(new ManualClock());
        var (session, client, _) = CreatePlayer(provider, 1003, x: 100, z: 100);
        var monitor = provider.GetRequiredService<IViolationMonitor>();

        monitor.Report(session, ViolationKind.ForgedEvent, "first");
        client.DidNotReceive().Disconnect();

        monitor.Report(session, ViolationKind.ForgedEvent, "second");
        monitor.Report(session, ViolationKind.ForgedEvent, "third");

        client.Received(1).Disconnect();
    }

    [Fact]
    public void ViolationMonitor_NeverDisconnectsAGameMaster()
    {
        using var provider = CreateHarness(new ManualClock());
        var (session, client, _) = CreatePlayer(provider, 1004, x: 100, z: 100);
        session.IsGM = true;
        var monitor = provider.GetRequiredService<IViolationMonitor>();

        for (var i = 0; i < 5; i++)
            monitor.Report(session, ViolationKind.ForgedEvent, "gm");

        client.DidNotReceive().Disconnect();
    }

    [Fact]
    public void ViolationMonitor_ScoreDecaysOverTime()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, _) = CreatePlayer(provider, 1005, x: 100, z: 100);
        var monitor = provider.GetRequiredService<IViolationMonitor>();

        monitor.Report(session, ViolationKind.ForgedEvent, "once");
        clock.Advance(TimeSpan.FromMinutes(1));

        monitor.ScoreOf(client.Id).Should().Be(ViolationMonitor.ForgedEventWeight - 30);
    }

    [Fact]
    public async Task Move_RunningAtNormalSpeedIsAccepted()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, _) = CreatePlayer(provider, 1006, x: 100, z: 100);

        for (var step = 1; step <= 30; step++)
        {
            clock.Advance(MoveInterval);
            await MoveAsync(provider, client, 100 + step * 0.6f, 100);
        }

        session.X.Should().BeApproximately(118f, 0.01f);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id).Should().Be(0);
    }

    [Fact]
    public async Task Move_ATeleportIsRefusedAndThePlayerIsPulledBack()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1007, x: 100, z: 100);

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 100.5f, 100);
        clock.Advance(TimeSpan.FromSeconds(5));
        sent.Clear();

        await MoveAsync(provider, client, 400, 400);

        session.X.Should().BeApproximately(100.5f, 0.01f);
        session.Z.Should().Be(100);
        var warp = sent.Single(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        warp.ResetOffset();
        warp.ReadUShort().Should().Be(1005);
        warp.ReadUShort().Should().Be(1000);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.TeleportWeight);
    }

    [Fact]
    public async Task Move_ASlowedPlayerCannotKeepRunningAtFullSpeed()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1008, x: 100, z: 100);
        session.SpeedAmount = 50;

        var x = 100f;
        for (var step = 0; step < 100; step++)
        {
            clock.Advance(MoveInterval);
            x += 0.6f;
            await MoveAsync(provider, client, x, 100);
        }

        session.X.Should().BeLessThan(x);
        sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
    }

    [Fact]
    public async Task Move_ASlowLandingMidRunIsNotReportedWhileTheClientLearnsOfIt()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1017, x: 100, z: 100);

        var x = await RunAsync(provider, client, clock, 100f, FullSpeedSteps);
        session.SpeedAmount = FrozenSpeedAmount;
        sent.Clear();
        await RunAsync(provider, client, clock, x, StepsWithinSlowdownGrace());

        sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id).Should().Be(0);
    }

    [Fact]
    public async Task Move_RunningOnThroughASlowPastItsGraceIsReported()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1018, x: 100, z: 100);

        var x = await RunAsync(provider, client, clock, 100f, FullSpeedSteps);
        session.SpeedAmount = FrozenSpeedAmount;
        sent.Clear();
        await RunAsync(provider, client, clock, x, StepsWithinSlowdownGrace() + FullSpeedSteps);

        sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().BeGreaterThanOrEqualTo(ViolationMonitor.SpeedHackWeight);
    }

    [Fact]
    public async Task Move_StaleMovesAfterAServerRelocationAreDroppedWithoutPenalty()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1009, x: 100, z: 100);

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 100.5f, 100);

        session.X = 500;
        session.Z = 500;
        sent.Clear();
        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 101, 100);

        session.X.Should().Be(500);
        sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id).Should().Be(0);

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 500.5f, 500);
        session.X.Should().BeApproximately(500.5f, 0.01f);
    }

    [Fact]
    public async Task Move_TheFirstLaggedMoveAfterARelocationIsPulledBackWithoutPenalty()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, sent) = CreatePlayer(provider, 1011, x: 100, z: 100);
        var monitor = provider.GetRequiredService<IViolationMonitor>();
        var pastGrace = TimeSpan.FromSeconds(5);

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 100.5f, 100);
        session.X = 500;
        session.Z = 500;
        sent.Clear();

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 101, 100);
        clock.Advance(pastGrace);
        await MoveAsync(provider, client, 101.5f, 100);

        session.X.Should().Be(500);
        sent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_WARP);
        monitor.ScoreOf(client.Id).Should().Be(0);

        clock.Advance(pastGrace);
        await MoveAsync(provider, client, 102, 100);

        monitor.ScoreOf(client.Id).Should().Be(ViolationMonitor.TeleportWeight);
    }

    [Fact]
    public async Task Move_GameMastersAreNotSpeedChecked()
    {
        var clock = new ManualClock();
        using var provider = CreateHarness(clock);
        var (session, client, _) = CreatePlayer(provider, 1010, x: 100, z: 100);
        session.IsGM = true;

        clock.Advance(MoveInterval);
        await MoveAsync(provider, client, 400, 400);

        session.X.Should().Be(400);
    }

    [Fact]
    public async Task NpcDeath_IsProcessedOnceEvenWhenTwoKillersReportIt()
    {
        var observer = new CountingKillObserver();
        using var provider = CreateHarness(
            new ManualClock(),
            services => services.AddSingleton<INpcKillObserver>(observer));
        var (first, _, _) = CreatePlayer(provider, 1011, x: 100, z: 100);
        var (second, _, _) = CreatePlayer(provider, 1012, x: 100, z: 100);
        var npc = SpawnMonster(provider, hp: 0);
        var lifecycle = provider.GetRequiredService<ICombatLifecycleService>();

        await Task.WhenAll(
            Task.Run(() => lifecycle.HandleNpcDeathAsync(npc, first)),
            Task.Run(() => lifecycle.HandleNpcDeathAsync(npc, second)));

        observer.Kills.Should().Be(1);
        npc.State.Should().Be(NpcState.Dead);
    }

    [Fact]
    public async Task NpcDeath_CountsAgainAfterTheNpcRespawns()
    {
        var observer = new CountingKillObserver();
        using var provider = CreateHarness(
            new ManualClock(),
            services => services.AddSingleton<INpcKillObserver>(observer));
        var (killer, _, _) = CreatePlayer(provider, 1013, x: 100, z: 100);
        var npc = SpawnMonster(provider, hp: 0);
        var lifecycle = provider.GetRequiredService<ICombatLifecycleService>();

        await lifecycle.HandleNpcDeathAsync(npc, killer);
        npc.Respawn();
        npc.Hp = 0;
        await lifecycle.HandleNpcDeathAsync(npc, killer);

        observer.Kills.Should().Be(2);
    }

    [Fact]
    public async Task PlayerDeath_IsProcessedOncePerLife()
    {
        using var provider = CreateHarness(new ManualClock());
        var (victim, _, _) = CreatePlayer(provider, 1014, x: 100, z: 100);
        var lifecycle = provider.GetRequiredService<ICombatLifecycleService>();
        victim.Hp = 0;

        await lifecycle.HandlePlayerDeathAsync(victim, killer: null);
        await lifecycle.HandlePlayerDeathAsync(victim, killer: null);
        victim.Deaths.Should().Be(1);

        victim.Hp = 50;
        victim.Hp = 0;
        await lifecycle.HandlePlayerDeathAsync(victim, killer: null);
        victim.Deaths.Should().Be(2);
    }

    [Fact]
    public void ApplyDamage_ReportsTheKillExactlyOnceUnderContention()
    {
        var npc = new NpcInstance { Hp = 1000, MaxHp = 1000 };

        var outcomes = Enumerable.Range(0, 200)
            .AsParallel()
            .Select(_ => npc.ApplyDamage(7))
            .ToList();

        outcomes.Count(outcome => outcome.Killed).Should().Be(1);
        outcomes.Sum(outcome => outcome.Dealt).Should().Be(1000);
        npc.Hp.Should().Be(0);
    }

    [Fact]
    public void TryBeginDeath_IsOnlyClaimedForTheDead()
    {
        var session = new SessionManager().CreateSession(Substitute.For<IClient>(), characterId: 1, accountId: 1);
        session.MaxHp = 100;
        session.Hp = 50;

        session.TryBeginDeath().Should().BeFalse("a player revived before the death was processed is alive");

        session.Hp = 0;
        session.TryBeginDeath().Should().BeTrue();
        session.TryBeginDeath().Should().BeFalse();
    }

    [Fact]
    public void TryClaimRevival_HoldsOneRevivalUntilTheCorpseStandsUp()
    {
        var session = new SessionManager().CreateSession(Substitute.For<IClient>(), characterId: 1, accountId: 1);
        session.MaxHp = 100;
        session.Hp = 50;

        session.TryClaimRevival().Should().BeFalse("the living need no revival");

        session.Hp = 0;
        session.TryClaimRevival().Should().BeTrue();
        session.TryClaimRevival().Should().BeFalse();

        session.Hp = 100;
        session.Hp = 0;
        session.TryClaimRevival().Should().BeTrue("standing up ends the claim");
    }

    [Fact]
    public async Task LevelUp_DoesNotRaiseTheDead()
    {
        using var provider = CreateHarness(new ManualClock());
        var (corpse, _, _) = CreatePlayer(provider, 1015, x: 100, z: 100);
        var (living, _, _) = CreatePlayer(provider, 1016, x: 100, z: 100);
        var progression = provider.GetRequiredService<IPlayerProgressionService>();
        corpse.Hp = 0;
        living.Hp = 1;

        await progression.SetLevelAsync(corpse, (byte)(corpse.Level + 1));
        await progression.SetLevelAsync(living, (byte)(living.Level + 1));

        corpse.Hp.Should().Be(0);
        living.Hp.Should().Be(living.MaxHp);
    }

    [Fact]
    public void Heal_DoesNothingForTheDead()
    {
        var session = new SessionManager().CreateSession(Substitute.For<IClient>(), characterId: 1, accountId: 1);
        session.MaxHp = 100;
        session.Hp = 0;

        session.Heal(50).Should().Be(0);
        session.Hp.Should().Be(0);

        session.Hp = 10;
        session.Heal(500).Should().Be(90);
        session.Hp.Should().Be(100);
    }

    private static ServiceProvider CreateHarness(
        ManualClock clock,
        Action<IServiceCollection>? configureServices = null)
        => CreateProvider(
            _ => { },
            configureServices: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                configureServices?.Invoke(services);
            });

    private static (UserSession Session, IClient Client, List<Packet> Sent) CreatePlayer(
        ServiceProvider provider, int characterId, float x, float z)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.AccountId.Returns(characterId + 1);
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(packet => sent.Add(packet)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = $"Player{characterId}";
        session.ZoneId = Zone;
        session.X = x;
        session.Z = z;
        session.MaxHp = 100;
        session.Hp = 100;
        sessionManager.Regions.AddToRegion(session);
        return (session, client, sent);
    }

    private static NpcInstance SpawnMonster(ServiceProvider provider, int hp)
        => provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = 750,
            Name = "Worm",
            ZoneId = Zone,
            X = 100,
            Z = 100,
            SpawnX = 100,
            SpawnZ = 100,
            Hp = hp,
            MaxHp = 100,
            IsMonster = true,
        });

    private static Task MoveAsync(ServiceProvider provider, IClient client, float x, float z)
    {
        var wireX = (ushort)MathF.Round(x * PositionScale);
        var wireZ = (ushort)MathF.Round(z * PositionScale);
        var packet = new Packet(GameOpcodes.GS_MOVE);
        packet.WriteUShort(wireX);
        packet.WriteUShort(wireZ);
        packet.WriteUShort(0);
        packet.WriteShort(0);
        packet.WriteByte(MoveEchoMove);
        packet.WriteUShort(wireX);
        packet.WriteUShort(wireZ);
        packet.WriteUShort(0);
        packet.ResetOffset();
        return provider.GetRequiredService<IWorldMovementService>().HandleMoveAsync(client, packet);
    }

    private static async Task<float> RunAsync(
        ServiceProvider provider, IClient client, ManualClock clock, float x, int steps)
    {
        for (var step = 0; step < steps; step++)
        {
            clock.Advance(MoveInterval);
            x += RunStepMeters;
            await MoveAsync(provider, client, x, 100);
        }

        return x;
    }

    private static int StepsWithinSlowdownGrace()
        => (int)(new MovementCheckSettings().SlowdownGraceSeconds / MoveInterval.TotalSeconds) - 1;

    private static Packet WarpRequest(ushort x, ushort z)
    {
        var packet = new Packet(GameOpcodes.GS_WARP);
        packet.WriteUShort(x);
        packet.WriteUShort(z);
        packet.ResetOffset();
        return packet;
    }

    private static Task RouteAsync(ServiceProvider provider, IClient client, Packet packet)
    {
        var handler = provider.GetRequiredService<IInGameOpcodeRouter>().Resolve((GameOpcodes)packet.GetOpcode());
        handler.Should().NotBeNull();
        return handler!(client, packet);
    }

    private sealed class CountingKillObserver : INpcKillObserver
    {
        private int _kills;

        public int Kills => Volatile.Read(ref _kills);

        public Task OnNpcKilledAsync(NpcInstance npc, UserSession killer, UserSession rewardRecipient)
        {
            Interlocked.Increment(ref _kills);
            return Task.CompletedTask;
        }
    }
}
