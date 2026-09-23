using System.Collections.Concurrent;

namespace LibreKO.Common.Infrastructure.Network;

public sealed class AttemptCounter<TKey>(int limit, TimeSpan window, TimeProvider time)
    where TKey : notnull
{
    public const int SweepThreshold = 4096;
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<TKey, Window> _windows = new();
    private long _lastSweepAt = time.GetTimestamp();

    public int TrackedKeys => _windows.Count;

    public bool IsExhausted(TKey key)
    {
        if (limit <= 0 || !_windows.TryGetValue(key, out var entry))
            return false;

        var now = time.GetTimestamp();
        using var scope = entry.Sync.EnterScope();
        return !entry.HasExpired(now, window, time) && entry.Count >= limit;
    }

    public void Record(TKey key) => Count(key, belowLimitOnly: false);

    public bool TryRecord(TKey key) => Count(key, belowLimitOnly: true);

    public void Reset(TKey key) => _windows.TryRemove(key, out _);

    private bool Count(TKey key, bool belowLimitOnly)
    {
        if (limit <= 0)
            return true;

        var now = time.GetTimestamp();
        bool? counted;
        do
            counted = TryCount(key, now, belowLimitOnly);
        while (counted == null);

        SweepIfDue(now);
        return counted.Value;
    }

    private bool? TryCount(TKey key, long now, bool belowLimitOnly)
    {
        var entry = _windows.GetOrAdd(key, _ => new Window(now));
        using var scope = entry.Sync.EnterScope();

        if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            return null;

        if (entry.HasExpired(now, window, time))
        {
            entry.StartedAt = now;
            entry.Count = 0;
        }

        if (belowLimitOnly && entry.Count >= limit)
            return false;

        entry.Count++;
        return true;
    }

    private void SweepIfDue(long now)
    {
        if (_windows.Count <= SweepThreshold)
            return;

        var lastSweep = Interlocked.Read(ref _lastSweepAt);
        if (time.GetElapsedTime(lastSweep, now) < SweepInterval
            || Interlocked.CompareExchange(ref _lastSweepAt, now, lastSweep) != lastSweep)
            return;

        foreach (var (key, entry) in _windows)
        {
            using var scope = entry.Sync.EnterScope();
            if (entry.HasExpired(now, window, time))
                _windows.TryRemove(new KeyValuePair<TKey, Window>(key, entry));
        }
    }

    private sealed class Window(long startedAt)
    {
        public readonly Lock Sync = new();
        public long StartedAt = startedAt;
        public int Count;

        public bool HasExpired(long now, TimeSpan window, TimeProvider time)
            => time.GetElapsedTime(StartedAt, now) >= window;
    }
}
