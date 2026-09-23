using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.Protocol;

public readonly record struct MailAttachmentDraft(MailAttachmentKind Kind, int ItemId, int Count, short Durability = 0);

public readonly record struct MailItemPick(byte Slot, ushort Count);

public interface IMailService
{
    Task SendSystemMailAsync(int recipientCharacterId, string subject, string body, IReadOnlyList<MailAttachmentDraft> attachments);
    Task SendInboxAsync(UserSession session);
    Task SendUnreadAsync(UserSession session);
    Task ReadAsync(UserSession session, int mailId);
    Task SendAsync(UserSession session, string recipientName, string subject, string body, int gold, IReadOnlyList<MailItemPick> items);
    Task DeleteAsync(UserSession session, int mailId);
    Task ClaimAsync(UserSession session, int mailId);
}

public class MailService(
    SessionManager sessionManager,
    IServiceScopeFactory scopeFactory,
    ICharacterStatePersister characterStatePersister,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    IPlayerProgressionService playerProgressionService,
    ILoyaltyService loyaltyService,
    ILogger<MailService> logger) : IMailService
{
    public async Task SendSystemMailAsync(int recipientCharacterId, string subject, string body, IReadOnlyList<MailAttachmentDraft> attachments)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Mails.Add(NewMail(null, MailLimits.SystemSenderName, recipientCharacterId, subject, body, attachments));
            await db.SaveChangesAsync();
        }

        var recipient = sessionManager.GetByCharacterId(recipientCharacterId);
        if (recipient != null)
            await NotifyNewMailAsync(recipient, MailLimits.SystemSenderName);
    }

    public async Task SendInboxAsync(UserSession session)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mails = await InboxQuery(db, session.CharacterId)
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.Id)
            .Take(MailLimits.InboxMax)
            .ToListAsync();
        await session.Client.SendPacket(MailPacketWriter.Inbox(mails));
    }

    public async Task SendUnreadAsync(UserSession session)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await session.Client.SendPacket(MailPacketWriter.Unread(await UnreadCountAsync(db, session.CharacterId)));
    }

    public async Task ReadAsync(UserSession session, int mailId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = await InboxQuery(db, session.CharacterId).FirstOrDefaultAsync(m => m.Id == mailId);
        if (mail == null)
        {
            await session.Client.SendPacket(MailPacketWriter.ReadResult(false, mailId, string.Empty));
            return;
        }

        if (mail.ReadAt == null)
        {
            mail.ReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await session.Client.SendPacket(MailPacketWriter.ReadResult(true, mail.Id, mail.Body));
    }

    public async Task SendAsync(UserSession session, string recipientName, string subject, string body, int gold, IReadOnlyList<MailItemPick> items)
    {
        recipientName = recipientName.Trim();
        subject = Truncate(subject.Trim(), MailLimits.SubjectMax);
        body = Truncate(body, MailLimits.BodyMax);

        if (recipientName.Length == 0 || subject.Length == 0)
        {
            await Fail(session, "A recipient and a subject are required.");
            return;
        }

        if (session.Trade.LocksInventory)
        {
            await Fail(session, "You cannot send mail while trading.");
            return;
        }

        if (string.Equals(recipientName, session.Name, StringComparison.OrdinalIgnoreCase))
        {
            await Fail(session, "You cannot mail yourself.");
            return;
        }

        if (gold < 0 || gold > session.Money)
        {
            await Fail(session, "You do not carry that much gold.");
            return;
        }

        if (items.Count > MailLimits.ItemAttachmentsMax)
        {
            await Fail(session, $"A mail carries at most {MailLimits.ItemAttachmentsMax} items.");
            return;
        }

        var outcome = await characterStatePersister.RunAsync(session, SendOutcome.Refused("You cannot send mail right now."), async unit =>
        {
            var recipient = await unit.Db.Characters
                .Where(c => c.Name == recipientName && c.DeletionTime == null)
                .Select(c => new MailRecipient(c.Id, c.Name))
                .FirstOrDefaultAsync();
            if (recipient == null)
                return SendOutcome.Refused($"No character named '{recipientName}'.");
            if (recipient.Id == session.CharacterId)
                return SendOutcome.Refused("You cannot mail yourself.");

            var parcel = session.WithLock(s => TakeParcel(s, gold, items));
            if (parcel.Error != null)
                return SendOutcome.Refused(parcel.Error);

            unit.Db.Mails.Add(NewMail(session.CharacterId, session.Name, recipient.Id, subject, body, parcel.Drafts));
            try
            {
                await unit.CommitAsync();
            }
            catch (Exception ex)
            {
                session.WithLock(s =>
                {
                    parcel.Restore(s);
                    s.RecalculateStatsWithBuffs(gameDataService);
                });
                logger.LogWarning(ex, "Mail from {Sender} to {Recipient} could not be stored", session.Name, recipient.Name);
                return SendOutcome.Refused("The mail could not be sent.");
            }

            return new SendOutcome(recipient, parcel, null);
        });

        if (outcome.Error != null || outcome.Recipient == null || outcome.Parcel == null)
        {
            await Fail(session, outcome.Error ?? string.Empty);
            return;
        }

        foreach (var slot in outcome.Parcel.Taken.Keys)
        {
            var entry = session.Inventory[slot];
            await userNotificationService.SendStackChangeAsync(session, (byte)slot, entry.ItemId, entry.Count, entry.Durability);
        }

        if (outcome.Parcel.Gold > 0)
            await userNotificationService.SendGoldLossAsync(session, outcome.Parcel.Gold);
        if (outcome.Parcel.Taken.Count > 0)
            await userNotificationService.SendWeightChangeAsync(session);

        logger.LogInformation("{Sender} mailed {Recipient}: '{Subject}' with {Attachments} attachments",
            session.Name, outcome.Recipient.Name, subject, outcome.Parcel.Drafts.Count);
        await session.Client.SendPacket(MailPacketWriter.SendResult(true, $"Mail sent to {outcome.Recipient.Name}."));

        var online = sessionManager.GetByCharacterId(outcome.Recipient.Id);
        if (online != null)
            await NotifyNewMailAsync(online, session.Name);
    }

    public async Task DeleteAsync(UserSession session, int mailId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mail = await InboxQuery(db, session.CharacterId).FirstOrDefaultAsync(m => m.Id == mailId);
        if (mail == null)
        {
            await session.Client.SendPacket(MailPacketWriter.DeleteResult(false, mailId, "That mail is gone."));
            return;
        }

        if (mail.HasUnclaimedAttachments)
        {
            await session.Client.SendPacket(MailPacketWriter.DeleteResult(false, mailId, "Claim the attachments before deleting this mail."));
            return;
        }

        mail.Deleted = true;
        await db.SaveChangesAsync();
        await session.Client.SendPacket(MailPacketWriter.DeleteResult(true, mailId, string.Empty));
    }

    public async Task ClaimAsync(UserSession session, int mailId)
    {
        if (session.Trade.LocksInventory)
        {
            await session.Client.SendPacket(MailPacketWriter.ClaimResult(false, mailId, "You cannot claim attachments while trading."));
            return;
        }

        var outcome = await characterStatePersister.RunAsync(session, ClaimOutcome.Refused("You cannot claim attachments right now."), async unit =>
        {
            var mail = await InboxQuery(unit.Db, session.CharacterId).FirstOrDefaultAsync(m => m.Id == mailId);
            if (mail == null || !mail.HasUnclaimedAttachments)
                return ClaimOutcome.Refused("Nothing to claim.");

            var delivery = session.WithLock(s => Deliver(s, mail.Attachments));
            if (delivery.Error != null)
                return ClaimOutcome.Refused(delivery.Error);

            mail.ClaimedAt = DateTime.UtcNow;
            mail.ReadAt ??= mail.ClaimedAt;
            try
            {
                await unit.CommitAsync();
            }
            catch (Exception ex)
            {
                session.WithLock(delivery.Undo);
                logger.LogWarning(ex, "{Name} could not claim mail {MailId}", session.Name, mailId);
                return ClaimOutcome.Refused("The attachments could not be claimed.");
            }

            return new ClaimOutcome(delivery, [.. mail.Attachments], null);
        });

        if (outcome.Error != null || outcome.Delivery == null)
        {
            await session.Client.SendPacket(MailPacketWriter.ClaimResult(false, mailId, outcome.Error ?? string.Empty));
            return;
        }

        await NotifyDeliveryAsync(session, outcome.Delivery);
        if (await AwardProgressAsync(session, outcome.Attachments))
            _ = characterStatePersister.RequestSaveAsync(session);

        await session.Client.SendPacket(MailPacketWriter.ClaimResult(true, mailId, "Attachments claimed."));
    }

    private Parcel TakeParcel(UserSession session, int gold, IReadOnlyList<MailItemPick> items)
    {
        if (gold < 0 || gold > session.Money)
            return Parcel.Refused("You do not carry that much gold.");

        var picks = new List<(int Slot, ushort Count, ItemData Item)>();
        var seenSlots = new HashSet<int>();
        foreach (var pick in items)
        {
            var slot = pick.Slot;
            if (slot < InventoryConstants.InventoryStart || slot >= InventoryConstants.InventoryStart + InventoryConstants.HaveMax || !seenSlots.Add(slot))
                return Parcel.Refused("That item cannot be attached.");

            var entry = session.Inventory[slot];
            var itemData = entry.IsEmpty ? null : gameDataService.GetItem(entry.ItemId);
            if (itemData == null || pick.Count == 0 || pick.Count > entry.Count)
                return Parcel.Refused("That item cannot be attached.");

            if (!ItemTransfer.CanLeaveOwner(entry, itemData))
                return Parcel.Refused($"{itemData.Name} cannot be traded, so it cannot be mailed.");

            picks.Add((slot, pick.Count, itemData));
        }

        var parcel = new Parcel(gold);
        foreach (var (slot, count, itemData) in picks)
        {
            var entry = session.Inventory[slot];
            parcel.Taken[slot] = ItemSlotState.Of(entry);
            var durability = entry.Durability;
            entry.Count = (ushort)(entry.Count - count);
            if (entry.Count == 0)
                entry.Clear();
            parcel.Drafts.Add(new MailAttachmentDraft(MailAttachmentKind.Item, itemData.Num, count, entry.IsEmpty ? durability : itemData.Duration));
        }

        if (gold > 0)
        {
            session.Money -= gold;
            parcel.Drafts.Add(new MailAttachmentDraft(MailAttachmentKind.Gold, InventoryConstants.ItemGold, gold));
        }

        if (picks.Count > 0)
            session.RecalculateStatsWithBuffs(gameDataService);

        return parcel;
    }

    private Delivery Deliver(UserSession session, IReadOnlyList<MailAttachment> attachments)
    {
        var delivery = new Delivery(gameDataService);
        var gold = attachments.Where(a => a.Kind == MailAttachmentKind.Gold).Sum(a => (long)a.Count);
        if (session.Money + gold > ExchangePacketConstants.CoinMax)
            return delivery.Refuse("You cannot carry that much gold.");

        foreach (var attachment in attachments.Where(a => a.Kind == MailAttachmentKind.Item))
        {
            if (Place(session, attachment, delivery))
                continue;

            delivery.Undo(session);
            return delivery.Refuse("Not enough room in your inventory.");
        }

        delivery.Gold = (int)gold;
        session.Money += delivery.Gold;
        if (delivery.Before.Count > 0)
            session.RecalculateStatsWithBuffs(gameDataService);
        return delivery;
    }

    private bool Place(UserSession session, MailAttachment attachment, Delivery delivery)
    {
        var itemData = gameDataService.GetItem(attachment.ItemId);
        if (itemData == null)
            return true;

        var remaining = attachment.Count;
        while (remaining > 0)
        {
            var portion = itemData.Countable == 0 ? 1 : Math.Min(remaining, InventoryConstants.MaxStackCount);
            var slot = session.FindSlotForItem(attachment.ItemId, gameDataService, (ushort)portion);
            if (slot < 0)
                return false;

            var entry = session.Inventory[slot];
            delivery.Before.TryAdd(slot, ItemSlotState.Of(entry));
            var isNew = entry.IsEmpty;
            entry.ItemId = attachment.ItemId;
            entry.Count = (ushort)(entry.Count + portion);
            if (isNew)
                entry.Durability = attachment.Durability > 0 ? attachment.Durability : itemData.Duration;
            remaining -= portion;
        }

        return true;
    }

    private async Task NotifyDeliveryAsync(UserSession session, Delivery delivery)
    {
        foreach (var (slot, before) in delivery.Before)
        {
            var entry = session.Inventory[slot];
            await userNotificationService.SendStackChangeAsync(session, (byte)slot, entry.ItemId, entry.Count, entry.Durability, before.ItemId == 0);
        }

        if (delivery.Gold > 0)
            await userNotificationService.SendGoldGainAsync(session, delivery.Gold);
        if (delivery.Before.Count > 0)
            await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task<bool> AwardProgressAsync(UserSession session, IReadOnlyList<MailAttachment> attachments)
    {
        var awarded = false;
        foreach (var attachment in attachments)
        {
            switch (attachment.Kind)
            {
                case MailAttachmentKind.Experience:
                    await playerProgressionService.AwardExperienceAsync(session, attachment.Count);
                    awarded = true;
                    break;
                case MailAttachmentKind.NationalPoints:
                    await loyaltyService.ChangeAsync(session, attachment.Count);
                    awarded = true;
                    break;
            }
        }

        return awarded;
    }

    private async Task NotifyNewMailAsync(UserSession recipient, string senderName)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await recipient.Client.SendPacket(MailPacketWriter.Unread(await UnreadCountAsync(db, recipient.CharacterId)));
        await recipient.Client.SendPacket(NoticePacketWriter.Broadcast($"You have new mail from {senderName}."));
    }

    private static Task<int> UnreadCountAsync(AppDbContext db, int characterId) =>
        db.Mails.CountAsync(m => m.RecipientCharacterId == characterId && !m.Deleted && m.ReadAt == null);

    private static IQueryable<Mail> InboxQuery(AppDbContext db, int characterId) =>
        db.Mails.Include(m => m.Attachments).Where(m => m.RecipientCharacterId == characterId && !m.Deleted);

    private static Mail NewMail(int? senderCharacterId, string senderName, int recipientCharacterId, string subject, string body, IReadOnlyList<MailAttachmentDraft> attachments) =>
        new()
        {
            SenderCharacterId = senderCharacterId,
            SenderName = Truncate(senderName, MailLimits.SenderNameMax),
            RecipientCharacterId = recipientCharacterId,
            Subject = Truncate(subject, MailLimits.SubjectMax),
            Body = Truncate(body, MailLimits.BodyMax),
            SentAt = DateTime.UtcNow,
            Attachments = attachments
                .Select(a => new MailAttachment { Kind = a.Kind, ItemId = a.ItemId, Count = a.Count, Durability = a.Durability })
                .ToList(),
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static Task Fail(UserSession session, string message) =>
        session.Client.SendPacket(MailPacketWriter.SendResult(false, message));

    private sealed record MailRecipient(int Id, string Name);

    private sealed record SendOutcome(MailRecipient? Recipient, Parcel? Parcel, string? Error)
    {
        public static SendOutcome Refused(string error) => new(null, null, error);
    }

    private sealed record ClaimOutcome(Delivery? Delivery, IReadOnlyList<MailAttachment> Attachments, string? Error)
    {
        public static ClaimOutcome Refused(string error) => new(null, [], error);
    }

    private sealed class Parcel(int gold)
    {
        public int Gold { get; } = gold;
        public Dictionary<int, ItemSlotState> Taken { get; } = [];
        public List<MailAttachmentDraft> Drafts { get; } = [];
        public string? Error { get; private init; }

        public static Parcel Refused(string error) => new(0) { Error = error };

        public void Restore(UserSession session)
        {
            foreach (var (slot, before) in Taken)
                before.RestoreTo(session.Inventory[slot]);
            session.Money += Gold;
        }
    }

    private sealed class Delivery(IGameDataService gameData)
    {
        public Dictionary<int, ItemSlotState> Before { get; } = [];
        public int Gold { get; set; }
        public string? Error { get; private set; }

        public Delivery Refuse(string error)
        {
            Error = error;
            return this;
        }

        public void Undo(UserSession session)
        {
            foreach (var (slot, before) in Before)
                before.RestoreTo(session.Inventory[slot]);
            session.Money -= Gold;
            Gold = 0;
            if (Before.Count > 0)
                session.RecalculateStatsWithBuffs(gameData);
            Before.Clear();
        }
    }
}
