namespace LibreKO.Domain;

public static class MagicType
{
    public const int None = 0;
    public const int Melee = 1;
    public const int Ranged = 2;
    public const int DotHeal = 3;
    public const int Buff = 4;
    public const int Special = 5;
    public const int Transform = 6;
    public const int Aoe = 7;
    public const int Warp = 8;
    public const int Stealth = 9;
}

public static class MagicSub
{
    public const int Casting = 1;
    public const int Flying = 2;
    public const int Effecting = 3;
    public const int Fail = 4;
    public const int DurationExpired = 5;
    public const int Cancel = 6;
    public const int CancelTransformation = 7;
}

public static class HealTarget
{
    public const int Hp = 1;
    public const int Mp = 2;
}

public static class SpecialMagic
{
    public const int RemoveDot = 1;
    public const int RemoveBuff = 2;
    public const int Resurrect = 3;
    public const int ResurrectSelf = 4;
    public const int RemoveBless = 5;
    public const int LifeCrystal = 6;
}

public static class BuffKind
{
    public const int AttackSpeed = 5;
    public const int Speed = 6;
    public const int Freeze = 22;
    public const int TripleAcHalfSpeed = 28;
    public const int Speed2 = 40;

    public const int NeutralPercent = 100;
    public const int Speed2Percent = 65;
    public const int HalfSpeedPercent = 50;
    public const int SlowestAttackSpeedPercent = 25;
    public const int FastestAttackSpeedPercent = 200;

    public static int MoveSpeedPercent(int magicType, int buffType, int? speed) =>
        magicType != MagicType.Buff ? NeutralPercent : buffType switch
        {
            Speed or Freeze when speed is { } absolute => System.Math.Max(0, absolute),
            Speed2 => Speed2Percent,
            TripleAcHalfSpeed => HalfSpeedPercent,
            _ => NeutralPercent,
        };

    public static float AttackSpeedMultiplier(System.Collections.Generic.IEnumerable<int> activePercents)
    {
        int percent = NeutralPercent;
        foreach (int active in activePercents) percent += active - NeutralPercent;
        return System.Math.Clamp(percent, SlowestAttackSpeedPercent, FastestAttackSpeedPercent) / (float)NeutralPercent;
    }
}
