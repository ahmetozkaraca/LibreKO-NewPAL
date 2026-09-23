namespace LibreKO.Game.World;

public static class Reach
{
    public const float LatencyAllowance = 2f;

    public static float DistanceSquared(float ax, float az, float bx, float bz)
    {
        var dx = ax - bx;
        var dz = az - bz;
        return dx * dx + dz * dz;
    }

    public static bool Within(float ax, float az, float bx, float bz, float range)
        => range >= 0 && DistanceSquared(ax, az, bx, bz) <= range * range;

    public static bool Within(UserSession session, float x, float z, float range)
        => Within(session.X, session.Z, x, z, range);

    public static bool Within(UserSession a, UserSession b, float range)
        => a.ZoneId == b.ZoneId && a.Room == b.Room && Within(a.X, a.Z, b.X, b.Z, range);

    public static bool Within(UserSession session, NpcInstance npc, float range)
        => session.ZoneId == npc.ZoneId && session.Room == npc.Room && Within(session.X, session.Z, npc.X, npc.Z, range);

    public static bool CanInteract(UserSession session, NpcInstance npc)
        => session.ZoneId == npc.ZoneId
            && session.Room == npc.Room
            && DistanceSquared(session.X, session.Z, npc.X, npc.Z) <= GameConstants.MaxNpcInteractionRangeSq;
}
