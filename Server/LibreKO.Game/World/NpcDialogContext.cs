namespace LibreKO.Game.World;

public static class NpcDialogContext
{
    public static NpcInstance? ActiveNpc(SessionManager sessionManager, UserSession session)
    {
        var npcUniqueId = session.Quest.EventNpcUniqueId;
        if (npcUniqueId <= 0)
            return null;

        var npc = sessionManager.Regions.GetNpc(npcUniqueId);
        return npc is { IsAlive: true } && Reach.CanInteract(session, npc) ? npc : null;
    }

    public static bool IsTalkingTo(SessionManager sessionManager, UserSession session, byte npcType)
        => ActiveNpc(sessionManager, session)?.NpcType == npcType;
}
