namespace LibreKO.Game.World;

public sealed class TravelState
{
    public long WarpStartedAt;
    public bool ArrivalSnapshotPending;
    public WarpOffer? Offer;
    public long RegionChangeAllowedAt;
    public long TownRecallAllowedAt;
    public long ZoneGateRetryAt;
}

public abstract record WarpSource
{
    public abstract bool IsWithinReach(UserSession session);
}

public sealed record GateWarpSource(byte ZoneId, float X, float Z, float Range) : WarpSource
{
    public override bool IsWithinReach(UserSession session)
        => session.ZoneId == ZoneId && Reach.Within(session, X, Z, Range);
}

public sealed record KeeperWarpSource(NpcInstance Keeper) : WarpSource
{
    public override bool IsWithinReach(UserSession session)
        => Keeper.IsAlive && Reach.CanInteract(session, Keeper);
}

public sealed record WarpOffer(WarpSource Source, IReadOnlySet<short> WarpIds, long ExpiresAt);
