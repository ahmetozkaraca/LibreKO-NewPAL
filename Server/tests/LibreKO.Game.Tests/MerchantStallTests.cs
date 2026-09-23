using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class MerchantStallTests : EconomyTestBase
{
    private const int Sword = 120150000;
    private const int Potion = 389014000;
    private const int QuestToken = 389217000;
    private const byte RaceUntradeable = 20;
    private const int Price = 1_000;
    private const short WornDurability = 40;
    private const ushort WantedCount = 5;
    private const ushort PotionStack = 100;
    private const int HalfTheCoinCap = CoinMax / 2;

    [Fact]
    public async Task StagingAnItemNeedsTheStallSetupOpen()
    {
        using var provider = Provider();
        var seller = Player(provider, 8001, out _);
        Give(seller, 0, Sword);

        await Merchant(provider, seller, Add(Sword, 1, Price, 0, 0));

        seller.Trade.MerchantItems[0].Should().Match<MerchantItem>(item => item == null || item.IsEmpty);
    }

    [Fact]
    public async Task AStallOnlySellsWhatTheSellerStillHolds()
    {
        using var provider = Provider();
        var seller = SellingStall(provider, 8002, Sword, stock: 1);
        Bag(seller, 0).Clear();
        Give(seller, 0, Potion);
        var buyer = Player(provider, 8003, out _, money: 100_000);

        await Browse(provider, buyer, seller);
        await Merchant(provider, buyer, Purchase(Sword, 1));

        TotalHeld(buyer, Sword).Should().Be(0, "the stall must not sell a copy of an item the seller no longer has");
        buyer.Money.Should().Be(100_000);
        seller.Money.Should().Be(0);
    }

    [Fact]
    public async Task ASaleHandsOverTheSellersActualItem()
    {
        using var provider = Provider();
        var seller = SellingStall(provider, 8004, Sword, stock: 1, flag: ItemFlag.NotBound, durability: WornDurability);
        var buyer = Player(provider, 8005, out _, money: 100_000);

        await Browse(provider, buyer, seller);
        await Merchant(provider, buyer, Purchase(Sword, 1));

        var bought = buyer.Inventory.Single(slot => slot.ItemId == Sword);
        bought.State.Should().Be(ItemFlag.NotBound);
        bought.Durability.Should().Be(WornDurability);
        Bag(seller, 0).IsEmpty.Should().BeTrue();
        buyer.Money.Should().Be(100_000 - Price);
        seller.Money.Should().Be(Price);
    }

    [Fact]
    public async Task TwoBuyersNeverBothGetTheLastPiece()
    {
        using var provider = Provider();
        const int rounds = 60;
        var nextId = 8100;

        for (var round = 0; round < rounds; round++)
        {
            var seller = SellingStall(provider, nextId++, Potion, stock: 1);
            var buyers = new[] { Player(provider, nextId++, out _, money: 100_000), Player(provider, nextId++, out _, money: 100_000) };
            foreach (var buyer in buyers)
                buyer.Trade.MerchantTargetUserId = seller.CharacterId;

            using var gate = new ManualResetEventSlim();
            var purchases = buyers.Select(buyer => Task.Run(async () =>
            {
                gate.Wait();
                await Merchant(provider, buyer, Purchase(Potion, 1));
            })).ToArray();
            gate.Set();
            await Task.WhenAll(purchases);

            buyers.Sum(buyer => TotalHeld(buyer, Potion)).Should().Be(1);
            TotalHeld(seller, Potion).Should().Be(0);
            seller.Money.Should().Be(Price);
            seller.Trade.MerchantItems[0].Should().Match<MerchantItem>(item => item == null || item.IsEmpty || item.Count == 0);
        }
    }

    [Fact]
    public async Task BrowsingAndBuyingNeedTheBuyerAtTheStall()
    {
        using var provider = Provider();
        var seller = SellingStall(provider, 8006, Sword, stock: 1);
        var buyer = Player(provider, 8007, out var buyerSent, money: 100_000);
        buyer.X = FarAway;

        await Browse(provider, buyer, seller);

        buyerSent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MERCHANT
            && FirstByte(packet) == (byte)MerchantSubOpcode.ItemList);

        buyer.Trade.MerchantTargetUserId = seller.CharacterId;
        await Merchant(provider, buyer, Purchase(Sword, 1));

        TotalHeld(buyer, Sword).Should().Be(0);
        buyer.Money.Should().Be(100_000);
    }

    [Fact]
    public async Task AnItemThatMayNotChangeHandsCannotBeStalled()
    {
        using var provider = Provider();
        var seller = Player(provider, 8008, out _);
        Give(seller, 0, QuestToken);

        await Merchant(provider, seller, Sub(MerchantSubOpcode.Open));
        await Merchant(provider, seller, Add(QuestToken, 1, Price, 0, 0));

        seller.Trade.MerchantItems[0].Should().Match<MerchantItem>(item => item == null || item.IsEmpty);
    }

    [Fact]
    public async Task AnItemThatMayNotChangeHandsCannotBeSoldToABuyingStall()
    {
        using var provider = Provider();
        var owner = BuyingStall(provider, 8009, QuestToken);
        var seller = Player(provider, 8010, out _);
        seller.Trade.MerchantTargetUserId = owner.CharacterId;
        Give(seller, 0, QuestToken, count: 1);

        await Merchant(provider, seller, SellToStall(0, 0, 1));

        TotalHeld(seller, QuestToken).Should().Be(1);
        TotalHeld(owner, QuestToken).Should().Be(0);
        seller.Money.Should().Be(0);
    }

    [Fact]
    public async Task ABuyingStallReceivesTheSellersActualItem()
    {
        using var provider = Provider();
        var owner = BuyingStall(provider, 8011, Sword);
        var seller = Player(provider, 8012, out _);
        seller.Trade.MerchantTargetUserId = owner.CharacterId;
        Give(seller, 0, Sword, flag: ItemFlag.NotBound, durability: WornDurability);

        await Merchant(provider, seller, SellToStall(0, 0, 1));

        var received = owner.Inventory.Single(slot => slot.ItemId == Sword);
        received.State.Should().Be(ItemFlag.NotBound);
        received.Durability.Should().Be(WornDurability);
        seller.Money.Should().Be(Price);
    }

    [Fact]
    public async Task TwoSellersNeverOverfillABuyingStall()
    {
        using var provider = Provider();
        const int rounds = 60;
        var nextId = 8400;

        for (var round = 0; round < rounds; round++)
        {
            var owner = BuyingStall(provider, nextId++, Potion, wanted: 1);
            var sellers = new[] { Player(provider, nextId++, out _), Player(provider, nextId++, out _) };
            foreach (var seller in sellers)
            {
                seller.Trade.MerchantTargetUserId = owner.CharacterId;
                Give(seller, 0, Potion);
            }

            using var gate = new ManualResetEventSlim();
            var sales = sellers.Select(seller => Task.Run(async () =>
            {
                gate.Wait();
                await Merchant(provider, seller, SellToStall(0, 0, 1));
            })).ToArray();
            gate.Set();
            await Task.WhenAll(sales);

            TotalHeld(owner, Potion).Should().Be(1);
            sellers.Sum(seller => seller.Money).Should().Be(Price);
            owner.Trade.BuyMerchantItems[0].Should().Match<MerchantItem>(item => item.IsEmpty || item.Count == 0);
        }
    }

    [Theory]
    [InlineData(Price, true)]
    [InlineData(HalfTheCoinCap, false)]
    public async Task AListingWhoseWholeStackPassesTheCoinCapIsRefused(int unitPrice, bool listed)
    {
        using var provider = Provider();
        var seller = Player(provider, 8201, out _);
        Give(seller, 0, Potion, PotionStack);
        seller.Trade.IsSellingMerchantPreparing = true;

        await Merchant(provider, seller, Add(Potion, PotionStack, unitPrice, 0, 0));

        (seller.Trade.MerchantItems[0] is { IsEmpty: false }).Should().Be(listed);
    }

    private ServiceProvider Provider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Countable = 0 });
        gameData.GetItem(Potion).Returns(new ItemData { Num = Potion, Countable = 1 });
        gameData.GetItem(QuestToken).Returns(new ItemData { Num = QuestToken, Countable = 1, Race = RaceUntradeable });
    });

    private static UserSession SellingStall(
        ServiceProvider provider, int characterId, int itemId, ushort stock,
        ItemFlag flag = ItemFlag.Unsealed, short durability = 0)
    {
        var seller = Player(provider, characterId, out _);
        Give(seller, 0, itemId, stock, flag, durability: durability);
        seller.Trade.MerchantState = MerchantMode.Selling;
        seller.Trade.MerchantItems[0] = new MerchantItem
        {
            ItemId = itemId,
            Count = stock,
            Price = Price,
            Durability = durability,
            Flag = (byte)flag,
            OriginalSlot = InventoryConstants.InventoryStart,
        };
        return seller;
    }

    private static UserSession BuyingStall(ServiceProvider provider, int characterId, int itemId, ushort wanted = WantedCount)
    {
        var owner = Player(provider, characterId, out _, money: 1_000_000);
        owner.Trade.MerchantState = MerchantMode.Buying;
        owner.Trade.BuyMerchantItems[0] = new MerchantItem { ItemId = itemId, Count = wanted, Price = Price };
        return owner;
    }

    private static Packet Sub(MerchantSubOpcode sub)
    {
        var packet = new Packet(GameOpcodes.GS_MERCHANT);
        packet.WriteByte((byte)sub);
        return packet;
    }

    private static Packet Add(int itemId, ushort count, int price, byte source, byte stallSlot)
    {
        var packet = Sub(MerchantSubOpcode.ItemAdd);
        packet.WriteInt(itemId);
        packet.WriteUShort(count);
        packet.WriteInt(price);
        packet.WriteByte(source);
        packet.WriteByte(stallSlot);
        return packet;
    }

    private static Packet Purchase(int itemId, ushort count)
    {
        var packet = Sub(MerchantSubOpcode.ItemBuy);
        packet.WriteInt(itemId);
        packet.WriteUShort(count);
        packet.WriteByte(0);
        packet.WriteByte(0);
        return packet;
    }

    private static Packet SellToStall(byte sellerSlot, byte wantedSlot, ushort count)
    {
        var packet = Sub(MerchantSubOpcode.BuyBuy);
        packet.WriteByte(sellerSlot);
        packet.WriteByte(wantedSlot);
        packet.WriteUShort(count);
        return packet;
    }

    private static Task Browse(ServiceProvider provider, UserSession buyer, UserSession seller)
    {
        var packet = Sub(MerchantSubOpcode.ItemList);
        packet.WriteInt(seller.CharacterId);
        return Merchant(provider, buyer, packet);
    }

    private static byte FirstByte(Packet packet)
    {
        packet.ResetOffset();
        return packet.ReadByte();
    }

    private static Task Merchant(ServiceProvider provider, UserSession session, Packet packet) =>
        provider.GetRequiredService<IMerchantPacketCoordinator>().HandleAsync(session.Client, packet);
}
