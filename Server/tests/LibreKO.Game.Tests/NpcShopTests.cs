using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence.Seed.Entities;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class NpcShopTests : EconomyTestBase
{
    private const byte TradeBuy = 1;
    private const byte TradeSell = 2;
    private const byte TradeMove = 3;
    private const byte TradeRefused = 0;
    private const byte TradeDone = 1;
    private const byte RepairInBag = 2;

    private const int Sundries = 255000;
    private const int LoyaltyMerchant = 249000;
    private const int Arrow = 391010000;
    private const int Sword = 120150000;
    private const int Gem = 379107000;
    private const int FreeTrinket = 389191000;
    private const int PricedTrinket = 389570000;
    private const int KnightsMedal = 389217000;
    private const int SilverBar = 379067000;
    private const int Potion = 389014000;
    private const int UnlistedItem = 700013000;

    private const int ArrowPrice = 10;
    private const int SwordPrice = 1_000;
    private const int GemPrice = 1_500_000_000;
    private const int TrinketShopPrice = 700;
    private const int MedalGoldPrice = 17_500;
    private const int MedalLoyaltyPrice = 10_000;
    private const int SilverBarPrice = 10_000_000;
    private const int PotionPrice = 600;
    private const byte PremiumWithSellBonus = 10;
    private const int PremiumSellBonus = 50;
    private const byte SaleTypeFull = 1;
    private const short SwordDuration = 100;
    private const short WornDurability = 10;
    private const int SellPriceDivisor = 6;
    private const int Percent = 100;

    private const byte ArrowLine = 0;
    private const byte ArrowIndex = 0;
    private const byte SwordIndex = 1;
    private const byte GemIndex = 2;
    private const byte FreeIndex = 3;
    private const byte PricedIndex = 4;

    [Fact]
    public async Task ABuyWhoseLaterLineIsRefusedGrantsAndChargesNothing()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5001, out _, money: 10_000);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);

        await Trade(provider, buyer, Buy(npc, Sundries,
            (Arrow, 0, 5, ArrowLine, ArrowIndex),
            (Arrow, 0, 5, ArrowLine, ArrowIndex)));

        Bag(buyer, 0).IsEmpty.Should().BeTrue("a refused basket must not leave its first line behind for free");
        buyer.Money.Should().Be(10_000);
    }

    [Fact]
    public async Task ABasketWhoseTotalPassesTheIntegerRangeIsRefused()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5002, out var sent, money: 1_600_000_000);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);

        await Trade(provider, buyer, Buy(npc, Sundries,
            (Gem, 0, 1, ArrowLine, GemIndex),
            (Gem, 1, 1, ArrowLine, GemIndex)));

        buyer.Money.Should().Be(1_600_000_000);
        Bag(buyer, 0).IsEmpty.Should().BeTrue();
        Bag(buyer, 1).IsEmpty.Should().BeTrue();
        Reply(sent).Result.Should().Be(TradeRefused);
    }

    [Theory]
    [InlineData(NpcData.TypeGuard, Sundries)]
    [InlineData(NpcData.TypeMonster, Sundries)]
    [InlineData(NpcData.TypeTradeMerchant, 0)]
    public async Task OnlyAMerchantOfThatSellingGroupSells(byte npcType, int npcGroup)
    {
        using var provider = Shop();
        var buyer = Player(provider, 5003, out _, money: 10_000);
        var npc = Npc(provider, npcType, npcGroup);

        await Trade(provider, buyer, Buy(npc, npcGroup, (Arrow, 0, 5, ArrowLine, ArrowIndex)));

        Bag(buyer, 0).IsEmpty.Should().BeTrue();
        buyer.Money.Should().Be(10_000);
    }

    [Fact]
    public async Task AnItemTheShopDoesNotListCannotBeBought()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5004, out _, money: 10_000);
        var npc = Npc(provider, NpcData.TypeRepairMerchant, Sundries);

        await Trade(provider, buyer, Buy(npc, Sundries, (UnlistedItem, 0, 1, ArrowLine, ArrowIndex)));

        Bag(buyer, 0).IsEmpty.Should().BeTrue();
        buyer.Money.Should().Be(10_000);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(buyer.Client.Id).Should().BePositive();
    }

    [Fact]
    public async Task ANonStackableItemIsSoldOnePerSlot()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5005, out _, money: 10_000);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);

        await Trade(provider, buyer, Buy(npc, Sundries, (Sword, 0, 3, ArrowLine, SwordIndex)));

        Bag(buyer, 0).IsEmpty.Should().BeTrue();
        buyer.Money.Should().Be(10_000);

        await Trade(provider, buyer, Buy(npc, Sundries, (Sword, 0, 1, ArrowLine, SwordIndex)));

        Bag(buyer, 0).ItemId.Should().Be(Sword);
        Bag(buyer, 0).Count.Should().Be(1);
        Bag(buyer, 0).Durability.Should().Be(SwordDuration);
        buyer.Money.Should().Be(10_000 - SwordPrice);
    }

    [Fact]
    public async Task AnUnpricedItemOnlySellsAtThePriceTheShopGivesIt()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5006, out _, money: 10_000);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);

        await Trade(provider, buyer, Buy(npc, Sundries, (FreeTrinket, 0, 1, ArrowLine, FreeIndex)));

        Bag(buyer, 0).IsEmpty.Should().BeTrue("an item with no price must not be handed out for free");

        await Trade(provider, buyer, Buy(npc, Sundries, (PricedTrinket, 0, 1, ArrowLine, PricedIndex)));

        Bag(buyer, 0).ItemId.Should().Be(PricedTrinket);
        buyer.Money.Should().Be(10_000 - TrinketShopPrice);
    }

    [Fact]
    public async Task TheLoyaltyMerchantChargesNationalPointsNotGold()
    {
        using var provider = Shop();
        var buyer = Player(provider, 5007, out var sent, money: 50_000);
        buyer.Loyalty = 25_000;
        var npc = Npc(provider, NpcData.TypeTradeMerchant, LoyaltyMerchant);

        await Trade(provider, buyer, Buy(npc, LoyaltyMerchant, (KnightsMedal, 0, 1, ArrowLine, ArrowIndex)));

        Bag(buyer, 0).ItemId.Should().Be(KnightsMedal);
        buyer.Money.Should().Be(50_000);
        buyer.Loyalty.Should().Be(25_000 - MedalLoyaltyPrice);
        var reply = Reply(sent);
        reply.Result.Should().Be(TradeDone);
        reply.Balance.Should().Be(25_000 - MedalLoyaltyPrice);
    }

    [Fact]
    public async Task ThePremiumSellBonusSkipsFullPriceItems()
    {
        using var provider = Shop();
        var seller = Player(provider, 5008, out _);
        seller.PremiumService = PremiumWithSellBonus;
        seller.PremiumExpiry = DateTime.UtcNow.AddDays(10);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);
        Give(seller, 0, SilverBar);
        Give(seller, 1, Potion);

        await Trade(provider, seller, Sell(npc, Sundries, (SilverBar, 0, 1)));

        seller.Money.Should().Be(SilverBarPrice, "buying a bar back must never pay more than it cost");

        await Trade(provider, seller, Sell(npc, Sundries, (Potion, 1, 1)));

        seller.Money.Should().Be(SilverBarPrice + PotionPrice / SellPriceDivisor * (Percent + PremiumSellBonus) / Percent);
    }

    [Fact]
    public async Task ASaleThatWouldPassTheCoinCapIsRefused()
    {
        using var provider = Shop();
        var seller = Player(provider, 5009, out _, money: CoinMax - 10);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);
        Give(seller, 0, SilverBar);

        await Trade(provider, seller, Sell(npc, Sundries, (SilverBar, 0, 1)));

        seller.Money.Should().Be(CoinMax - 10);
        Bag(seller, 0).ItemId.Should().Be(SilverBar);
    }

    [Fact]
    public async Task SellingTheSameSlotTwiceInOneBasketIsRefused()
    {
        using var provider = Shop();
        var seller = Player(provider, 5010, out _);
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);
        Give(seller, 0, Potion, count: 4);

        await Trade(provider, seller, Sell(npc, Sundries, (Potion, 0, 4), (Potion, 0, 4)));

        Bag(seller, 0).Count.Should().Be(4);
        seller.Money.Should().Be(0);
    }

    [Fact]
    public async Task TheBagSwapRequestTheClientNeverSendsIsRefused()
    {
        using var provider = Shop();
        var player = Player(provider, 5011, out _);
        Give(player, 0, Arrow, count: 50);
        Give(player, 1, Sword);

        var packet = new Packet(GameOpcodes.GS_ITEM_TRADE);
        packet.WriteByte(TradeMove);
        packet.WriteInt(Arrow);
        packet.WriteByte(0);
        packet.WriteByte(1);
        await Trade(provider, player, packet);

        Bag(player, 0).ItemId.Should().Be(Arrow);
        Bag(player, 1).ItemId.Should().Be(Sword);
    }

    [Fact]
    public async Task ShopTradesAreRefusedWhileAStallIsBeingSetUp()
    {
        using var provider = Shop();
        var seller = Player(provider, 5012, out _);
        seller.Trade.IsSellingMerchantPreparing = true;
        var npc = Npc(provider, NpcData.TypeTradeMerchant, Sundries);
        Give(seller, 0, Potion, count: 4);

        await Trade(provider, seller, Sell(npc, Sundries, (Potion, 0, 4)));

        Bag(seller, 0).Count.Should().Be(4);
        seller.Money.Should().Be(0);
    }

    [Theory]
    [InlineData(NpcData.TypeMonster, StandX, false)]
    [InlineData(NpcData.TypeTradeMerchant, StandX, false)]
    [InlineData(NpcData.TypeRepairMerchant, FarAway, false)]
    [InlineData(NpcData.TypeRepairMerchant, StandX, true)]
    public async Task RepairsNeedABlacksmithWithinReach(byte npcType, float npcX, bool repaired)
    {
        using var provider = Shop();
        var player = Player(provider, 5013, out _, money: 100_000);
        var npc = Npc(provider, npcType, Sundries, x: npcX);
        Give(player, 0, Sword, durability: WornDurability);

        var packet = new Packet(GameOpcodes.GS_ITEM_REPAIR);
        packet.WriteByte(RepairInBag);
        packet.WriteByte(0);
        packet.WriteInt(npc.UniqueId);
        packet.WriteInt(Sword);
        await provider.GetRequiredService<IItemPacketCoordinator>().HandleRepairAsync(player.Client, packet);

        Bag(player, 0).Durability.Should().Be(repaired ? SwordDuration : WornDurability);
        (player.Money < 100_000).Should().Be(repaired);
    }

    [Fact]
    public void TheShopTableMirrorsTheSellingGroupsTheClientShows()
    {
        var rows = new SellingGroupItemSeed().GetSeedData().ToList();

        rows.Should().NotBeEmpty();
        rows.Select(row => (row.SellingGroup, row.Line, row.Index)).Should().OnlyHaveUniqueItems();
        rows.Should().Contain(row => row.SellingGroup == Sundries && row.Line == 0 && row.Index == 0
            && row.ItemId == Arrow);
        rows.Should().Contain(row => row.SellingGroup == LoyaltyMerchant && row.ItemId == KnightsMedal);
    }

    private ServiceProvider Shop() => CreateProvider(_ => { }, gameData =>
    {
        Item(gameData, new ItemData { Num = Arrow, Countable = 1, BuyPrice = ArrowPrice });
        Item(gameData, new ItemData { Num = Sword, Countable = 0, BuyPrice = SwordPrice, Duration = SwordDuration });
        Item(gameData, new ItemData { Num = Gem, Countable = 0, BuyPrice = GemPrice });
        Item(gameData, new ItemData { Num = FreeTrinket, Countable = 1, BuyPrice = 0 });
        Item(gameData, new ItemData { Num = PricedTrinket, Countable = 1, BuyPrice = 0 });
        Item(gameData, new ItemData
        {
            Num = KnightsMedal, Countable = 1, BuyPrice = MedalGoldPrice, NpBuyPrice = MedalLoyaltyPrice,
        });
        Item(gameData, new ItemData { Num = SilverBar, Countable = 0, BuyPrice = SilverBarPrice, SellPrice = SaleTypeFull });
        Item(gameData, new ItemData { Num = Potion, Countable = 1, BuyPrice = PotionPrice });
        Item(gameData, new ItemData { Num = UnlistedItem, Countable = 0, BuyPrice = 1 });

        Listed(gameData, Sundries, ArrowIndex, Arrow);
        Listed(gameData, Sundries, SwordIndex, Sword);
        Listed(gameData, Sundries, GemIndex, Gem);
        Listed(gameData, Sundries, FreeIndex, FreeTrinket);
        Listed(gameData, Sundries, PricedIndex, PricedTrinket, TrinketShopPrice);
        Listed(gameData, LoyaltyMerchant, ArrowIndex, KnightsMedal);

        gameData.GetPremiumProperty(PremiumWithSellBonus, PremiumPropertyType.ItemSell).Returns(PremiumSellBonus);
    });

    private static void Item(IGameDataService gameData, ItemData item) => gameData.GetItem(item.Num).Returns(item);

    private static void Listed(IGameDataService gameData, int group, byte index, int itemId, int price = 0) =>
        gameData.GetSellingGroupItem(group, ArrowLine, index).Returns(new SellingGroupItemData
        {
            SellingGroup = group, Line = ArrowLine, Index = index, ItemId = itemId, Price = price,
        });

    private static Packet Buy(NpcInstance npc, int group, params (int ItemId, byte Position, ushort Count, byte Line, byte Index)[] lines)
    {
        var packet = Header(TradeBuy, npc, group, lines.Length);
        foreach (var (itemId, position, count, line, index) in lines)
        {
            packet.WriteInt(itemId);
            packet.WriteByte(position);
            packet.WriteUShort(count);
            packet.WriteByte(line);
            packet.WriteByte(index);
        }

        return packet;
    }

    private static Packet Sell(NpcInstance npc, int group, params (int ItemId, byte Position, ushort Count)[] lines)
    {
        var packet = Header(TradeSell, npc, group, lines.Length);
        foreach (var (itemId, position, count) in lines)
        {
            packet.WriteInt(itemId);
            packet.WriteByte(position);
            packet.WriteUShort(count);
        }

        return packet;
    }

    private static Packet Header(byte type, NpcInstance npc, int group, int lineCount)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_TRADE);
        packet.WriteByte(type);
        packet.WriteInt(group);
        packet.WriteInt(npc.UniqueId);
        packet.WriteByte((byte)lineCount);
        return packet;
    }

    private static Task Trade(ServiceProvider provider, UserSession session, Packet packet) =>
        provider.GetRequiredService<IItemPacketCoordinator>().HandleTradeAsync(session.Client, packet);

    private static (byte Result, int Balance) Reply(List<Packet> sent)
    {
        var reply = Last(sent, GameOpcodes.GS_ITEM_TRADE);
        reply.Should().NotBeNull();
        var result = reply!.ReadByte();
        return (result, result == TradeDone ? reply.ReadInt() : 0);
    }
}
