using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IShoppingMallLetterMutationService
{
    Task HandleSendAsync(UserSession session, Packet packet);
    Task HandleDeleteAsync(UserSession session, Packet packet);
    Task HandleGetItemAsync(UserSession session, Packet packet);
}

public class ShoppingMallLetterMutationService(
    SessionManager sessionManager,
    IServiceScopeFactory scopeFactory,
    ICharacterStatePersister characterStatePersister,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    ILogger<ShoppingMallLetterMutationService> logger) : IShoppingMallLetterMutationService
{
    private const byte Malformed = 0;
    private const byte Succeeded = 1;
    private const byte Rejected = unchecked((byte)-1);
    private const byte GetItemNoLetter = unchecked((byte)-2);
    private const byte DeleteTooMany = unchecked((byte)-3);
    private const byte SendToSelf = unchecked((byte)-6);
    private const byte SendItemNotMailable = unchecked((byte)-32);

    public async Task HandleSendAsync(UserSession session, Packet packet)
    {

        if (packet.RemainingBytes < 3)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Malformed));
            return;
        }

        var recipientName = packet.ReadSByteString();
        var subject = packet.ReadSByteString();
        if (packet.RemainingBytes < 1)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Malformed));
            return;
        }

        var letterType = packet.ReadByte();
        if (letterType is not (ShoppingMallLetterProtocol.LetterTypeText or ShoppingMallLetterProtocol.LetterTypeItem))
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Rejected));
            return;
        }

        var itemId = 0;
        var cost = ShoppingMallLetterProtocol.LetterSendCost;
        byte sourcePosition = 0;

        if (letterType == ShoppingMallLetterProtocol.LetterTypeItem)
        {
            if (packet.RemainingBytes < 9)
            {
                await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                    ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Malformed));
                return;
            }

            itemId = packet.ReadInt();
            sourcePosition = packet.ReadByte();
            _ = packet.ReadInt(); // Coins are part of the wire format but disabled for this protocol branch.
            cost = ShoppingMallLetterProtocol.LetterSendItemCost;
        }

        if (packet.RemainingBytes < 1)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Malformed));
            return;
        }

        var message = packet.ReadString();
        if (string.IsNullOrEmpty(recipientName)
            || string.IsNullOrEmpty(subject)
            || string.IsNullOrEmpty(message)
            || subject.Length > ShoppingMallLetterProtocol.MaxLetterSubject
            || message.Length > ShoppingMallLetterProtocol.MaxLetterMessage)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Rejected));
            return;
        }

        if (string.Equals(recipientName, session.Name, StringComparison.OrdinalIgnoreCase))
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, SendToSelf));
            return;
        }

        if (session.Trade.LocksInventory || session.Money < cost)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Rejected));
            return;
        }

        var postage = await characterStatePersister.RunAsync(session, Postage.Refused(Rejected), async unit =>
        {
            var recipientExists = await unit.Db.Characters
                .AnyAsync(character => character.Name == recipientName && character.DeletionTime == null);
            if (!recipientExists)
                return Postage.Refused(Rejected);

            var taken = session.WithLock(s => TakePostage(s, letterType, itemId, sourcePosition, cost));
            if (taken.Result != Succeeded)
                return taken;

            unit.Db.MailBoxes.Add(new MailBox
            {
                SendDate = DateTime.UtcNow,
                Status = ShoppingMallLetterProtocol.LetterStatusUnread,
                SenderId = session.Name,
                RecipientId = recipientName,
                Subject = subject,
                Message = message,
                Type = letterType,
                ItemId = taken.TookItem ? itemId : 0,
                Count = taken.TookItem ? (short)taken.Before.Count : (short)0,
                Durability = taken.TookItem ? taken.Before.Durability : (short)0,
                SerialNumber = 0,
                Coins = 0,
                Deleted = false
            });

            try
            {
                await unit.CommitAsync();
            }
            catch (Exception ex)
            {
                session.WithLock(s => taken.Undo(s, gameDataService));
                logger.LogWarning(ex, "{Name} could not send a letter to {Recipient}", session.Name, recipientName);
                return Postage.Refused(Rejected);
            }

            return taken;
        });

        if (postage.Result != Succeeded)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, postage.Result));
            return;
        }

        await userNotificationService.SendGoldLossAsync(session, postage.Cost);
        if (postage.TookItem)
        {
            await userNotificationService.SendStackChangeAsync(session, (byte)postage.SourceIndex, 0, 0, 0);
            await userNotificationService.SendWeightChangeAsync(session);
        }

        logger.LogInformation("{Name} sent mail to {Recipient} (type={LetterType})", session.Name, recipientName, letterType);

        await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
            ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterSend, Succeeded));

        var recipient = sessionManager.GetByName(recipientName);
        if (recipient != null)
            await userNotificationService.SendUnreadNotificationAsync(recipient);
    }

    public async Task HandleDeleteAsync(UserSession session, Packet packet)
    {
        if (packet.RemainingBytes < 1)
            return;

        var count = packet.ReadByte();
        if (count > ShoppingMallLetterProtocol.MaxDeleteCount)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterDelete, DeleteTooMany));
            return;
        }

        if (packet.RemainingBytes < count * 4)
            return;

        var letterIds = new int[count];
        for (var index = 0; index < count; index++)
            letterIds[index] = packet.ReadInt();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var letters = await db.MailBoxes
            .Where(mail => letterIds.Contains(mail.LetterId) && mail.RecipientId == session.Name && !mail.Deleted)
            .ToListAsync();

        foreach (var letter in letters)
            letter.Deleted = true;

        await db.SaveChangesAsync();

        await session.Client.SendPacket(ShoppingMallPacketWriter.DeletedLetters(
            ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterDelete,
            letters.Select(letter => letter.LetterId).ToList()));
    }

    public async Task HandleGetItemAsync(UserSession session, Packet packet)
    {
        if (packet.RemainingBytes < 4)
            return;

        var letterId = packet.ReadInt();
        if (session.Trade.LocksInventory)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterGetItem, Rejected));
            return;
        }

        var receipt = await characterStatePersister.RunAsync(session, Receipt.Refused(Rejected), async unit =>
        {
            var letter = await unit.Db.MailBoxes
                .OrderBy(mail => mail.LetterId)
                .FirstOrDefaultAsync(mail => mail.LetterId == letterId
                    && mail.RecipientId == session.Name
                    && mail.Status == ShoppingMallLetterProtocol.LetterStatusUnread
                    && mail.Type == ShoppingMallLetterProtocol.LetterTypeItem
                    && !mail.Deleted);
            if (letter == null)
                return Receipt.Refused(GetItemNoLetter);

            var received = session.WithLock(s => Receive(s, letter));
            if (received.Result != Succeeded)
                return received;

            letter.Status = ShoppingMallLetterProtocol.LetterStatusRead;
            try
            {
                await unit.CommitAsync();
            }
            catch (Exception ex)
            {
                session.WithLock(s => received.Undo(s, gameDataService));
                logger.LogWarning(ex, "{Name} could not take the contents of letter {LetterId}", session.Name, letterId);
                return Receipt.Refused(Rejected);
            }

            return received;
        });

        if (receipt.Result != Succeeded)
        {
            await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
                ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterGetItem, receipt.Result));
            return;
        }

        if (receipt.Slot >= 0)
        {
            var slotEntry = session.Inventory[receipt.Slot];
            await userNotificationService.SendStackChangeAsync(
                session,
                (byte)receipt.Slot,
                slotEntry.ItemId,
                slotEntry.Count,
                slotEntry.Durability,
                receipt.Before.ItemId == 0);
        }

        if (receipt.Coins > 0)
            await userNotificationService.SendGoldGainAsync(session, receipt.Coins);

        await session.Client.SendPacket(ShoppingMallPacketWriter.Result(
            ShoppingMallLetterProtocol.StoreLetter, ShoppingMallLetterProtocol.LetterGetItem, Succeeded));
    }

    private Postage TakePostage(UserSession session, byte letterType, int itemId, byte sourcePosition, int cost)
    {
        if (session.Money < cost)
            return Postage.Refused(Rejected);

        if (letterType != ShoppingMallLetterProtocol.LetterTypeItem)
        {
            session.Money -= cost;
            return new Postage(Succeeded, cost, -1, default);
        }

        var sourceIndex = InventoryConstants.InventoryStart + sourcePosition;
        if (sourcePosition >= InventoryConstants.HaveMax)
            return Postage.Refused(Rejected);

        var itemSlot = session.Inventory[sourceIndex];
        if (itemSlot.IsEmpty || itemSlot.ItemId != itemId)
            return Postage.Refused(Rejected);

        if (!ItemTransfer.CanLeaveOwner(itemSlot, gameDataService.GetItem(itemId)))
            return Postage.Refused(SendItemNotMailable);

        var before = ItemSlotState.Of(itemSlot);
        session.Money -= cost;
        itemSlot.Clear();
        session.RecalculateStatsWithBuffs(gameDataService);
        return new Postage(Succeeded, cost, sourceIndex, before);
    }

    private Receipt Receive(UserSession session, MailBox letter)
    {
        if (letter.Coins < 0 || (long)session.Money + letter.Coins > ExchangePacketConstants.CoinMax)
            return Receipt.Refused(Rejected);

        var slot = -1;
        ItemSlotState before = default;
        if (letter.ItemId > 0)
        {
            slot = session.FindSlotForItem(letter.ItemId, gameDataService, (ushort)letter.Count);
            var itemData = gameDataService.GetItem(letter.ItemId);
            if (slot < 0 || itemData == null || !CanReceiveItem(session, itemData, letter.Count))
                return Receipt.Refused(Rejected);

            var slotEntry = session.Inventory[slot];
            before = ItemSlotState.Of(slotEntry);
            var isNewItem = slotEntry.IsEmpty;
            slotEntry.ItemId = letter.ItemId;
            slotEntry.Count += (ushort)letter.Count;
            slotEntry.Durability = isNewItem
                ? letter.Durability
                : (short)(slotEntry.Durability + letter.Durability);
            session.RecalculateStatsWithBuffs(gameDataService);
        }

        session.Money += letter.Coins;
        return new Receipt(Succeeded, slot, before, letter.Coins);
    }

    private static bool CanReceiveItem(UserSession session, ItemData itemData, short count)
    {
        if (count <= 0)
            return false;

        var totalWeight = itemData.Weight * count;
        return session.Stats.ItemWeight + totalWeight <= session.Stats.MaxWeight;
    }

    private readonly record struct Postage(byte Result, int Cost, int SourceIndex, ItemSlotState Before)
    {
        public bool TookItem => SourceIndex >= 0;

        public static Postage Refused(byte result) => new(result, 0, -1, default);

        public void Undo(UserSession session, IGameDataService gameData)
        {
            session.Money += Cost;
            if (!TookItem)
                return;

            Before.RestoreTo(session.Inventory[SourceIndex]);
            session.RecalculateStatsWithBuffs(gameData);
        }
    }

    private readonly record struct Receipt(byte Result, int Slot, ItemSlotState Before, int Coins)
    {
        public static Receipt Refused(byte result) => new(result, -1, default, 0);

        public void Undo(UserSession session, IGameDataService gameData)
        {
            session.Money -= Coins;
            if (Slot < 0)
                return;

            Before.RestoreTo(session.Inventory[Slot]);
            session.RecalculateStatsWithBuffs(gameData);
        }
    }
}
