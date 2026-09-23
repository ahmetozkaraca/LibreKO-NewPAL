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

public class ItemTransferRuleTests : EconomyTestBase
{
    private const int Sword = 120150000;
    private const int Arrow = 391010000;
    private const int QuestToken = 389217000;
    private const int KrowazBoots = 208101000;
    private const byte RaceUntradeable = 20;
    private const short BindStones = 10;
    private const short SwordDuration = 100;
    private const short WornDurability = 33;
    private const long Expiry = 4_102_444_800;
    private const string SealCode = "12345678";
    private const int SealPrice = 1_000_000;
    private const byte MoveRequest = 1;
    private const byte ArrangeRequest = 2;
    private const byte FreePosition = 5;
    private const ushort Taken = 5;
    private const ushort Held = 7;
    private const ushort ArrivedMeanwhile = 3;
    private const int HomeSlots = 4;

    [Fact]
    public async Task ARentedItemCannotBeSealedIntoAPermanentOne()
    {
        using var provider = Provider();
        var player = Player(provider, 9101, out var sent, money: 5_000_000);
        player.SealCode = SealCode;
        Give(player, 0, KrowazBoots, flag: ItemFlag.Rented, expiresAt: Expiry);

        await Seal(provider, player, ItemSealType.Seal, KrowazBoots, 0, SealCode);

        Bag(player, 0).State.Should().Be(ItemFlag.Rented);
        player.Money.Should().Be(5_000_000);
        SealResult(sent).Should().Be(ItemSealResult.Failed);
    }

    [Fact]
    public async Task ARentedItemCannotBeBound()
    {
        using var provider = Provider();
        var player = Player(provider, 9102, out var sent);
        Give(player, 0, KrowazBoots, flag: ItemFlag.Rented, expiresAt: Expiry);

        await Seal(provider, player, ItemSealType.Bind, KrowazBoots, 0, string.Empty);

        Bag(player, 0).State.Should().Be(ItemFlag.Rented);
        SealResult(sent).Should().Be(ItemSealResult.Failed);
    }

    [Fact]
    public async Task AnItemWithoutABindingCostCannotBeBound()
    {
        using var provider = Provider();
        var player = Player(provider, 9103, out var sent);
        Give(player, 0, Sword);

        await Seal(provider, player, ItemSealType.Bind, Sword, 0, string.Empty);

        Bag(player, 0).State.Should().Be(ItemFlag.Unsealed);
        SealResult(sent).Should().Be(ItemSealResult.Failed);
    }

    [Fact]
    public async Task MovesSwapsAndArrangingKeepTheWholeSlot()
    {
        using var provider = Provider();
        var player = Player(provider, 9104, out _);
        Give(player, 0, Sword, flag: ItemFlag.Rented, expiresAt: Expiry, durability: WornDurability);
        Give(player, 1, Arrow, count: 20);

        await Move(provider, player, 0, FreePosition);

        Bag(player, FreePosition).ExpiresAt.Should().Be(Expiry);
        Bag(player, FreePosition).State.Should().Be(ItemFlag.Rented);

        await Move(provider, player, FreePosition, 1, Sword);

        Bag(player, 1).ItemId.Should().Be(Sword);
        Bag(player, 1).ExpiresAt.Should().Be(Expiry);
        Bag(player, FreePosition).ItemId.Should().Be(Arrow);
        Bag(player, FreePosition).ExpiresAt.Should().Be(0);

        var arrange = new Packet(GameOpcodes.GS_ITEM_MOVE);
        arrange.WriteByte(ArrangeRequest);
        await provider.GetRequiredService<IItemPacketCoordinator>().HandleMoveAsync(player.Client, arrange);

        var sword = player.Inventory.Single(slot => slot.ItemId == Sword);
        sword.ExpiresAt.Should().Be(Expiry);
        sword.Durability.Should().Be(WornDurability);
        player.Inventory.Single(slot => slot.ItemId == Arrow).ExpiresAt.Should().Be(0);
    }

    [Fact]
    public async Task WithdrawingKeepsTheStoredExpiry()
    {
        using var provider = Provider();
        var player = Player(provider, 9105, out _);
        var npc = Npc(provider, NpcData.TypeWarehouse);
        new ItemStack(Sword, SwordDuration, 1, (byte)ItemFlag.Unsealed, Expiry).WriteTo(player.Warehouse[0]);

        await Warehouse(provider, player, WarehouseSubOpcode.Output, npc, Sword, 0, 0);

        Bag(player, 0).ItemId.Should().Be(Sword);
        Bag(player, 0).ExpiresAt.Should().Be(Expiry);
    }

    [Fact]
    public async Task WithdrawingOntoABoundStackNeverRewritesItsFlag()
    {
        using var provider = Provider();
        var player = Player(provider, 9106, out _);
        var npc = Npc(provider, NpcData.TypeWarehouse);
        Give(player, 0, Arrow, count: 5, flag: ItemFlag.Bound);
        new ItemStack(Arrow, 0, 5, (byte)ItemFlag.Unsealed, 0).WriteTo(player.Warehouse[0]);

        await Warehouse(provider, player, WarehouseSubOpcode.Output, npc, Arrow, 0, 0, count: 5);

        Bag(player, 0).State.Should().Be(ItemFlag.Bound);
        Bag(player, 0).Count.Should().Be(5);
        player.Warehouse[0].Count.Should().Be(5);
    }

    [Theory]
    [InlineData(QuestToken, ItemFlag.Unsealed, 0L)]
    [InlineData(Sword, ItemFlag.Bound, 0L)]
    [InlineData(Sword, ItemFlag.Rented, Expiry)]
    [InlineData(Sword, ItemFlag.Sealed, 0L)]
    public async Task WhatMayNotLeaveTheCharacterCannotBeStoredOrDropped(int itemId, ItemFlag flag, long expiresAt)
    {
        using var provider = Provider();
        var player = Player(provider, 9107, out _);
        var npc = Npc(provider, NpcData.TypeWarehouse);
        Give(player, 0, itemId, flag: flag, expiresAt: expiresAt);

        await Warehouse(provider, player, WarehouseSubOpcode.Input, npc, itemId, 0, 0);
        await Drop(provider, player, 0, itemId);

        Bag(player, 0).ItemId.Should().Be(itemId);
        player.Warehouse[0].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task ADroppedItemIsPickedUpExactlyAsItWas()
    {
        using var provider = Provider();
        var dropper = Player(provider, 9108, out var sent);
        Give(dropper, 0, Sword, flag: ItemFlag.NotBound, durability: WornDurability);

        await Drop(provider, dropper, 0, Sword);

        Bag(dropper, 0).IsEmpty.Should().BeTrue();
        var drop = Last(sent, GameOpcodes.GS_ITEM_DROP);
        drop.Should().NotBeNull();
        drop!.ReadInt();
        var bundleId = drop.ReadInt();
        provider.GetRequiredService<SessionManager>().Regions.GetBundle(bundleId)!.ZoneId.Should().Be(Zone);

        await PickUp(provider, dropper, bundleId, Sword);

        var picked = dropper.Inventory.Single(slot => slot.ItemId == Sword);
        picked.State.Should().Be(ItemFlag.NotBound);
        picked.Durability.Should().Be(WornDurability);
    }

    [Fact]
    public async Task LootInAnotherZoneIsOutOfReach()
    {
        using var provider = Provider();
        var player = Player(provider, 9109, out _);
        var bundle = provider.GetRequiredService<SessionManager>().Regions.CreateBundle(StandX, StandZ, 0);
        bundle.ZoneId = OtherZone;
        bundle.OwnerCharId = player.CharacterId;
        bundle.Items.Add(new LootItem { ItemId = Arrow, Count = 5 });

        await PickUp(provider, player, bundle.BundleId, Arrow);

        TotalHeld(player, Arrow).Should().Be(0);
        bundle.SnapshotItems().Should().ContainSingle();
    }

    [Fact]
    public async Task ARefusedDropIsAnsweredWithAnEmptyDrop()
    {
        using var provider = Provider();
        var player = Player(provider, 9110, out var sent);
        Give(player, 0, Sword, flag: ItemFlag.Bound);

        await Drop(provider, player, 0, Sword);

        Bag(player, 0).ItemId.Should().Be(Sword);
        var reply = Last(sent, GameOpcodes.GS_ITEM_DROP);
        reply.Should().NotBeNull();
        reply!.ReadInt().Should().Be(player.CharacterId);
        reply.ReadInt().Should().Be(ItemDropPacketWriter.NoBundle);
        reply.ReadByte().Should().Be(0);
    }

    [Fact]
    public void RevertingAnUntouchedSlotRestoresIt()
    {
        var home = Home(ItemStack.Fresh(Arrow, 0, Held));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Take(home[0], Taken);
        ledger.Settle();

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].Count.Should().Be(Held);
    }

    [Fact]
    public void RevertingKeepsWhatArrivedAfterTheChange()
    {
        var home = Home();
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Put(home[0], ItemStack.Fresh(Arrow, 0, Taken));
        ledger.Settle();
        home[0].Count += ArrivedMeanwhile;

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].ItemId.Should().Be(Arrow);
        home[0].Count.Should().Be(ArrivedMeanwhile);
    }

    [Fact]
    public void RevertingAnEmptiedSlotAddsBackOntoWhatArrived()
    {
        var home = Home(ItemStack.Fresh(Arrow, 0, Taken));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Take(home[0], Taken);
        ledger.Settle();
        ItemTransfer.Put(home[0], ItemStack.Fresh(Arrow, 0, ArrivedMeanwhile));

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].Count.Should().Be(Taken + ArrivedMeanwhile);
    }

    [Fact]
    public void RevertingNeverStacksAnItemThatDoesNotStack()
    {
        var home = Home(ItemStack.Fresh(Sword, WornDurability, ItemTransfer.SingleItem));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Take(home[0], ItemTransfer.SingleItem);
        ledger.Settle();
        ItemTransfer.Put(home[0], ItemStack.Fresh(Sword, SwordDuration, ItemTransfer.SingleItem));

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].Count.Should().Be(ItemTransfer.SingleItem);
        home[0].Durability.Should().Be(SwordDuration);
        home[1].ItemId.Should().Be(Sword);
        home[1].Count.Should().Be(ItemTransfer.SingleItem);
        home[1].Durability.Should().Be(WornDurability);
    }

    [Fact]
    public void RevertingReturnsTheItemToAFreeSlotWhenADifferentItemArrived()
    {
        var home = Home(ItemStack.Fresh(Arrow, 0, Taken));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Take(home[0], Taken);
        ledger.Settle();
        ItemTransfer.Put(home[0], ItemStack.Fresh(Sword, SwordDuration, ItemTransfer.SingleItem));

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].ItemId.Should().Be(Sword);
        home[1].ItemId.Should().Be(Arrow);
        home[1].Count.Should().Be(Taken);
    }

    [Fact]
    public void RevertingChangesNothingWhenAnySlotCannotBeReverted()
    {
        var bag = new[] { ItemStack.Fresh(Arrow, 0, Taken).ToSlot() };
        var vault = Home();
        var ledger = new SlotLedger().Touch(bag[0], bag).Touch(vault[0], vault);
        ItemTransfer.TryTransfer(bag[0], vault[0], Taken, stackable: true).Should().BeTrue();
        ledger.Settle();
        ItemTransfer.Put(bag[0], ItemStack.Fresh(Sword, SwordDuration, ItemTransfer.SingleItem));

        ledger.Revert(Catalog()).Should().BeFalse();

        bag[0].ItemId.Should().Be(Sword);
        vault[0].ItemId.Should().Be(Arrow);
        vault[0].Count.Should().Be(Taken);
    }

    [Fact]
    public void RevertingGivesBackWhatWasTakenFromASlotThatEmptiedMeanwhile()
    {
        var home = Home(ItemStack.Fresh(Arrow, 0, Held));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Take(home[0], Taken);
        ledger.Settle();
        home[0].Clear();

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].ItemId.Should().Be(Arrow);
        home[0].Count.Should().Be(Taken);
    }

    [Fact]
    public void RevertingRefusesToTakeBackWhatAlreadyLeftTheSlot()
    {
        var home = Home();
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemTransfer.Put(home[0], ItemStack.Fresh(Arrow, 0, Taken));
        ledger.Settle();
        home[0].Clear();

        ledger.Revert(Catalog()).Should().BeFalse();

        home[0].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void RevertingASwappedSlotKeepsWhatArrivedAndReturnsTheOriginal()
    {
        var home = Home(ItemStack.Fresh(Sword, SwordDuration, ItemTransfer.SingleItem));
        var ledger = new SlotLedger().Touch(home[0], home);
        ItemStack.Fresh(Arrow, 0, Taken).WriteTo(home[0]);
        ledger.Settle();
        home[0].Count += ArrivedMeanwhile;

        ledger.Revert(Catalog()).Should().BeTrue();

        home[0].ItemId.Should().Be(Arrow);
        home[0].Count.Should().Be(ArrivedMeanwhile);
        home[1].ItemId.Should().Be(Sword);
    }

    private static ItemSlot[] Home(params ItemStack[] stacks) =>
        Enumerable.Range(0, HomeSlots).Select(index => index < stacks.Length ? stacks[index].ToSlot() : new ItemSlot()).ToArray();

    private static IGameDataService Catalog()
    {
        var gameData = Substitute.For<IGameDataService>();
        gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Countable = 0, Duration = SwordDuration });
        gameData.GetItem(Arrow).Returns(new ItemData { Num = Arrow, Countable = 1 });
        return gameData;
    }

    private ServiceProvider Provider() => CreateProvider(_ => { }, gameData =>
    {
        gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Countable = 0, Duration = SwordDuration });
        gameData.GetItem(Arrow).Returns(new ItemData { Num = Arrow, Countable = 1 });
        gameData.GetItem(QuestToken).Returns(new ItemData { Num = QuestToken, Countable = 1, Race = RaceUntradeable });
        gameData.GetItem(KrowazBoots).Returns(new ItemData { Num = KrowazBoots, Countable = 0, Bound = BindStones });
    });

    private static Task Seal(
        ServiceProvider provider, UserSession player, ItemSealType type, int itemId, byte position, string code)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_UPGRADE);
        packet.WriteByte((byte)ItemUpgradeSubOpcode.ItemSeal);
        packet.WriteByte((byte)type);
        packet.WriteInt(-1);
        packet.WriteInt(itemId);
        packet.WriteByte(position);
        packet.WriteString(code);
        return provider.GetRequiredService<IItemPacketCoordinator>().HandleUpgradeAsync(player.Client, packet);
    }

    private static ItemSealResult SealResult(List<Packet> sent)
    {
        var reply = Last(sent, GameOpcodes.GS_ITEM_UPGRADE);
        reply.Should().NotBeNull();
        reply!.ReadByte();
        reply.ReadByte();
        return (ItemSealResult)reply.ReadByte();
    }

    private static Task Move(ServiceProvider provider, UserSession player, byte from, byte to, int itemId = Sword)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_MOVE);
        packet.WriteByte(MoveRequest);
        packet.WriteByte((byte)ItemMoveDirection.InventoryToInventory);
        packet.WriteInt(itemId);
        packet.WriteByte(from);
        packet.WriteByte(to);
        return provider.GetRequiredService<IItemPacketCoordinator>().HandleMoveAsync(player.Client, packet);
    }

    private static Task Warehouse(
        ServiceProvider provider, UserSession player, WarehouseSubOpcode sub, NpcInstance npc,
        int itemId, byte source, byte destination, int count = 1)
    {
        var packet = new Packet(GameOpcodes.GS_WAREHOUSE);
        packet.WriteByte((byte)sub);
        packet.WriteInt(npc.UniqueId);
        packet.WriteInt(itemId);
        packet.WriteByte(0);
        packet.WriteByte(source);
        packet.WriteByte(destination);
        packet.WriteInt(count);
        return provider.GetRequiredService<IWarehousePacketCoordinator>().HandleAsync(player.Client, packet);
    }

    private static Task Drop(ServiceProvider provider, UserSession player, byte position, int itemId)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_DROP);
        packet.WriteByte(position);
        packet.WriteInt(itemId);
        packet.WriteUShort(1);
        return provider.GetRequiredService<ILootPacketCoordinator>().HandleItemDropAsync(player.Client, packet);
    }

    private static Task PickUp(ServiceProvider provider, UserSession player, int bundleId, int itemId)
    {
        var packet = new Packet(GameOpcodes.GS_ITEM_GET);
        packet.WriteInt(bundleId);
        packet.WriteInt(itemId);
        packet.WriteUShort(0);
        return provider.GetRequiredService<ILootPacketCoordinator>().HandleItemGetAsync(player.Client, packet);
    }
}
