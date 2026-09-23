namespace LibreKO.Game.World;

public sealed class RewardQuestKillObserver(IRewardQuestService rewardQuests) : INpcKillObserver
{
    public Task OnNpcKilledAsync(NpcInstance npc, UserSession killer, UserSession rewardRecipient) =>
        rewardQuests.RecordKillAsync(npc, rewardRecipient);
}
