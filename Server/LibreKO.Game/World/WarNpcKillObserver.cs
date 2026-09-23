using System.Collections.Frozen;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol;

namespace LibreKO.Game.World;

public sealed class WarNpcKillObserver(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    ILoyaltyService loyaltyService,
    IEventSystemsPacketCoordinator eventSystemsPacketCoordinator) : INpcKillObserver
{
    private readonly Lock _sync = new();
    private IReadOnlyList<NpcPosData>? _indexedPositions;
    private FrozenDictionary<(byte ZoneId, int NpcId), short> _warNpcs =
        FrozenDictionary<(byte ZoneId, int NpcId), short>.Empty;

    public async Task OnNpcKilledAsync(NpcInstance npc, UserSession killer, UserSession rewardRecipient)
    {
        var battle = sessionManager.Battle;
        if (!battle.IsBattleActive || npc.ZoneId != battle.BattleZone)
            return;

        if (!WarNpcs().TryGetValue((npc.ZoneId, npc.NpcId), out var specialType)
            || !BattleZoneManager.TryGetWarNpc(specialType, out var owner, out var loyalty)
            || killer.Nation == owner)
            return;

        var winner = battle.RegisterNpcKill(owner);

        var credited = rewardRecipient.Nation != owner && rewardRecipient.ZoneId == npc.ZoneId
            ? rewardRecipient
            : killer;
        await loyaltyService.ChangeAsync(credited, loyalty);

        if (winner != (byte)AccountNation.None)
            await eventSystemsPacketCoordinator.DeclareBattleWinnerAsync(winner);
    }

    private FrozenDictionary<(byte ZoneId, int NpcId), short> WarNpcs()
    {
        var positions = gameDataService.NpcPositions;
        using var scope = _sync.EnterScope();
        if (ReferenceEquals(positions, _indexedPositions))
            return _warNpcs;

        var index = new Dictionary<(byte ZoneId, int NpcId), short>();
        foreach (var position in positions)
        {
            if (BattleZoneManager.TryGetWarNpc(position.SpecialType, out _, out _))
                index[((byte)position.ZoneId, position.NpcId)] = position.SpecialType;
        }

        _warNpcs = index.ToFrozenDictionary();
        _indexedPositions = positions;
        return _warNpcs;
    }
}
