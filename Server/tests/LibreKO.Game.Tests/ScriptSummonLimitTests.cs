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
    private const int RequestedGuards = 20;

    [Fact]
    public async Task APlayerKeepsOnlyAFewSummonsAliveAtOnce()
    {
        var world = new SummonWorld();
        var session = world.Player(1);

        await world.SummonAsync(session, RequestedGuards);

        world.LiveGuards().Should().Be(ScriptEffectApplier.MaxLiveSummonsPerPlayer);
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

        world.Clock.Advance(ScriptEffectApplier.SummonCooldown);
        await world.SummonAsync(session, 1);

        world.LiveGuards().Should().Be(1);
    }

    [Fact]
    public async Task AZoneHoldsABoundedNumberOfSummons()
    {
        var world = new SummonWorld();
        var players = ScriptEffectApplier.MaxLiveSummonsPerZone / ScriptEffectApplier.MaxLiveSummonsPerPlayer + 1;

        for (var characterId = 10; characterId < 10 + players; characterId++)
            await world.SummonAsync(world.Player(characterId), RequestedGuards);

        world.LiveGuards().Should().Be(ScriptEffectApplier.MaxLiveSummonsPerZone);
    }

    private sealed class SummonWorld
    {
        private readonly SessionManager _sessions = new();
        private readonly IGameDataService _gameData = Substitute.For<IGameDataService>();
        private readonly ScriptEffectApplier _applier;

        public ManualClock Clock { get; } = new();

        public SummonWorld()
        {
            _gameData.GetNpc(GuardOfBlackMarketer).Returns(new NpcData
            {
                Id = GuardOfBlackMarketer, Name = "Guard of Black Marketer", IsMonster = true, Hp = 1373,
            });
            var provider = new ServiceCollection()
                .AddSingleton(_sessions)
                .AddSingleton(_gameData)
                .AddSingleton(Substitute.For<IMonsterAggressionPolicy>())
                .AddSingleton<INpcLifecycleService>(new NpcLifecycleService(
                    _sessions, Substitute.For<ILogger<NpcLifecycleService>>()))
                .AddSingleton<INpcSummonService, NpcSummonService>()
                .BuildServiceProvider();
            _applier = new ScriptEffectApplier(_gameData, Substitute.For<ICharacterStatePersister>(), provider,
                Clock, Substitute.For<ILogger<ScriptEffectApplier>>());
        }

        public UserSession Player(int characterId)
        {
            var client = Substitute.For<IClient>();
            client.Id.Returns(Guid.NewGuid());
            var session = _sessions.CreateSession(client, characterId, characterId);
            session.ZoneId = Moradon;
            session.X = 464;
            session.Z = 523;
            _sessions.Regions.AddToRegion(session);
            return session;
        }

        public Task SummonAsync(UserSession session, int count)
        {
            var context = new QuestScriptContext(session, null, _gameData, _sessions, Substitute.For<ILogger>(), 1);
            context.RequestSummon(GuardOfBlackMarketer, count, 467, 519);
            return _applier.ApplyAsync(session, context, "25022_21.quest");
        }

        public int LiveGuards()
            => _sessions.Regions.GetAllNpcsInZone(Moradon).Count(npc => npc.NpcId == GuardOfBlackMarketer && npc.IsAlive);

        public void KillGuards()
        {
            foreach (var npc in _sessions.Regions.GetAllNpcsInZone(Moradon))
                npc.Hp = 0;
        }
    }
}
