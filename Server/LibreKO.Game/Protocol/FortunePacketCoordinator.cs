using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IFortunePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class FortunePacketCoordinator(
    SessionManager sessionManager,
    IRewardDrawService rewardDraws) : IFortunePacketCoordinator
{
    private const byte FortuneSubStatus = 1;
    private const byte FortuneSubDraw = 2;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = packet.ReadByte();
        switch (sub)
        {
            case FortuneSubStatus:
                await session.Client.SendPacket(FortunePacketWriter.Status(
                    FortuneSubStatus, await rewardDraws.IsDailyRewardAvailableAsync(session, PrizePool.Fortune)));
                break;
            case FortuneSubDraw:
                await HandleDrawAsync(session);
                break;
        }
    }

    private async Task HandleDrawAsync(UserSession session)
    {
        var result = await rewardDraws.ClaimDailyRewardAsync(session, PrizePool.Fortune);
        var succeeded = result.Outcome == RewardOutcome.Succeeded;
        await session.Client.SendPacket(FortunePacketWriter.DrawResult(
            FortuneSubDraw,
            succeeded ? FortunePacketWriter.Succeeded : FortunePacketWriter.Failed,
            result.PrizeItemId,
            result.PrizeGold));
        if (!succeeded)
            await RewardNotices.SendAsync(session, result.Outcome);
    }
}
