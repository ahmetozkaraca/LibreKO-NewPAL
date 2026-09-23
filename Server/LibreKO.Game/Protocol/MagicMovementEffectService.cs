using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IMagicMovementEffectService
{
    Task ExecuteAsync(UserSession caster, MagicData magic, int skillId, int targetId, int[] data, MagicCharge charge);
}

public class MagicMovementEffectService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IWorldMovementService worldMovementService,
    IStealthService stealthService,
    ILogger<MagicMovementEffectService> logger) : IMagicMovementEffectService
{
    private const short NoClan = 0;

    public async Task ExecuteAsync(
        UserSession caster, MagicData magic, int skillId, int targetId, int[] data, MagicCharge charge)
    {
        if (!MagicTypeLookup.TryResolve(gameDataService.MagicType8Table, magic, skillId, out var type8Data))
        {
            await caster.Client.SendPacket(MagicProcessPacketWriter.CreateFail(skillId, caster.CharacterId));
            return;
        }

        var warpType = (MagicWarpType)type8Data.WarpType;
        var moved = warpType switch
        {
            MagicWarpType.BindPoint => await WarpToBindPointAsync(caster, magic, charge),
            MagicWarpType.SummonInZone => await SummonToCasterAsync(caster, magic, targetId, charge),
            MagicWarpType.MoveToTarget => await MoveToTargetAsync(caster, magic, targetId, charge),
            _ => await UnhandledAsync(warpType, skillId, caster, charge),
        };

        if (!moved)
        {
            await caster.Client.SendPacket(MagicProcessPacketWriter.CreateFail(skillId, caster.CharacterId));
            return;
        }

        await sessionManager.Regions.SendToRegion(
            caster,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                (short)caster.CharacterId,
                targetId,
                data),
            excludeSender: false);
    }

    private async Task<bool> UnhandledAsync(MagicWarpType warpType, int skillId, UserSession caster, MagicCharge charge)
    {
        logger.LogDebug(
            "Warp type {WarpType} has no destination rule: skill={SkillId} caster={Name}",
            warpType, skillId, caster.Name);
        return await charge.TryPayAsync();
    }

    private async Task<bool> WarpToBindPointAsync(UserSession caster, MagicData magic, MagicCharge charge)
    {
        if (!caster.CanTeleport)
            return false;

        var party = (SkillMoral)magic.Moral == SkillMoral.PartyAll && caster.IsInParty
            ? sessionManager.Parties.GetParty(caster.PartyIndex)
            : null;
        if (party == null)
            return await charge.TryPayAsync() && await SendHomeAsync(caster);

        var members = party.MemberIds
            .Where(memberId => memberId >= 0)
            .Select(memberId => sessionManager.GetByCharacterId(memberId))
            .Where(member => member is { Hp: > 0, CanTeleport: true } && member.ZoneId == caster.ZoneId)
            .Select(member => member!)
            .ToList();

        if (!await charge.TryPayAsync())
            return false;

        var warped = false;
        foreach (var member in members)
            warped |= await SendHomeAsync(member);

        return warped;
    }

    private async Task<bool> SendHomeAsync(UserSession caster)
    {
        var bindEvent = caster.Quest.BindPoint > 0
            ? sessionManager.Maps?.GetObjectEvent(caster.ZoneId, caster.Quest.BindPoint)
            : null;

        if (bindEvent is { Life: ObjectEventAlive })
        {
            await worldMovementService.WarpAsync(caster, ToTenths(bindEvent.PosX), ToTenths(bindEvent.PosZ));
            return true;
        }

        var startPosition = gameDataService.GetStartPosition(caster.ZoneId);
        if (startPosition == null)
            return false;

        var (x, z) = startPosition.RandomSpawn(caster.Nation);
        await worldMovementService.WarpAsync(caster, ToTenths(x), ToTenths(z));
        return true;
    }

    private async Task<bool> SummonToCasterAsync(UserSession caster, MagicData magic, int targetId, MagicCharge charge)
    {
        var target = sessionManager.GetByCharacterId(targetId);
        if (target == null
            || target.CharacterId == caster.CharacterId
            || target.Hp <= 0
            || target.ZoneId != caster.ZoneId
            || target.Nation != caster.Nation
            || !target.CanTeleport
            || !IsSummonable(caster, magic, target)
            || !await charge.TryPayAsync())
            return false;

        await worldMovementService.WarpAsync(target, (ushort)caster.GetPosX, (ushort)caster.GetPosZ);
        return true;
    }

    private static bool IsSummonable(UserSession caster, MagicData magic, UserSession target) =>
        (SkillMoral)magic.Moral switch
        {
            SkillMoral.Party or SkillMoral.PartyAll => caster.IsInParty && caster.PartyIndex == target.PartyIndex,
            SkillMoral.Clan or SkillMoral.ClanAll => caster.KnightsId != NoClan && caster.KnightsId == target.KnightsId,
            _ => false,
        };

    private async Task<bool> MoveToTargetAsync(UserSession caster, MagicData magic, int targetId, MagicCharge charge)
    {
        var target = sessionManager.GetByCharacterId(targetId);
        if (target == null
            || !caster.CanTeleport
            || target.CharacterId == caster.CharacterId
            || target.Hp <= 0
            || target.ZoneId != caster.ZoneId
            || !MayMoveTo(caster, magic, target)
            || !await charge.TryPayAsync())
            return false;

        await worldMovementService.WarpAsync(caster, (ushort)target.GetPosX, (ushort)target.GetPosZ);
        return true;
    }

    private bool MayMoveTo(UserSession caster, MagicData magic, UserSession target) =>
        (SkillMoral)magic.Moral switch
        {
            SkillMoral.Party or SkillMoral.PartyAll => caster.IsInParty && caster.PartyIndex == target.PartyIndex,
            SkillMoral.Enemy => PvpRules.CanAttackPlayer(caster, target) && stealthService.CanSee(caster, target),
            _ => target.Nation == caster.Nation && !PvpRules.IsEnemy(caster, target),
        };

    private const byte ObjectEventAlive = 1;

    private static ushort ToTenths(float value) => (ushort)(value * 10);

    private static ushort ToTenths(short value) => (ushort)(value * 10);
}
