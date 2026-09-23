namespace LibreKO.Game.World;

public readonly record struct DamageOutcome(int Dealt, bool Killed)
{
    public static readonly DamageOutcome None = new(0, false);
}
