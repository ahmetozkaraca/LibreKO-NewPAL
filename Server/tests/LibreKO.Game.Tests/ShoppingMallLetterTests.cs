using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class ShoppingMallLetterTests : GameTestBase
{
    private const int SenderId = 8100;
    private const int RecipientId = 8101;
    private const string SenderName = "Sender";
    private const string RecipientName = "Recipient";
    private const int GiftId = 810002000;
    private const int UntradeableId = 810005000;
    private const byte RaceTradeable = 1;
    private const byte RaceUntradeable = 20;
    private const byte StoreLetter = 6;
    private const byte LetterGetItem = 4;
    private const byte LetterSend = 6;
    private const byte LetterWithItem = 2;
    private const byte LetterUnread = 1;
    private const byte Succeeded = 1;
    private const byte Rejected = unchecked((byte)-1);
    private const byte ItemNotMailable = unchecked((byte)-32);
    private const int SendItemCost = 10_000;
    private const int StartingMoney = 20_000;
    private const int CoinMax = 2_100_000_000;
    private const int GiftCoins = 990;
    private const int LetterId = 5001;
    private const byte FarBagSlot = 20;

    [Fact]
    public async Task LetterSend_RefusesAnItemItsTableMarksUntradeable()
    {
        using var provider = Provider();
        var (sender, sent) = Online(provider, SenderId, SenderName);
        var slot = Stock(sender, 0, UntradeableId);

        await Route(provider, sender, SendItemLetter(UntradeableId, 0));

        Result(sent, LetterSend).Should().Be(ItemNotMailable);
        slot.ItemId.Should().Be(UntradeableId);
        sender.Money.Should().Be(StartingMoney);
        (await LetterCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task LetterSend_RefusesWhileTheInventoryIsLocked()
    {
        using var provider = Provider();
        var (sender, sent) = Online(provider, SenderId, SenderName);
        var slot = Stock(sender, 0, GiftId);
        sender.Trade.IsSellingMerchantPreparing = true;

        await Route(provider, sender, SendItemLetter(GiftId, 0));

        Result(sent, LetterSend).Should().Be(Rejected);
        slot.ItemId.Should().Be(GiftId);
        sender.Money.Should().Be(StartingMoney);
        (await LetterCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task LetterSend_CommitsTheSendersInventoryAndGoldWithTheLetter()
    {
        using var provider = Provider();
        var (sender, sent) = Online(provider, SenderId, SenderName);
        Stock(sender, 0, GiftId);

        await Route(provider, sender, SendItemLetter(GiftId, 0));

        Result(sent, LetterSend).Should().Be(Succeeded);
        var stored = await StoredCharacterAsync(provider, SenderId);
        stored.Money.Should().Be(StartingMoney - SendItemCost);
        StoredSlot(stored, InventoryConstants.InventoryStart).IsEmpty.Should().BeTrue();
        (await LetterCountAsync(provider)).Should().Be(1);
    }

    [Fact]
    public async Task LetterSend_PutsTheItemAndPostageBackWhenTheCommitFails()
    {
        var probe = new SaveChangesProbe
        {
            FailWhen = db => db.ChangeTracker.Entries<MailBox>().Any(entry => entry.State == EntityState.Added),
        };
        using var provider = Provider(configureServices: probe.Register);
        var (sender, sent) = Online(provider, SenderId, SenderName);
        var slot = Stock(sender, 0, GiftId);

        await Route(provider, sender, SendItemLetter(GiftId, 0));

        Result(sent, LetterSend).Should().Be(Rejected);
        slot.ItemId.Should().Be(GiftId);
        sender.Money.Should().Be(StartingMoney);
        (await LetterCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task LetterSend_ClearsTheSlotTheItemLeftOnTheClient()
    {
        using var provider = Provider();
        var (sender, sent) = Online(provider, SenderId, SenderName);
        Stock(sender, FarBagSlot, GiftId);

        await Route(provider, sender, SendItemLetter(GiftId, FarBagSlot));

        var change = sent.Single(p => p.GetOpcode() == (byte)GameOpcodes.GS_ITEM_COUNT_CHANGE);
        change.ResetOffset();
        change.ReadShort();
        change.ReadByte();
        change.ReadByte().Should().Be(FarBagSlot);
    }

    [Fact]
    public async Task LetterGetItem_RefusesCoinsPastTheCoinCap()
    {
        using var provider = Provider(GiftLetter(GiftCoins));
        var (recipient, sent) = Online(provider, RecipientId, RecipientName);
        recipient.Money = CoinMax - 10;
        recipient.Stats.MaxWeight = 100;

        await Route(provider, recipient, ClaimLetter());

        Result(sent, LetterGetItem).Should().Be(Rejected);
        recipient.Money.Should().Be(CoinMax - 10);
        recipient.Inventory.Should().NotContain(slot => slot.ItemId == GiftId);
        (await LetterStatusAsync(provider)).Should().Be(LetterUnread);
    }

    [Fact]
    public async Task LetterGetItem_CommitsTheGiftWithTheClaim()
    {
        using var provider = Provider(GiftLetter(GiftCoins));
        var (recipient, sent) = Online(provider, RecipientId, RecipientName);
        recipient.Stats.MaxWeight = 100;

        await Route(provider, recipient, ClaimLetter());

        Result(sent, LetterGetItem).Should().Be(Succeeded);
        var stored = await StoredCharacterAsync(provider, RecipientId);
        stored.Money.Should().Be(StartingMoney + GiftCoins);
        StoredSlot(stored, InventoryConstants.InventoryStart).ItemId.Should().Be(GiftId);
    }

    [Fact]
    public async Task LetterGetItem_RefusesWhileTheInventoryIsLocked()
    {
        using var provider = Provider(GiftLetter(0));
        var (recipient, sent) = Online(provider, RecipientId, RecipientName);
        recipient.Stats.MaxWeight = 100;
        recipient.Trade.ExchangeUser = SenderId;

        await Route(provider, recipient, ClaimLetter());

        Result(sent, LetterGetItem).Should().Be(Rejected);
        recipient.Inventory.Should().NotContain(slot => slot.ItemId == GiftId);
        (await LetterStatusAsync(provider)).Should().Be(LetterUnread);
    }

    private static MailBox GiftLetter(int coins) => new()
    {
        LetterId = LetterId,
        RecipientId = RecipientName,
        SenderId = SenderName,
        Subject = "Gift",
        Message = "Take it",
        Type = LetterWithItem,
        Status = LetterUnread,
        ItemId = GiftId,
        Count = 1,
        Durability = 10,
        Coins = coins,
        SendDate = DateTime.UtcNow,
    };

    private static ServiceProvider Provider(MailBox? letter = null, Action<IServiceCollection>? configureServices = null) => CreateProvider(
        db =>
        {
            db.Characters.AddRange(Row(SenderId, SenderName), Row(RecipientId, RecipientName));
            if (letter != null)
                db.MailBoxes.Add(letter);
        },
        gameData =>
        {
            gameData.GetItem(GiftId).Returns(new ItemData { Num = GiftId, Race = RaceTradeable, Countable = 1, Weight = 1, Duration = 10 });
            gameData.GetItem(UntradeableId).Returns(new ItemData { Num = UntradeableId, Race = RaceUntradeable, Weight = 1, Duration = 10 });
        },
        configureServices: configureServices);

    private static Character Row(int id, string name) => new()
    {
        Id = id,
        AccountId = id,
        Name = name,
        Money = StartingMoney,
        Items = id == SenderId
            ? CreateInventory((InventoryConstants.InventoryStart, GiftId, 10))
            : new byte[InventoryConstants.InventoryTotal * UserSessionBinaryState.BytesPerItem],
    };

    private static (UserSession Session, List<Packet> Sent) Online(ServiceProvider provider, int characterId, string name)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = provider.GetRequiredService<SessionManager>().CreateSession(client, characterId, characterId);
        session.Name = name;
        session.Money = StartingMoney;
        session.Hp = 100;
        return (session, sent);
    }

    private static ItemSlot Stock(UserSession session, byte bagSlot, int itemId)
    {
        var slot = session.Inventory[InventoryConstants.InventoryStart + bagSlot];
        slot.ItemId = itemId;
        slot.Count = 1;
        slot.Durability = 10;
        return slot;
    }

    private static Packet SendItemLetter(int itemId, byte bagSlot)
    {
        var packet = new Packet(GameOpcodes.GS_SHOPPING_MALL);
        packet.WriteByte(StoreLetter);
        packet.WriteByte(LetterSend);
        packet.WriteSByteString(RecipientName);
        packet.WriteSByteString("Subject");
        packet.WriteByte(LetterWithItem);
        packet.WriteInt(itemId);
        packet.WriteByte(bagSlot);
        packet.WriteInt(0);
        packet.WriteString("Message");
        return packet;
    }

    private static Packet ClaimLetter()
    {
        var packet = new Packet(GameOpcodes.GS_SHOPPING_MALL);
        packet.WriteByte(StoreLetter);
        packet.WriteByte(LetterGetItem);
        packet.WriteInt(LetterId);
        return packet;
    }

    private static Task Route(ServiceProvider provider, UserSession session, Packet packet)
    {
        packet.ResetOffset();
        return provider.GetRequiredService<IShoppingMallPacketCoordinator>().HandleAsync(session.Client, packet);
    }

    private static byte Result(List<Packet> sent, byte sub)
    {
        var packet = sent.Last(p => p.GetOpcode() == (byte)GameOpcodes.GS_SHOPPING_MALL && p.GetData()[1] == sub);
        packet.ResetOffset();
        packet.ReadByte();
        packet.ReadByte();
        return packet.ReadByte();
    }

    private static async Task<int> LetterCountAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailBoxes.CountAsync();
    }

    private static async Task<byte> LetterStatusAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().MailBoxes.SingleAsync(l => l.LetterId == LetterId)).Status;
    }

    private static async Task<Character> StoredCharacterAsync(ServiceProvider provider, int characterId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Characters
            .AsNoTracking().SingleAsync(c => c.Id == characterId);
    }

    private static ItemSlot StoredSlot(Character character, int index)
    {
        var slots = Enumerable.Range(0, InventoryConstants.InventoryTotal).Select(_ => new ItemSlot()).ToArray();
        UserSessionBinaryState.LoadItems(slots, character.Items);
        return slots[index];
    }
}
