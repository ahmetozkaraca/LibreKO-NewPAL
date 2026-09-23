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

public class ExchangeEscrowTests : EconomyTestBase
{
    private const byte ExchangeRequest = 1;
    private const byte ExchangeAgree = 2;
    private const byte Accept = 1;
    private const byte ExchangeAdd = 3;
    private const byte ExchangeDecide = 5;
    private const byte ExchangeDone = 7;
    private const byte ExchangeCancel = 8;
    private const byte TradeMove = 3;
    private const byte GoldPosition = byte.MaxValue;
    private const int Gold = 900_000_000;

    private const int Arrow = 391010000;
    private const int Scroll = 379021000;
    private const int Sword = 120150000;
    private const int Trinket = 389570000;
    private const short SwordWeight = 10;
    private const int RoomyWeight = 10_000;
    private const byte OfferLimit = 12;
    private const short WornDurability = 55;
    private const int Anvil = 389999000;
    private const short AnvilWeight = 2_000;

    [Fact]
    public async Task TheSwapAndCancelTrickNoLongerMintsScrolls()
    {
        using var provider = Provider();
        var (giver, taker) = Traders(provider, 6001, 6002);
        Give(giver, 0, Arrow, count: InventoryConstants.MaxStackCount);
        Give(giver, 1, Scroll);

        await Exchange(provider, giver, Offer(0, Arrow, InventoryConstants.MaxStackCount));
        await provider.GetRequiredService<IItemPacketCoordinator>().HandleTradeAsync(giver.Client, Swap(Arrow, 0, 1));
        await Exchange(provider, giver, Sub(ExchangeCancel));

        TotalHeld(giver, Scroll).Should().Be(1);
        TotalHeld(giver, Arrow).Should().Be(InventoryConstants.MaxStackCount);
        taker.Trade.IsTrading.Should().BeFalse();
    }

    [Fact]
    public async Task ACancelledSwordNeverStacksOntoAnotherSword()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6003, 6004);
        Give(giver, 0, Sword);
        Give(giver, 1, Sword);

        await Exchange(provider, giver, Offer(0, Sword, 1));
        await provider.GetRequiredService<IItemPacketCoordinator>().HandleTradeAsync(giver.Client, Swap(Sword, 0, 1));
        await Exchange(provider, giver, Sub(ExchangeCancel));

        TotalHeld(giver, Sword).Should().Be(2);
        giver.Inventory.Should().NotContain(slot => slot.ItemId == Sword && slot.Count != 1);
    }

    [Fact]
    public async Task AnOfferLeavesTheBagAndComesBackWhole()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6005, 6006);
        Give(giver, 0, Sword, flag: ItemFlag.NotBound, durability: WornDurability);

        await Exchange(provider, giver, Offer(0, Sword, 1));

        Bag(giver, 0).IsEmpty.Should().BeTrue("an offered item sits in escrow, not in the bag");

        await Exchange(provider, giver, Sub(ExchangeCancel));

        Bag(giver, 0).ItemId.Should().Be(Sword);
        Bag(giver, 0).Count.Should().Be(1);
        Bag(giver, 0).State.Should().Be(ItemFlag.NotBound);
        Bag(giver, 0).Durability.Should().Be(WornDurability);
    }

    [Fact]
    public async Task AnEscrowedItemIsNeverAddedOntoWhateverTookItsSlot()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6007, 6008);
        Give(giver, 0, Arrow, count: 40);

        await Exchange(provider, giver, Offer(0, Arrow, 40));
        Give(giver, 0, Scroll, count: 3);
        await Exchange(provider, giver, Sub(ExchangeCancel));

        Bag(giver, 0).ItemId.Should().Be(Scroll);
        Bag(giver, 0).Count.Should().Be(3);
        TotalHeld(giver, Arrow).Should().Be(40);
    }

    [Fact]
    public async Task APartOfAStackMergesBackIntoItsOwnStack()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6009, 6010);
        Give(giver, 0, Arrow, count: 100);

        await Exchange(provider, giver, Offer(0, Arrow, 30));
        Bag(giver, 0).Count.Should().Be(70);
        await Exchange(provider, giver, Sub(ExchangeCancel));

        Bag(giver, 0).Count.Should().Be(100);
    }

    [Fact]
    public async Task TheThirteenthOfferIsRefusedBeforeItLeavesTheBag()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6011, 6012);
        for (var position = 0; position <= OfferLimit; position++)
            Give(giver, position, Sword);

        for (byte position = 0; position <= OfferLimit; position++)
            await Exchange(provider, giver, Offer(position, Sword, 1));

        giver.Trade.ExchangeItemList.Should().HaveCount(OfferLimit);
        Bag(giver, OfferLimit).ItemId.Should().Be(Sword);
        Bag(giver, OfferLimit).Count.Should().Be(1);
    }

    [Fact]
    public async Task AnOfferToSomeoneTradingWithAnotherPlayerIsRefused()
    {
        using var provider = Provider();
        var giver = Player(provider, 6013, out _);
        var partner = Player(provider, 6014, out var partnerSent);
        var other = Player(provider, 6015, out _);
        giver.Trade.ExchangeUser = partner.CharacterId;
        giver.Trade.AskedForExchange = true;
        giver.Trade.ExchangeStarted = true;
        partner.Trade.ExchangeUser = other.CharacterId;
        other.Trade.ExchangeUser = partner.CharacterId;
        Give(giver, 0, Sword);

        await Exchange(provider, giver, Offer(0, Sword, 1));

        Bag(giver, 0).ItemId.Should().Be(Sword);
        giver.Trade.ExchangeItemList.Should().BeEmpty();
        partnerSent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_EXCHANGE);
    }

    [Fact]
    public async Task DecideOnlyCompletesBetweenTwoPlayersTradingWithEachOther()
    {
        using var provider = Provider();
        var giver = Player(provider, 6016, out _);
        var partner = Player(provider, 6017, out _);
        var other = Player(provider, 6018, out _);
        partner.Stats = new DerivedStats { MaxWeight = RoomyWeight };
        giver.Trade.ExchangeUser = partner.CharacterId;
        giver.Trade.ExchangeStarted = true;
        partner.Trade.ExchangeUser = other.CharacterId;
        partner.Trade.ExchangeOk = true;
        other.Trade.ExchangeUser = partner.CharacterId;
        giver.Trade.ExchangeItemList.Add(new ExchangeItem
        {
            ItemId = Sword, Count = 1, SrcPos = InventoryConstants.InventoryStart,
        });

        await Exchange(provider, giver, Sub(ExchangeDecide));

        TotalHeld(partner, Sword).Should().Be(0);
        TotalHeld(giver, Sword).Should().Be(1);
        giver.Trade.IsTrading.Should().BeFalse();
    }

    [Fact]
    public async Task ACompletedTradeHandsOverTheWholeSlot()
    {
        using var provider = Provider();
        var (giver, taker) = Traders(provider, 6019, 6020, out var takerSent);
        Give(giver, 0, Sword, flag: ItemFlag.NotBound, durability: WornDurability);
        Give(giver, 1, Arrow, count: 50);
        Give(taker, 0, Arrow, count: 100);

        await Exchange(provider, giver, Offer(0, Sword, 1));
        await Exchange(provider, giver, Offer(1, Arrow, 50));
        await Exchange(provider, taker, Sub(ExchangeDecide));
        await Exchange(provider, giver, Sub(ExchangeDecide));

        Bag(taker, 0).Count.Should().Be(150);
        var sword = taker.Inventory.Single(slot => slot.ItemId == Sword);
        sword.State.Should().Be(ItemFlag.NotBound);
        sword.Durability.Should().Be(WornDurability);
        TotalHeld(giver, Sword).Should().Be(0);
        TotalHeld(giver, Arrow).Should().Be(0);

        var done = takerSent.Last(packet =>
        {
            packet.ResetOffset();
            return packet.GetOpcode() == (byte)GameOpcodes.GS_EXCHANGE && packet.ReadByte() == ExchangeDone;
        });
        done.ResetOffset();
        done.ReadByte();
        done.ReadByte().Should().Be(1);
        done.ReadInt();
        var entries = done.ReadUShort();
        var counts = new Dictionary<int, ushort>();
        for (var entry = 0; entry < entries; entry++)
        {
            done.ReadByte();
            var itemId = done.ReadInt();
            counts[itemId] = done.ReadUShort();
            done.ReadShort();
            done.ReadByte();
            done.ReadInt();
        }

        counts[Arrow].Should().Be(150, "the client overwrites the slot with what the server reports");
    }

    [Fact]
    public async Task DyingToAMonsterGivesBothSidesTheirOffersBack()
    {
        using var provider = Provider();
        var (giver, taker) = Traders(provider, 6021, 6022, out var takerSent);
        taker.Money = 5_000;
        Give(giver, 0, Sword);

        await Exchange(provider, giver, Offer(0, Sword, 1));
        await Exchange(provider, taker, Offer(GoldPosition, Gold, 1_000));
        taker.Money.Should().Be(4_000);

        giver.Hp = 0;
        await provider.GetRequiredService<INpcAiDeathService>()
            .HandlePlayerKilledByNpcAsync(giver, new NpcInstance { NpcType = NpcData.TypeMonster, Level = 1 });

        TotalHeld(giver, Sword).Should().Be(1);
        taker.Money.Should().Be(5_000);
        taker.Trade.IsTrading.Should().BeFalse();
        giver.Trade.IsTrading.Should().BeFalse();
        takerSent.Should().Contain(packet => IsSub(packet, ExchangeCancel));
    }

    [Fact]
    public async Task ReturnedGoldNeverPassesTheCoinCap()
    {
        using var provider = Provider();
        var (giver, _) = Traders(provider, 6023, 6024);
        giver.Money = 5_000;

        await Exchange(provider, giver, Offer(GoldPosition, Gold, 1_000));
        giver.Money = CoinMax - 10;
        await Exchange(provider, giver, Sub(ExchangeCancel));

        giver.Money.Should().Be(CoinMax);
    }

    [Fact]
    public async Task AnUnansweredRequestLeavesTheTargetsBagUsable()
    {
        using var provider = Provider();
        var asker = Player(provider, 6030, out _);
        var target = Player(provider, 6031, out var targetSent);

        await Exchange(provider, asker, Request(target));

        ItemTransfer.IsInventoryLocked(target).Should().BeFalse();

        var agree = Sub(ExchangeAgree);
        agree.WriteByte(Accept);
        await Exchange(provider, target, agree);

        ItemTransfer.IsInventoryLocked(target).Should().BeTrue();
        targetSent.Should().Contain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_EXCHANGE
            && packet.GetData()[0] == ExchangeAgree && packet.GetData()[1] == ExchangePacketWriter.Succeeded);
    }

    [Fact]
    public async Task AnOfferBeforeTheRequestIsAcceptedCancelsItAndKeepsTheItem()
    {
        using var provider = Provider();
        var asker = Player(provider, 6032, out var askerSent);
        var target = Player(provider, 6033, out var targetSent);
        Give(asker, 0, Sword);
        await Exchange(provider, asker, Request(target));

        await Exchange(provider, asker, Offer(0, Sword, 1));

        Bag(asker, 0).ItemId.Should().Be(Sword);
        asker.Trade.IsTrading.Should().BeFalse();
        target.Trade.IsTrading.Should().BeFalse();
        askerSent.Should().Contain(packet => IsCancel(packet));
        targetSent.Should().Contain(packet => IsCancel(packet));
    }

    [Fact]
    public async Task AnswersAndOffersWithoutATradeAreToldItIsOver()
    {
        using var provider = Provider();
        var stray = Player(provider, 6034, out var sent);

        var agree = Sub(ExchangeAgree);
        agree.WriteByte(Accept);
        await Exchange(provider, stray, agree);
        await Exchange(provider, stray, Sub(ExchangeDecide));

        sent.Count(IsCancel).Should().Be(2);
    }

    private static bool IsCancel(Packet packet) =>
        packet.GetOpcode() == (byte)GameOpcodes.GS_EXCHANGE && packet.GetData()[0] == ExchangeCancel;

    private static Packet Request(UserSession target)
    {
        var packet = Sub(ExchangeRequest);
        packet.WriteInt(target.CharacterId);
        return packet;
    }

    [Fact]
    public async Task OnlyOneOfManySimultaneousRequestsPairsWithTheTarget()
    {
        using var provider = Provider();
        const int rounds = 40;
        const int requestersPerRound = 8;
        var nextId = 7000;

        for (var round = 0; round < rounds; round++)
        {
            var target = Player(provider, nextId++, out _);
            var requesters = Enumerable.Range(0, requestersPerRound)
                .Select(index => Player(provider, nextId + index, out _))
                .ToList();
            nextId += requestersPerRound;

            using var gate = new ManualResetEventSlim();
            var coordinator = provider.GetRequiredService<IExchangePacketCoordinator>();
            var requests = requesters.Select(requester => Task.Run(async () =>
            {
                gate.Wait();
                var packet = new Packet(GameOpcodes.GS_EXCHANGE);
                packet.WriteByte(ExchangeRequest);
                packet.WriteInt(target.CharacterId);
                await coordinator.HandleAsync(requester.Client, packet);
            })).ToArray();
            gate.Set();
            await Task.WhenAll(requests);

            var paired = requesters.Where(requester => requester.Trade.ExchangeUser == target.CharacterId).ToList();
            paired.Should().HaveCountLessThanOrEqualTo(1);
            if (paired.Count == 1)
                target.Trade.ExchangeUser.Should().Be(paired[0].CharacterId);
        }
    }

    [Fact]
    public async Task ATraderNearTheWeightCapCanSwapForAnEquallyHeavyItem()
    {
        using var provider = HeavyProvider();
        var gameData = provider.GetRequiredService<IGameDataService>();
        var (giver, taker) = Traders(provider, 6101, 6102);
        Give(giver, 0, Anvil);
        Give(taker, 0, Anvil);
        giver.RecalculateStatsWithBuffs(gameData);
        taker.RecalculateStatsWithBuffs(gameData);
        taker.Stats.MaxWeight.Should().BeLessThan(AnvilWeight * 2, "each trader can only carry one anvil");

        await Exchange(provider, giver, Offer(0, Anvil, 1));
        await Exchange(provider, taker, Offer(0, Anvil, 1));
        await Exchange(provider, giver, Sub(ExchangeDecide));
        await Exchange(provider, taker, Sub(ExchangeDecide));

        TotalHeld(giver, Anvil).Should().Be(1);
        TotalHeld(taker, Anvil).Should().Be(1);
        giver.Trade.IsTrading.Should().BeFalse();
        giver.Stats.ItemWeight.Should().Be(AnvilWeight);
    }

    [Fact]
    public async Task CancellingAnOfferPutsItsWeightBack()
    {
        using var provider = HeavyProvider();
        var gameData = provider.GetRequiredService<IGameDataService>();
        var (giver, _) = Traders(provider, 6103, 6104);
        Give(giver, 0, Anvil);
        giver.RecalculateStatsWithBuffs(gameData);

        await Exchange(provider, giver, Offer(0, Anvil, 1));
        giver.Stats.ItemWeight.Should().Be(0);

        await Exchange(provider, giver, Sub(ExchangeCancel));
        giver.Stats.ItemWeight.Should().Be(AnvilWeight);
    }

    private ServiceProvider HeavyProvider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetItem(Anvil).Returns(new ItemData { Num = Anvil, Countable = 0, Weight = AnvilWeight });
        gameData.GetCoefficient(Arg.Any<short>()).Returns(CreateBasicCoefficient(0));
        gameData.PremiumItemTable.Returns(new Dictionary<byte, PremiumItemData>());
    });

    private ServiceProvider Provider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetItem(Arrow).Returns(new ItemData { Num = Arrow, Countable = 1 });
        gameData.GetItem(Scroll).Returns(new ItemData { Num = Scroll, Countable = 1 });
        gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Countable = 0, Weight = SwordWeight });
        gameData.GetItem(Trinket).Returns(new ItemData { Num = Trinket, Countable = 1 });
        gameData.PremiumItemTable.Returns(new Dictionary<byte, PremiumItemData>());
    });

    private (UserSession Giver, UserSession Taker) Traders(ServiceProvider provider, int giverId, int takerId) =>
        Traders(provider, giverId, takerId, out _);

    private (UserSession Giver, UserSession Taker) Traders(
        ServiceProvider provider, int giverId, int takerId, out List<Packet> takerSent)
    {
        var giver = Player(provider, giverId, out _);
        var taker = Player(provider, takerId, out takerSent);
        giver.Stats = new DerivedStats { MaxWeight = RoomyWeight };
        taker.Stats = new DerivedStats { MaxWeight = RoomyWeight };
        giver.Trade.ExchangeUser = taker.CharacterId;
        giver.Trade.AskedForExchange = true;
        taker.Trade.ExchangeUser = giver.CharacterId;
        giver.Trade.ExchangeStarted = taker.Trade.ExchangeStarted = true;
        return (giver, taker);
    }

    private static Packet Offer(byte position, int itemId, int count)
    {
        var packet = Sub(ExchangeAdd);
        packet.WriteByte(position);
        packet.WriteInt(itemId);
        packet.WriteInt(count);
        return packet;
    }

    private static Packet Sub(byte sub)
    {
        var packet = new Packet(GameOpcodes.GS_EXCHANGE);
        packet.WriteByte(sub);
        return packet;
    }

    private static Packet Swap(int itemId, byte from, byte to)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_TRADE);
        packet.WriteByte(TradeMove);
        packet.WriteInt(itemId);
        packet.WriteByte(from);
        packet.WriteByte(to);
        return packet;
    }

    private static bool IsSub(Packet packet, byte sub)
    {
        if (packet.GetOpcode() != (byte)GameOpcodes.GS_EXCHANGE)
            return false;
        packet.ResetOffset();
        return packet.ReadByte() == sub;
    }

    private static Task Exchange(ServiceProvider provider, UserSession session, Packet packet) =>
        provider.GetRequiredService<IExchangePacketCoordinator>().HandleAsync(session.Client, packet);
}
