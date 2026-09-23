using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class EquipRequirementTests : EconomyTestBase
{
    private const int NoviceSword = 121410000;
    private const int EliteSword = 127410000;
    private const int WarriorPauldron = 201011000;
    private const int StrongAxe = 128410000;
    private const int Shield = 190410000;
    private const int BandedRing = 310410000;

    private const byte SlotEitherHand = 0;
    private const byte SlotLeftHandOnly = 2;
    private const byte SlotPauldron = 5;
    private const byte SlotRing = 12;
    private const byte EliteLevel = 60;
    private const byte NoviceLevelCap = 30;
    private const byte WarriorClass = 1;
    private const short RequiredStrength = 100;
    private const byte LowStrength = 50;
    private const short BonusStrength = 60;

    private const short KarusWarrior = 101;
    private const short KarusMage = 103;
    private const short KarusKurian = 113;
    private const byte MoveRequest = 1;

    [Theory]
    [InlineData(10, false)]
    [InlineData(EliteLevel, true)]
    public async Task GearNeedsItsRequiredLevel(byte level, bool equipped)
    {
        using var provider = Provider();
        var player = Warrior(provider, 9201, level);
        Give(player, 0, EliteSword);

        await Equip(provider, player, EliteSword, 0, InventoryConstants.RightHand);

        (player.Inventory[InventoryConstants.RightHand].ItemId == EliteSword).Should().Be(equipped);
    }

    [Fact]
    public async Task GearOutsideItsLevelBandIsRefused()
    {
        using var provider = Provider();
        var player = Warrior(provider, 9202, level: NoviceLevelCap + 10);
        Give(player, 0, NoviceSword);

        await Equip(provider, player, NoviceSword, 0, InventoryConstants.RightHand);

        player.Inventory[InventoryConstants.RightHand].IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(KarusWarrior, true)]
    [InlineData(KarusKurian, true)]
    [InlineData(KarusMage, false)]
    public async Task ArmourFollowsTheClassFamilyTheClientShows(short classId, bool equipped)
    {
        using var provider = Provider();
        var player = Warrior(provider, 9203, EliteLevel);
        player.Class = classId;
        Give(player, 0, WarriorPauldron);

        await Equip(provider, player, WarriorPauldron, 0, InventoryConstants.Breast);

        (player.Inventory[InventoryConstants.Breast].ItemId == WarriorPauldron).Should().Be(equipped);
    }

    [Fact]
    public async Task StrengthRequirementsCountGearBonuses()
    {
        using var provider = Provider();
        var player = Warrior(provider, 9204, EliteLevel);
        player.Strength = LowStrength;
        Give(player, 0, StrongAxe);

        await Equip(provider, player, StrongAxe, 0, InventoryConstants.RightHand);

        player.Inventory[InventoryConstants.RightHand].IsEmpty.Should().BeTrue();

        player.Stats = new DerivedStats { StrBonus = BonusStrength };
        await Equip(provider, player, StrongAxe, 0, InventoryConstants.RightHand);

        player.Inventory[InventoryConstants.RightHand].ItemId.Should().Be(StrongAxe);
    }

    [Fact]
    public async Task UnequippingOntoGearCannotSmuggleItIntoTheSlot()
    {
        using var provider = Provider();
        var player = Warrior(provider, 9205, level: 10);
        player.Inventory[InventoryConstants.RightHand].ItemId = NoviceSword;
        player.Inventory[InventoryConstants.RightHand].Count = 1;
        Give(player, 0, EliteSword);

        await Move(provider, player, ItemMoveDirection.SlotToInventory, NoviceSword, InventoryConstants.RightHand, 0);

        player.Inventory[InventoryConstants.RightHand].ItemId.Should().Be(NoviceSword);
        Bag(player, 0).ItemId.Should().Be(EliteSword);
    }

    [Fact]
    public async Task SwappingHandsChecksBothItems()
    {
        using var provider = Provider();
        var player = Warrior(provider, 9206, EliteLevel);
        player.Inventory[InventoryConstants.RightHand].ItemId = EliteSword;
        player.Inventory[InventoryConstants.RightHand].Count = 1;
        player.Inventory[InventoryConstants.LeftHand].ItemId = Shield;
        player.Inventory[InventoryConstants.LeftHand].Count = 1;

        await Move(provider, player, ItemMoveDirection.SlotToSlot, EliteSword,
            InventoryConstants.RightHand, InventoryConstants.LeftHand);

        player.Inventory[InventoryConstants.RightHand].ItemId.Should().Be(EliteSword);
        player.Inventory[InventoryConstants.LeftHand].ItemId.Should().Be(Shield);
    }

    [Fact]
    public async Task SwappingRingsStillWorks()
    {
        using var provider = Provider();
        var player = Warrior(provider, 9207, EliteLevel);
        player.Inventory[InventoryConstants.RightRing].ItemId = BandedRing;
        player.Inventory[InventoryConstants.RightRing].Count = 1;

        await Move(provider, player, ItemMoveDirection.SlotToSlot, BandedRing,
            InventoryConstants.RightRing, InventoryConstants.LeftRing);

        player.Inventory[InventoryConstants.LeftRing].ItemId.Should().Be(BandedRing);
        player.Inventory[InventoryConstants.RightRing].IsEmpty.Should().BeTrue();
    }

    private ServiceProvider Provider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetCoefficient(Arg.Any<short>()).Returns(CreateBasicCoefficient(KarusWarrior));
        gameData.GetItem(NoviceSword).Returns(new ItemData
        {
            Num = NoviceSword, Slot = SlotEitherHand, ReqLevelMax = NoviceLevelCap,
        });
        gameData.GetItem(EliteSword).Returns(new ItemData
        {
            Num = EliteSword, Slot = SlotEitherHand, ReqLevel = EliteLevel,
        });
        gameData.GetItem(WarriorPauldron).Returns(new ItemData
        {
            Num = WarriorPauldron, Slot = SlotPauldron, Class = WarriorClass,
        });
        gameData.GetItem(StrongAxe).Returns(new ItemData
        {
            Num = StrongAxe, Slot = SlotEitherHand, ReqStr = RequiredStrength,
        });
        gameData.GetItem(Shield).Returns(new ItemData { Num = Shield, Slot = SlotLeftHandOnly });
        gameData.GetItem(BandedRing).Returns(new ItemData { Num = BandedRing, Slot = SlotRing });
    });

    private static UserSession Warrior(ServiceProvider provider, int characterId, byte level)
    {
        var player = Player(provider, characterId, out _);
        player.Class = KarusWarrior;
        player.Level = level;
        player.Strength = (byte)RequiredStrength;
        return player;
    }

    private static Task Equip(ServiceProvider provider, UserSession player, int itemId, byte bagPosition, int slot) =>
        Move(provider, player, ItemMoveDirection.InventoryToSlot, itemId, bagPosition, slot);

    private static Task Move(
        ServiceProvider provider, UserSession player, ItemMoveDirection direction, int itemId, int from, int to)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_MOVE);
        packet.WriteByte(MoveRequest);
        packet.WriteByte((byte)direction);
        packet.WriteInt(itemId);
        packet.WriteByte((byte)from);
        packet.WriteByte((byte)to);
        return provider.GetRequiredService<IItemPacketCoordinator>().HandleMoveAsync(player.Client, packet);
    }
}
