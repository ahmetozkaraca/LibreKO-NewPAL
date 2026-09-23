using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IEventQuestPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class EventQuestPacketCoordinator(
    SessionManager sessionManager,
    IRewardQuestService rewardQuests) : IEventQuestPacketCoordinator
{
    private const byte EventQuestSubList = 1;
    private const byte EventQuestSubAccept = 2;
    private const byte EventQuestSubClaim = 3;
    private const int NoQuest = 0;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var sub = packet.ReadByte();
        switch (sub)
        {
            case EventQuestSubList:
                await SendListAsync(session);
                break;
            case EventQuestSubAccept:
                await SendResultAsync(session, EventQuestSubAccept, ReadQuestId(packet), rewardQuests.AcceptAsync);
                break;
            case EventQuestSubClaim:
                await SendResultAsync(session, EventQuestSubClaim, ReadQuestId(packet),
                    (target, questId) => rewardQuests.ClaimAsync(target, QuestBoard.Event, questId));
                break;
        }
    }

    private async Task SendListAsync(UserSession session)
    {
        var views = await rewardQuests.ListAsync(session, QuestBoard.Event);
        var entries = views
            .Select(view => new EventQuestPacketWriter.Entry(
                view.QuestId, RewardQuestTitles.ForEventBoard(view), view.Accepted || view.Claimed, view.Claimable))
            .ToList();

        await session.Client.SendPacket(EventQuestPacketWriter.QuestList(EventQuestSubList, entries));
    }

    private async Task SendResultAsync(
        UserSession session, byte sub, int questId, Func<UserSession, int, Task<RewardOutcome>> action)
    {
        var outcome = await action(session, questId);
        var succeeded = outcome == RewardOutcome.Succeeded;
        await session.Client.SendPacket(EventQuestPacketWriter.Result(
            sub, succeeded ? EventQuestPacketWriter.Succeeded : EventQuestPacketWriter.Failed, questId));
        if (succeeded)
            return;

        await RewardNotices.SendAsync(session, outcome);
        await SendListAsync(session);
    }

    private static int ReadQuestId(Packet packet) =>
        packet.RemainingBytes >= sizeof(int) ? packet.ReadInt() : NoQuest;
}
