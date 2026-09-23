namespace LibreKO.Game.World;

public sealed class SummonGrant(int ownerId, byte zoneId, int allowance)
{
    public int OwnerId { get; } = ownerId;
    public byte ZoneId { get; } = zoneId;
    public int Remaining { get; internal set; } = allowance;
    public int Spawned { get; internal set; }
}

public sealed class SummonQuota(SessionManager sessionManager, TimeProvider time)
{
    public const int MaxLiveSummonsPerPlayer = 4;
    public const int MaxLiveSummonsPerZone = 40;
    public static readonly TimeSpan SummonCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan SummonLifetime = TimeSpan.FromMinutes(10);

    private readonly record struct LiveSummon(int OwnerId, byte ZoneId, NpcInstance Npc, DateTimeOffset ExpiresAt);

    private readonly Lock _sync = new();
    private readonly List<LiveSummon> _live = [];
    private readonly Dictionary<int, DateTimeOffset> _lastSummonAt = [];
    private readonly Dictionary<byte, int> _reserved = [];

    public SummonGrant? Reserve(UserSession session)
    {
        var now = time.GetUtcNow();
        using var scope = _sync.EnterScope();
        Prune(now);

        if (_lastSummonAt.ContainsKey(session.CharacterId))
            return null;

        var allowance = Allowance(session.CharacterId, session.ZoneId);
        if (allowance <= 0)
            return null;

        _lastSummonAt[session.CharacterId] = now;
        _reserved[session.ZoneId] = _reserved.GetValueOrDefault(session.ZoneId) + allowance;
        return new SummonGrant(session.CharacterId, session.ZoneId, allowance);
    }

    public void Track(SummonGrant grant, NpcInstance npc)
    {
        using var scope = _sync.EnterScope();
        _live.Add(new LiveSummon(grant.OwnerId, grant.ZoneId, npc, time.GetUtcNow() + SummonLifetime));
        if (grant.Remaining <= 0)
            return;

        grant.Remaining--;
        grant.Spawned++;
        Unreserve(grant.ZoneId, 1);
    }

    public void Release(SummonGrant grant)
    {
        using var scope = _sync.EnterScope();
        Unreserve(grant.ZoneId, grant.Remaining);
        grant.Remaining = 0;
        if (grant.Spawned == 0)
            _lastSummonAt.Remove(grant.OwnerId);
    }

    public IReadOnlyList<NpcInstance> TakeExpired()
    {
        var now = time.GetUtcNow();
        using var scope = _sync.EnterScope();
        var expired = _live.Where(summon => now >= summon.ExpiresAt).Select(summon => summon.Npc).ToList();
        _live.RemoveAll(summon => now >= summon.ExpiresAt);
        return expired;
    }

    private int Allowance(int ownerId, byte zoneId)
        => Math.Min(
            MaxLiveSummonsPerPlayer - _live.Count(summon => summon.OwnerId == ownerId),
            MaxLiveSummonsPerZone - _live.Count(summon => summon.ZoneId == zoneId) - _reserved.GetValueOrDefault(zoneId));

    private void Prune(DateTimeOffset now)
    {
        _live.RemoveAll(summon => !summon.Npc.IsAlive
            || !ReferenceEquals(sessionManager.Regions.GetNpc(summon.Npc.UniqueId), summon.Npc));

        foreach (var ownerId in _lastSummonAt.Where(entry => now - entry.Value >= SummonCooldown)
                     .Select(entry => entry.Key).ToList())
            _lastSummonAt.Remove(ownerId);
    }

    private void Unreserve(byte zoneId, int slots)
    {
        var reserved = _reserved.GetValueOrDefault(zoneId) - slots;
        if (reserved > 0)
            _reserved[zoneId] = reserved;
        else
            _reserved.Remove(zoneId);
    }
}
