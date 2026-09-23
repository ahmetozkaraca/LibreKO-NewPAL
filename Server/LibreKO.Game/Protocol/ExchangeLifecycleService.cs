using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IExchangeLifecycleService
{
    Task RequestAsync(UserSession session, Packet packet);
    Task AgreeAsync(UserSession session, Packet packet);
    Task CancelAsync(UserSession session, bool isOnDeath = false);
}

public class ExchangeLifecycleService(SessionManager sessionManager,
    ILogger<ExchangeLifecycleService> logger) : IExchangeLifecycleService
{
    public async Task RequestAsync(UserSession session, Packet packet)
    {
        if (session.Hp <= 0 || ExchangePacketConstants.IsBusyElsewhere(session))
        {
            await SendCancelAsync(session);
            return;
        }

        if (session.Trade.IsTrading)
        {
            await CancelAsync(session);
            return;
        }

        var destId = packet.ReadInt();
        var target = sessionManager.GetByCharacterId(destId);
        if (target == null || !CanTrade(session, target) || !TryPair(session, target))
        {
            await SendCancelAsync(session);
            return;
        }

        logger.LogInformation("Trade requested by {Name} to {TargetId}", session.Name, target.CharacterId);

        await target.Client.SendPacket(ExchangePacketWriter.Partner(
            ExchangePacketConstants.ExchangeRequest, session.CharacterId));
    }

    public async Task AgreeAsync(UserSession session, Packet packet)
    {
        if (!session.Trade.IsTrading || session.Hp <= 0 || ExchangePacketConstants.IsBusyElsewhere(session)
            || session.Trade.AskedForExchange)
            return;

        var agreed = packet.ReadByte();
        var asker = sessionManager.GetByCharacterId(session.Trade.ExchangeUser);
        if (asker == null || asker == session)
        {
            session.WithLock(s => Release(s, start: false));
            return;
        }

        var accepted = agreed != ExchangePacketWriter.Failed && CanTrade(session, asker);
        var answered = false;
        UserSession.WithBoth(session, asker, (me, other) =>
        {
            if (!ExchangePacketConstants.ArePartners(me, other) || !other.Trade.AskedForExchange)
            {
                Release(me, start: false);
                return;
            }

            Release(me, start: accepted);
            Release(other, start: accepted);
            answered = true;
        });

        if (!answered)
            return;

        await asker.Client.SendPacket(ExchangePacketWriter.Result(
            ExchangePacketConstants.ExchangeAgree, accepted ? ExchangePacketWriter.Succeeded : ExchangePacketWriter.Failed));

        if (!accepted && agreed != ExchangePacketWriter.Failed)
            await SendCancelAsync(session);
    }

    public async Task CancelAsync(UserSession session, bool isOnDeath = false)
    {
        if (!session.Trade.IsTrading)
            return;

        var partner = sessionManager.GetByCharacterId(session.Trade.ExchangeUser);
        var partnerReleased = false;
        if (partner == null || partner == session)
        {
            session.WithLock(s => Release(s, start: false));
        }
        else
        {
            UserSession.WithBoth(session, partner, (me, other) =>
            {
                partnerReleased = ExchangePacketConstants.ArePartners(me, other);
                Release(me, start: false);
                if (partnerReleased)
                    Release(other, start: false);
            });
        }

        logger.LogInformation("Trade cancelled for {Name} (death: {OnDeath})", session.Name, isOnDeath);

        await SendCancelAsync(session);
        if (partnerReleased)
            await SendCancelAsync(partner!);
    }

    private static bool CanTrade(UserSession session, UserSession target) =>
        target.CharacterId != session.CharacterId
        && target.AccountId != session.AccountId
        && target.Hp > 0
        && !ExchangePacketConstants.IsBusyElsewhere(target)
        && ExchangePacketConstants.IsWithinTradeRange(session, target)
        && (target.Nation == session.Nation || ZoneRules.Allows(session.ZoneId, ZoneFlags.TradeOtherNation));

    private static bool TryPair(UserSession asker, UserSession target)
    {
        var paired = false;
        UserSession.WithBoth(asker, target, (a, t) =>
        {
            if (a.Trade.IsTrading || ItemTransfer.IsInventoryLocked(t))
                return;

            a.Trade.ExchangeUser = t.CharacterId;
            a.Trade.AskedForExchange = true;
            a.Trade.ExchangeOk = false;
            t.Trade.ExchangeUser = a.CharacterId;
            t.Trade.AskedForExchange = false;
            t.Trade.ExchangeOk = false;
            paired = true;
        });
        return paired;
    }

    private void Release(UserSession session, bool start)
    {
        var unreturned = session.InitExchange(start);
        if (unreturned.Count > 0)
        {
            logger.LogError("Could not return {Count} escrowed items to {Name}: {Items}",
                unreturned.Count, session.Name, string.Join(", ", unreturned.Select(item => $"{item.ItemId}x{item.Count}")));
        }
    }

    private static async Task SendCancelAsync(UserSession session)
    {
        await session.Client.SendPacket(
            ExchangePacketWriter.Sub(ExchangePacketConstants.ExchangeCancel));
    }
}
