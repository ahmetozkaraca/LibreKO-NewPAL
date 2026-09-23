using System.Net;
using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Common.Tests;

public class LoginAttemptLimiterTests
{
    private static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress OtherAttacker = IPAddress.Parse("203.0.113.11");
    private static readonly IPAddress Player = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void AnAccountIsLockedOutAfterTooManyFailuresFromAnyAddress()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 4, MaxLoginFailuresPerIp = 100 };
        var limiter = new LoginAttemptLimiter(limits, new ManualClock());

        for (var i = 0; i < limits.MaxLoginFailuresPerAccount; i++)
            limiter.RecordFailure(i % 2 == 0 ? Attacker : OtherAttacker, "victim");

        limiter.IsLockedOut(Player, "victim").Should().BeTrue(
            "spreading guesses over several addresses must not reset the account counter");
        limiter.IsLockedOut(Player, "someone-else").Should().BeFalse();
    }

    [Fact]
    public void AnAddressIsLockedOutAfterTooManyFailuresAcrossAccounts()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 100, MaxLoginFailuresPerIp = 3 };
        var limiter = new LoginAttemptLimiter(limits, new ManualClock());

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
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 2, LoginFailureWindowSeconds = 60 };
        var limiter = new LoginAttemptLimiter(limits, clock);

        limiter.RecordFailure(Attacker, "victim");
        limiter.RecordFailure(Attacker, "victim");
        limiter.IsLockedOut(Player, "victim").Should().BeTrue();

        clock.Advance(limits.LoginFailureWindow);

        limiter.IsLockedOut(Player, "victim").Should().BeFalse();
    }

    [Fact]
    public void ASuccessClearsTheAccountButNotTheAddress()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 2, MaxLoginFailuresPerIp = 2 };
        var limiter = new LoginAttemptLimiter(limits, new ManualClock());

        limiter.RecordFailure(Attacker, "own-account");
        limiter.RecordFailure(Attacker, "own-account");
        limiter.RecordSuccess("own-account");

        limiter.IsLockedOut(Player, "own-account").Should().BeFalse();
        limiter.IsLockedOut(Attacker, "own-account").Should().BeTrue(
            "logging into an account the attacker owns must not buy more guesses against others");
    }

    [Fact]
    public void AccountNamesAreCountedWithoutRegardToCase()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 2 };
        var limiter = new LoginAttemptLimiter(limits, new ManualClock());

        limiter.RecordFailure(Attacker, "Victim");
        limiter.RecordFailure(OtherAttacker, "VICTIM");

        limiter.IsLockedOut(Player, "victim").Should().BeTrue();
    }

    [Fact]
    public void AZeroLimitDisablesTheCounter()
    {
        var limits = new ConnectionLimitsSettings { MaxLoginFailuresPerAccount = 0, MaxLoginFailuresPerIp = 0 };
        var limiter = new LoginAttemptLimiter(limits, new ManualClock());

        for (var i = 0; i < 50; i++)
            limiter.RecordFailure(Attacker, "victim");

        limiter.IsLockedOut(Attacker, "victim").Should().BeFalse();
    }
}
