namespace LibreKO.Game.World;

public sealed record MagicCastBinding(int TargetId, int[] Data)
{
    public bool HasFlown { get; init; }
}
