using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Common.Infrastructure.Persistence.Seed.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LibreKO.Game.Tests;

public class RewardSeedTests
{
    private const byte MaxLevel = 83;
    private const int DaysInLeapYear = 366;
    private const string RewardMigrationSuffix = "_AddRewardSystems";

    private static readonly Version MariaDbVersion = new(11, 4);
    private static readonly DateOnly NewYear = new(2026, 1, 1);

    [Fact]
    public void RewardQuestSeed_EveryQuestHasAnObjectiveAndARewardItCanPay()
    {
        var quests = new RewardQuestSeed().GetSeedData().ToList();
        var targets = new RewardQuestTargetSeed().GetSeedData().ToList();
        var requirements = new RewardQuestItemSeed().GetSeedData().ToList();
        var rewards = new RewardQuestRewardSeed().GetSeedData().ToList();
        var ids = quests.Select(quest => quest.Id).ToHashSet();

        quests.Should().NotBeEmpty();
        ids.Should().HaveCount(quests.Count);
        foreach (var quest in quests)
        {
            quest.Title.Should().NotBeEmpty().And.MatchRegex("^[ -~]+$");
            quest.Title.Length.Should().BeLessThanOrEqualTo(RewardQuestData.TitleMaxLength);
            quest.MinLevel.Should().BeGreaterThan(0);
            quest.MaxLevel.Should().BeInRange(quest.MinLevel, MaxLevel);
            Enum.IsDefined(quest.Board).Should().BeTrue();
            Enum.IsDefined(quest.Recurrence).Should().BeTrue();
            if (quest.Board == QuestBoard.Event)
            {
                quest.Recurrence.Should().Be(QuestRecurrence.Once, $"event quest {quest.Id} is paid once per festival");
                quest.DurationDays.Should().BePositive($"event quest {quest.Id} needs a festival window");
            }
            else
            {
                quest.Recurrence.Should().BeOneOf(QuestRecurrence.Daily, QuestRecurrence.Weekly);
            }

            var hasObjective = quest.KillCount > 0
                ? targets.Any(target => target.QuestId == quest.Id)
                : requirements.Any(requirement => requirement.QuestId == quest.Id);
            hasObjective.Should().BeTrue($"quest {quest.Id} must be earned");
            rewards.Should().Contain(reward => reward.QuestId == quest.Id);
        }

        targets.Select(target => target.Id).Should().OnlyHaveUniqueItems();
        targets.Should().OnlyContain(target => ids.Contains(target.QuestId));
        requirements.Select(requirement => requirement.Id).Should().OnlyHaveUniqueItems();
        requirements.Should().OnlyContain(requirement => ids.Contains(requirement.QuestId) && requirement.ItemId > 0 && requirement.Count > 0);
        rewards.Select(reward => reward.Id).Should().OnlyHaveUniqueItems();
        rewards.Should().OnlyContain(reward => ids.Contains(reward.QuestId)
            && reward.Count > 0
            && Enum.IsDefined(reward.Kind)
            && (reward.Kind == RewardKind.Item) == (reward.ItemId > 0));
    }

    [Fact]
    public void RewardQuestSeed_TargetsAreMonsters()
    {
        var monsters = new NpcSeed().GetSeedData().Where(npc => npc.IsMonster).Select(npc => npc.Id).ToHashSet();

        new RewardQuestTargetSeed().GetSeedData().Should().OnlyContain(target => monsters.Contains(target.NpcId));
    }

    [Fact]
    public void RewardQuestSeed_EveryFestivalOpensOnceAYear()
    {
        var events = new RewardQuestSeed().GetSeedData().Where(quest => quest.Board == QuestBoard.Event).ToList();
        var autumn = new DateOnly(2026, 9, 23);

        events.Where(quest => quest.CurrentPeriod(autumn) != null).Should().NotBeEmpty()
            .And.OnlyContain(quest => quest.Title.StartsWith("Harvest Moon"));
        foreach (var quest in events)
        {
            Enumerable.Range(0, DaysInLeapYear)
                .Any(offset => quest.CurrentPeriod(NewYear.AddDays(offset)) != null)
                .Should().BeTrue($"event quest {quest.Id} must open during the year");
        }
    }

    [Fact]
    public void RewardPrizeSeed_EveryPoolPaysEveryEligibleLevelWithPrizesItsWindowCanShow()
    {
        var prizes = new RewardPrizeSeed().GetSeedData().ToList();

        prizes.Select(prize => prize.Id).Should().OnlyHaveUniqueItems();
        prizes.Should().OnlyContain(prize => prize.Weight > 0 && prize.Count > 0 && prize.MinLevel <= prize.MaxLevel && prize.MaxLevel <= MaxLevel);
        prizes.Should().OnlyContain(prize => prize.Kind == RewardKind.Gold
            || (prize.Kind == RewardKind.Item && prize.ItemId > 0 && prize.Pool != PrizePool.Genie));
        foreach (var pool in Enum.GetValues<PrizePool>())
        {
            var rows = prizes.Where(prize => prize.Pool == pool).ToList();
            rows.Should().NotBeEmpty();
            for (var level = rows.Min(prize => prize.MinLevel); level <= MaxLevel; level++)
                rows.Should().Contain(prize => prize.AcceptsLevel(level), $"{pool} must pay at level {level}");
        }

        var genie = prizes.Where(prize => prize.Pool == PrizePool.Genie).ToList();
        for (var level = genie.Min(prize => prize.MinLevel); level <= MaxLevel; level++)
            genie.Count(prize => prize.AcceptsLevel(level)).Should().Be(1, $"the genie pays a single tier at level {level}");
    }

    [Fact]
    public void RewardQuestData_PeriodsFollowTheirRecurrence()
    {
        var monday = new DateOnly(2025, 12, 29);

        Quest(QuestRecurrence.Daily).CurrentPeriod(NewYear).Should().Be(NewYear);
        Quest(QuestRecurrence.Weekly).CurrentPeriod(NewYear).Should().Be(monday);
        Quest(QuestRecurrence.Weekly).CurrentPeriod(monday.AddDays(6)).Should().Be(monday);
        Quest(QuestRecurrence.Weekly).CurrentPeriod(monday.AddDays(7)).Should().Be(monday.AddDays(7));
        Quest(QuestRecurrence.Once).CurrentPeriod(NewYear).Should().Be(RewardQuestData.PermanentPeriod);
    }

    [Fact]
    public void RewardQuestData_FestivalWindowsWrapAroundTheNewYear()
    {
        var winter = Quest(QuestRecurrence.Once, startMonth: 12, startDay: 20, durationDays: 20);
        var leapDay = Quest(QuestRecurrence.Once, startMonth: 2, startDay: 29, durationDays: 1);
        var invalid = Quest(QuestRecurrence.Once, startMonth: 13, startDay: 1, durationDays: 10);

        winter.CurrentPeriod(new DateOnly(2026, 1, 8)).Should().Be(new DateOnly(2025, 12, 20));
        winter.CurrentPeriod(new DateOnly(2026, 1, 9)).Should().BeNull();
        winter.CurrentPeriod(new DateOnly(2026, 12, 19)).Should().BeNull();
        winter.CurrentPeriod(new DateOnly(2026, 12, 20)).Should().Be(new DateOnly(2026, 12, 20));
        leapDay.CurrentPeriod(new DateOnly(2027, 2, 28)).Should().Be(new DateOnly(2027, 2, 28));
        invalid.CurrentPeriod(NewYear).Should().BeNull();
    }

    [Fact]
    public void RewardModel_KeysMakeClaimsIdempotentAndLoginsUnique()
    {
        using var context = RelationalContext();

        KeyOf(context.Model, typeof(CharacterRewardQuest)).Should().Equal(
            nameof(CharacterRewardQuest.CharacterId), nameof(CharacterRewardQuest.QuestId), nameof(CharacterRewardQuest.PeriodStart));
        KeyOf(context.Model, typeof(DailyRewardClaim)).Should().Equal(
            nameof(DailyRewardClaim.AccountId), nameof(DailyRewardClaim.Pool), nameof(DailyRewardClaim.Day));
        KeyOf(context.Model, typeof(EventCoinWallet)).Should().Equal(nameof(EventCoinWallet.CharacterId));
        context.Model.FindEntityType(typeof(CharacterRewardQuest))!.FindProperty(nameof(CharacterRewardQuest.ClaimedAt))!
            .IsConcurrencyToken.Should().BeTrue();
        context.Model.FindEntityType(typeof(EventCoinWallet))!.FindProperty(nameof(EventCoinWallet.Coins))!
            .IsConcurrencyToken.Should().BeTrue();
        context.Model.FindEntityType(typeof(Account))!.GetIndexes().Should().Contain(index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(Account.Login) }));
    }

    [Fact]
    public void Migrations_CoverTheRewardModel()
    {
        using var context = RelationalContext();

        context.Database.GetMigrations().Should().Contain(migration => migration.EndsWith(RewardMigrationSuffix));
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void Migrations_TheRewardMigrationCreatesTheUniqueLoginIndexBeforeAnyTable()
    {
        using var context = RelationalContext();

        var operations = RewardMigration(context).UpOperations;

        operations[0].Should().BeOfType<CreateIndexOperation>()
            .Which.Should().Match<CreateIndexOperation>(index => index.Table == nameof(AppDbContext.Accounts) && index.IsUnique);
        operations.OfType<CreateTableOperation>().Should().NotBeEmpty();
    }

    [Fact]
    public void Migrations_TheModelSnapshotMatchesTheCurrentModel()
    {
        using var context = RelationalContext();
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;

        var target = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot);

        context.GetService<IMigrationsModelDiffer>().HasDifferences(
            target.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel()).Should().BeFalse();
    }

    private static Migration RewardMigration(AppDbContext context)
    {
        var migrations = context.GetService<IMigrationsAssembly>();
        var reward = migrations.Migrations.Single(migration => migration.Key.EndsWith(RewardMigrationSuffix));
        return migrations.CreateMigration(reward.Value, context.Database.ProviderName!);
    }

    private static RewardQuestData Quest(QuestRecurrence recurrence, byte startMonth = 0, byte startDay = 0, short durationDays = 0) =>
        new()
        {
            Recurrence = recurrence,
            StartMonth = startMonth,
            StartDay = startDay,
            DurationDays = durationDays,
        };

    private static IEnumerable<string> KeyOf(IModel model, Type entity) =>
        model.FindEntityType(entity)!.FindPrimaryKey()!.Properties.Select(property => property.Name);

    private static AppDbContext RelationalContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=libreko", new MariaDbServerVersion(MariaDbVersion))
            .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);
}
