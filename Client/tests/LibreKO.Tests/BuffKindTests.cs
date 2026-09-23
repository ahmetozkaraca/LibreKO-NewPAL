using LibreKO.Domain;
using Xunit;

namespace LibreKO.Tests;

public class BuffKindTests
{
    private const int Swift = 150;
    private const int Slowed = 50;

    [Theory]
    [InlineData(BuffKind.Speed, Swift, Swift)]
    [InlineData(BuffKind.Freeze, 0, 0)]
    [InlineData(BuffKind.Speed2, null, BuffKind.Speed2Percent)]
    [InlineData(BuffKind.TripleAcHalfSpeed, null, BuffKind.HalfSpeedPercent)]
    [InlineData(BuffKind.AttackSpeed, Swift, BuffKind.NeutralPercent)]
    public void MoveSpeedFollowsTheServersBuffRules(int buffType, int? speed, int expected)
    {
        Assert.Equal(expected, BuffKind.MoveSpeedPercent(MagicType.Buff, buffType, speed));
    }

    [Fact]
    public void OnlyBuffSkillsChangeMoveSpeed()
    {
        Assert.Equal(BuffKind.NeutralPercent, BuffKind.MoveSpeedPercent(MagicType.Melee, BuffKind.Speed, Slowed));
    }

    [Fact]
    public void AttackSpeedSlowsAddUpLikeTheServers()
    {
        Assert.Equal(1f, BuffKind.AttackSpeedMultiplier([Swift, Slowed]));
        Assert.Equal(0.5f, BuffKind.AttackSpeedMultiplier([Slowed]));
        Assert.Equal(BuffKind.SlowestAttackSpeedPercent / (float)BuffKind.NeutralPercent,
            BuffKind.AttackSpeedMultiplier([Slowed, Slowed, Slowed]));
    }
}
