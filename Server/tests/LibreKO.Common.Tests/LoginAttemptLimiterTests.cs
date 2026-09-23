using System.Diagnostics;
using System.Net;
using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LibreKO.Common.Tests;

public class LoginAttemptLimiterTests
{
    private const int SlowdownMs = 200;
    private const int ManyFailures = 50;
    private const int SlowdownsToWait = 2;

    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress OtherAttacker = IPAddress.Parse("203.0.113.11");
    private static readonly IPAddress Player = IPAddress.Parse("198.51.100.20");

    private static LoginAttemptLimiter Create(ConnectionLimitsSettings limits, TimeProvider? time = null)
        => new(Options.Create(limits), time ?? new ManualClock());

    [Fact]
    public async Task FailuresFromOtherAddressesNeverLockTheOwnerOut()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccountAndIp = 2, AccountSlowdownMilliseconds = ConnectionLimitsSettings.Disabled };
        var limiter = Create(limits);

        for (var i = 0; i < ManyFailures; i++)
            limiter.RecordFailure(i % 2 == 0 ? Attacker : OtherAttacker, "victim");

        (await limiter.AdmitAsync(Player, "victim")).Should().NotBeNull(
            "someone else guessing the password must not keep the owner out of the account");
        (await limiter.AdmitAsync(Attacker, "victim")).Should().BeNull();
    }

    [Fact]
    public async Task AnAccountUnderAttackIsSlowedDownButStillAdmitted()
    {
        var limits = new ConnectionLimitsSettings { AccountSlowdownFailures = 3, AccountSlowdownMilliseconds = SlowdownMs };
        var limiter = Create(limits, TimeProvider.System);

        for (var i = 0; i < limits.AccountSlowdownFailures; i++)
            limiter.RecordFailure(IPAddress.Parse($"203.0.113.{i + 1}"), "victim");

        var watch = Stopwatch.StartNew();
        (await limiter.AdmitAsync(Player, "victim"))!.Dispose();
        watch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(SlowdownMs - 1);

        watch.Restart();
        (await limiter.AdmitAsync(Player, "someone-else")).Should().NotBeNull();
        watch.ElapsedMilliseconds.Should().BeLessThan(SlowdownMs);
    }

    [Fact]
    public async Task GuessesAgainstAnAccountUnderAttackAreCheckedOneAtATime()
    {
        var limits = new ConnectionLimitsSettings { AccountSlowdownFailures = 1, AccountSlowdownMilliseconds = SlowdownMs };
        var limiter = Create(limits, TimeProvider.System);
        limiter.RecordFailure(Attacker, "victim");

        var first = await limiter.AdmitAsync(Attacker, "victim");
        var second = limiter.AdmitAsync(OtherAttacker, "victim");
        var elsewhere = await limiter.AdmitAsync(OtherAttacker, "someone-else");
        await Task.Delay(SlowdownMs * SlowdownsToWait);

        second.IsCompleted.Should().BeFalse("a second guess waits until the first has been checked");
        elsewhere.Should().BeSameAs(LoginAdmission.Unthrottled);

        first!.Dispose();
        (await second).Should().NotBeNull();
    }

    [Fact]
    public void TheFormerAccountLimitKeyStillBinds()
    {
        const int limit = 4;
        var limits = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MaxLoginFailuresPerAccount"] = limit.ToString() })
            .Build()
            .Get<ConnectionLimitsSettings>()!;

        limits.MaxLoginFailuresPerAccountAndIp.Should().Be(limit);
    }

    [Fact]
    public void AnAddressIsLockedOutAfterTooManyFailuresAcrossAccounts()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccountAndIp = 100, MaxLoginFailuresPerIp = 3 };
        var limiter = Create(limits);

        limiter.RecordFailure(Attacker, "alpha");
        limiter.RecordFailure(Attacker, "beta");
        limiter.RecordFailure(Attacker, "gamma");

        limiter.IsLockedOut(Attacker, "delta").Should().BeTrue();
        limiter.IsLockedOut(Player, "delta").Should().BeFalse();
    }

    [Fact]
    public void TheLockoutLiftsOnceTheWindowHasPassed()
    {
        var clock = new ManualClock();
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccountAndIp = 2, LoginFailureWindowSeconds = 60 };
        var limiter = Create(limits, clock);

        limiter.RecordFailure(Attacker, "victim");
        limiter.RecordFailure(Attacker, "victim");
        limiter.IsLockedOut(Attacker, "victim").Should().BeTrue();

        clock.Advance(limits.LoginFailureWindow);

        limiter.IsLockedOut(Attacker, "victim").Should().BeFalse();
    }

    [Fact]
    public void ASuccessClearsTheAccountButNotTheAddress()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccountAndIp = 2, MaxLoginFailuresPerIp = 2 };
        var limiter = Create(limits);

        limiter.RecordFailure(Attacker, "own-account");
        limiter.RecordFailure(Attacker, "own-account");
        limiter.RecordSuccess(Attacker, "own-account");

        limiter.IsLockedOut(Attacker, "own-account").Should().BeTrue(
            "logging into an account the attacker owns must not buy more guesses against others");
        limiter.IsLockedOut(Player, "own-account").Should().BeFalse();
    }

    [Fact]
    public void AccountNamesAreCountedWithoutRegardToCase()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccountAndIp = 2 };
        var limiter = Create(limits);

        limiter.RecordFailure(Attacker, "Victim");
        limiter.RecordFailure(Attacker, "VICTIM");

        limiter.IsLockedOut(Attacker, "victim").Should().BeTrue();
    }

    [Fact]
    public async Task AZeroLimitDisablesTheCounters()
    {
        var limits = new ConnectionLimitsSettings
        {
            MaxLoginFailuresPerAccountAndIp = ConnectionLimitsSettings.Disabled,
            MaxLoginFailuresPerIp = ConnectionLimitsSettings.Disabled,
            AccountSlowdownFailures = ConnectionLimitsSettings.Disabled,
        };
        var limiter = Create(limits, TimeProvider.System);

        for (var i = 0; i < ManyFailures; i++)
            limiter.RecordFailure(Attacker, "victim");

        var watch = Stopwatch.StartNew();
        (await limiter.AdmitAsync(Attacker, "victim")).Should().NotBeNull();
        watch.Elapsed.Should().BeLessThan(limits.AccountSlowdown);
    }
}
