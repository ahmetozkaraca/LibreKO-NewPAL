using System.Diagnostics;
using FluentAssertions;
using LibreKO.Common.Domain.Services;

namespace LibreKO.Common.Tests;

public class PasswordHasherTests
{
    private const int TimingSamples = 5;
    private const double MinimumCostRatio = 0.3;

    [Fact]
    public void Verify_WithoutAStoredHash_CostsAsMuchAsAWrongPassword()
    {
        var stored = PasswordHasher.Hash("correct");
        PasswordHasher.Verify("warm-up", stored);
        PasswordHasher.Verify("warm-up", null);

        var wrongPassword = MedianTicks(() => PasswordHasher.Verify("guess", stored));
        var unknownAccount = MedianTicks(() => PasswordHasher.Verify("guess", null));

        unknownAccount.Should().BeGreaterThan((long)(wrongPassword * MinimumCostRatio),
            "an unknown account must not answer measurably faster than a wrong password, or the timing tells an attacker which accounts exist");
    }

    [Fact]
    public void Verify_WithoutAStoredHash_NeverSucceeds()
    {
        PasswordHasher.Verify(string.Empty, null).Should().BeFalse();
        PasswordHasher.Verify("anything", null).Should().BeFalse();
    }

    [Fact]
    public void Verify_AcceptsALegacyPlaintextPasswordAndFlagsItForRehash()
    {
        PasswordHasher.Verify("secret", "secret").Should().BeTrue();
        PasswordHasher.Verify("Secret", "secret").Should().BeFalse();
        PasswordHasher.NeedsRehash("secret").Should().BeTrue("plaintext passwords are upgraded on the next successful login");
    }

    [Fact]
    public void Verify_TreatsAMalformedHashLikeStringAsLegacyPlaintextInsteadOfThrowing()
    {
        var verify = () => PasswordHasher.Verify("1.x.y", "1.x.y");

        verify.Should().NotThrow();
        verify().Should().BeTrue();
        PasswordHasher.Verify("other", "1.x.y").Should().BeFalse();
    }

    [Fact]
    public void Hash_ProducesAValueThatVerifiesAndNeedsNoRehash()
    {
        var stored = PasswordHasher.Hash("hunter2");

        PasswordHasher.Verify("hunter2", stored).Should().BeTrue();
        PasswordHasher.Verify("hunter3", stored).Should().BeFalse();
        PasswordHasher.NeedsRehash(stored).Should().BeFalse();
    }

    private static long MedianTicks(Action action)
    {
        var samples = new long[TimingSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[i] = Stopwatch.GetTimestamp() - started;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }
}
