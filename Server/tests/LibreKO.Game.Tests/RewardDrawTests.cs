using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LibreKO.Game.Tests;

public class RewardDrawTests : RewardTestBase
{
    private const byte RouletteOpen = (byte)EventBoardSubOpcode.RouletteOpen;
    private const byte RouletteSpin = (byte)EventBoardSubOpcode.RouletteSpin;
    private const byte RoulettePrizeList = (byte)EventBoardSubOpcode.RoulettePrizeList;
    private const byte StatusSub = 1;
    private const byte DrawSub = 2;
    private const byte Succeeded = 1;
    private const byte Failed = 0;
    private const int ConcurrentRequests = 8;
    private const int SpinAttempts = 10;

    [Fact]
    public async Task Roulette_ANewCharacterHasNoCoinsAndCannotSpin()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7001, 8001, level: 10);

        var coins = await OpenRouletteAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);

        coins.Should().Be(0);
        LastSpin(player).Should().Be((Failed, 0, 0));
        player.Session.Money.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task Roulette_CoinsEarnedFromQuestsArePaidOutOnePerSpin()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7002, 8002, level: 10);
        await EarnDailyHuntAsync(provider, player);

        var coins = await OpenRouletteAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);

        coins.Should().Be(DailyHuntCoins);
        player.Sent.Where(IsSpin).Select(Spin).Should().Equal((Succeeded, 0, RouletteGold), (Failed, 0, 0));
        player.Session.Money.Should().Be(DailyHuntGold + RouletteGold);
        player.Session.Rewards.EventCoins.Should().Be(0);
        (await StoredCoinsAsync(provider, 7002)).Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.InvalidStateWeight);
    }

    [Fact]
    public async Task Roulette_SpinsArePersistedAndSurviveARestart()
    {
        using var provider = CreateRewardProvider(new ManualClock(), new ScriptedRandom(ItemPrizeRoll));
        var player = Player(provider, 7003, 8003, level: 10);
        await EarnDailyHuntAsync(provider, player);
        await OpenRouletteAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);
        LastSpin(player).Should().Be((Succeeded, PotionItemId, 0));
        CountItem(player.Session, PotionItemId).Should().Be(RoulettePotions);

        var restarted = Relog(provider, player);
        var coins = await OpenRouletteAsync(provider, restarted);
        await RouteAsync(provider, restarted, GameOpcodes.GS_EVENT_BOARD, RoulettePrizeList, 0);

        coins.Should().Be(0);
        var log = Last(restarted, GameOpcodes.GS_EVENT_BOARD, RoulettePrizeList);
        log.ReadInt();
        log.ReadInt().Should().Be(RoulettePacketWriter.ResultOk);
        log.ReadInt().Should().Be(1);
        log.ReadInt().Should().Be(PotionItemId);
        log.ReadInt().Should().Be(RoulettePotions);
        (await InScopeAsync(provider, db => db.RouletteSpins.CountAsync(spin => spin.CharacterId == 7003))).Should().Be(1);
    }

    [Fact]
    public async Task Roulette_AFullInventoryRefusesTheSpinBeforeRolling()
    {
        var random = new ScriptedRandom(GoldPrizeRoll);
        using var provider = CreateRewardProvider(new ManualClock(), random);
        var player = Player(provider, 7004, 8004, level: 10);
        await EarnDailyHuntAsync(provider, player);
        FillInventoryGrid(player.Session, JunkItemId);
        await OpenRouletteAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);

        LastSpin(player).Should().Be((Failed, 0, 0));
        ReceivedNotice(player, RewardNotices.InventoryFull).Should().BeTrue();
        random.Calls.Should().Be(0);
        player.Session.Rewards.EventCoins.Should().Be(DailyHuntCoins);
        (await StoredCoinsAsync(provider, 7004)).Should().Be(DailyHuntCoins);
    }

    [Fact]
    public async Task Roulette_ConcurrentSpinsNeverSpendMoreCoinsThanEarned()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7005, 8005, level: 10);
        await ClaimEventHuntAsync(provider, player);
        await OpenRouletteAsync(provider, player);
        var draws = provider.GetRequiredService<IRewardDrawService>();

        var results = await Task.WhenAll(Enumerable.Range(0, SpinAttempts)
            .Select(_ => Task.Run(() => draws.SpinRouletteAsync(player.Session))));

        results.Count(result => result.Outcome == RewardOutcome.Succeeded).Should().Be(EventHuntCoins);
        player.Session.Money.Should().Be(EventHuntGold + EventHuntCoins * RouletteGold);
        (await StoredCoinsAsync(provider, 7005)).Should().Be(0);
        (await InScopeAsync(provider, db => db.RouletteSpins.CountAsync(spin => spin.CharacterId == 7005))).Should().Be(EventHuntCoins);
    }

    [Fact]
    public async Task Fortune_OneDrawPerAccountPerUtcDay()
    {
        var clock = new ManualClock();
        using var provider = CreateRewardProvider(clock);
        var main = Player(provider, 7006, 8006, level: 20);
        var alt = Player(provider, 7007, 8006, level: 20);

        (await FortuneStatusAsync(provider, main)).Should().BeTrue();
        await RouteAsync(provider, main, GameOpcodes.GS_FORTUNE, DrawSub);
        var altStatus = await FortuneStatusAsync(provider, alt);
        await RouteAsync(provider, alt, GameOpcodes.GS_FORTUNE, DrawSub);

        LastDraw(main, GameOpcodes.GS_FORTUNE).Should().Be((Succeeded, 0, FortuneGold));
        altStatus.Should().BeFalse();
        LastDraw(alt, GameOpcodes.GS_FORTUNE).Should().Be((Failed, 0, 0));
        alt.Session.Money.Should().Be(0);
        ViolationScore(provider, alt).Should().Be(ViolationMonitor.InvalidStateWeight);

        clock.Advance(TimeSpan.FromDays(1));
        (await FortuneStatusAsync(provider, alt)).Should().BeTrue();
        await RouteAsync(provider, alt, GameOpcodes.GS_FORTUNE, DrawSub);
        alt.Session.Money.Should().Be(FortuneGold);
    }

    [Fact]
    public async Task Fortune_AFreshLevelOneAltIsRefusedAsForged()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var alt = Player(provider, 7008, 8008, level: 1);

        var status = await FortuneStatusAsync(provider, alt);
        await RouteAsync(provider, alt, GameOpcodes.GS_FORTUNE, DrawSub);

        status.Should().BeFalse();
        LastDraw(alt, GameOpcodes.GS_FORTUNE).Should().Be((Failed, 0, 0));
        alt.Session.Money.Should().Be(0);
        ViolationScore(provider, alt).Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task Fortune_TheDailyDrawSurvivesARestart()
    {
        using var provider = CreateRewardProvider(new ManualClock(), new ScriptedRandom(ItemPrizeRoll));
        var player = Player(provider, 7009, 8009, level: 20);
        await FortuneStatusAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_FORTUNE, DrawSub);
        LastDraw(player, GameOpcodes.GS_FORTUNE).Should().Be((Succeeded, PotionItemId, 0));

        var restarted = Relog(provider, player);
        var status = await FortuneStatusAsync(provider, restarted);
        await RouteAsync(provider, restarted, GameOpcodes.GS_FORTUNE, DrawSub);

        status.Should().BeFalse();
        LastDraw(restarted, GameOpcodes.GS_FORTUNE).Should().Be((Failed, 0, 0));
        CountItem(restarted.Session, PotionItemId).Should().Be(0);
        var claim = await InScopeAsync(provider, db => db.DailyRewardClaims.SingleAsync());
        claim.Should().BeEquivalentTo(new { AccountId = 8009, Pool = PrizePool.Fortune, CharacterId = 7009, ItemId = PotionItemId, Count = RoulettePotions });
    }

    [Fact]
    public async Task Fortune_ConcurrentDrawsPayOnce()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7010, 8010, level: 20);
        await FortuneStatusAsync(provider, player);
        var draws = provider.GetRequiredService<IRewardDrawService>();

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => Task.Run(() => draws.ClaimDailyRewardAsync(player.Session, PrizePool.Fortune))));

        results.Count(result => result.Outcome == RewardOutcome.Succeeded).Should().Be(1);
        player.Session.Money.Should().Be(FortuneGold);
    }

    [Fact]
    public async Task Fortune_AFullInventoryRefusesTheDrawAndKeepsItAvailable()
    {
        var random = new ScriptedRandom(GoldPrizeRoll);
        using var provider = CreateRewardProvider(new ManualClock(), random);
        var player = Player(provider, 7011, 8011, level: 20);
        FillInventoryGrid(player.Session, JunkItemId);
        await FortuneStatusAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_FORTUNE, DrawSub);

        LastDraw(player, GameOpcodes.GS_FORTUNE).Should().Be((Failed, 0, 0));
        ReceivedNotice(player, RewardNotices.InventoryFull).Should().BeTrue();
        random.Calls.Should().Be(0);
        (await FortuneStatusAsync(provider, player)).Should().BeTrue();
    }

    [Fact]
    public async Task Genie_PaysTheLevelTierOncePerAccountPerDay()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7012, 8012, level: 35);
        var alt = Player(provider, 7013, 8012, level: 35);

        var status = await GenieStatusAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_GENIE, DrawSub);
        await RouteAsync(provider, player, GameOpcodes.GS_GENIE, DrawSub);
        var altStatus = await GenieStatusAsync(provider, alt);

        status.Should().BeTrue();
        player.Sent.Where(packet => IsSub(packet, GameOpcodes.GS_GENIE, DrawSub)).Select(GenieClaim)
            .Should().Equal((Succeeded, GenieHighTierGold), (Failed, 0));
        player.Session.Money.Should().Be(GenieHighTierGold);
        altStatus.Should().BeFalse();
        ViolationScore(provider, player).Should().Be(ViolationMonitor.InvalidStateWeight);
    }

    [Fact]
    public async Task Genie_BelowTheFirstTierNothingIsPaid()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7014, 8014, level: 5);

        var status = await GenieStatusAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_GENIE, DrawSub);

        status.Should().BeFalse();
        player.Session.Money.Should().Be(0);
        ViolationScore(provider, player).Should().Be(ViolationMonitor.InvalidStateWeight);
    }

    [Fact]
    public async Task Genie_AFullPurseRefusesTheClaimAndKeepsItAvailable()
    {
        using var provider = CreateRewardProvider(new ManualClock());
        var player = Player(provider, 7015, 8015, level: 20);
        player.Session.Money = ExchangePacketConstants.CoinMax - 1;

        await GenieStatusAsync(provider, player);
        await RouteAsync(provider, player, GameOpcodes.GS_GENIE, DrawSub);

        ReceivedNotice(player, RewardNotices.PurseFull).Should().BeTrue();
        player.Session.Money.Should().Be(ExchangePacketConstants.CoinMax - 1);
        (await GenieStatusAsync(provider, player)).Should().BeTrue();
    }

    [Fact]
    public async Task DailyRewards_AFailedDatabaseWriteRollsTheDrawBack()
    {
        using var provider = CreateRewardProvider(
            new ManualClock(),
            new ScriptedRandom(ItemPrizeRoll),
            services => services.AddScoped<IRewardStateRepository, FailingWritesRepository>());
        var player = Player(provider, 7016, 8016, level: 20);
        await FortuneStatusAsync(provider, player);

        await RouteAsync(provider, player, GameOpcodes.GS_FORTUNE, DrawSub);

        LastDraw(player, GameOpcodes.GS_FORTUNE).Should().Be((Failed, 0, 0));
        ReceivedNotice(player, RewardNotices.Unavailable).Should().BeTrue();
        CountItem(player.Session, PotionItemId).Should().Be(0);
        (await FortuneStatusAsync(provider, player)).Should().BeTrue();
    }

    private static async Task EarnDailyHuntAsync(ServiceProvider provider, RewardPlayer player)
    {
        await KillAsync(provider, player.Session, WormId, HuntKills);
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.List);
        await RouteAsync(provider, player, GameOpcodes.GS_DAILY_QUEST, (byte)DailyQuestSubOpcode.Claim, DailyHuntId);
        player.Session.Rewards.EventCoins.Should().Be(DailyHuntCoins);
    }

    private static async Task ClaimEventHuntAsync(ServiceProvider provider, RewardPlayer player)
    {
        var quests = provider.GetRequiredService<IRewardQuestService>();
        await quests.ListAsync(player.Session, QuestBoard.Event);
        await quests.AcceptAsync(player.Session, EventHuntId);
        await KillAsync(provider, player.Session, WormId, HuntKills);
        (await quests.ClaimAsync(player.Session, QuestBoard.Event, EventHuntId)).Should().Be(RewardOutcome.Succeeded);
    }

    private static async Task<int> OpenRouletteAsync(ServiceProvider provider, RewardPlayer player)
    {
        await RouteAsync(provider, player, GameOpcodes.GS_EVENT_BOARD, RouletteOpen);
        return Last(player, GameOpcodes.GS_EVENT_BOARD, RouletteOpen).ReadInt();
    }

    private static async Task<bool> FortuneStatusAsync(ServiceProvider provider, RewardPlayer player)
    {
        await RouteAsync(provider, player, GameOpcodes.GS_FORTUNE, StatusSub);
        return Last(player, GameOpcodes.GS_FORTUNE, StatusSub).ReadByte() == Succeeded;
    }

    private static async Task<bool> GenieStatusAsync(ServiceProvider provider, RewardPlayer player)
    {
        await RouteAsync(provider, player, GameOpcodes.GS_GENIE, StatusSub);
        var packet = Last(player, GameOpcodes.GS_GENIE, StatusSub);
        packet.ReadSByteString().Should().NotBeEmpty();
        return packet.ReadByte() == Succeeded;
    }

    private static bool IsSpin(Packet packet) => IsSub(packet, GameOpcodes.GS_EVENT_BOARD, RouletteSpin);

    private static (byte Result, int ItemId, int Gold) Spin(Packet packet)
    {
        packet.ResetOffset();
        packet.ReadByte();
        return (packet.ReadByte(), packet.ReadInt(), packet.ReadInt());
    }

    private static (byte Result, int ItemId, int Gold) LastSpin(RewardPlayer player) => Spin(player.Sent.Last(IsSpin));

    private static (byte Result, int ItemId, int Gold) LastDraw(RewardPlayer player, GameOpcodes opcode)
    {
        var packet = Last(player, opcode, DrawSub);
        return (packet.ReadByte(), packet.ReadInt(), packet.ReadInt());
    }

    private static (byte Result, int Gold) GenieClaim(Packet packet)
    {
        packet.ResetOffset();
        packet.ReadByte();
        return (packet.ReadByte(), packet.ReadInt());
    }

    private static bool IsSub(Packet packet, GameOpcodes opcode, byte sub) =>
        packet.GetOpcode() == (byte)opcode && packet.GetData()[0] == sub;

    private static Packet Last(RewardPlayer player, GameOpcodes opcode, byte sub)
    {
        var packet = player.Sent.Last(sent => IsSub(sent, opcode, sub));
        packet.ResetOffset();
        packet.ReadByte();
        return packet;
    }
}
