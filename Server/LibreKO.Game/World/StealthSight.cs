namespace LibreKO.Game.World;

public static class StealthSight
{
    public static bool CanSee(UserSession viewer, UserSession target)
        => !target.IsInvisible || Detects(viewer, target);

    public static bool CanTarget(UserSession viewer, UserSession target)
        => !target.IsInvisible
            || viewer.CharacterId == target.CharacterId
            || (viewer.SightRadius > 0 && Reach.Within(viewer, target, viewer.SightRadius + Reach.LatencyAllowance));

    public static bool Detects(UserSession viewer, UserSession target)
        => viewer.SightRadius > 0 || DetectsUnaided(viewer, target);

    public static bool DetectsUnaided(UserSession viewer, UserSession target)
        => viewer.CharacterId == target.CharacterId
            || viewer.IsGM
            || PvpRules.SharesPartyWith(viewer, target)
            || IsCompatriot(viewer, target);

    private static bool IsCompatriot(UserSession viewer, UserSession target)
        => viewer.Nation == target.Nation
            && !viewer.IsInArena
            && !target.IsInArena
            && !BattleZoneManager.IsFreeForAllZone(target.ZoneId);
}
