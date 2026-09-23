using System.Collections.Concurrent;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public sealed class GuardedInGameOpcodeRouter(InGameOpcodeRouter inner, IPacketGuard guard, SessionManager sessions) : IInGameOpcodeRouter
{
    private readonly ConcurrentDictionary<GameOpcodes, Func<IClient, Packet, Task>?> _guarded = new();

    public Func<IClient, Packet, Task>? Resolve(GameOpcodes opcode)
        => _guarded.GetOrAdd(opcode, Wrap);

    private Func<IClient, Packet, Task>? Wrap(GameOpcodes opcode)
    {
        var handler = inner.Resolve(opcode);
        if (handler == null)
            return null;

        return (client, packet) => sessions.GetByClientId(client.Id) is not { IsClosing: true } && guard.Admit(client, opcode)
            ? handler(client, packet)
            : Task.CompletedTask;
    }
}
