namespace LibreKO.Game.World;

public interface INpcKillObserver
{
    Task OnNpcKilledAsync(NpcInstance npc, UserSession killer, UserSession rewardRecipient);
}
