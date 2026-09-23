using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public abstract class RewardTestBase : GameTestBase
{
    protected const byte Zone = 21;
    protected const float SpawnX = 100;
    protected const float SpawnZ = 100;

    protected const int WormId = 750;
    protected const int WolfId = 550;
    protected const int BossId = 8647;

    protected const int HerbItemId = 810418000;
    protected const int ScrollItemId = 379016000;
    protected const int PotionItemId = 389013000;
    protected const int JunkItemId = 379048000;

    protected const int DailyHuntId = 1;
    protected const int DailySoloHuntId = 2;
    protected const int DailySupplyId = 3;
    protected const int WeeklyBossId = 4;
    protected const int EventHuntId = 9001;
    protected const int EventGatherId = 9002;
    protected const int EventVeteranId = 9003;
    protected const int EventSummerId = 9004;

    protected const int DailyHuntGold = 5_000;
    protected const int DailyHuntCoins = 1;
    protected const int DailySoloHuntGold = 1_000;
    protected const int DailySupplyScrolls = 2;
    protected const int DailySupplyCoins = 2;
    protected const int WeeklyBossGold = 10_000;
    protected const int EventHuntGold = 20_000;
    protected const int EventHuntCoins = 3;
    protected const int EventGatherGold = 15_000;
    protected const int EventVeteranGold = 50_000;
    protected const int HuntKills = 3;
    protected const int SoloHuntKills = 2;
    protected const int HerbsRequired = 5;

    protected const int RouletteGold = 2_000;
    protected const int RoulettePotions = 5;
    protected const int FortuneGold = 3_000;
    protected const int GenieLowTierGold = 5_000;
    protected const int GenieHighTierGold = 10_000;
    protected const byte FortuneMinLevel = 10;
    protected const byte GenieMinLevel = 10;
    protected const byte GenieHighTierLevel = 30;
    protected const byte MaxLevel = 83;

    protected const long ExperiencePerLevel = 1_000_000_000_000;

    protected const int GoldPrizeRoll = 0;
    protected const int ItemPrizeRoll = 1;

    protected sealed record RewardPlayer(UserSession Session, IClient Client, List<Packet> Sent);

    protected sealed class ScriptedRandom(int roll) : IRewardRandom
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int Next(int maxExclusive)
        {
            Interlocked.Increment(ref _calls);
            return Math.Min(roll, maxExclusive - 1);
        }
    }

    protected sealed class RewardCatalog
    {
        public List<RewardQuestData> Quests { get; } = [];
        public List<RewardQuestTargetData> Targets { get; } = [];
        public List<RewardQuestItemData> Requirements { get; } = [];
        public List<RewardQuestRewardData> Rewards { get; } = [];
        public List<RewardPrizeData> Prizes { get; } = [];
        public Dictionary<int, ItemData> Items { get; } = [];

        public RewardCatalog Quest(int id, QuestBoard board, byte minLevel, byte maxLevel, QuestRecurrence recurrence,
            int killCount, bool partyShared = false, byte startMonth = 0, byte startDay = 0, short durationDays = 0)
        {
            Quests.Add(new RewardQuestData
            {
                Id = id,
                Board = board,
                Title = $"Quest {id}",
                MinLevel = minLevel,
                MaxLevel = maxLevel,
                Recurrence = recurrence,
                KillCount = killCount,
                PartyShared = partyShared,
                StartMonth = startMonth,
                StartDay = startDay,
                DurationDays = durationDays,
            });
            return this;
        }

        public RewardCatalog Target(int questId, int npcId)
        {
            Targets.Add(new RewardQuestTargetData { Id = Targets.Count + 1, QuestId = questId, NpcId = npcId });
            return this;
        }

        public RewardCatalog Requirement(int questId, int itemId, int count)
        {
            Requirements.Add(new RewardQuestItemData { Id = Requirements.Count + 1, QuestId = questId, ItemId = itemId, Count = count });
            return this;
        }

        public RewardCatalog Reward(int questId, RewardKind kind, int count, int itemId = 0)
        {
            Rewards.Add(new RewardQuestRewardData { Id = Rewards.Count + 1, QuestId = questId, Kind = kind, ItemId = itemId, Count = count });
            return this;
        }

        public RewardCatalog Prize(int id, PrizePool pool, byte minLevel, byte maxLevel, RewardKind kind, int count, int itemId = 0)
        {
            Prizes.Add(new RewardPrizeData
            {
                Id = id,
                Pool = pool,
                MinLevel = minLevel,
                MaxLevel = maxLevel,
                Weight = 1,
                Kind = kind,
                ItemId = itemId,
                Count = count,
            });
            return this;
        }

        public RewardCatalog Item(int itemId, short weight)
        {
            Items[itemId] = new ItemData { Num = itemId, Countable = 1, Weight = weight, Duration = 1 };
            return this;
        }

        public void Apply(IGameDataService gameData)
        {
            gameData.RewardQuestTable.Returns(Quests.ToDictionary(quest => quest.Id));
            gameData.RewardQuestTargetsByNpc.Returns(Targets.ToLookup(target => target.NpcId));
            gameData.RewardQuestItemsByQuest.Returns(Requirements.ToLookup(requirement => requirement.QuestId));
            gameData.RewardQuestRewardsByQuest.Returns(Rewards.ToLookup(reward => reward.QuestId));
            gameData.RewardPrizesByPool.Returns(Prizes.ToLookup(prize => prize.Pool));
            gameData.GetItem(Arg.Any<int>()).Returns(call => Items.GetValueOrDefault(call.Arg<int>()));
            gameData.GetMaxExpForLevel(Arg.Any<byte>()).Returns(ExperiencePerLevel);
        }
    }

    protected static RewardCatalog DefaultCatalog() => new RewardCatalog()
        .Item(HerbItemId, 1)
        .Item(ScrollItemId, 0)
        .Item(PotionItemId, 40)
        .Item(JunkItemId, 0)
        .Quest(DailyHuntId, QuestBoard.Daily, 1, 20, QuestRecurrence.Daily, HuntKills, partyShared: true)
        .Target(DailyHuntId, WormId)
        .Reward(DailyHuntId, RewardKind.Gold, DailyHuntGold)
        .Reward(DailyHuntId, RewardKind.EventCoins, DailyHuntCoins)
        .Reward(DailyHuntId, RewardKind.Experience, DailyHuntGold)
        .Quest(DailySoloHuntId, QuestBoard.Daily, 1, MaxLevel, QuestRecurrence.Daily, SoloHuntKills)
        .Target(DailySoloHuntId, WolfId)
        .Reward(DailySoloHuntId, RewardKind.Gold, DailySoloHuntGold)
        .Quest(DailySupplyId, QuestBoard.Daily, 1, MaxLevel, QuestRecurrence.Daily, 0)
        .Requirement(DailySupplyId, HerbItemId, HerbsRequired)
        .Reward(DailySupplyId, RewardKind.Item, DailySupplyScrolls, ScrollItemId)
        .Reward(DailySupplyId, RewardKind.EventCoins, DailySupplyCoins)
        .Quest(WeeklyBossId, QuestBoard.Daily, 1, MaxLevel, QuestRecurrence.Weekly, 1, partyShared: true)
        .Target(WeeklyBossId, BossId)
        .Reward(WeeklyBossId, RewardKind.Gold, WeeklyBossGold)
        .Quest(EventHuntId, QuestBoard.Event, 1, 35, QuestRecurrence.Once, HuntKills, startMonth: 1, startDay: 1, durationDays: 31)
        .Target(EventHuntId, WormId)
        .Reward(EventHuntId, RewardKind.Gold, EventHuntGold)
        .Reward(EventHuntId, RewardKind.EventCoins, EventHuntCoins)
        .Quest(EventGatherId, QuestBoard.Event, 1, 35, QuestRecurrence.Once, 0, startMonth: 1, startDay: 1, durationDays: 31)
        .Requirement(EventGatherId, HerbItemId, HerbsRequired)
        .Reward(EventGatherId, RewardKind.Gold, EventGatherGold)
        .Reward(EventGatherId, RewardKind.Item, 1, ScrollItemId)
        .Quest(EventVeteranId, QuestBoard.Event, 40, MaxLevel, QuestRecurrence.Once, 1, startMonth: 1, startDay: 1, durationDays: 31)
        .Target(EventVeteranId, WolfId)
        .Reward(EventVeteranId, RewardKind.Gold, EventVeteranGold)
        .Quest(EventSummerId, QuestBoard.Event, 1, MaxLevel, QuestRecurrence.Once, 1, startMonth: 6, startDay: 1, durationDays: 21)
        .Target(EventSummerId, WormId)
        .Reward(EventSummerId, RewardKind.Gold, EventVeteranGold)
        .Prize(1, PrizePool.Roulette, 1, MaxLevel, RewardKind.Gold, RouletteGold)
        .Prize(2, PrizePool.Roulette, 1, MaxLevel, RewardKind.Item, RoulettePotions, PotionItemId)
        .Prize(101, PrizePool.Fortune, FortuneMinLevel, MaxLevel, RewardKind.Gold, FortuneGold)
        .Prize(102, PrizePool.Fortune, FortuneMinLevel, MaxLevel, RewardKind.Item, RoulettePotions, PotionItemId)
        .Prize(201, PrizePool.Genie, GenieMinLevel, GenieHighTierLevel - 1, RewardKind.Gold, GenieLowTierGold)
        .Prize(202, PrizePool.Genie, GenieHighTierLevel, MaxLevel, RewardKind.Gold, GenieHighTierGold);

    protected static ServiceProvider CreateRewardProvider(
        ManualClock clock,
        IRewardRandom? random = null,
        Action<IServiceCollection>? configureServices = null,
        RewardCatalog? catalog = null,
        Action<AppDbContext>? seed = null)
    {
        var rewards = catalog ?? DefaultCatalog();
        return CreateProvider(
            seed ?? (_ => { }),
            rewards.Apply,
            configureServices: services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton(random ?? new ScriptedRandom(GoldPrizeRoll));
                configureServices?.Invoke(services);
            });
    }

    protected static RewardPlayer Player(ServiceProvider provider, int characterId, int accountId, byte level,
        float x = SpawnX, float z = SpawnZ)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.AccountId.Returns(accountId);
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var sessions = provider.GetRequiredService<SessionManager>();
        var session = sessions.CreateSession(client, characterId, accountId);
        session.Name = $"Player{characterId}";
        session.Nation = AccountNation.Karus;
        session.Level = level;
        session.ZoneId = Zone;
        session.X = x;
        session.Z = z;
        session.MaxHp = 100;
        session.Hp = 100;
        session.Stats.MaxWeight = 10_000;
        sessions.Regions.AddToRegion(session);
        return new RewardPlayer(session, client, sent);
    }

    protected static RewardPlayer Relog(ServiceProvider provider, RewardPlayer player)
    {
        provider.GetRequiredService<SessionManager>().RemoveSession(player.Session);
        return Player(provider, player.Session.CharacterId, player.Session.AccountId, player.Session.Level);
    }

    protected static void Give(UserSession session, int slot, int itemId, ushort count, ItemFlag flag = ItemFlag.Unsealed)
    {
        var entry = session.Inventory[slot];
        entry.ItemId = itemId;
        entry.Count = count;
        entry.Durability = 1;
        entry.Flag = (byte)flag;
    }

    protected static Action<AppDbContext> StoredCharacter(int characterId, int accountId) => db =>
    {
        db.Accounts.Add(new Account { Id = accountId, Login = $"account{accountId}", Password = "pw", Nation = AccountNation.Karus });
        db.Characters.Add(new Character
        {
            Id = characterId,
            AccountId = accountId,
            Name = $"Player{characterId}",
            Items = new byte[InventoryConstants.InventoryTotal * UserSessionBinaryState.BytesPerItem],
        });
    };

    protected static Task<Character> StoredCharacterAsync(ServiceProvider provider, int characterId) =>
        InScopeAsync(provider, db => db.Characters.AsNoTracking().SingleAsync(character => character.Id == characterId));

    protected static SaveChangesProbe FailingCommitsOf<TEntity>() where TEntity : class => new()
    {
        FailWhen = db => db.ChangeTracker.Entries<TEntity>().Any(entry => entry.State is EntityState.Added or EntityState.Modified),
    };

    protected static void FillInventoryGrid(UserSession session, int itemId)
    {
        for (var slot = InventoryConstants.InventoryStart; slot < InventoryConstants.InventoryStart + InventoryConstants.HaveMax; slot++)
        {
            if (session.Inventory[slot].IsEmpty)
                Give(session, slot, itemId, InventoryConstants.MaxStackCount);
        }
    }

    protected static int CountItem(UserSession session, int itemId) =>
        session.Inventory.Where(slot => slot.ItemId == itemId).Sum(slot => slot.Count);

    protected static async Task KillAsync(ServiceProvider provider, UserSession killer, int npcId, int times = 1,
        float x = SpawnX, float z = SpawnZ)
    {
        var sessions = provider.GetRequiredService<SessionManager>();
        var lifecycle = provider.GetRequiredService<ICombatLifecycleService>();
        for (var kill = 0; kill < times; kill++)
        {
            var npc = sessions.Regions.SpawnNpc(new NpcInstance
            {
                NpcId = npcId,
                Name = $"Monster{npcId}",
                ZoneId = Zone,
                X = x,
                Z = z,
                SpawnX = x,
                SpawnZ = z,
                Hp = 0,
                MaxHp = 100,
                IsMonster = true,
            });
            await lifecycle.HandleNpcDeathAsync(npc, killer);
        }

        await provider.GetRequiredService<IRewardStateService>().FlushAsync(killer);
    }

    protected static Task RouteAsync(ServiceProvider provider, RewardPlayer player, GameOpcodes opcode, params int[] fields)
    {
        var packet = new Packet(opcode);
        packet.WriteByte((byte)fields[0]);
        foreach (var field in fields.Skip(1))
            packet.WriteInt(field);
        packet.ResetOffset();

        var handler = provider.GetRequiredService<IInGameOpcodeRouter>().Resolve(opcode);
        return handler!(player.Client, packet);
    }

    protected static async Task<T> InScopeAsync<T>(ServiceProvider provider, Func<AppDbContext, Task<T>> query)
    {
        await using var scope = provider.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected static Task<List<CharacterRewardQuest>> StoredQuestsAsync(ServiceProvider provider, int characterId) =>
        InScopeAsync(provider, db => db.CharacterRewardQuests.AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .ToListAsync());

    protected static Task<int> StoredCoinsAsync(ServiceProvider provider, int characterId) =>
        InScopeAsync(provider, db => db.EventCoinWallets.AsNoTracking()
            .Where(wallet => wallet.CharacterId == characterId)
            .Select(wallet => wallet.Coins)
            .FirstOrDefaultAsync());

    protected static int ViolationScore(ServiceProvider provider, RewardPlayer player) =>
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(player.Client.Id);

    protected static bool ReceivedNotice(RewardPlayer player, string message) =>
        player.Sent.Any(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT && ContainsText(packet, message));

    private static bool ContainsText(Packet packet, string text)
    {
        var bytes = packet.GetData();
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }
}
