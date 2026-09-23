using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IClanWarehousePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class ClanWarehousePacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IUserNotificationService userNotificationService,
    IKnightsRuntimeService knightsRuntime,
    ICharacterStatePersister characterStatePersister,
    ILogger<ClanWarehousePacketCoordinator> logger) : IClanWarehousePacketCoordinator
{
    private const int WarehousePageSize = 24;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1) return;

        var sub = (WarehouseSubOpcode)packet.ReadByte();

        if (session.KnightsId <= 0
            || sessionManager.Knights.GetClan(session.KnightsId) == null
            || session.Trade.LocksInventory
            || !NpcDialogContext.IsTalkingTo(sessionManager, session, NpcData.TypeWarehouse))
        {
            await SendResult(session, sub, ClanWarehouseResult.Failed);
            return;
        }

        switch (sub)
        {
            case WarehouseSubOpcode.Open: await OpenAsync(session); break;
            case WarehouseSubOpcode.Input: await InputAsync(session, packet); break;
            case WarehouseSubOpcode.Output: await OutputAsync(session, packet); break;
            case WarehouseSubOpcode.Move: await MoveAsync(session, packet); break;
            case WarehouseSubOpcode.InventoryMove: await InventoryMoveAsync(session, packet); break;
            default:
                logger.LogDebug("Unknown clan warehouse sub-opcode {Sub:X2}", sub);
                break;
        }
    }

    private async Task OpenAsync(UserSession session)
    {
        var view = sessionManager.Knights.WithClanWarehouse<ClanVaultView?>(session.KnightsId,
            (clan, slots) => new ClanVaultView(clan.ClanWarehouseGold, [.. slots.Select(Copy)]), null);
        if (view == null)
        {
            await SendResult(session, WarehouseSubOpcode.Open, ClanWarehouseResult.Failed);
            return;
        }

        await session.Client.SendPacket(ClanWarehousePacketWriter.Contents(
            WarehouseSubOpcode.Open, ClanWarehouseResult.Succeeded, view.Gold, view.Slots));
    }

    private async Task InputAsync(UserSession session, Packet packet)
    {
        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        if (itemId == InventoryConstants.ItemGold)
        {
            var goldOk = await CommitAsync(session, (clan, _, s) =>
            {
                if (count <= 0 || count > s.Money || clan.ClanWarehouseGold > ExchangePacketConstants.CoinMax - count)
                    return null;
                s.Money -= count;
                clan.ClanWarehouseGold += count;
                return new ClanChange { ClanGoldDelta = count, MoneyDelta = -count };
            });

            await SendResult(session, WarehouseSubOpcode.Input, goldOk ? ClanWarehouseResult.Succeeded : ClanWarehouseResult.Failed);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null
            || srcPos >= InventoryConstants.HaveMax
            || dstPos >= WarehousePageSize
            || count <= 0
            || count > InventoryConstants.MaxStackCount
            || (itemData.Countable == 0 && count != 1))
        {
            await SendResult(session, WarehouseSubOpcode.Input, ClanWarehouseResult.Failed);
            return;
        }

        var absSrc = InventoryConstants.SlotMax + srcPos;
        var realDst = page * WarehousePageSize + dstPos;
        if (realDst >= KnightsManager.ClanWarehouseSlots)
        {
            await SendResult(session, WarehouseSubOpcode.Input, ClanWarehouseResult.Failed);
            return;
        }

        var success = await CommitAsync(session, (_, slots, s) =>
        {
            var source = s.Inventory[absSrc];
            var destination = slots[realDst];
            if (source.ItemId != itemId
                || source.Count < count
                || !ItemTransfer.CanLeaveOwner(source, itemData)
                || !VaultTransfer.Fits(itemData, source, destination, count))
                return null;

            var change = new ClanChange().Touch(source).Touch(destination);
            VaultTransfer.Move(source, destination, count);
            s.RecalculateStatsWithBuffs(gameDataService);
            return change;
        });

        if (!success)
        {
            await SendResult(session, WarehouseSubOpcode.Input, ClanWarehouseResult.Failed);
            return;
        }

        await BroadcastDepositAsync(session, session.KnightsId, itemId, count, isDeposit: true);
        await SendResult(session, WarehouseSubOpcode.Input, ClanWarehouseResult.Succeeded);
        await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task OutputAsync(UserSession session, Packet packet)
    {
        if (!IsLeaderOrAssistant(session))
        {
            packet.ReadInt(); packet.ReadInt(); packet.ReadByte(); packet.ReadByte(); packet.ReadByte(); packet.ReadInt();
            await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Failed);
            return;
        }

        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();
        var count = packet.ReadInt();

        if (itemId == InventoryConstants.ItemGold)
        {
            var goldOk = await CommitAsync(session, (clan, _, s) =>
            {
                if (count <= 0 || count > clan.ClanWarehouseGold || s.Money > ExchangePacketConstants.CoinMax - count)
                    return null;
                clan.ClanWarehouseGold -= count;
                s.Money += count;
                return new ClanChange { ClanGoldDelta = -count, MoneyDelta = count };
            });

            await SendResult(session, WarehouseSubOpcode.Output, goldOk ? ClanWarehouseResult.Succeeded : ClanWarehouseResult.Failed);
            return;
        }

        if (srcPos >= WarehousePageSize || dstPos >= InventoryConstants.HaveMax || count <= 0)
        {
            await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Failed);
            return;
        }

        var realSrc = page * WarehousePageSize + srcPos;
        if (realSrc >= KnightsManager.ClanWarehouseSlots)
        {
            await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Failed);
            return;
        }

        var itemData = gameDataService.GetItem(itemId);
        if (itemData == null)
        {
            await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Failed);
            return;
        }

        var absDst = InventoryConstants.SlotMax + dstPos;

        var success = await CommitAsync(session, (_, slots, s) =>
        {
            var source = slots[realSrc];
            var destination = s.Inventory[absDst];
            if (source.ItemId != itemId
                || source.Count < count
                || (itemData.Countable == 0 && count != 1)
                || !VaultTransfer.Fits(itemData, source, destination, count))
                return null;

            var change = new ClanChange().Touch(source).Touch(destination);
            VaultTransfer.Move(source, destination, count);
            s.RecalculateStatsWithBuffs(gameDataService);
            return change;
        });

        if (!success)
        {
            await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Failed);
            return;
        }

        await BroadcastDepositAsync(session, session.KnightsId, itemId, count, isDeposit: false);
        await SendResult(session, WarehouseSubOpcode.Output, ClanWarehouseResult.Succeeded);
        await userNotificationService.SendWeightChangeAsync(session);
    }

    private async Task MoveAsync(UserSession session, Packet packet)
    {
        if (!IsLeaderOrAssistant(session))
        {
            packet.ReadInt(); packet.ReadInt(); packet.ReadByte(); packet.ReadByte(); packet.ReadByte();
            await SendResult(session, WarehouseSubOpcode.Move, ClanWarehouseResult.Failed);
            return;
        }

        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        var page = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        if (srcPos >= WarehousePageSize || dstPos >= WarehousePageSize)
        {
            await SendResult(session, WarehouseSubOpcode.Move, ClanWarehouseResult.Failed);
            return;
        }

        var realSrc = page * WarehousePageSize + srcPos;
        var realDst = page * WarehousePageSize + dstPos;
        if (realSrc >= KnightsManager.ClanWarehouseSlots || realDst >= KnightsManager.ClanWarehouseSlots)
        {
            await SendResult(session, WarehouseSubOpcode.Move, ClanWarehouseResult.Failed);
            return;
        }

        var moved = await CommitAsync(session, (_, slots, _) =>
        {
            var source = slots[realSrc];
            var destination = slots[realDst];
            if (source.ItemId != itemId || source.IsEmpty || !destination.IsEmpty)
                return null;

            var change = new ClanChange().Touch(source).Touch(destination);
            ItemSlotState.Of(source).RestoreTo(destination);
            source.Clear();
            return change;
        });

        await SendResult(session, WarehouseSubOpcode.Move, moved ? ClanWarehouseResult.Succeeded : ClanWarehouseResult.Failed);
    }

    private async Task InventoryMoveAsync(UserSession session, Packet packet)
    {
        if (!IsLeaderOrAssistant(session))
        {
            packet.ReadInt(); packet.ReadInt(); packet.ReadByte(); packet.ReadByte(); packet.ReadByte();
            await SendResult(session, WarehouseSubOpcode.InventoryMove, ClanWarehouseResult.Failed);
            return;
        }

        _ = packet.ReadInt();
        var itemId = packet.ReadInt();
        _ = packet.ReadByte();
        var srcPos = packet.ReadByte();
        var dstPos = packet.ReadByte();

        if (srcPos >= InventoryConstants.HaveMax || dstPos >= InventoryConstants.HaveMax)
        {
            await SendResult(session, WarehouseSubOpcode.InventoryMove, ClanWarehouseResult.Failed);
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

        await SendResult(session, WarehouseSubOpcode.InventoryMove, moved ? ClanWarehouseResult.Succeeded : ClanWarehouseResult.Failed);
    }

    private Task<bool> CommitAsync(UserSession session, Func<KnightsEntity, ItemSlot[], UserSession, ClanChange?> mutate)
    {
        var clanId = session.KnightsId;
        return sessionManager.Knights.WithClanWarehouseGateAsync(clanId, () =>
            characterStatePersister.RunAsync(session, false, async unit =>
            {
                var change = sessionManager.Knights.WithClanWarehouse<ClanChange?>(clanId,
                    (clan, slots) => session.WithLock(s => Apply(clan, slots, s, mutate)), null);
                if (change == null)
                    return false;

                var row = await unit.Db.Knights.FindAsync([clanId]);
                if (row != null)
                {
                    row.ClanWarehouseItems = change.Items;
                    row.ClanWarehouseGold = change.Gold;
                    try
                    {
                        await unit.CommitAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Clan warehouse change by {Name} could not be stored", session.Name);
                    }
                }

                sessionManager.Knights.WithClanWarehouse(clanId, (clan, slots) =>
                {
                    session.WithLock(s => change.Undo(clan, slots, s, gameDataService));
                    return true;
                }, false);
                return false;
            }));
    }

    private static ClanChange? Apply(
        KnightsEntity clan,
        ItemSlot[] slots,
        UserSession session,
        Func<KnightsEntity, ItemSlot[], UserSession, ClanChange?> mutate)
    {
        var change = mutate(clan, slots, session);
        if (change == null)
            return null;

        change.Items = UserSessionBinaryState.SerializeWarehouse(slots);
        change.Gold = clan.ClanWarehouseGold;
        clan.ClanWarehouseItems = change.Items;
        return change;
    }

    private static ItemSlot Copy(ItemSlot slot)
    {
        var copy = new ItemSlot();
        ItemSlotState.Of(slot).RestoreTo(copy);
        return copy;
    }

    private static async Task SendResult(UserSession session, WarehouseSubOpcode sub, ClanWarehouseResult result)
    {
        await session.Client.SendPacket(ClanWarehousePacketWriter.Result(sub, result));
    }

    private static bool IsLeaderOrAssistant(UserSession session)
        => session.KnightsFame == 1 || session.KnightsFame == 2;

    private async Task BroadcastDepositAsync(UserSession actor, short clanId, int itemId, int count, bool isDeposit)
    {
        var itemName = gameDataService.GetItem(itemId)?.Name ?? $"Item#{itemId}";
        var verb = isDeposit ? "deposited" : "withdrew";
        var message = count > 1
            ? $"### {actor.Name} {verb} {count}x {itemName} from clan bank ###"
            : $"### {actor.Name} {verb} {itemName} from clan bank ###";

        var pkt = ChatPacketWriter.SystemNotice((byte)actor.Nation, message);

        await knightsRuntime.NotifyOnlineClanMembersAsync(clanId, pkt);
    }

    private sealed record ClanVaultView(int Gold, IReadOnlyList<ItemSlot> Slots);

    private sealed class ClanChange
    {
        private readonly List<(ItemSlot Slot, ItemSlotState Before)> _touched = [];

        public int ClanGoldDelta { get; init; }
        public int MoneyDelta { get; init; }
        public byte[] Items { get; set; } = [];
        public int Gold { get; set; }

        public ClanChange Touch(ItemSlot slot)
        {
            _touched.Add((slot, ItemSlotState.Of(slot)));
            return this;
        }

        public void Undo(KnightsEntity clan, ItemSlot[] slots, UserSession session, IGameDataService gameData)
        {
            foreach (var (slot, before) in _touched)
                before.RestoreTo(slot);

            clan.ClanWarehouseGold -= ClanGoldDelta;
            session.Money -= MoneyDelta;
            clan.ClanWarehouseItems = UserSessionBinaryState.SerializeWarehouse(slots);
            session.RecalculateStatsWithBuffs(gameData);
        }
    }
}
