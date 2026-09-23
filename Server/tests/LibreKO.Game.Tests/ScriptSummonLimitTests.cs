using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Scripting;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class ScriptSummonLimitTests
{
    private const int GuardOfBlackMarketer = 9666;
    private const byte Moradon = 21;
    private const byte DungeonZone = 32;
    private const short DungeonSet = 1;
    private const int RequestedGuards = 20;
    private const short SummonQuest = 1214;
    private const byte QuestStarted = 1;
    private const int GuardsBeforeTheFailure = 2;

    [Fact]
    public async Task APlayerKeepsOnlyAFewSummonsAliveAtOnce()
    {
        var world = new SummonWorld();
        var session = world.Player(1);

        await world.SummonAsync(session, RequestedGuards);

        world.LiveGuards().Should().Be(SummonQuota.MaxLiveSummonsPerPlayer);
    }

    [Fact]
    public async Task ASecondSummonWaitsForTheCooldown()
    {
        var world = new SummonWorld();
        var session = world.Player(2);

        await world.SummonAsync(session, 1);
        world.KillGuards();
        await world.SummonAsync(session, 1);

        world.LiveGuards().Should().Be(0);

        world.Clock.Advance(SummonQuota.SummonCooldown);
        await world.SummonAsync(session, 1);

        world.LiveGuards().Should().Be(1);
    }

    [Fact]
    public async Task AZoneHoldsABoundedNumberOfSummons()
    {
        var world = new SummonWorld();
        var players = SummonQuota.MaxLiveSummonsPerZone / SummonQuota.MaxLiveSummonsPerPlayer + 1;

        for (var characterId = 10; characterId < 10 + players; characterId++)
            await world.SummonAsync(world.Player(characterId), RequestedGuards);

        world.LiveGuards().Should().Be(SummonQuota.MaxLiveSummonsPerZone);
    }

    [Fact]
    public async Task ARefusedSummonFailsTheScriptSoItsQuestStepDoesNotAdvance()
    {
        var world = new SummonWorld();
        var session = world.Player(3);
        await world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);

        var context = world.Context(session);
        context.RequestSummon(GuardOfBlackMarketer, 1, 467, 519);

        context.Quest.SetQuestState(SummonQuest, QuestStarted, hasKillObjectives: false);

        context.ActionFailed.Should().BeTrue();
        context.FailureReason.Should().Be(QuestScriptContext.SummonRefusedReason);
        context.PendingSummons.Should().BeEmpty();
        session.Quest.QuestMap.Should().NotContainKey(SummonQuest);
    }

    [Fact]
    public void TheSummonIsReservedWhenTheScriptAsksForIt()
    {
        var world = new SummonWorld();
        var session = world.Player(7);
        var first = world.Context(session);
        var second = world.Context(session);

        first.RequestSummon(GuardOfBlackMarketer, 1, 467, 519);
        second.RequestSummon(GuardOfBlackMarketer, 1, 467, 519);

        first.SummonGrant.Should().NotBeNull();
        second.ActionFailed.Should().BeTrue("the first script already holds this player's summon");
    }

    [Fact]
    public async Task AReservedSummonIsHandedBackWhenTheScriptFails()
    {
        var world = new SummonWorld();
        var session = world.Player(8);
        var context = world.Context(session);
        context.RequestSummon(GuardOfBlackMarketer, 1, 467, 519);
        context.FailAction("refused");

        await world.ApplyAsync(session, context);

        world.LiveGuards().Should().Be(0);
        world.Quota.Reserve(session).Should().NotBeNull("nothing was summoned, so no cooldown applies");
    }

    [Fact]
    public async Task SummonsThatOutliveTheirLifetimeAreDespawnedAndFreeTheirSlots()
    {
        var world = new SummonWorld();
        var session = world.Player(4);
        await world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);

        world.Clock.Advance(SummonQuota.SummonLifetime);
        var expired = world.Quota.TakeExpired();

        expired.Should().HaveCount(SummonQuota.MaxLiveSummonsPerPlayer);
        world.Quota.Reserve(session).Should().NotBeNull();
    }

    [Fact]
    public async Task SummonsInADungeonRoomLeaveWithTheRoomAndStopCounting()
    {
        var world = new SummonWorld();
        var session = world.Player(5);
        var room = world.Rooms.Open(DungeonZone, DungeonSet, TimeSpan.FromMinutes(1));
        world.Rooms.Join(room, session);
        session.ZoneId = DungeonZone;

        await world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);
        room.Npcs.Should().HaveCount(SummonQuota.MaxLiveSummonsPerPlayer);

        world.Rooms.Close(room);
        world.Clock.Advance(SummonQuota.SummonCooldown);

        world.Sessions.Regions.GetAllNpcsInZone(DungeonZone).Should().BeEmpty();
        world.Quota.Reserve(session).Should().NotBeNull();
    }

    [Fact]
    public async Task ASummonThatFailsHalfwayStillCountsWhatReachedTheWorld()
    {
        var world = new SummonWorld(w => new FailingLifecycle(w.Sessions, GuardsBeforeTheFailure));
        var session = world.Player(6);

        var summon = () => world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);

        await summon.Should().ThrowAsync<InvalidOperationException>();
        world.LiveGuards().Should().Be(GuardsBeforeTheFailure);
        world.Clock.Advance(SummonQuota.SummonCooldown);
        world.Quota.Reserve(session)!.Remaining.Should().Be(SummonQuota.MaxLiveSummonsPerPlayer - GuardsBeforeTheFailure);
    }

    [Fact]
    public async Task ASummonLandingInARoomThatJustClosedIsTakenBackOut()
    {
        var world = new SummonWorld(w => new RoomClosingLifecycle(w.Sessions, w.Rooms));
        var session = world.Player(9);
        var room = world.Rooms.Open(DungeonZone, DungeonSet, TimeSpan.FromMinutes(1));
        world.Rooms.Join(room, session);
        session.ZoneId = DungeonZone;

        await world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);

        world.Sessions.Regions.GetAllNpcsInZone(DungeonZone).Should().BeEmpty();
    }

    [Fact]
    public async Task TheRespawnTickDespawnsSummonsThatOutlivedTheirLifetime()
    {
        var world = new SummonWorld();
        var session = world.Player(10);
        await world.SummonAsync(session, SummonQuota.MaxLiveSummonsPerPlayer);
        var respawns = new NpcRespawnService(world.Sessions, world.Quota,
            new NpcLifecycleService(world.Sessions, Substitute.For<ILogger<NpcLifecycleService>>()),
            Substitute.For<ILogger<NpcRespawnService>>());

        await respawns.TickAsync();
        world.LiveGuards().Should().Be(SummonQuota.MaxLiveSummonsPerPlayer);

        world.Clock.Advance(SummonQuota.SummonLifetime);
        await respawns.TickAsync();

        world.LiveGuards().Should().Be(0);
    }

    private sealed class SummonWorld
    {
        private readonly IGameDataService _gameData = Substitute.For<IGameDataService>();
        private readonly ScriptEffectApplier _applier;

        public SessionManager Sessions { get; } = new();
        public ManualClock Clock { get; } = new();
        public SummonQuota Quota { get; }
        public InstanceRoomRegistry Rooms { get; }

        public SummonWorld(Func<SummonWorld, INpcLifecycleService>? lifecycleFor = null)
        {
            _gameData.GetNpc(GuardOfBlackMarketer).Returns(new NpcData
            {
                Id = GuardOfBlackMarketer, Name = "Guard of Black Marketer", IsMonster = true, Hp = 1373,
            });
            Quota = new SummonQuota(Sessions, Clock);
            Rooms = new InstanceRoomRegistry(Sessions, Substitute.For<ILogger<InstanceRoomRegistry>>());
            var lifecycle = lifecycleFor?.Invoke(this)
                ?? new NpcLifecycleService(Sessions, Substitute.For<ILogger<NpcLifecycleService>>());

            var summons = new NpcSummonService(
                Sessions, _gameData, Substitute.For<IMonsterAggressionPolicy>(), lifecycle, Rooms, Quota);
            var provider = new ServiceCollection()
                .AddSingleton<INpcSummonService>(summons)
                .BuildServiceProvider();
            _applier = new ScriptEffectApplier(_gameData, Substitute.For<ICharacterStatePersister>(), provider,
                Quota, Substitute.For<ILogger<ScriptEffectApplier>>());
        }

        public UserSession Player(int characterId)
        {
            var client = Substitute.For<IClient>();
            client.Id.Returns(Guid.NewGuid());
            var session = Sessions.CreateSession(client, characterId, characterId);
            session.ZoneId = Moradon;
            session.X = 464;
            session.Z = 523;
            Sessions.Regions.AddToRegion(session);
            return session;
        }

        public QuestScriptContext Context(UserSession session)
            => new(session, null, _gameData, Sessions, Substitute.For<ILogger>(), 1, Quota);

        public Task SummonAsync(UserSession session, int count)
        {
            var context = Context(session);
            context.RequestSummon(GuardOfBlackMarketer, count, 467, 519);
            return ApplyAsync(session, context);
        }

        public Task ApplyAsync(UserSession session, QuestScriptContext context)
            => _applier.ApplyAsync(session, context, "25022_21.quest");

        public int LiveGuards()
            => Sessions.Regions.GetAllNpcsInZone(Moradon).Count(npc => npc.NpcId == GuardOfBlackMarketer && npc.IsAlive);

        public void KillGuards()
        {
            foreach (var npc in Sessions.Regions.GetAllNpcsInZone(Moradon))
                npc.Hp = 0;
        }
    }

    private sealed class FailingLifecycle(SessionManager sessions, int failOnSpawn) : INpcLifecycleService
    {
        private int _spawns;

        public Task SpawnAsync(NpcInstance npc)
        {
            sessions.Regions.SpawnNpc(npc);
            return ++_spawns == failOnSpawn
                ? throw new InvalidOperationException("broadcast failed")
                : Task.CompletedTask;
        }

        public Task DespawnAsync(NpcInstance npc) => Task.CompletedTask;
    }

    private sealed class RoomClosingLifecycle(SessionManager sessions, InstanceRoomRegistry rooms) : INpcLifecycleService
    {
        public Task SpawnAsync(NpcInstance npc)
        {
            if (rooms.Get(npc.Room) is { } room)
                rooms.Close(room);
            sessions.Regions.SpawnNpc(npc);
            return Task.CompletedTask;
        }

        public Task DespawnAsync(NpcInstance npc) => Task.CompletedTask;
    }
}
