using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LibreKO.Game.Tests;

public class MailServiceTests : GameTestBase
{
    private const int AliceId = 1;
    private const int BobId = 2;
    private const int Apple = 810418000;
    private const int Sword = 110110001;
    private const int CoinMax = 2_100_000_000;
    private const int FreeSlotsLeft = 2;

    private static ServiceProvider Provider(SaveChangesProbe? probe = null) => CreateProvider(
        db => db.Characters.AddRange(
            new Character { Id = AliceId, AccountId = 1, Name = "Alice" },
            new Character { Id = BobId, AccountId = 2, Name = "Bob" }),
        ConfigureItems,
        configureServices: services => probe?.Register(services));

    private static void ConfigureItems(IGameDataService gameData)
    {
        gameData.GetItem(Apple).Returns(new ItemData { Num = Apple, Name = "Apples of Moradon", Countable = 1, Duration = 1, Weight = 1 });
        gameData.GetItem(Sword).Returns(new ItemData { Num = Sword, Name = "Sword", Countable = 0, Duration = 100, Weight = 10 });
    }

    private static ItemSlot Stock(UserSession session, int index, int itemId, ushort count)
    {
        var slot = session.Inventory[index];
        slot.ItemId = itemId;
        slot.Count = count;
        return slot;
    }

    private static async Task<int> SystemMailAsync(ServiceProvider provider, UserSession recipient, List<Packet> sent, params MailAttachmentDraft[] attachments)
    {
        var mail = provider.GetRequiredService<IMailService>();
        await mail.SendSystemMailAsync(recipient.CharacterId, "Gift", "A gift.", attachments);
        await mail.SendInboxAsync(recipient);
        var inbox = MailPacket(sent, MailPacketWriter.SubList);
        inbox.ReadUShort();
        return inbox.ReadInt();
    }

    private static async Task<int> MailCountAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Mails.CountAsync();
    }

    private static async Task<bool> UnclaimedAsync(ServiceProvider provider, int mailId)
    {
        using var scope = provider.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Mails
            .Include(m => m.Attachments).SingleAsync(m => m.Id == mailId);
        return stored.HasUnclaimedAttachments;
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

    private static (UserSession Session, List<Packet> Sent) Online(ServiceProvider provider, int characterId, string name)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(p => sent.Add(p)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = provider.GetRequiredService<SessionManager>().CreateSession(client, characterId, characterId);
        session.Name = name;
        session.Level = 50;
        session.Hp = 100;
        return (session, sent);
    }

    private static Packet MailPacket(List<Packet> sent, byte sub)
    {
        var packet = sent.Last(p => p.GetOpcode() == (byte)GameOpcodes.GS_MAIL && p.GetData()[0] == sub);
        packet.ResetOffset();
        packet.ReadByte();
        return packet;
    }

    [Fact]
    public async Task SendAsync_TakesGoldAndItems_StoresMail_AndNotifiesAnOnlineRecipient()
    {
        using var provider = Provider();
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        alice.Money = 10_000;
        var slot = alice.Inventory[InventoryConstants.InventoryStart];
        slot.ItemId = Apple;
        slot.Count = 5;
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "Enjoy them.", 2_500, [new MailItemPick((byte)InventoryConstants.InventoryStart, 3)]);

        alice.Money.Should().Be(7_500);
        slot.Count.Should().Be(2);
        var result = MailPacket(aliceSent, MailPacketWriter.SubSend);
        result.ReadByte().Should().Be(MailPacketWriter.Succeeded);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Mails.Include(m => m.Attachments).SingleAsync();
        stored.RecipientCharacterId.Should().Be(BobId);
        stored.SenderName.Should().Be("Alice");
        stored.Body.Should().Be("Enjoy them.");
        stored.Attachments.Select(a => (a.Kind, a.ItemId, a.Count)).Should().BeEquivalentTo(
        [
            (MailAttachmentKind.Item, Apple, 3),
            (MailAttachmentKind.Gold, InventoryConstants.ItemGold, 2_500),
        ]);

        var unread = MailPacket(bobSent, MailPacketWriter.SubUnread);
        unread.ReadUShort().Should().Be(1);
        bobSent.Should().Contain(p => p.GetOpcode() == (byte)GameOpcodes.GS_NOTICE);
    }

    [Fact]
    public async Task SendAsync_RefusesUnknownRecipient_SelfAndMissingGold()
    {
        using var provider = Provider();
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        alice.Money = 100;
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Nobody", "Hi", "", 0, []);
        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);

        await mail.SendAsync(alice, "alice", "Hi", "", 0, []);
        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);

        await mail.SendAsync(alice, "Bob", "Hi", "", 500, []);
        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);

        alice.Money.Should().Be(100);
        using var scope = provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Mails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_RefusesBoundItems()
    {
        using var provider = Provider();
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        var slot = alice.Inventory[InventoryConstants.InventoryStart];
        slot.ItemId = Apple;
        slot.Count = 5;
        slot.Flag = (byte)ItemFlag.Bound;
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "", 0, [new MailItemPick((byte)InventoryConstants.InventoryStart, 3)]);

        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);
        slot.Count.Should().Be(5);
        using var scope = provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Mails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ClaimAsync_DeliversAttachments_ThenDeleteSucceeds()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        bob.Money = 10;
        var mail = provider.GetRequiredService<IMailService>();
        await mail.SendSystemMailAsync(BobId, "Gift", "A gift.",
        [
            new MailAttachmentDraft(MailAttachmentKind.Item, Apple, 4, 1),
            new MailAttachmentDraft(MailAttachmentKind.Gold, InventoryConstants.ItemGold, 990),
        ]);

        await mail.SendInboxAsync(bob);
        var inbox = MailPacket(bobSent, MailPacketWriter.SubList);
        inbox.ReadUShort().Should().Be(1);
        var mailId = inbox.ReadInt();
        inbox.ReadSByteString().Should().Be(MailLimits.SystemSenderName);
        inbox.ReadSByteString().Should().Be("Gift");
        inbox.ReadByte().Should().Be(0);
        inbox.ReadByte().Should().Be(MailPacketWriter.AttachmentsPending);
        inbox.ReadLong();
        inbox.ReadByte().Should().Be(2);

        await mail.DeleteAsync(bob, mailId);
        MailPacket(bobSent, MailPacketWriter.SubDelete).ReadByte().Should().Be(MailPacketWriter.Failed);

        await mail.ClaimAsync(bob, mailId);
        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Succeeded);
        bob.Money.Should().Be(1_000);
        bob.Inventory.Skip(InventoryConstants.InventoryStart).Where(s => s.ItemId == Apple).Sum(s => (int)s.Count).Should().Be(4);

        await mail.ClaimAsync(bob, mailId);
        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Failed);
        bob.Money.Should().Be(1_000);

        await mail.DeleteAsync(bob, mailId);
        MailPacket(bobSent, MailPacketWriter.SubDelete).ReadByte().Should().Be(MailPacketWriter.Succeeded);
        await mail.SendInboxAsync(bob);
        MailPacket(bobSent, MailPacketWriter.SubList).ReadUShort().Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_CommitsTheSendersInventoryAndGoldWithTheMail()
    {
        using var provider = Provider();
        var (alice, _) = Online(provider, AliceId, "Alice");
        alice.Money = 10_000;
        Stock(alice, InventoryConstants.InventoryStart, Apple, 5);
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "", 2_500, [new MailItemPick((byte)InventoryConstants.InventoryStart, 3)]);

        var stored = await StoredCharacterAsync(provider, AliceId);
        stored.Money.Should().Be(7_500);
        StoredSlot(stored, InventoryConstants.InventoryStart).Count.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_RefusesWhileTheInventoryIsLocked()
    {
        using var provider = Provider();
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        alice.Money = 10_000;
        var slot = Stock(alice, InventoryConstants.InventoryStart, Apple, 5);
        alice.Trade.ExchangeUser = BobId;
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "", 100, [new MailItemPick((byte)InventoryConstants.InventoryStart, 3)]);

        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);
        slot.Count.Should().Be(5);
        alice.Money.Should().Be(10_000);
        (await MailCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_RefusesARecipientPendingDeletion()
    {
        using var provider = CreateProvider(
            db => db.Characters.AddRange(
                new Character { Id = AliceId, AccountId = 1, Name = "Alice" },
                new Character { Id = BobId, AccountId = 2, Name = "Bob", DeletionTime = DateTime.UtcNow }),
            ConfigureItems);
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        alice.Money = 1_000;
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Hello", "", 500, []);

        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);
        alice.Money.Should().Be(1_000);
        (await MailCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_PutsTheAttachmentsBackWhenTheCommitFails()
    {
        var probe = new SaveChangesProbe
        {
            FailWhen = db => db.ChangeTracker.Entries<Mail>().Any(entry => entry.State == EntityState.Added),
        };
        using var provider = Provider(probe);
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        alice.Money = 10_000;
        var slot = Stock(alice, InventoryConstants.InventoryStart, Apple, 5);
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "", 2_500, [new MailItemPick((byte)InventoryConstants.InventoryStart, 5)]);

        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);
        slot.ItemId.Should().Be(Apple);
        slot.Count.Should().Be(5);
        alice.Money.Should().Be(10_000);
        (await MailCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task SendAsync_RefusesOnceTheSenderIsLeaving()
    {
        using var provider = Provider();
        var (alice, aliceSent) = Online(provider, AliceId, "Alice");
        alice.Money = 10_000;
        var slot = Stock(alice, InventoryConstants.InventoryStart, Apple, 5);
        alice.TryBeginClosing();
        var mail = provider.GetRequiredService<IMailService>();

        await mail.SendAsync(alice, "Bob", "Apples", "", 100, [new MailItemPick((byte)InventoryConstants.InventoryStart, 3)]);

        MailPacket(aliceSent, MailPacketWriter.SubSend).ReadByte().Should().Be(MailPacketWriter.Failed);
        slot.Count.Should().Be(5);
        alice.Money.Should().Be(10_000);
        (await MailCountAsync(provider)).Should().Be(0);
    }

    [Fact]
    public async Task ClaimAsync_CommitsTheAttachmentsWithTheClaim()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        bob.Money = 10;
        var mail = provider.GetRequiredService<IMailService>();
        var mailId = await SystemMailAsync(provider, bob, bobSent,
            new MailAttachmentDraft(MailAttachmentKind.Item, Apple, 4, 1),
            new MailAttachmentDraft(MailAttachmentKind.Gold, InventoryConstants.ItemGold, 990));

        await mail.ClaimAsync(bob, mailId);

        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Succeeded);
        var stored = await StoredCharacterAsync(provider, BobId);
        stored.Money.Should().Be(1_000);
        StoredSlot(stored, InventoryConstants.InventoryStart).ItemId.Should().Be(Apple);
    }

    [Fact]
    public async Task ClaimAsync_RefusesGoldPastTheCoinCap()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        bob.Money = CoinMax - 10;
        var mail = provider.GetRequiredService<IMailService>();
        var mailId = await SystemMailAsync(provider, bob, bobSent,
            new MailAttachmentDraft(MailAttachmentKind.Gold, InventoryConstants.ItemGold, 990));

        await mail.ClaimAsync(bob, mailId);

        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Failed);
        bob.Money.Should().Be(CoinMax - 10);
        (await UnclaimedAsync(provider, mailId)).Should().BeTrue();
    }

    [Fact]
    public async Task ClaimAsync_NeedsRoomForEveryPieceBeforeTakingAny()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        for (var index = InventoryConstants.InventoryStart + FreeSlotsLeft; index < InventoryConstants.InventoryStart + InventoryConstants.HaveMax; index++)
            Stock(bob, index, Apple, 1);
        var mail = provider.GetRequiredService<IMailService>();
        var mailId = await SystemMailAsync(provider, bob, bobSent,
            new MailAttachmentDraft(MailAttachmentKind.Item, Sword, FreeSlotsLeft + 1, 1));

        await mail.ClaimAsync(bob, mailId);

        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Failed);
        bob.Inventory.Should().NotContain(slot => slot.ItemId == Sword);
        (await UnclaimedAsync(provider, mailId)).Should().BeTrue();
    }

    [Fact]
    public async Task ClaimAsync_RefusesWhileTheInventoryIsLocked()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        var mail = provider.GetRequiredService<IMailService>();
        var mailId = await SystemMailAsync(provider, bob, bobSent,
            new MailAttachmentDraft(MailAttachmentKind.Gold, InventoryConstants.ItemGold, 990));
        bob.Trade.MerchantState = MerchantMode.Selling;

        await mail.ClaimAsync(bob, mailId);

        MailPacket(bobSent, MailPacketWriter.SubClaim).ReadByte().Should().Be(MailPacketWriter.Failed);
        bob.Money.Should().Be(0);
        (await UnclaimedAsync(provider, mailId)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ReturnsBodyAndClearsUnread()
    {
        using var provider = Provider();
        var (bob, bobSent) = Online(provider, BobId, "Bob");
        var mail = provider.GetRequiredService<IMailService>();
        await mail.SendSystemMailAsync(BobId, "Welcome", "Hello Bob.", []);
        await mail.SendInboxAsync(bob);
        var inbox = MailPacket(bobSent, MailPacketWriter.SubList);
        inbox.ReadUShort();
        var mailId = inbox.ReadInt();

        await mail.ReadAsync(bob, mailId);
        var read = MailPacket(bobSent, MailPacketWriter.SubRead);
        read.ReadByte().Should().Be(MailPacketWriter.Succeeded);
        read.ReadInt().Should().Be(mailId);
        read.ReadSByteString().Should().Be("Hello Bob.");

        await mail.SendUnreadAsync(bob);
        MailPacket(bobSent, MailPacketWriter.SubUnread).ReadUShort().Should().Be(0);
    }
}
