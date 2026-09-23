using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IVipWarehousePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class VipWarehousePacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    ICharacterStatePersister characterStatePersister,
    TimeProvider timeProvider,
    ILogger<VipWarehousePacketCoordinator> logger) : IVipWarehousePacketCoordinator
{


    private const int VipVaultKey = 800_442_000;
    private const int VipSafeKey1 = 810_442_000;
    private const int VipSafeKey7 = 998_019_000;

    // Vault extension durations.
    private const int VaultDurationDaysDefault = 7;
    private const int VaultDurationDaysSafe1 = 1;
    private const int VaultDurationDaysSafe7 = 7;
    private const int VipWarehousePageSize = 12;
    private const int ItemNoTradeMin = 900_000_001;
    private const int ItemNoTradeMax = 999_999_999;
    private const int PinLength = 4;
    private const int PinGuessLimit = 5;
    private const int PinUnlockMinutes = 10;

    private static readonly TimeSpan PinUnlockLifetime = TimeSpan.FromMinutes(PinUnlockMinutes);

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1) return;

        var sub = (VipWarehouseSubOpcode)packet.ReadByte();

        if (session.Hp <= 0 || session.Trade.LocksInventory || session.IsGathering
            || !NpcDialogContext.IsTalkingTo(sessionManager, session, NpcData.TypeWarehouse))
        {
            await SendResult(session, sub, VipWarehouseResult.Failed);
            return;
        }

        switch (sub)
        {
            case VipWarehouseSubOpcode.Open:
                await OpenAsync(session);
                return;
            case VipWarehouseSubOpcode.EnterPassword:
                await EnterPasswordAsync(session, packet);
                return;
        }

        if (!IsUnlocked(session))
        {
            await SendResult(session, sub, VipWarehouseResult.Failed);
            await PromptForPinAsync(session);
            return;
        }

        KeepUnlocked(session);
        switch (sub)
        {
            case VipWarehouseSubOpcode.Input: await InputAsync(session, packet); break;
            case VipWarehouseSubOpcode.Output: await OutputAsync(session, packet); break;
            case VipWarehouseSubOpcode.Store: await MoveAsync(session, packet); break;
            case VipWarehouseSubOpcode.InventoryMove: await InventoryMoveAsync(session, packet); break;
            case VipWarehouseSubOpcode.UseVault: await UseVaultAsync(session, packet); break;
            case VipWarehouseSubOpcode.SetPassword: await SetPasswordAsync(session, packet); break;
            case VipWarehouseSubOpcode.CancelPassword: await CancelPasswordAsync(session); break;
            case VipWarehouseSubOpcode.ChangePassword: await ChangePasswordAsync(session, packet); break;
            default:
                logger.LogDebug("Unknown VIP warehouse sub-opcode {Sub:X2}", sub);
                break;
        }
    }

    private async Task OpenAsync(UserSession session)
    {
        if (session.VipVaultExpiry <= DateTime.UtcNow)
        {
            await SendResult(session, VipWarehouseSubOpcode.Open, VipWarehouseResult.Expired);
            return;
        }

        if (!IsUnlocked(session))
        {
            await PromptForPinAsync(session);
            return;
        }

        KeepUnlocked(session);
        await SendOpenResponseAsync(session);
    }

    private static async Task SendOpenResponseAsync(UserSession session)
    {
        // Remaining seconds — client uses this for "expires in X" display.
        var remaining = (long)Math.Max(0, (session.VipVaultExpiry - DateTime.UtcNow).TotalSeconds);

        var slots = session.WithLock(s =>
        {
            var copies = new List<ItemSlot>(UserSession.VipWarehouseMax);
            for (var i = 0; i < UserSession.VipWarehouseMax; i++)
            {
                var copy = new ItemSlot();
                ItemSlotState.Of(s.VipWarehouse[i]).RestoreTo(copy);
                copies.Add(copy);
            }
            return copies;
        });

        await session.Client.SendPacket(WarehousePacketWriter.VipContents(
            VipWarehouseSubOpcode.Open, VipWarehouseResult.Succeeded, (int)Math.Min(remaining, int.MaxValue), slots));
    }

    private async Task InputAsync(UserSession session, Packet packet)
    {
        if (session.VipVaultExpiry <= DateTime.UtcNow)
        {
            await SendResult(session, VipWarehouseSubOpcode.Input, VipWarehouseResult.Expired);
            return;
        }

        _ = packet.ReadInt(); // npc id (ignored)
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null
            || srcPos >= InventoryConstants.HaveMax
            || dstPos >= VipWarehousePageSize
            || (itemId >= ItemNoTradeMin && itemId <= ItemNoTradeMax)
            || count <= 0
            || count > InventoryConstants.MaxStackCount
            || (itemData.Countable == 0 && count != 1))
        {
            await SendResult(session, VipWarehouseSubOpcode.Input, VipWarehouseResult.Failed);
            return;
        }

        var absSrc = InventoryConstants.SlotMax + srcPos;
        var realDst = page * VipWarehousePageSize + dstPos;
        if (realDst >= UserSession.VipWarehouseMax)
        {
            await SendResult(session, VipWarehouseSubOpcode.Input, VipWarehouseResult.Failed);
            return;
        }

        var success = await CommitAsync(session, s =>
        {
            var source = s.Inventory[absSrc];
            var destination = s.VipWarehouse[realDst];
            if (source.ItemId != itemId || source.Count < count || !VaultTransfer.Fits(itemData, source, destination, count))
                return null;

            var change = new VaultChange().Touch(source).Touch(destination);
            VaultTransfer.Move(source, destination, count);
            s.RecalculateStatsWithBuffs(gameDataService);
            return change;
        });

        if (!success)
        {
            await SendResult(session, VipWarehouseSubOpcode.Input, VipWarehouseResult.Failed);
            return;
        }

        await SendResult(session, VipWarehouseSubOpcode.Input, VipWarehouseResult.Succeeded);
        await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task OutputAsync(UserSession session, Packet packet)
    {
        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        if (srcPos >= VipWarehousePageSize || dstPos >= InventoryConstants.HaveMax || count <= 0)
        {
            await SendResult(session, VipWarehouseSubOpcode.Output, VipWarehouseResult.Failed);
            return;
        }

        var realSrc = page * VipWarehousePageSize + srcPos;
        if (realSrc >= UserSession.VipWarehouseMax)
        {
            await SendResult(session, VipWarehouseSubOpcode.Output, VipWarehouseResult.Failed);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null)
        {
            await SendResult(session, VipWarehouseSubOpcode.Output, VipWarehouseResult.Failed);
            return;
        }

        var absDst = InventoryConstants.SlotMax + dstPos;

        var success = await CommitAsync(session, s =>
        {
            var source = s.VipWarehouse[realSrc];
            var destination = s.Inventory[absDst];
            if (source.ItemId != itemId || source.Count < count
                || (itemData.Countable == 0 && count != 1)
                || !VaultTransfer.Fits(itemData, source, destination, count))
                return null;

            var change = new VaultChange().Touch(source).Touch(destination);
            VaultTransfer.Move(source, destination, count);
            s.RecalculateStatsWithBuffs(gameDataService);
            return change;
        });

        if (!success)
        {
            await SendResult(session, VipWarehouseSubOpcode.Output, VipWarehouseResult.Failed);
            return;
        }

        await SendResult(session, VipWarehouseSubOpcode.Output, VipWarehouseResult.Succeeded);
        await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task MoveAsync(UserSession session, Packet packet)
    {
        if (session.VipVaultExpiry <= DateTime.UtcNow)
        {
            await SendResult(session, VipWarehouseSubOpcode.Store, VipWarehouseResult.Expired);
            return;
        }

        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        if (srcPos >= VipWarehousePageSize || dstPos >= VipWarehousePageSize)
        {
            await SendResult(session, VipWarehouseSubOpcode.Store, VipWarehouseResult.Failed);
            return;
        }

        var realSrc = page * VipWarehousePageSize + srcPos;
        var realDst = page * VipWarehousePageSize + dstPos;
        if (realSrc >= UserSession.VipWarehouseMax || realDst >= UserSession.VipWarehouseMax)
        {
            await SendResult(session, VipWarehouseSubOpcode.Store, VipWarehouseResult.Failed);
            return;
        }

        var moved = await CommitAsync(session, s =>
        {
            var source = s.VipWarehouse[realSrc];
            var destination = s.VipWarehouse[realDst];
            if (source.ItemId != itemId || source.IsEmpty || !destination.IsEmpty)
                return null;

            var change = new VaultChange().Touch(source).Touch(destination);
            ItemSlotState.Of(source).RestoreTo(destination);
            source.Clear();
            return change;
        });

        await SendResult(session, VipWarehouseSubOpcode.Store, moved ? VipWarehouseResult.Succeeded : VipWarehouseResult.Failed);
    }

    private async Task InventoryMoveAsync(UserSession session, Packet packet)
    {
        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        _ = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        if (srcPos >= InventoryConstants.HaveMax || dstPos >= InventoryConstants.HaveMax)
        {
            await SendResult(session, VipWarehouseSubOpcode.InventoryMove, VipWarehouseResult.Failed);
            return;
        }

        var absSrc = InventoryConstants.SlotMax + srcPos;
        var absDst = InventoryConstants.SlotMax + dstPos;

        var moved = session.WithLock(s =>
        {
            var source = s.Inventory[absSrc];
            var destination = s.Inventory[absDst];
            if (source.ItemId != itemId || source.IsEmpty || !destination.IsEmpty)
                return false;

            ItemSlotState.Of(source).RestoreTo(destination);
            source.Clear();
            return true;
        });

        await SendResult(session, VipWarehouseSubOpcode.InventoryMove, moved ? VipWarehouseResult.Succeeded : VipWarehouseResult.Failed);
    }

    private async Task UseVaultAsync(UserSession session, Packet packet)
    {
        if (packet.RemainingBytes < 4)
        {
            await SendResult(session, VipWarehouseSubOpcode.UseVault, VipWarehouseResult.Failed);
            return;
        }

        var itemId = packet.ReadInt();
        var days = itemId switch
        {
            VipVaultKey => VaultDurationDaysDefault,
            VipSafeKey1 => VaultDurationDaysSafe1,
            VipSafeKey7 => VaultDurationDaysSafe7,
            _ => 0
        };
        if (days <= 0)
        {
            await SendResult(session, VipWarehouseSubOpcode.UseVault, VipWarehouseResult.Failed);
            return;
        }

        var extended = await CommitAsync(session, s =>
        {
            var keySlot = FindKeyItemSlot(s, itemId);
            if (keySlot < 0)
                return null;

            var slot = s.Inventory[keySlot];
            var change = new VaultChange { VaultExpiryBefore = s.VipVaultExpiry }.Touch(slot);
            var basis = s.VipVaultExpiry > DateTime.UtcNow ? s.VipVaultExpiry : DateTime.UtcNow;
            s.VipVaultExpiry = basis.AddDays(days);

            slot.Count--;
            if (slot.Count == 0) slot.Clear();
            return change;
        });

        if (!extended)
        {
            await SendResult(session, VipWarehouseSubOpcode.UseVault, VipWarehouseResult.Failed);
            return;
        }

        var newExpiry = session.VipVaultExpiry;
        await session.Client.SendPacket(WarehousePacketWriter.VipVaultExtended(
            VipWarehouseSubOpcode.UseVault, VipWarehouseResult.Succeeded, (int)(newExpiry - DateTime.UtcNow).TotalSeconds));

        logger.LogInformation("{Name} activated VIP vault with key {ItemId}: expires {Expiry:u}",
            session.Name, itemId, newExpiry);
    }

    private static int FindKeyItemSlot(UserSession session, int itemId)
    {
        for (var i = InventoryConstants.InventoryStart;
             i < InventoryConstants.InventoryStart + InventoryConstants.HaveMax;
             i++)
        {
            if (session.Inventory[i].ItemId == itemId && session.Inventory[i].Count > 0)
                return i;
        }
        return -1;
    }

    private static async Task SendResult(UserSession session, VipWarehouseSubOpcode sub, VipWarehouseResult result)
    {
        await session.Client.SendPacket(WarehousePacketWriter.VipResult(sub, result));
    }

    private static Task PromptForPinAsync(UserSession session) =>
        SendResult(session, VipWarehouseSubOpcode.EnterPassword, VipWarehouseResult.Succeeded);

    private async Task SetPasswordAsync(UserSession session, Packet packet)
    {
        var pin = ReadPin(packet);
        if (!IsValidPin(pin))
        {
            await SendResult(session, VipWarehouseSubOpcode.SetPassword, VipWarehouseResult.Rejected);
            return;
        }

        var stored = await ReplacePinAsync(session, pin);
        await SendResult(session, VipWarehouseSubOpcode.SetPassword, stored ? VipWarehouseResult.Succeeded : VipWarehouseResult.Failed);
    }

    private async Task CancelPasswordAsync(UserSession session)
    {
        var stored = await ReplacePinAsync(session, string.Empty);
        await SendResult(session, VipWarehouseSubOpcode.CancelPassword, stored ? VipWarehouseResult.Succeeded : VipWarehouseResult.Failed);
    }

    private async Task ChangePasswordAsync(UserSession session, Packet packet)
    {
        var pin = ReadPin(packet);
        if (!IsValidPin(pin))
        {
            await SendResult(session, VipWarehouseSubOpcode.ChangePassword, VipWarehouseResult.Rejected);
            return;
        }

        var stored = await ReplacePinAsync(session, pin);
        await SendResult(session, VipWarehouseSubOpcode.ChangePassword, stored ? VipWarehouseResult.Succeeded : VipWarehouseResult.Failed);
    }

    private async Task EnterPasswordAsync(UserSession session, Packet packet)
    {
        var pin = ReadPin(packet);
        if (session.VipPinFailures >= PinGuessLimit || !IsValidPin(pin) || pin != session.VipPassword)
        {
            if (session.VipPinFailures < PinGuessLimit)
                session.VipPinFailures++;
            await SendResult(session, VipWarehouseSubOpcode.EnterPassword, VipWarehouseResult.Rejected);
            return;
        }

        session.VipPinFailures = 0;
        KeepUnlocked(session);

        await session.Client.SendPacket(WarehousePacketWriter.VipPasswordAccepted(
            VipWarehouseSubOpcode.EnterPassword, VipWarehouseResult.Succeeded));

        if (session.VipVaultExpiry > DateTime.UtcNow)
            await SendOpenResponseAsync(session);
    }

    private Task<bool> ReplacePinAsync(UserSession session, string pin) =>
        CommitAsync(session, s =>
        {
            var change = new VaultChange { PinBefore = s.VipPassword };
            s.VipPassword = pin;
            return change;
        });

    private bool IsUnlocked(UserSession session) =>
        session.VipPassword.Length != PinLength || timeProvider.GetUtcNow() < session.VipUnlockedUntil;

    private void KeepUnlocked(UserSession session) =>
        session.VipUnlockedUntil = timeProvider.GetUtcNow() + PinUnlockLifetime;

    private static string ReadPin(Packet packet)
    {
        // PINs are sent as sbyte-prefixed strings on the wire.
        try { return packet.ReadSByteString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool IsValidPin(string pin)
    {
        if (pin.Length != PinLength) return false;
        for (var i = 0; i < pin.Length; i++)
            if (pin[i] < '0' || pin[i] > '9') return false;
        return true;
    }

    private Task<bool> CommitAsync(UserSession session, Func<UserSession, VaultChange?> mutate) =>
        characterStatePersister.RunAsync(session, false, async unit =>
        {
            var change = session.WithLock(mutate);
            if (change == null)
                return false;

            try
            {
                await unit.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                session.WithLock(s => change.Undo(s, gameDataService));
                logger.LogWarning(ex, "VIP vault change by {Name} could not be stored", session.Name);
                return false;
            }
        });

    private sealed class VaultChange
    {
        private readonly List<(ItemSlot Slot, ItemSlotState Before)> _touched = [];

        public DateTime? VaultExpiryBefore { get; init; }
        public string? PinBefore { get; init; }

        public VaultChange Touch(ItemSlot slot)
        {
            _touched.Add((slot, ItemSlotState.Of(slot)));
            return this;
        }

        public void Undo(UserSession session, IGameDataService gameData)
        {
            foreach (var (slot, before) in _touched)
                before.RestoreTo(slot);
            if (VaultExpiryBefore is { } expiry)
                session.VipVaultExpiry = expiry;
            if (PinBefore != null)
                session.VipPassword = PinBefore;
            session.RecalculateStatsWithBuffs(gameData);
        }
    }
}
