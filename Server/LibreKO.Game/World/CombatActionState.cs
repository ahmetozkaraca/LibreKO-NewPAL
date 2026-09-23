using System.Collections.Concurrent;

namespace LibreKO.Game.World;

public sealed class CombatActionState
{
    public long NextSwingTicks { get; set; }
    public long LastActionTicks { get; set; }
    public ConcurrentDictionary<int, MagicCastBinding> CastBindings { get; } = new();
}
