using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class VipWarehouseTests : GameTestBase
{
    private const int AccountId = 9300;
    private const int CharacterId = 9400;
    private const string CharacterName = "Vaulter";
    private const string Pin = "1234";
    private const string WrongPin = "0000";
    private const string NewPin = "5678";
    private const int GemId = 379050000;
    private const byte Zone = 21;
    private const float Here = 100f;
    private const float KeeperOffset = 2f;
    private const int InnHostessNpcId = 12000;
    private const int VaultDays = 7;
    private const int RentalDays = 1;
    private const int PinGuessLimit = 5;
    private static readonly TimeSpan PastTheUnlock = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task Output_RequiresThePinToHaveBeenEntered()
    {
        using var provider = Provider(Pin);
        var (session, sent) = Online(provider, Pin);
        StockVault(session, 0);

        await Route(provider, session, Output(0, 0));

        Result(sent, VipWarehouseSubOpcode.Output).Should().Be((byte)VipWarehouseResult.Failed);
        session.VipWarehouse[0].ItemId.Should().Be(GemId);
        session.Inventory[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Output_WorksOnceThePinIsEntered()
    {
        using var provider = Provider(Pin);
        var (session, sent) = Online(provider, Pin);
        StockVault(session, 0);

        await Route(provider, session, PinPacket(VipWarehouseSubOpcode.EnterPassword, Pin));
        await Route(provider, session, Output(0, 0));

        Result(sent, VipWarehouseSubOpcode.Output).Should().Be((byte)VipWarehouseResult.Succeeded);
        session.Inventory[InventoryConstants.InventoryStart].ItemId.Should().Be(GemId);
    }

    [Fact]
    public async Task CancelPassword_RequiresThePin()
    {
        using var provider = Provider(Pin);
        var (session, sent) = Online(provider, Pin);

        await Route(provider, session, Sub(VipWarehouseSubOpcode.CancelPassword));

        Result(sent, VipWarehouseSubOpcode.CancelPassword).Should().NotBe((byte)VipWarehouseResult.Succeeded);
        session.VipPassword.Should().Be(Pin);
    }

    [Fact]
    public async Task ChangePassword_RequiresThePin()
    {
        using var provider = Provider(Pin);
        var (session, sent) = Online(provider, Pin);

        await Route(provider, session, PinPacket(VipWarehouseSubOpcode.ChangePassword, NewPin));

        Result(sent, VipWarehouseSubOpcode.ChangePassword).Should().NotBe((byte)VipWarehouseResult.Succeeded);
        session.VipPassword.Should().Be(Pin);
    }

    [Fact]
    public async Task TheUnlockLapsesAfterAWhile()
    {
        var clock = new ManualClock();
        using var provider = Provider(Pin, clock);
        var (session, sent) = Online(provider, Pin);
        StockVault(session, 0);
        StockVault(session, 1);

        await Route(provider, session, PinPacket(VipWarehouseSubOpcode.EnterPassword, Pin));
        await Route(provider, session, Output(0, 0));
        Result(sent, VipWarehouseSubOpcode.Output).Should().Be((byte)VipWarehouseResult.Succeeded);

        clock.Advance(PastTheUnlock);
        await Route(provider, session, Output(1, 1));

        Result(sent, VipWarehouseSubOpcode.Output).Should().Be((byte)VipWarehouseResult.Failed);
        session.VipWarehouse[1].ItemId.Should().Be(GemId);
    }

    [Fact]
    public async Task WrongGuessesLockThePinForTheRestOfTheSession()
    {
        using var provider = Provider(Pin);
        var (session, sent) = Online(provider, Pin);

        for (var guess = 0; guess < PinGuessLimit; guess++)
            await Route(provider, session, PinPacket(VipWarehouseSubOpcode.EnterPassword, WrongPin));
        await Route(provider, session, PinPacket(VipWarehouseSubOpcode.EnterPassword, Pin));

        Result(sent, VipWarehouseSubOpcode.EnterPassword).Should().Be((byte)VipWarehouseResult.Rejected);
    }

    [Fact]
    public async Task TheVaultNeedsAnInnHostessWithinReach()
    {
        using var provider = Provider(string.Empty);
        var (session, sent) = Online(provider, string.Empty, keeperClicked: false);

        await Route(provider, session, Sub(VipWarehouseSubOpcode.Open));

        Result(sent, VipWarehouseSubOpcode.Open).Should().Be((byte)VipWarehouseResult.Failed);
    }

    [Fact]
    public async Task Deposit_CommitsTheInventoryWithTheVault()
    {
        using var provider = Provider(string.Empty);
        var (session, sent) = Online(provider, string.Empty);
        StockBag(session);

        await Route(provider, session, Input(0, 0));

        Result(sent, VipWarehouseSubOpcode.Input).Should().Be((byte)VipWarehouseResult.Succeeded);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var character = await db.Characters.AsNoTracking().SingleAsync(c => c.Id == CharacterId);
        StoredSlots(character.Items, InventoryConstants.InventoryTotal)[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
        var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == AccountId);
        StoredSlots(account.VipWarehouseItems, UserSession.VipWarehouseMax)[0].ItemId.Should().Be(GemId);
    }

    [Fact]
    public async Task Deposit_KeepsARentedItemsExpiry()
    {
        using var provider = Provider(string.Empty);
        var (session, _) = Online(provider, string.Empty);
        var bag = StockBag(session);
        bag.ExpireInDays(RentalDays, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var expiresAt = bag.ExpiresAt;

        await Route(provider, session, Input(0, 0));

        session.VipWarehouse[0].ExpiresAt.Should().Be(expiresAt);
        session.VipWarehouse[0].State.Should().Be(ItemFlag.Rented);
    }

    [Fact]
    public async Task InventoryMove_KeepsARentedItemsExpiry()
    {
        using var provider = Provider(string.Empty);
        var (session, sent) = Online(provider, string.Empty);
        var bag = StockBag(session);
        bag.ExpireInDays(RentalDays, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var expiresAt = bag.ExpiresAt;
        var packet = Sub(VipWarehouseSubOpcode.InventoryMove);
        packet.WriteInt(0);
        packet.WriteInt(GemId);
        packet.WriteByte(0);
        packet.WriteByte(0);
        packet.WriteByte(1);

        await Route(provider, session, packet);

        Result(sent, VipWarehouseSubOpcode.InventoryMove).Should().Be((byte)VipWarehouseResult.Succeeded);
        var moved = session.Inventory[InventoryConstants.InventoryStart + 1];
        moved.ItemId.Should().Be(GemId);
        moved.ExpiresAt.Should().Be(expiresAt);
    }

    [Fact]
    public async Task Deposit_RefusesWhileAMerchantStallIsBeingSetUp()
    {
        using var provider = Provider(string.Empty);
        var (session, sent) = Online(provider, string.Empty);
        var bag = StockBag(session);
        session.Trade.IsBuyingMerchantPreparing = true;

        await Route(provider, session, Input(0, 0));

        Result(sent, VipWarehouseSubOpcode.Input).Should().Be((byte)VipWarehouseResult.Failed);
        bag.ItemId.Should().Be(GemId);
    }

    private static ServiceProvider Provider(string pin, ManualClock? clock = null) => CreateProvider(
        db =>
        {
            db.Accounts.Add(new Account
            {
                Id = AccountId,
                Login = "vaulter",
                Password = "pw",
                Nation = AccountNation.Karus,
                VipPassword = pin,
                VipVaultExpiry = DateTime.UtcNow.AddDays(VaultDays),
            });
            db.Characters.Add(new Character
            {
                Id = CharacterId,
                AccountId = AccountId,
                Name = CharacterName,
                Items = CreateInventory((InventoryConstants.InventoryStart, GemId, 1)),
            });
        },
        gameData => gameData.GetItem(GemId).Returns(new ItemData { Num = GemId, Countable = 0, Weight = 1, Duration = 1 }),
        configureServices: services =>
        {
            if (clock != null)
                services.AddSingleton<TimeProvider>(clock);
        });

    private static (UserSession Session, List<Packet> Sent) Online(ServiceProvider provider, string pin, bool keeperClicked = true)
    {
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = sessionManager.CreateSession(client, CharacterId, AccountId);
        session.Name = CharacterName;
        session.MaxHp = 100;
        session.Hp = 100;
        session.ZoneId = Zone;
        session.X = Here;
        session.Z = Here;
        session.VipPassword = pin;
        session.VipVaultExpiry = DateTime.UtcNow.AddDays(VaultDays);

        var hostess = sessionManager.Regions.SpawnNpc(new NpcInstance
        {
            NpcId = InnHostessNpcId,
            Name = "[Inn Hostess] Lined",
            NpcType = NpcData.TypeWarehouse,
            ZoneId = Zone,
            X = Here + KeeperOffset,
            Z = Here,
            Hp = 100,
            MaxHp = 100,
        });
        if (keeperClicked)
            session.Quest.EventNpcUniqueId = hostess.UniqueId;
        return (session, sent);
    }

    private static void StockVault(UserSession session, int index)
    {
        session.VipWarehouse[index].ItemId = GemId;
        session.VipWarehouse[index].Count = 1;
        session.VipWarehouse[index].Durability = 1;
    }

    private static ItemSlot StockBag(UserSession session)
    {
        var slot = session.Inventory[InventoryConstants.InventoryStart];
        slot.ItemId = GemId;
        slot.Count = 1;
        slot.Durability = 1;
        return slot;
    }

    private static Packet Input(byte bagSlot, byte vaultSlot) => Transfer(VipWarehouseSubOpcode.Input, bagSlot, vaultSlot);

    private static Packet Output(byte vaultSlot, byte bagSlot) => Transfer(VipWarehouseSubOpcode.Output, vaultSlot, bagSlot);

    private static Packet Transfer(VipWarehouseSubOpcode sub, byte source, byte destination)
    {
        var packet = Sub(sub);
        packet.WriteInt(0);
        packet.WriteInt(GemId);
        packet.WriteByte(0);
        packet.WriteByte(source);
        packet.WriteByte(destination);
        packet.WriteInt(1);
        return packet;
    }

    private static Packet PinPacket(VipWarehouseSubOpcode sub, string pin)
    {
        var packet = Sub(sub);
        packet.WriteSByteString(pin);
        return packet;
    }

    private static Packet Sub(VipWarehouseSubOpcode sub)
    {
        var packet = new Packet(GameOpcodes.GS_VIP_WAREHOUSE);
        packet.WriteByte((byte)sub);
        return packet;
    }

    private static Task Route(ServiceProvider provider, UserSession session, Packet packet)
    {
        packet.ResetOffset();
        return provider.GetRequiredService<IVipWarehousePacketCoordinator>().HandleAsync(session.Client, packet);
    }

    private static byte Result(List<Packet> sent, VipWarehouseSubOpcode sub)
    {
        var packet = sent.Last(p => p.GetOpcode() == (byte)GameOpcodes.GS_VIP_WAREHOUSE && p.GetData()[0] == (byte)sub);
        packet.ResetOffset();
        packet.ReadByte();
        return packet.ReadByte();
    }

    private static ItemSlot[] StoredSlots(byte[] blob, int length)
    {
        var slots = Enumerable.Range(0, length).Select(_ => new ItemSlot()).ToArray();
        UserSessionBinaryState.LoadSlots(slots, blob);
        return slots;
    }
}
