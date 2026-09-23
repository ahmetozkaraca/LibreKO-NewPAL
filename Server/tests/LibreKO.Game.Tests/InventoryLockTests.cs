using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class InventoryLockTests : EconomyTestBase
{
    public enum Route { Move, Arrange, Destroy, Deposit, Withdraw, Drop, Pickup, Upgrade }

    public enum LockedBy { Trade, StallSetup, OpenStall, BuyingStallSetup, Mining }

    private const int Sword = 120150000;
    private const int Potion = 389014000;
    private const int Scroll = 379021000;
    private const byte SwordPosition = 3;
    private const byte FreePosition = 10;
    private const byte MoveRequest = 1;
    private const byte ArrangeRequest = 2;
    private const byte RemoveFromBag = 0;
    private const byte UpgradeTypeNormal = 1;
    private const int UpgradeSlotCount = 10;
    private const short AnvilId = 500;
    private const byte AnvilObjectType = 8;

    public static TheoryData<Route, LockedBy> EveryRouteUnderEveryLock()
    {
        var data = new TheoryData<Route, LockedBy>();
        foreach (var route in Enum.GetValues<Route>())
        {
            foreach (var locked in Enum.GetValues<LockedBy>())
                data.Add(route, locked);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRouteUnderEveryLock))]
    public async Task ALockedBagCannotBeChangedByAnyRoute(Route route, LockedBy locked)
    {
        using var provider = CreateProvider(_ => { }, gameData =>
        {
            gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Countable = 0, Kind = 52, ItemType = 5, ItemClass = 3 });
            gameData.GetItem(Potion).Returns(new ItemData { Num = Potion, Countable = 1 });
            gameData.GetItem(Scroll).Returns(new ItemData { Num = Scroll, Countable = 1 });
            gameData.GetUpgradeRecipe(Sword, Scroll).Returns(new ItemUpgradeRecipeData
            {
                OriginNumber = Sword, NewNumber = Sword + 1, RequiredItem = Scroll,
            });
            gameData.GetItem(Sword + 1).Returns(new ItemData { Num = Sword + 1, Countable = 0 });
            gameData.GetUpgradeSetting(Arg.Any<short>(), Arg.Any<short>(), Scroll, 0)
                .Returns(new ItemUpgradeSettingsData { SuccessRate = 10000 });
        });

        var player = Player(provider, 9001, out _, money: 1_000_000);
        var partner = Player(provider, 9002, out _);
        var warehouseNpc = Npc(provider, NpcData.TypeWarehouse);
        Give(player, SwordPosition, Sword);
        Give(player, SwordPosition + 1, Scroll);
        player.Warehouse[0].ItemId = Potion;
        player.Warehouse[0].Count = 1;

        var sessionManager = provider.GetRequiredService<SessionManager>();
        sessionManager.Maps = CreateMapManagerWithObjectEvent(Zone, new ObjectEvent
        {
            Index = AnvilId, Type = AnvilObjectType, Status = 1, PosX = StandX, PosZ = StandZ,
        });
        var bundle = sessionManager.Regions.CreateBundle(player.X, player.Z, player.Y);
        bundle.ZoneId = Zone;
        bundle.OwnerCharId = player.CharacterId;
        bundle.Items.Add(new LootItem { ItemId = Potion, Count = 1 });

        Lock(player, partner, locked);

        await Take(provider, player, route, warehouseNpc, bundle);

        Bag(player, SwordPosition).ItemId.Should().Be(Sword);
        Bag(player, SwordPosition).Count.Should().Be(1);
        Bag(player, SwordPosition + 1).ItemId.Should().Be(Scroll);
        TotalHeld(player, Potion).Should().Be(0);
        player.Warehouse[0].ItemId.Should().Be(Potion);
        player.Warehouse.Should().NotContain(slot => slot.ItemId == Sword);
        bundle.SnapshotItems().Should().ContainSingle();
    }

    private static void Lock(UserSession player, UserSession partner, LockedBy locked)
    {
        switch (locked)
        {
            case LockedBy.Trade:
                player.Trade.ExchangeUser = partner.CharacterId;
                partner.Trade.ExchangeUser = player.CharacterId;
                break;
            case LockedBy.StallSetup:
                player.Trade.IsSellingMerchantPreparing = true;
                break;
            case LockedBy.OpenStall:
                player.Trade.MerchantState = MerchantMode.Selling;
                break;
            case LockedBy.BuyingStallSetup:
                player.Trade.IsBuyingMerchantPreparing = true;
                break;
            case LockedBy.Mining:
                player.IsMining = true;
                break;
        }
    }

    private static Task Take(ServiceProvider provider, UserSession player, Route route, NpcInstance warehouseNpc, LootBundle bundle)
    {
        var items = provider.GetRequiredService<IItemPacketCoordinator>();
        var warehouse = provider.GetRequiredService<IWarehousePacketCoordinator>();
        var loot = provider.GetRequiredService<ILootPacketCoordinator>();

        switch (route)
        {
            case Route.Move:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_MOVE);
                packet.WriteByte(MoveRequest);
                packet.WriteByte((byte)ItemMoveDirection.InventoryToInventory);
                packet.WriteInt(Sword);
                packet.WriteByte(SwordPosition);
                packet.WriteByte(FreePosition);
                return items.HandleMoveAsync(player.Client, packet);
            }
            case Route.Arrange:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_MOVE);
                packet.WriteByte(ArrangeRequest);
                return items.HandleMoveAsync(player.Client, packet);
            }
            case Route.Destroy:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_REMOVE);
                packet.WriteByte(RemoveFromBag);
                packet.WriteByte(SwordPosition);
                packet.WriteInt(Sword);
                return items.HandleRemoveAsync(player.Client, packet);
            }
            case Route.Deposit:
                return warehouse.HandleAsync(player.Client,
                    WarehouseMove(WarehouseSubOpcode.Input, warehouseNpc, Sword, SwordPosition, 0));
            case Route.Withdraw:
                return warehouse.HandleAsync(player.Client,
                    WarehouseMove(WarehouseSubOpcode.Output, warehouseNpc, Potion, 0, FreePosition));
            case Route.Drop:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_DROP);
                packet.WriteByte(SwordPosition);
                packet.WriteInt(Sword);
                packet.WriteUShort(1);
                return loot.HandleItemDropAsync(player.Client, packet);
            }
            case Route.Pickup:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_GET);
                packet.WriteInt(bundle.BundleId);
                packet.WriteInt(Potion);
                packet.WriteUShort(0);
                return loot.HandleItemGetAsync(player.Client, packet);
            }
            default:
            {
                var packet = new Packet(GameOpcodes.GS_ITEM_UPGRADE);
                packet.WriteByte((byte)ItemUpgradeSubOpcode.Upgrade);
                packet.WriteByte(UpgradeTypeNormal);
                packet.WriteInt(AnvilId);
                packet.WriteInt(Sword);
                packet.WriteByte(SwordPosition);
                packet.WriteInt(Scroll);
                packet.WriteByte(SwordPosition + 1);
                for (var index = 2; index < UpgradeSlotCount; index++)
                {
                    packet.WriteInt(0);
                    packet.WriteByte(byte.MaxValue);
                }

                return items.HandleUpgradeAsync(player.Client, packet);
            }
        }
    }

    private static Packet WarehouseMove(WarehouseSubOpcode sub, NpcInstance npc, int itemId, byte source, byte destination)
    {
        var packet = new Packet(GameOpcodes.GS_WAREHOUSE);
        packet.WriteByte((byte)sub);
        packet.WriteInt(npc.UniqueId);
        packet.WriteInt(itemId);
        packet.WriteByte(0);
        packet.WriteByte(source);
        packet.WriteByte(destination);
        packet.WriteInt(1);
        return packet;
    }
}
