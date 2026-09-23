using System.Collections.Concurrent;

namespace LibreKO.Common.Infrastructure.Network;

public readonly record struct TokenRate(double Capacity, double RefillPerSecond);

public sealed class ConnectionRateLimiter<TKey>(TokenRate total, Func<TKey, TokenRate> rateOf, TimeProvider time)
    where TKey : notnull
{
    private const double TokenCost = 1;

    private readonly ConcurrentDictionary<Guid, Buckets> _connections = new();

    public bool TryAcquire(Guid connectionId, TKey key)
        => _connections.GetOrAdd(connectionId, _ => new Buckets()).TryTake(key, total, rateOf, time);

    public void Forget(Guid connectionId) => _connections.TryRemove(connectionId, out _);

    private sealed class Buckets
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<TKey, TokenBucket> _perKey = [];
        private TokenBucket? _total;

        public bool TryTake(TKey key, TokenRate total, Func<TKey, TokenRate> rateOf, TimeProvider time)
        {
            var now = time.GetTimestamp();
            using var scope = _sync.EnterScope();

            _total ??= new TokenBucket(total, now);
            if (!_perKey.TryGetValue(key, out var bucket))
            {
                bucket = new TokenBucket(rateOf(key), now);
                _perKey[key] = bucket;
            }

            _total.Refill(now, time);
            bucket.Refill(now, time);
            if (_total.Tokens < TokenCost || bucket.Tokens < TokenCost)
                return false;

            _total.Tokens -= TokenCost;
            bucket.Tokens -= TokenCost;
            return true;
        }
    }

    private sealed class TokenBucket(TokenRate rate, long createdAt)
    {
        private long _refilledAt = createdAt;

        public double Tokens = rate.Capacity;

        public void Refill(long now, TimeProvider time)
        {
            var seconds = time.GetElapsedTime(_refilledAt, now).TotalSeconds;
            if (seconds <= 0)
                return;

            Tokens = Math.Min(rate.Capacity, Tokens + seconds * rate.RefillPerSecond);
            _refilledAt = now;
        }
    }
}
