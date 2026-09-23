using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Common.Tests;

public class AttemptCounterTests
{
    private const int Limit = 5;
    private const int ConcurrentAttempts = 200;
    private const int HotKey = -1;
    private static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ConcurrentReservationsNeverGoPastTheLimit()
    {
        var counter = new AttemptCounter<string>(Limit, Window, new ManualClock());

        var results = await Task.WhenAll(Enumerable.Range(0, ConcurrentAttempts)
            .Select(_ => Task.Run(() => counter.TryRecord("address"))));

        results.Count(reserved => reserved).Should().Be(Limit);
        counter.IsExhausted("address").Should().BeTrue();
    }

    [Fact]
    public void AReservationIsGrantedAgainOnceTheWindowHasPassed()
    {
        var clock = new ManualClock();
        var counter = new AttemptCounter<string>(Limit, Window, clock);
        for (var i = 0; i < Limit; i++)
            counter.TryRecord("address").Should().BeTrue();

        counter.TryRecord("address").Should().BeFalse();
        clock.Advance(Window);

        counter.TryRecord("address").Should().BeTrue();
    }

    [Fact]
    public void ExpiredKeysAreSweptAtMostOncePerInterval()
    {
        var clock = new ManualClock();
        var counter = new AttemptCounter<int>(Limit, ShortWindow, clock);
        var nextKey = Fill(counter, 0);

        clock.Advance(AttemptCounter<int>.SweepInterval);
        counter.Record(nextKey++);
        counter.TrackedKeys.Should().Be(1, "every other key had expired when the sweep was due");

        nextKey = Fill(counter, nextKey);
        clock.Advance(ShortWindow);
        counter.Record(nextKey++);
        counter.TrackedKeys.Should().Be(AttemptCounter<int>.SweepThreshold + 3, "the last sweep was too recent to run again");

        clock.Advance(AttemptCounter<int>.SweepInterval);
        counter.Record(nextKey);
        counter.TrackedKeys.Should().Be(1);
    }

    [Fact]
    public async Task ACountRacingTheSweepOfItsExpiredWindowIsNeverLost()
    {
        var clock = new ManualClock();
        var counter = new AttemptCounter<int>(Limit, ShortWindow, clock);
        counter.Record(HotKey);
        var nextKey = Fill(counter, HotKey + 1);
        clock.Advance(AttemptCounter<int>.SweepInterval);

        var results = await Task.WhenAll(Enumerable.Range(nextKey, ConcurrentAttempts)
            .Select(freshKey => Task.Run(() =>
            {
                counter.Record(freshKey);
                return counter.TryRecord(HotKey);
            })));

        results.Count(reserved => reserved).Should().Be(Limit);
        counter.IsExhausted(HotKey).Should().BeTrue();
    }

    private static int Fill(AttemptCounter<int> counter, int firstKey)
    {
        var end = firstKey + AttemptCounter<int>.SweepThreshold + 1;
        for (var key = firstKey; key < end; key++)
            counter.Record(key);
        return end;
    }
}
