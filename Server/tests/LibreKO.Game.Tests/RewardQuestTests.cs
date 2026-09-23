using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;

namespace LibreKO.Game.Tests;

public class RewardQuestTests : RewardTestBase
{
    private const byte EventListSub = 1;
    private const byte EventAcceptSub = 2;
    private const byte EventClaimSub = 3;
    private const byte Succeeded = 1;
    private const byte Failed = 0;
    private const int UnknownQuestId = 424_242;
    private const int ConcurrentClaims = 8;
    private const float FarAway = 300;

    private sealed record EventRow(int Id, string Title, bool Accepted, bool Claimable);

    private sealed record DailyRow(int Id, bool Available, bool Completed, string Title);

    [Fact]
    public async Task EventQuest_ClaimWithoutProgressPaysNothingAndIsFlaggedAsForged()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5001, 6001, level: 10);

        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventHuntId);
        LastEventResult(player, EventAcceptSub).Should().Be((Succeeded, EventHuntId));

        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);

        LastEventResult(player, EventClaimSub).Should().Be((Failed, EventHuntId));
        player.Session.Money.Should().Be(0);
        player.Session.Rewards.EventCoins.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
        (await StoredQuestsAsync(provider, 5001)).Should()
            .ContainSingle(row => row.QuestId == EventHuntId && row.AcceptedAt != null && row.ClaimedAt == null);
    }

    [Fact]
    public async Task EventQuest_KillsAfterAcceptingCompleteTheQuestAndItPaysExactlyOnce()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5002, 6002, level: 10);
        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventHuntId);

        await KillAsync(provider, player.Session, WormId, HuntKills);
        var row = (await ListEventsAsync(provider, player)).Single(entry => entry.Id == EventHuntId);
        row.Should().Be(new EventRow(EventHuntId, $"Quest {EventHuntId} ({HuntKills}/{HuntKills})", true, true));

        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);

        player.Session.Money.Should().Be(EventHuntGold);
        player.Session.Rewards.EventCoins.Should().Be(EventHuntCoins);
        (await StoredCoinsAsync(provider, 5002)).Should().Be(EventHuntCoins);
        LastEventResult(player, EventClaimSub).Should().Be((Failed, EventHuntId));
        ViolationScore(provider, player).Should().Be(ViolationMonitor.InvalidStateWeight);
        var stored = (await StoredQuestsAsync(provider, 5002)).Single(entry => entry.QuestId == EventHuntId);
        stored.Kills.Should().Be(HuntKills);
        stored.ClaimedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task EventQuest_KillsBeforeAcceptingDoNotCount()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5003, 6003, level: 10);

        await KillAsync(provider, player.Session, WormId, HuntKills);
        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventHuntId);

        var row = (await ListEventsAsync(provider, player)).Single(entry => entry.Id == EventHuntId);
        row.Should().Be(new EventRow(EventHuntId, $"Quest {EventHuntId} (0/{HuntKills})", true, false));
    }

    [Fact]
    public async Task EventQuest_AClaimedQuestStaysClaimedAfterARestart()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5004, 6004, level: 10);
        await CompleteEventHuntAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);
        player.Session.Money.Should().Be(EventHuntGold);

        var restarted = Relog(provider, player);
        var row = (await ListEventsAsync(provider, restarted)).Single(entry => entry.Id == EventHuntId);
        await RouteAsync(provider, restarted, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);
        await RouteAsync(provider, restarted, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventHuntId);

        row.Should().Be(new EventRow(EventHuntId, $"Quest {EventHuntId}{RewardQuestTitles.ClaimedSuffix}", true, false));
        LastEventResult(restarted, EventClaimSub).Should().Be((Failed, EventHuntId));
        LastEventResult(restarted, EventAcceptSub).Should().Be((Failed, EventHuntId));
        restarted.Session.Money.Should().Be(0);
        (await StoredCoinsAsync(provider, 5004)).Should().Be(EventHuntCoins);
    }

    [Fact]
    public async Task EventQuest_ItemObjectiveConsumesTheItemsOnlyWhenAllArePresent()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5005, 6005, level: 10);
        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventGatherId);
        Give(player.Session, InventoryConstants.InventoryStart, HerbItemId, HerbsRequired - 1);

        var partial = (await ListEventsAsync(provider, player)).Single(entry => entry.Id == EventGatherId);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventGatherId);

        partial.Should().Be(new EventRow(EventGatherId, $"Quest {EventGatherId} ({HerbsRequired - 1}/{HerbsRequired})", true, false));
        CountItem(player.Session, HerbItemId).Should().Be(HerbsRequired - 1);
        player.Session.Money.Should().Be(0);

        Give(player.Session, InventoryConstants.InventoryStart + 1, HerbItemId, 1);
        (await ListEventsAsync(provider, player)).Single(entry => entry.Id == EventGatherId).Claimable.Should().BeTrue();
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventGatherId);

        LastEventResult(player, EventClaimSub).Should().Be((Succeeded, EventGatherId));
        CountItem(player.Session, HerbItemId).Should().Be(0);
        CountItem(player.Session, ScrollItemId).Should().Be(1);
        player.Session.Money.Should().Be(EventGatherGold);
    }

    [Fact]
    public async Task EventQuest_OnlyInSeasonQuestsForTheLevelAreOffered()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var novice = Player(provider, 5006, 6006, level: 10);
        var veteran = Player(provider, 5007, 6007, level: 50);

        var noviceRows = await ListEventsAsync(provider, novice);
        var veteranRows = await ListEventsAsync(provider, veteran);
        await RouteAsync(provider, novice, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventVeteranId);

        noviceRows.Select(row => row.Id).Should().Equal(EventHuntId, EventGatherId);
        veteranRows.Select(row => row.Id).Should().Equal(EventVeteranId);
        LastEventResult(novice, EventAcceptSub).Should().Be((Failed, EventVeteranId));
        ViolationScore(provider, novice).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task EventQuest_OutOfSeasonQuestsCannotBeAccepted()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5008, 6008, level: 10);

        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventSummerId);

        LastEventResult(player, EventAcceptSub).Should().Be((Failed, EventSummerId));
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
        (await StoredQuestsAsync(provider, 5008)).Should().BeEmpty();
    }

    [Fact]
    public async Task EventQuest_TheBoardEmptiesWhenTheEventWindowCloses()
    {
        var clock = new ManualClock();
        using var provider = CreateRewardProvider(clock);
        var player = Player(provider, 5009, 6009, level: 10);
        await CompleteEventHuntAsync(provider, player);

        clock.Advance(TimeSpan.FromDays(31));
        var rows = await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, EventHuntId);

        rows.Should().BeEmpty();
        player.Session.Money.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task EventQuest_UnknownQuestIdsAreForged()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5010, 6010, level: 10);

        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventClaimSub, UnknownQuestId);

        LastEventResult(player, EventClaimSub).Should().Be((Failed, UnknownQuestId));
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task DailyQuest_ClaimWithoutKillsIsRefusedAsForged()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5011, 6011, level: 10);

        var row = (await ListDailyAsync(provider, player)).Single(entry => entry.Id == DailyHuntId);
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailyHuntId);

        row.Should().Be(new DailyRow(DailyHuntId, false, false, $"Quest {DailyHuntId} (0/{HuntKills})"));
        LastDailyResult(player).Should().Be((Failed, DailyHuntId));
        player.Session.Money.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task DailyQuest_KillsCountOnlyInsideTheLevelRange()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5012, 6012, level: 30);

        await KillAsync(provider, player.Session, WormId, HuntKills);
        var row = (await ListDailyAsync(provider, player)).Single(entry => entry.Id == DailyHuntId);

        row.Should().Be(new DailyRow(DailyHuntId, false, false, $"Quest {DailyHuntId} (0/{HuntKills}) [Lv 1-20]"));
        (await StoredQuestsAsync(provider, 5012)).Should().BeEmpty();
    }

    [Fact]
    public async Task DailyQuest_PaysOncePerUtcDayAndReopensTomorrow()
    {
        var clock = new ManualClock();
        using var provider = CreateRewardProvider(clock);
        var player = Player(provider, 5013, 6013, level: 10);

        await KillAsync(provider, player.Session, WormId, HuntKills);
        (await ListDailyAsync(provider, player)).Single(entry => entry.Id == DailyHuntId).Available.Should().BeTrue();
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailyHuntId);
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailyHuntId);

        player.Session.Money.Should().Be(DailyHuntGold);
        player.Session.Experience.Should().Be(DailyHuntGold);
        (await StoredCoinsAsync(provider, 5013)).Should().Be(DailyHuntCoins);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.InvalidStateWeight);
        (await ListDailyAsync(provider, player)).Single(entry => entry.Id == DailyHuntId).Completed.Should().BeTrue();

        clock.Advance(TimeSpan.FromDays(1));
        var tomorrow = (await ListDailyAsync(provider, player)).Single(entry => entry.Id == DailyHuntId);
        await KillAsync(provider, player.Session, WormId, HuntKills);
        await ListDailyAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailyHuntId);

        tomorrow.Should().Be(new DailyRow(DailyHuntId, false, false, $"Quest {DailyHuntId} (0/{HuntKills})"));
        player.Session.Money.Should().Be(DailyHuntGold * 2);
        (await StoredQuestsAsync(provider, 5013)).Should().HaveCount(2).And.OnlyContain(row => row.ClaimedAt != null);
    }

    [Fact]
    public async Task DailyQuest_AFreshLevelOneAltCannotClaimAnything()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var veteran = Player(provider, 5014, 6014, level: 10);
        await KillAsync(provider, veteran.Session, WormId, HuntKills);
        await KillAsync(provider, veteran.Session, WolfId, SoloHuntKills);
        var alt = Player(provider, 5015, 6014, level: 1);

        await ListDailyAsync(provider, alt);
        foreach (var questId in new[] { DailyHuntId, DailySoloHuntId, DailySupplyId, WeeklyBossId })
            await provider.GetRequiredService<IRewardQuestService>().ClaimAsync(alt.Session, QuestBoard.Daily, questId);

        alt.Session.Money.Should().Be(0);
        alt.Session.Rewards.EventCoins.Should().Be(0);
        (await StoredQuestsAsync(provider, 5015)).Should().BeEmpty();
    }

    [Fact]
    public async Task DailyQuest_ConcurrentClaimsPayOnce()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5016, 6016, level: 10);
        await KillAsync(provider, player.Session, WormId, HuntKills);
        await ListDailyAsync(provider, player);
        var quests = provider.GetRequiredService<IRewardQuestService>();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, ConcurrentClaims)
            .Select(_ => Task.Run(() => quests.ClaimAsync(player.Session, QuestBoard.Daily, DailyHuntId))));

        outcomes.Count(outcome => outcome == RewardOutcome.Succeeded).Should().Be(1);
        player.Session.Money.Should().Be(DailyHuntGold);
        (await StoredCoinsAsync(provider, 5016)).Should().Be(DailyHuntCoins);
    }

    [Fact]
    public async Task DailyQuest_TwoSessionsOfTheSameCharacterCannotBothClaim()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var first = Player(provider, 5017, 6017, level: 10);
        await KillAsync(provider, first.Session, WormId, HuntKills);
        await ListDailyAsync(provider, first);
        var second = Player(provider, 5017, 6017, level: 10);
        await ListDailyAsync(provider, second);
        var quests = provider.GetRequiredService<IRewardQuestService>();

        var outcomes = await Task.WhenAll(
            Task.Run(() => quests.ClaimAsync(first.Session, QuestBoard.Daily, DailyHuntId)),
            Task.Run(() => quests.ClaimAsync(second.Session, QuestBoard.Daily, DailyHuntId)));

        outcomes.Count(outcome => outcome == RewardOutcome.Succeeded).Should().Be(1);
        (first.Session.Money + second.Session.Money).Should().Be(DailyHuntGold);
        (await StoredCoinsAsync(provider, 5017)).Should().Be(DailyHuntCoins);
    }

    [Fact]
    public async Task WeeklyQuest_ResetsOnMonday()
    {
        var clock = new ManualClock();
        using var provider = CreateRewardProvider(clock);
        var player = Player(provider, 5018, 6018, level: 40);
        var quests = provider.GetRequiredService<IRewardQuestService>();

        await KillAsync(provider, player.Session, BossId);
        (await quests.ClaimAsync(player.Session, QuestBoard.Daily, WeeklyBossId)).Should().Be(RewardOutcome.Succeeded);

        clock.Advance(TimeSpan.FromDays(1));
        await KillAsync(provider, player.Session, BossId);
        var sameWeek = await quests.ClaimAsync(player.Session, QuestBoard.Daily, WeeklyBossId);

        clock.Advance(TimeSpan.FromDays(3));
        await KillAsync(provider, player.Session, BossId);
        var nextWeek = await quests.ClaimAsync(player.Session, QuestBoard.Daily, WeeklyBossId);

        sameWeek.Should().Be(RewardOutcome.Refused);
        nextWeek.Should().Be(RewardOutcome.Succeeded);
        player.Session.Money.Should().Be(WeeklyBossGold * 2);
    }

    [Fact]
    public async Task PartySharedKillsCreditMembersInRangeOnly()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var leader = Player(provider, 5019, 6019, level: 10);
        var near = Player(provider, 5020, 6020, level: 10);
        var far = Player(provider, 5021, 6021, level: 10, x: FarAway, z: FarAway);
        var party = provider.GetRequiredService<SessionManager>().Parties.CreateParty((short)leader.Session.CharacterId);
        party.MemberIds[1] = (short)near.Session.CharacterId;
        party.MemberIds[2] = (short)far.Session.CharacterId;
        foreach (var member in new[] { leader, near, far })
            member.Session.PartyIndex = party.Index;

        await KillAsync(provider, leader.Session, WormId, HuntKills);
        await KillAsync(provider, leader.Session, WolfId, SoloHuntKills);
        await provider.GetRequiredService<IRewardStateService>().FlushAsync(near.Session);
        await provider.GetRequiredService<IRewardStateService>().FlushAsync(far.Session);

        var nearRows = await ListDailyAsync(provider, near);
        var farRows = await ListDailyAsync(provider, far);
        var leaderRows = await ListDailyAsync(provider, leader);

        leaderRows.Single(row => row.Id == DailyHuntId).Available.Should().BeTrue();
        leaderRows.Single(row => row.Id == DailySoloHuntId).Available.Should().BeTrue();
        nearRows.Single(row => row.Id == DailyHuntId).Available.Should().BeTrue();
        nearRows.Single(row => row.Id == DailySoloHuntId).Available.Should().BeFalse();
        farRows.Single(row => row.Id == DailyHuntId).Title.Should().Be($"Quest {DailyHuntId} (0/{HuntKills})");
        (await StoredQuestsAsync(provider, 5020)).Should().ContainSingle(row => row.QuestId == DailyHuntId && row.Kills == HuntKills);
    }

    [Fact]
    public async Task KillProgressSurvivesARestart()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5022, 6022, level: 10);

        await KillAsync(provider, player.Session, WormId, HuntKills - 1);
        var restarted = Relog(provider, player);
        var row = (await ListDailyAsync(provider, restarted)).Single(entry => entry.Id == DailyHuntId);

        row.Title.Should().Be($"Quest {DailyHuntId} ({HuntKills - 1}/{HuntKills})");
    }

    [Fact]
    public async Task ItemClaimsAreRefusedWhileTheInventoryIsLockedByATrade()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5023, 6023, level: 10);
        Give(player.Session, InventoryConstants.InventoryStart, HerbItemId, HerbsRequired);
        await ListDailyAsync(provider, player);
        player.Session.Trade.ExchangeUser = 5099;

        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailySupplyId);

        LastDailyResult(player).Should().Be((Failed, DailySupplyId));
        ReceivedNotice(player, RewardNotices.InventoryLocked).Should().BeTrue();
        CountItem(player.Session, HerbItemId).Should().Be(HerbsRequired);
        CountItem(player.Session, ScrollItemId).Should().Be(0);
        ViolationScore(provider, player).Should().Be(0);
    }

    [Fact]
    public async Task ARewardThatDoesNotFitIsRefusedWithoutConsumingAnything()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5024, 6024, level: 10);
        Give(player.Session, InventoryConstants.InventoryStart, HerbItemId, HerbsRequired * 2);
        FillInventoryGrid(player.Session, JunkItemId);
        await ListDailyAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailySupplyId);

        LastDailyResult(player).Should().Be((Failed, DailySupplyId));
        ReceivedNotice(player, RewardNotices.InventoryFull).Should().BeTrue();
        CountItem(player.Session, HerbItemId).Should().Be(HerbsRequired * 2);
        player.Session.Rewards.EventCoins.Should().Be(0);
        (await StoredQuestsAsync(provider, 5024)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedDatabaseWriteRollsTheClaimBack()
    {
        using var provider = CreateRewardProvider(
            new ManualClock(),
            configureServices: services => services.AddScoped<LibreKO.Common.Domain.Services.IRewardStateRepository, FailingWritesRepository>());
        var player = Player(provider, 5025, 6025, level: 10);
        Give(player.Session, InventoryConstants.InventoryStart, HerbItemId, HerbsRequired);
        await ListDailyAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailySupplyId);

        LastDailyResult(player).Should().Be((Failed, DailySupplyId));
        ReceivedNotice(player, RewardNotices.Unavailable).Should().BeTrue();
        CountItem(player.Session, HerbItemId).Should().Be(HerbsRequired);
        CountItem(player.Session, ScrollItemId).Should().Be(0);
        player.Session.Rewards.EventCoins.Should().Be(0);
        (await ListDailyAsync(provider, player)).Single(row => row.Id == DailySupplyId).Available.Should().BeTrue();
    }

    [Fact]
    public async Task DailyQuest_ClaimingAnEventQuestThroughTheDailyBoardIsForged()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5026, 6026, level: 10);
        await CompleteEventHuntAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, EventHuntId);

        LastDailyResult(player).Should().Be((Failed, EventHuntId));
        player.Session.Money.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task CompletingAKillObjectiveSendsAClaimReminder()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 5027, 6027, level: 10);

        await KillAsync(provider, player.Session, WormId, HuntKills);

        ReceivedNotice(player, $"Quest {DailyHuntId} is complete.").Should().BeTrue();
    }

    private async Task CompleteEventHuntAsync(ServiceProvider provider, RewardPlayer player)
    {
        await ListEventsAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventAcceptSub, EventHuntId);
        await KillAsync(provider, player.Session, WormId, HuntKills);
        await ListEventsAsync(provider, player);
    }

    private static async Task<List<EventRow>> ListEventsAsync(ServiceProvider provider, RewardPlayer player)
    {
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_QUEST, EventListSub);
        var packet = Last(player, GameOpcodes.GS_EVENT_QUEST, EventListSub);
        var count = packet.ReadUShort();
        var rows = new List<EventRow>(count);
        for (var index = 0; index < count; index++)
            rows.Add(new EventRow(packet.ReadInt(), packet.ReadSByteString(), packet.ReadByte() != 0, packet.ReadByte() != 0));

        packet.RemainingBytes.Should().Be(0);
        return rows;
    }

    private static async Task<List<DailyRow>> ListDailyAsync(ServiceProvider provider, RewardPlayer player)
    {
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.List);
        var packet = Last(player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.List);
        var count = packet.ReadUShort();
        var rows = new List<DailyRow>(count);
        for (var index = 0; index < count; index++)
            rows.Add(new DailyRow(packet.ReadInt(), packet.ReadByte() != 0, packet.ReadByte() != 0, packet.ReadSByteString()));

        packet.RemainingBytes.Should().Be(0);
        return rows;
    }

    private static (byte Result, int QuestId) LastEventResult(RewardPlayer player, byte sub)
    {
        var packet = Last(player, GameOpcodes.GS_EVENT_QUEST, sub);
        return (packet.ReadByte(), packet.ReadInt());
    }

    private static (byte Result, int QuestId) LastDailyResult(RewardPlayer player)
    {
        var packet = Last(player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim);
        return (packet.ReadByte(), packet.ReadInt());
    }

    private static Packet Last(RewardPlayer player, GameOpcodes opcode, byte sub)
    {
        var packet = player.Sent.Last(sent => sent.GetOpcode() == (byte)opcode && sent.GetData()[0] == sub);
        packet.ResetOffset();
        packet.ReadByte();
        return packet;
    }
}
