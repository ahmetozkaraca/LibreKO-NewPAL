using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IRoulettePacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class RoulettePacketCoordinator(
    SessionManager sessionManager,
    IRewardDrawService rewardDraws) : IRoulettePacketCoordinator
{
    private const int PrizeListPage = 1;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = (EventBoardSubOpcode)packet.ReadByte();
        switch (sub)
        {
            case EventBoardSubOpcode.RouletteOpen:
                await session.Client.SendPacket(RoulettePacketWriter.Open(await rewardDraws.OpenRouletteAsync(session)));
                break;
            case EventBoardSubOpcode.RouletteSpin:
                await HandleSpinAsync(session);
                break;
            case EventBoardSubOpcode.RoulettePrizeList:
                await SendPrizeListAsync(session);
                break;
        }
    }

    private async Task HandleSpinAsync(UserSession session)
    {
        var result = await rewardDraws.SpinRouletteAsync(session);
        var succeeded = result.Outcome == RewardOutcome.Succeeded;
        await session.Client.SendPacket(RoulettePacketWriter.Spin(
            succeeded ? RoulettePacketWriter.ResultOk : RoulettePacketWriter.ResultFail,
            result.PrizeItemId,
            result.PrizeGold));
        if (!succeeded)
            await RewardNotices.SendAsync(session, result.Outcome);
    }

    private async Task SendPrizeListAsync(UserSession session)
    {
        var history = await rewardDraws.RouletteHistoryAsync(session);
        var entries = history.Select(spin => new RoulettePacketWriter.PrizeLogEntry(
            spin.Kind == RewardKind.Item ? spin.ItemId : RewardDrawResult.NoPrize,
            spin.Count,
            (int)new DateTimeOffset(spin.SpunAt, TimeSpan.Zero).ToUnixTimeSeconds()));

        await session.Client.SendPacket(RoulettePacketWriter.PrizeList(PrizeListPage, entries));
    }
}
