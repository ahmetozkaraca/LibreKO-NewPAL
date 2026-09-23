using System.Collections.Frozen;
using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Game.World;

public static class OpcodePolicies
{
    public static readonly TokenRate ClientTotal = new(300, 100);
    public static readonly TokenRate Default = new(40, 20);

    private static readonly TokenRate Movement = new(60, 20);
    private static readonly TokenRate Combat = new(30, 10);
    private static readonly TokenRate Magic = new(60, 20);
    private static readonly TokenRate Inventory = new(40, 20);
    private static readonly TokenRate WorldQuery = new(30, 10);
    private static readonly TokenRate ZoneChange = new(10, 2);
    private static readonly TokenRate StateToggle = new(20, 10);
    private static readonly TokenRate Lookup = new(10, 2);
    private static readonly TokenRate Settings = new(10, 1);
    private static readonly TokenRate Persistence = new(2, 1.0 / 30);

    private static readonly FrozenDictionary<GameOpcodes, TokenRate> Limits = new Dictionary<GameOpcodes, TokenRate>
    {
        [GameOpcodes.GS_MOVE] = Movement,
        [GameOpcodes.GS_ROTATE] = Movement,
        [GameOpcodes.GS_ATTACK] = Combat,
        [GameOpcodes.GS_MAGIC_PROCESS] = Magic,
        [GameOpcodes.GS_ITEM_MOVE] = Inventory,
        [GameOpcodes.GS_TARGET_HP] = WorldQuery,
        [GameOpcodes.GS_REQ_USERIN] = WorldQuery,
        [GameOpcodes.GS_REQ_NPCIN] = WorldQuery,
        [GameOpcodes.GS_USER_INFO] = WorldQuery,
        [GameOpcodes.GS_ZONE_CHANGE] = ZoneChange,
        [GameOpcodes.GS_STATE_CHANGE] = StateToggle,
        [GameOpcodes.GS_CORPSE] = Lookup,
        [GameOpcodes.GS_CLIENT_SETTINGS] = Settings,
        [GameOpcodes.GS_DATASAVE] = Persistence,
    }.ToFrozenDictionary();

    private static readonly FrozenSet<GameOpcodes> ServerOnly = new[]
    {
        GameOpcodes.GS_WARP,
    }.ToFrozenSet();

    public static TokenRate LimitOf(GameOpcodes opcode) => Limits.GetValueOrDefault(opcode, Default);

    public static bool IsServerOnly(GameOpcodes opcode) => ServerOnly.Contains(opcode);
}

public interface IPacketGuard
{
    bool Admit(IClient client, GameOpcodes opcode);
    void Forget(Guid clientId);
}

public sealed class PacketGuard(
    SessionManager sessionManager,
    IViolationMonitor violations,
    TimeProvider time) : IPacketGuard
{
    private readonly ConnectionRateLimiter<GameOpcodes> _limiter = new(OpcodePolicies.ClientTotal, OpcodePolicies.LimitOf, time);

    public bool Admit(IClient client, GameOpcodes opcode)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session is { IsClosing: true })
            return false;

        if (OpcodePolicies.IsServerOnly(opcode) && session?.IsGM != true)
        {
            violations.Report(client, ViolationKind.ServerOnlyOpcode, $"sent server-only opcode {opcode}");
            return false;
        }

        if (_limiter.TryAcquire(client.Id, opcode))
            return true;

        violations.Report(client, ViolationKind.RateLimit, $"exceeded the rate limit for {opcode}");
        return false;
    }

    public void Forget(Guid clientId) => _limiter.Forget(clientId);
}
