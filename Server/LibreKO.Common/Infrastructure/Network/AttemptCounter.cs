using System.Collections.Concurrent;

namespace LibreKO.Common.Infrastructure.Network;

public sealed class AttemptCounter<TKey>(int limit, TimeSpan window, TimeProvider time)
    where TKey : notnull
{
    private const int SweepThreshold = 4096;

    private readonly ConcurrentDictionary<TKey, Window> _windows = new();

    public bool IsExhausted(TKey key)
    {
        if (limit <= 0 || !_windows.TryGetValue(key, out var entry))
            return false;

        var now = time.GetTimestamp();
        using var scope = entry.Sync.EnterScope();
        return !entry.HasExpired(now, window, time) && entry.Count >= limit;
    }

    public void Record(TKey key)
    {
        if (limit <= 0)
            return;

        var now = time.GetTimestamp();
        var entry = _windows.GetOrAdd(key, _ => new Window(now));
        using (entry.Sync.EnterScope())
        {
            if (entry.HasExpired(now, window, time))
            {
                entry.StartedAt = now;
                entry.Count = 0;
            }

            entry.Count++;
        }

        if (_windows.Count > SweepThreshold)
            Sweep(now);
    }

    public void Reset(TKey key) => _windows.TryRemove(key, out _);

    private void Sweep(long now)
    {
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
