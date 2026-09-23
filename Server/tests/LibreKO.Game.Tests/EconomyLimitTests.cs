using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class EconomyLimitTests : EconomyTestBase
{
    private const int Gold = 900_000_000;
    private const int Potion = 389014000;
    private const int Earring = 310110005;
    private const int UpgradedEarring = 310110006;
    private const int AccessoryScroll = 379159000;
    private const int Sword = 156210008;
    private const int UpgradedSword = 156210009;
    private const int HighClassScroll = 379021000;
    private const short DropGroup = 750;
    private const short AnvilId = 500;
    private const byte AnvilObjectType = 8;
    private const byte UpgradeTypeNormal = 1;
    private const byte UpgradeNoMatch = 4;
    private const byte UpgradeSucceeded = 1;
    private const int UpgradeSlotCount = 10;
    private const int GuaranteedRate = 10000;
    private const byte AccessoryKind = 91;
    private const byte WeaponKind = 52;
    private const byte StandardItemType = 5;
    private const short HighItemClass = 3;
    private const short EarringGrade = 5;
    private const short SwordGrade = 8;
    private const int FillerMaterials = 8;
    private const int SellersPerGlobalCap = 60;
    private const byte AuctionRegister = 2;
    private const byte AuctionSucceeded = 1;
    private const int ParallelCallers = 64;
    private const byte InnSetHome = 2;
    private const byte CombineRecipe = 2;
    private const int FirstRecipe = 1;
    private const int StartingBid = 100;
    private const int Buyout = 200;

    [Fact]
    public async Task LootedGoldNeverPassesTheCoinCap()
    {
        using var provider = CreateProvider(_ => { });
        var player = Player(provider, 9301, out _, money: CoinMax - 100);
        var bundle = provider.GetRequiredService<SessionManager>().Regions.CreateBundle(StandX, StandZ, 0);
        bundle.ZoneId = Zone;
        bundle.OwnerCharId = player.CharacterId;
        bundle.Items.Add(new LootItem { ItemId = Gold, Count = 1_000 });

        var packet = new Packet(GameOpcodes.GS_ITEM_GET);
        packet.WriteInt(bundle.BundleId);
        packet.WriteInt(Gold);
        packet.WriteUShort(0);
        await provider.GetRequiredService<ILootPacketCoordinator>().HandleItemGetAsync(player.Client, packet);

        player.Money.Should().BeLessThanOrEqualTo(CoinMax);
    }

    [Fact]
    public async Task KillRewardGoldNeverPassesTheCoinCap()
    {
        using var provider = CreateProvider(_ => { });
        var killer = Player(provider, 9302, out _, money: CoinMax - 10);
        var npc = new NpcInstance
        {
            UniqueId = 10_500, NpcId = 750, ZoneId = Zone, X = StandX, Z = StandZ, IsMonster = true, GoldDrop = 1_000,
        };
        npc.DamageMap[killer.CharacterId] = 10;
        npc.TopDamagerCharId = killer.CharacterId;

        await provider.GetRequiredService<ICombatRewardService>().AwardNpcKillAsync(npc, killer);

        killer.Money.Should().Be(CoinMax);
    }

    [Fact]
    public async Task MonsterLootRemembersTheZoneItFellIn()
    {
        using var provider = CreateProvider(_ => { }, gameData =>
        {
            gameData.GetNpcItem(DropGroup, true).Returns(new NpcItemData
            {
                Index = DropGroup, IsMonster = true, Item1 = Potion, Percent1 = GuaranteedRate,
            });
            gameData.GetItem(Potion).Returns(new ItemData { Num = Potion, Countable = 1 });
        });
        var killer = Player(provider, 9303, out var sent);
        var npc = new NpcInstance
        {
            UniqueId = 10_501, NpcId = 750, ZoneId = Zone, X = StandX, Z = StandZ, IsMonster = true,
            DropItemGroup = DropGroup,
        };
        npc.DamageMap[killer.CharacterId] = 10;
        npc.TopDamagerCharId = killer.CharacterId;

        await provider.GetRequiredService<ICombatRewardService>().AwardNpcKillAsync(npc, killer);

        var drop = Last(sent, GameOpcodes.GS_ITEM_DROP);
        drop.Should().NotBeNull();
        drop!.ReadInt();
        provider.GetRequiredService<SessionManager>().Regions.GetBundle(drop.ReadInt())!.ZoneId.Should().Be(Zone);
    }

    [Fact]
    public async Task AnAccessoryCannotCountOneCopyTwice()
    {
        using var provider = UpgradeProvider();
        var (player, sent) = Smith(provider, 9304, Earring, Earring, AccessoryScroll);

        await Upgrade(provider, player, ItemUpgradeSubOpcode.UpgradeAccessories,
            (Earring, 0), (Earring, 0), (Earring, 1), (AccessoryScroll, 2));

        UpgradeResult(sent).Should().Be(UpgradeNoMatch);
        TotalHeld(player, Earring).Should().Be(2);
        TotalHeld(player, UpgradedEarring).Should().Be(0);
        TotalHeld(player, AccessoryScroll).Should().Be(1);
    }

    [Fact]
    public async Task AMaterialSlotNamedTwiceIsRefused()
    {
        using var provider = UpgradeProvider();
        var (player, sent) = Smith(provider, 9305, Earring, Earring, AccessoryScroll);

        await Upgrade(provider, player, ItemUpgradeSubOpcode.UpgradeAccessories,
            (Earring, 0), (Earring, 1), (Earring, 1), (AccessoryScroll, 2));

        UpgradeResult(sent).Should().Be(UpgradeNoMatch);
        TotalHeld(player, Earring).Should().Be(2);
        TotalHeld(player, AccessoryScroll).Should().Be(1);
    }

    [Fact]
    public async Task EveryListedMaterialIsSpent()
    {
        using var provider = UpgradeProvider();
        var inventory = new List<int> { Sword };
        inventory.AddRange(Enumerable.Repeat(Potion, FillerMaterials));
        inventory.Add(HighClassScroll);
        var (player, sent) = Smith(provider, 9306, inventory.ToArray());

        var slots = inventory.Select((itemId, position) => (itemId, (byte)position)).ToArray();
        await Upgrade(provider, player, ItemUpgradeSubOpcode.Upgrade, slots);

        UpgradeResult(sent).Should().Be(UpgradeSucceeded);
        TotalHeld(player, UpgradedSword).Should().Be(1);
        TotalHeld(player, HighClassScroll).Should().Be(0, "a scroll listed last must be spent like the others");
    }

    [Fact]
    public async Task ASellerCannotFloodTheAuctionHouse()
    {
        using var provider = CreateProvider(_ => { });
        var seller = Player(provider, 9307, out var sent);
        var results = new List<byte>();

        for (var lot = 0; lot <= AuctionPacketCoordinator.MaxLotsPerSeller; lot++)
        {
            sent.Clear();
            await Register(provider, seller);
            results.Add(RegisterResult(sent));
        }

        results.Take(AuctionPacketCoordinator.MaxLotsPerSeller).Should().AllSatisfy(result => result.Should().Be(AuctionSucceeded));
        results.Last().Should().NotBe(AuctionSucceeded);
    }

    [Fact]
    public async Task TheAuctionHouseHasAGlobalCeiling()
    {
        using var provider = CreateProvider(_ => { });
        var accepted = 0;
        for (var sellerIndex = 0; sellerIndex < SellersPerGlobalCap; sellerIndex++)
        {
            var seller = Player(provider, 9400 + sellerIndex, out var sent);
            for (var lot = 0; lot < AuctionPacketCoordinator.MaxLotsPerSeller; lot++)
            {
                sent.Clear();
                await Register(provider, seller);
                if (RegisterResult(sent) == AuctionSucceeded)
                    accepted++;
            }
        }

        accepted.Should().Be(AuctionPacketCoordinator.MaxLots);
    }

    [Fact]
    public async Task TheSmallServiceWindowsSurviveManyPlayersAtOnce()
    {
        using var provider = CreateProvider(_ => { });
        var players = Enumerable.Range(0, ParallelCallers).Select(index => Player(provider, 9500 + index, out _)).ToList();
        var router = provider.GetRequiredService<IInGameOpcodeRouter>();

        var calls = players.Select(player => Task.Run(async () =>
        {
            foreach (var packet in ServicePackets())
            {
                var handler = router.Resolve((GameOpcodes)packet.GetOpcode());
                handler.Should().NotBeNull();
                await handler!(player.Client, packet);
            }
        }));

        var all = () => Task.WhenAll(calls);
        await all.Should().NotThrowAsync();
    }

    private static IEnumerable<Packet> ServicePackets()
    {
        var inn = new Packet(GameOpcodes.GS_INN);
        inn.WriteByte(InnSetHome);
        yield return inn;

        var ring = new Packet(GameOpcodes.GS_RING_UPGRADE);
        ring.WriteByte((byte)RingUpgradeSubOpcode.Upgrade);
        ring.WriteByte(0);
        yield return ring;

        var exchange = new Packet(GameOpcodes.GS_ITEM_EXCHANGE);
        exchange.WriteByte((byte)ItemExchangeSubOpcode.Exchange);
        exchange.WriteInt(FirstRecipe);
        yield return exchange;

        var combine = new Packet(GameOpcodes.GS_ITEM_COMBINE);
        combine.WriteByte(CombineRecipe);
        combine.WriteInt(FirstRecipe);
        yield return combine;
    }

    private ServiceProvider UpgradeProvider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetItem(Earring).Returns(new ItemData
        {
            Num = Earring, Kind = AccessoryKind, ItemType = StandardItemType, Grade = EarringGrade,
        });
        gameData.GetItem(UpgradedEarring).Returns(new ItemData { Num = UpgradedEarring, Kind = AccessoryKind });
        gameData.GetUpgradeRecipe(Earring, AccessoryScroll).Returns(new ItemUpgradeRecipeData
        {
            OriginNumber = Earring, NewNumber = UpgradedEarring, RequiredItem = AccessoryScroll,
        });
        gameData.GetUpgradeSetting(StandardItemType, EarringGrade, AccessoryScroll, 0)
            .Returns(new ItemUpgradeSettingsData { SuccessRate = GuaranteedRate });

        gameData.GetItem(Sword).Returns(new ItemData
        {
            Num = Sword, Kind = WeaponKind, ItemType = StandardItemType, ItemClass = HighItemClass, Grade = SwordGrade,
        });
        gameData.GetItem(UpgradedSword).Returns(new ItemData { Num = UpgradedSword, Kind = WeaponKind });
        gameData.GetUpgradeRecipe(Sword, HighClassScroll).Returns(new ItemUpgradeRecipeData
        {
            OriginNumber = Sword, NewNumber = UpgradedSword, RequiredItem = HighClassScroll,
        });
        gameData.GetUpgradeSetting(StandardItemType, SwordGrade, HighClassScroll, 0)
            .Returns(new ItemUpgradeSettingsData { SuccessRate = GuaranteedRate });
        gameData.GetItem(Potion).Returns(new ItemData { Num = Potion, Countable = 1 });
    });

    private static (UserSession Player, List<Packet> Sent) Smith(ServiceProvider provider, int characterId, params int[] inventory)
    {
        var player = Player(provider, characterId, out var sent, money: 1_000_000);
        provider.GetRequiredService<SessionManager>().Maps = CreateMapManagerWithObjectEvent(Zone, new ObjectEvent
        {
            Index = AnvilId, Type = AnvilObjectType, Status = 1, PosX = StandX, PosZ = StandZ,
        });
        for (var position = 0; position < inventory.Length; position++)
            Give(player, position, inventory[position]);
        return (player, sent);
    }

    private static Task Upgrade(
        ServiceProvider provider, UserSession player, ItemUpgradeSubOpcode sub, params (int ItemId, byte Position)[] slots)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_UPGRADE);
        packet.WriteByte((byte)sub);
        packet.WriteByte(UpgradeTypeNormal);
        packet.WriteInt(AnvilId);
        for (var index = 0; index < UpgradeSlotCount; index++)
        {
            packet.WriteInt(index < slots.Length ? slots[index].ItemId : 0);
            packet.WriteByte(index < slots.Length ? slots[index].Position : byte.MaxValue);
        }

        return provider.GetRequiredService<IItemPacketCoordinator>().HandleUpgradeAsync(player.Client, packet);
    }

    private static byte UpgradeResult(List<Packet> sent)
    {
        var reply = Last(sent, GameOpcodes.GS_ITEM_UPGRADE);
        reply.Should().NotBeNull();
        reply!.ReadByte();
        reply.ReadByte();
        return reply.ReadByte();
    }

    private static Task Register(ServiceProvider provider, UserSession seller)
    {
        var packet = new Packet(GameOpcodes.GS_AUCTION);
        packet.WriteByte(AuctionRegister);
        packet.WriteInt(Potion);
        packet.WriteInt(StartingBid);
        packet.WriteInt(Buyout);
        packet.WriteInt(1);
        return provider.GetRequiredService<IAuctionPacketCoordinator>().HandleAsync(seller.Client, packet);
    }

    private static byte RegisterResult(List<Packet> sent)
    {
        var reply = sent.First(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_AUCTION);
        reply.ResetOffset();
        reply.ReadByte();
        return reply.ReadByte();
    }
}
