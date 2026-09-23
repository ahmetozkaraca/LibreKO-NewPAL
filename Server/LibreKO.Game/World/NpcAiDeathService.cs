using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.World;

public interface INpcAiDeathService
{
    Task HandlePlayerKilledByNpcAsync(UserSession target, NpcInstance npc);
}

public class NpcAiDeathService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IPlayerProgressionService playerProgressionService,
    IMiningPacketCoordinator miningPacketCoordinator,
    IExchangePacketCoordinator exchangePacketCoordinator,
    IMerchantPacketCoordinator merchantPacketCoordinator) : INpcAiDeathService
{
    public async Task HandlePlayerKilledByNpcAsync(UserSession target, NpcInstance npc)
    {
        if (!target.TryBeginDeath())
            return;

        var deadPacket = DeathPacketWriter.PlayerDeath(target.CharacterId, npc.UniqueId);
        await sessionManager.Regions.SendToRegion(target, deadPacket, excludeSender: false);

        if (target.Trade.IsTrading)
            await exchangePacketCoordinator.CancelAsync(target, isOnDeath: true);

        if (target.Trade.IsMerchanting || target.Trade.IsMerchantPreparing)
            await merchantPacketCoordinator.CloseStallAsync(target);

        await miningPacketCoordinator.StopGatheringAsync(target);

        var expLoss = DeathPenaltyCalculator.CalculateNpcDeathExpLoss(target, npc, gameDataService);
        target.DeathExpLoss = expLoss;
        if (expLoss > 0)
            await playerProgressionService.ChangeExperienceAsync(target, -expLoss);

        target.KillerNpcType = npc.NpcType;
    }
}
