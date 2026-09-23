using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Common.Tests;

public class ConnectionRateLimiterTests
{
    private const int Slow = 1;
    private const int Fast = 2;
    private static readonly TokenRate SlowRate = new(2, 0.5);
    private static readonly TokenRate FastRate = new(100, 100);

    [Fact]
    public void AKeyIsRefusedOnceItsBucketIsEmptyUntilItRefills()
    {
        var clock = new ManualClock();
        var limiter = CreateLimiter(clock, new TokenRate(100, 100));
        var connection = Guid.NewGuid();

        limiter.TryAcquire(connection, Slow).Should().BeTrue();
        limiter.TryAcquire(connection, Slow).Should().BeTrue();
        limiter.TryAcquire(connection, Slow).Should().BeFalse();
        limiter.TryAcquire(connection, Fast).Should().BeTrue("each key has its own bucket");

        clock.Advance(TimeSpan.FromSeconds(1 / SlowRate.RefillPerSecond));

        limiter.TryAcquire(connection, Slow).Should().BeTrue();
    }

    [Fact]
    public void TheConnectionTotalCapsEveryKeyTogether()
    {
        var limiter = CreateLimiter(new ManualClock(), new TokenRate(3, 1));
        var connection = Guid.NewGuid();

        limiter.TryAcquire(connection, Fast).Should().BeTrue();
        limiter.TryAcquire(connection, Fast).Should().BeTrue();
        limiter.TryAcquire(connection, Slow).Should().BeTrue();
        limiter.TryAcquire(connection, Fast).Should().BeFalse();
    }

    [Fact]
    public void ConnectionsAreLimitedIndependently()
    {
        var limiter = CreateLimiter(new ManualClock(), new TokenRate(100, 100));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        limiter.TryAcquire(first, Slow);
        limiter.TryAcquire(first, Slow);

        limiter.TryAcquire(first, Slow).Should().BeFalse();
        limiter.TryAcquire(second, Slow).Should().BeTrue();
    }

    [Fact]
    public void ForgettingAConnectionReleasesItsState()
    {
        var limiter = CreateLimiter(new ManualClock(), new TokenRate(100, 100));
        var connection = Guid.NewGuid();

        limiter.TryAcquire(connection, Slow);
        limiter.TryAcquire(connection, Slow);
        limiter.Forget(connection);

        limiter.TryAcquire(connection, Slow).Should().BeTrue();
    }

    private static ConnectionRateLimiter<int> CreateLimiter(ManualClock clock, TokenRate total) =>
        new(total, key => key == Slow ? SlowRate : FastRate, clock);
}
