using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IDailyQuestPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class DailyQuestPacketCoordinator(
    SessionManager sessionManager,
    IRewardQuestService rewardQuests) : IDailyQuestPacketCoordinator
{
    private const int NoQuest = 0;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = (DailyQuestSubOpcode)packet.ReadByte();
        switch (sub)
        {
            case DailyQuestSubOpcode.List:
                await SendListAsync(session);
                break;
            case DailyQuestSubOpcode.Claim:
                await HandleClaimAsync(session, packet.RemainingBytes >= sizeof(int) ? packet.ReadInt() : NoQuest);
                break;
        }
    }

    private async Task SendListAsync(UserSession session)
    {
        var views = await rewardQuests.ListAsync(session, QuestBoard.Daily);
        var entries = views
            .Select(view => new DailyQuestPacketWriter.Entry(
                view.QuestId, view.Claimable, view.Claimed, RewardQuestTitles.ForDailyBoard(view)))
            .ToList();

        await session.Client.SendPacket(DailyQuestPacketWriter.QuestList(DailyQuestSubOpcode.List, entries));
    }

    private async Task HandleClaimAsync(UserSession session, int questId)
    {
        var outcome = await rewardQuests.ClaimAsync(session, QuestBoard.Daily, questId);
        var succeeded = outcome == RewardOutcome.Succeeded;
        await session.Client.SendPacket(DailyQuestPacketWriter.Result(
            DailyQuestSubOpcode.Claim,
            succeeded ? DailyQuestPacketWriter.Succeeded : DailyQuestPacketWriter.Failed,
            questId));
        if (succeeded)
            return;

        await RewardNotices.SendAsync(session, outcome);
        await SendListAsync(session);
    }
}
