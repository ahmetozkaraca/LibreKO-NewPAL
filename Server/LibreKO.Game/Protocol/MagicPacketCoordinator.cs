using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.Protocol;

public interface IMagicPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
}

public class MagicPacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IMagicItemUsageService magicItemUsageService,
    IMagicExecutionService magicExecutionService,
    IMagicTimingService magicTimingService,
    IMagicCostService magicCostService,
    IMagicTargetingService magicTargetingService,
    IViolationMonitor violationMonitor,
    TimeProvider timeProvider,
    ILogger<MagicPacketCoordinator> logger) : IMagicPacketCoordinator
{
    private const int HeaderBytes = 13;
    private const int PayloadSlotBytes = 4;
    private const int NoTarget = 0;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < HeaderBytes)
            return;

        var magicOpcode = packet.ReadByte();
        var skillId = packet.ReadInt();
        var casterId = packet.ReadInt();
        var targetId = packet.ReadInt();
        var data = new int[MagicProcessPacketWriter.PayloadSlotCount];
        for (var i = 0; i < data.Length && packet.RemainingBytes >= PayloadSlotBytes; i++)
            data[i] = packet.ReadInt();

        if (casterId != session.CharacterId)
        {
            logger.LogWarning(
                "Refusing magic from {Name}: claimed caster {ClaimedCaster}, session is {CharacterId} skill={SkillId}",
                session.Name, casterId, session.CharacterId, skillId);
            violationMonitor.Report(session, ViolationKind.InvalidRequest, $"cast skill {skillId} as caster {casterId}");
            return;
        }

        var magic = gameDataService.GetMagic(skillId);
        if (magic == null)
        {
            logger.LogWarning(
                "Ignoring unknown magic skill {SkillId} from {Name} opcode={MagicOpcode} target={TargetId}",
                skillId,
                session.Name,
                magicOpcode,
                targetId);
            await SendMagicFailAsync(session, skillId);
            return;
        }

        if (!Enum.IsDefined(typeof(MagicProcessOpcode), magicOpcode))
        {
            logger.LogWarning("Unknown magic sub-opcode {SubOpcode} from {Name} skill={SkillId} type={SkillType}",
                magicOpcode, session.Name, skillId, magic.PrimaryType);
            await SendMagicFailAsync(session, skillId);
            return;
        }

        switch ((MagicProcessOpcode)magicOpcode)
        {
            case MagicProcessOpcode.Casting:
                await HandleCastingAsync(session, magic, targetId, data);
                break;
            case MagicProcessOpcode.Flying:
                await HandleFlyingAsync(session, magic, targetId);
                break;
            case MagicProcessOpcode.Effecting:
                await HandleEffectingAsync(session, magic, targetId);
                break;
            case MagicProcessOpcode.Fail:
                await AbortAsync(session, magic);
                break;
            case MagicProcessOpcode.Cancel:
            case MagicProcessOpcode.SkillValueUpdate:
            case MagicProcessOpcode.DurationExpired:
                AbandonCast(session, skillId);
                if (!IsPlayerCancellable(magic, skillId))
                    return;

                await magicExecutionService.CancelAsync(session, skillId);
                break;
        }
    }

    private async Task HandleCastingAsync(UserSession session, MagicData magic, int targetId, int[] data)
    {
        if (!MayCast(session, magic)
            || !AllowTiming(session, magic, magicTimingService.CheckCasting(session, magic), MagicProcessOpcode.Casting))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        var target = magicTargetingService.CheckCast(session, magic, targetId, data);
        if (target.Binding == null)
        {
            ReportImplausibleReach(session, magic, target);
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        if (!magicCostService.CanAfford(session, CastCost(magic)))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        magicTimingService.OnCastAccepted(session, magic);
        session.CombatActions.CastBindings[magic.Id] = target.Binding;
        MarkCombatAction(session);

        await sessionManager.Regions.SendToRegion(
            session,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Casting,
                magic.Id,
                session.CharacterId,
                target.Binding.TargetId,
                target.Binding.Data),
            excludeSender: false);
    }

    private async Task HandleFlyingAsync(UserSession session, MagicData magic, int targetId)
    {
        var isVolley = magic.PrimaryType == MagicSkillType.Ranged;
        if (!MayCast(session, magic)
            || !AllowTiming(session, magic, magicTimingService.CheckRelease(session, magic), MagicProcessOpcode.Flying)
            || (isVolley && !MagicTypeLookup.TryResolve(gameDataService.MagicType2Table, magic, magic.Id, out _)))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        var binding = ReleaseTarget(session, magic, targetId);
        if (binding == null)
        {
            await RefuseReleaseAsync(session, magic);
            return;
        }

        if (!isVolley && !TryMarkFlown(session, magic.Id))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        if (isVolley)
        {
            var arrows = VolleySize(magic);
            if (!await magicCostService.TryPayAsync(session, magicCostService.VolleyCostOf(magic, arrows)))
            {
                await RefuseReleaseAsync(session, magic);
                return;
            }

            magicTimingService.OnVolleyAccepted(session, magic, arrows);
            session.CombatActions.CastBindings.TryRemove(magic.Id, out _);
            MarkCombatAction(session);
        }

        await sessionManager.Regions.SendToRegion(
            session,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Flying,
                magic.Id,
                session.CharacterId,
                binding.TargetId,
                binding.Data),
            excludeSender: false);
    }

    private async Task HandleEffectingAsync(UserSession session, MagicData magic, int targetId)
    {
        if (!MayCast(session, magic))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        if (magic.PrimaryType == MagicSkillType.Ranged)
        {
            await HandleVolleyHitAsync(session, magic, targetId);
            return;
        }

        if (!AllowTiming(session, magic, magicTimingService.CheckRelease(session, magic), MagicProcessOpcode.Effecting))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        var binding = ReleaseTarget(session, magic, targetId);
        if (binding == null)
        {
            await RefuseReleaseAsync(session, magic);
            return;
        }

        magicTimingService.OnReleaseAccepted(session, magic);
        session.CombatActions.CastBindings.TryRemove(magic.Id, out _);

        if (OpensTransformationList(session, magic))
        {
            magicTimingService.RefundCooldown(session, magic.Id);
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        MarkCombatAction(session);
        var charge = MagicCharge.Deferred(() => magicCostService.TryPayAsync(session, magicCostService.CostOf(magic)));
        await magicExecutionService.ExecuteAsync(session, magic, magic.Id, binding.TargetId, [.. binding.Data], charge);
        if (!charge.IsPaid)
            magicTimingService.RefundCooldown(session, magic.Id);
    }

    private async Task HandleVolleyHitAsync(UserSession session, MagicData magic, int targetId)
    {
        if (!magicTimingService.IsVolleyInFlight(session, magic.Id))
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        magicTimingService.OnVolleyHit(session, magic.Id);
        var hit = magicTargetingService.CheckRelease(
            session,
            magic,
            new MagicCastBinding(targetId, new int[MagicProcessPacketWriter.PayloadSlotCount]),
            (float)(magicTimingService.CastDuration(session, magic) + MagicTimingService.ArrowFlightAllowance).TotalSeconds);
        if (hit.Binding == null)
        {
            await SendMagicFailAsync(session, magic.Id);
            return;
        }

        MarkCombatAction(session);
        await magicExecutionService.ExecuteAsync(
            session, magic, magic.Id, hit.Binding.TargetId, [.. hit.Binding.Data], MagicCharge.Prepaid);
    }

    private async Task AbortAsync(UserSession session, MagicData magic)
    {
        session.CombatActions.CastBindings.TryGetValue(magic.Id, out var binding);
        if (!AbandonCast(session, magic.Id))
            return;

        await sessionManager.Regions.SendToRegion(
            session,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Fail,
                magic.Id,
                session.CharacterId,
                binding?.TargetId ?? NoTarget),
            excludeSender: true);
    }

    private async Task RefuseReleaseAsync(UserSession session, MagicData magic)
    {
        AbandonCast(session, magic.Id);
        await SendMagicFailAsync(session, magic.Id);
    }

    private static bool TryMarkFlown(UserSession session, int skillId) =>
        session.CombatActions.CastBindings.TryGetValue(skillId, out var bound)
        && !bound.HasFlown
        && session.CombatActions.CastBindings.TryUpdate(skillId, bound with { HasFlown = true }, bound);

    private bool AbandonCast(UserSession session, int skillId)
    {
        if (!magicTimingService.OnCastAborted(session, skillId))
            return false;

        session.CombatActions.CastBindings.TryRemove(skillId, out _);
        return true;
    }

    private bool MayCast(UserSession session, MagicData magic)
    {
        if ((session.Hp <= 0 && !IsSelfRevival(magic)) || !session.CanUseSkills)
            return false;

        switch (MagicSkillRequirement.AccessFor(magic, session.Class))
        {
            case MagicAccess.Forged:
                logger.LogWarning(
                    "Refusing skill {SkillId} from {Name}: class {Class} cannot own it",
                    magic.Id, session.Name, session.Class);
                violationMonitor.Report(
                    session, ViolationKind.ForgedEvent, $"cast skill {magic.Id} as class {session.Class}");
                return false;
            case MagicAccess.Unavailable:
                return false;
        }

        if (!MagicSkillRequirement.IsMet(magic, session.Level, session.SkillPoints))
        {
            logger.LogWarning(
                "Refusing unlearned skill {SkillId} from {Name}: needs {Requirement}, has level {Level} and mastery [{Mastery}]",
                magic.Id,
                session.Name,
                MagicSkillRequirement.Describe(magic),
                session.Level,
                string.Join(",", session.SkillPoints));
            return false;
        }

        return MagicWeaponRequirement.IsMet(magic, session, gameDataService)
            && (magic.UseItem == 0 || magic.ItemGroup != MagicWeaponRequirement.PotionItemGroup || session.CanUsePotions);
    }

    private bool IsSelfRevival(MagicData magic) =>
        magic.UseItem != 0
        && magic.PrimaryType == MagicSkillType.Special
        && MagicTypeLookup.TryResolve(gameDataService.MagicType5Table, magic, magic.Id, out var type5Data)
        && (SpecialMagicType)type5Data.Type == SpecialMagicType.ResurrectionSelf;

    private MagicCastBinding? ReleaseTarget(UserSession session, MagicData magic, int targetId)
    {
        if (!session.CombatActions.CastBindings.TryGetValue(magic.Id, out var bound)
            || (bound.TargetId != targetId && (SkillMoral)magic.Moral != SkillMoral.Self))
            return null;

        return magicTargetingService.CheckRelease(
            session, magic, bound, (float)magicTimingService.CastDuration(session, magic).TotalSeconds).Binding;
    }

    private MagicCost CastCost(MagicData magic) =>
        magic.PrimaryType == MagicSkillType.Ranged
            ? magicCostService.VolleyCostOf(magic, VolleySize(magic))
            : magicCostService.CostOf(magic);

    private int VolleySize(MagicData magic) =>
        MagicTypeLookup.TryResolve(gameDataService.MagicType2Table, magic, magic.Id, out var type2Data)
            ? Math.Max(MagicTimingService.MinimumVolley, (int)type2Data.NeedArrow)
            : MagicTimingService.MinimumVolley;

    private void MarkCombatAction(UserSession session)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        session.WithLock(caster =>
        {
            caster.IsSitting = false;
            caster.CombatActions.LastActionTicks = now;
        });
    }

    private void ReportImplausibleReach(UserSession session, MagicData magic, MagicTargetCheck target)
    {
        if (target.Verdict != MagicTargetVerdict.Implausible)
            return;

        violationMonitor.Report(
            session,
            ViolationKind.OutOfRange,
            $"cast skill {magic.Id} {target.Distance:F0}m away, range {magicTargetingService.CastRange(session, magic):F0}m");
    }

    private bool OpensTransformationList(UserSession session, MagicData magic) =>
        magic.PrimaryType == MagicSkillType.None
        && magic.SecondaryType != MagicSkillType.None
        && magic.UseItem != 0
        && magicItemUsageService.CanUseSkillItems(session, magic)
        && !BattleZoneManager.IsBattleZone(session.ZoneId)
        && !BattleZoneManager.IsPvpZone(session.ZoneId);

    private bool IsPlayerCancellable(MagicData magic, int skillId) => magic.PrimaryType switch
    {
        MagicSkillType.Buff =>
            MagicTypeLookup.TryResolve(gameDataService.MagicType4Table, magic, skillId, out var type4Data)
            && MagicBuffClassifier.IsBuff(type4Data),
        MagicSkillType.Transform or MagicSkillType.Stealth => true,
        _ => false,
    };

    private bool AllowTiming(
        UserSession session,
        MagicData magic,
        MagicTimingVerdict verdict,
        MagicProcessOpcode opcode)
    {
        if (verdict == MagicTimingVerdict.Allowed)
            return true;

        logger.LogDebug(
            "Dropping magic {SubOpcode} from {Name} skill={SkillId}: {Verdict} (cast={CastTime} recast={ReCastTime})",
            opcode,
            session.Name,
            magic.Id,
            verdict,
            magic.CastTime,
            magic.ReCastTime);
        return false;
    }

    private static Task SendMagicFailAsync(UserSession session, int skillId) =>
        session.Client.SendPacket(MagicProcessPacketWriter.CreateFail(skillId, session.CharacterId));
}
