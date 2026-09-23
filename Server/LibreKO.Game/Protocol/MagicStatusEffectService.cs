using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IMagicStatusEffectService
{
    Task ExecuteAsync(
        UserSession caster, MagicData magic, MagicSkillType skillType, int skillId, int targetId,
        int[] data, MagicCharge charge);
    Task CancelAsync(UserSession session, int skillId);
}

public class MagicStatusEffectService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IMagicItemUsageService magicItemUsageService,
    ICombatLifecycleService combatLifecycleService,
    ICombatNotificationService combatNotificationService,
    IUserNotificationService userNotificationService,
    IPlayerProgressionService playerProgressionService,
    IStealthService stealthService,
    ILogger<MagicStatusEffectService> logger) : IMagicStatusEffectService
{
    private const int SkillSucceeded = 1;
    private const int PercentRoll = 100;
    private const int CurseReflectChance = 25;
    private const int NoStones = 0;

    public Task ExecuteAsync(
        UserSession caster, MagicData magic, MagicSkillType skillType, int skillId, int targetId,
        int[] data, MagicCharge charge) =>
        skillType switch
        {
            MagicSkillType.Buff => ExecuteBuffAsync(caster, magic, skillId, targetId, data, charge),
            MagicSkillType.Special => ExecuteSpecialAsync(caster, magic, skillId, targetId, charge),
            MagicSkillType.Transform => ExecuteTransformAsync(caster, magic, skillId, targetId, charge),
            MagicSkillType.Stealth => ExecuteStealthAsync(caster, magic, skillId, targetId, data, charge),
            _ => Task.CompletedTask
        };

    public async Task CancelAsync(UserSession session, int skillId)
    {
        var magicRow = gameDataService.GetMagic(skillId);
        var cancelled = session.WithLock<(ActiveBuff? Buff, bool EndsTransformation)>(s =>
        {
            if (!s.ActiveBuffs.TryRemove(skillId, out var buff))
                return (null, false);

            var wasTransformed = s.IsTransformed;
            s.RebuildSpecialStates(gameDataService);
            s.RecalculateStatsWithBuffs(gameDataService);
            return (buff, wasTransformed && !s.IsTransformed);
        });

        if (cancelled.Buff == null)
            return;

        if (magicRow?.PrimaryType == MagicSkillType.Transform && cancelled.EndsTransformation)
            await AnnounceTransformationEndAsync(session);

        await userNotificationService.SendStatUpdateAsync(session);

        if (magicRow?.PrimaryType == MagicSkillType.Stealth
            && MagicTypeLookup.TryResolve(
                gameDataService.MagicType9Table, magicRow, skillId, out var expiredType9))
        {
            await stealthService.EndAsync(session, (MagicStealthType)expiredType9.StateChange);
            return;
        }

        var removedBuff = cancelled.Buff;
        if (magicRow?.PrimaryType == MagicSkillType.Buff && removedBuff.BuffType != BuffType.None)
        {
            await session.Client.SendPacket(
                MagicProcessPacketWriter.CreateDurationExpired((byte)removedBuff.BuffType));

            // Drop the party-panel status icon on natural debuff expiry.
            var statusType = GetPartyStatusCode(removedBuff.BuffType);
            if (statusType > 0)
                await combatNotificationService.SendPartyStatusUpdateAsync(session, statusType, applied: false);
            return;
        }

        await sessionManager.Regions.SendToRegion(
            session,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.DurationExpired,
                skillId,
                (short)session.CharacterId,
                (short)session.CharacterId),
            excludeSender: false);
    }

    private static bool IsTransformationClassAllowed(short classId, short allowedClasses)
    {
        if (allowedClasses == 0)
            return true;

        var digit = ClassIdHelper.IsWarrior(classId) ? allowedClasses / 1000
            : ClassIdHelper.IsRogue(classId) ? allowedClasses % 1000 / 100
            : ClassIdHelper.IsMage(classId) ? allowedClasses % 100 / 10
            : allowedClasses % 10;

        return digit == 1;
    }

    private async Task AnnounceTransformationEndAsync(UserSession session)
    {
        await sessionManager.Regions.SendToRegion(
            session,
            MovementPacketWriter.Transformation(
                session.CharacterId, (byte)StateChangeType.Transformation, NotTransformed),
            excludeSender: false);

        await session.Client.SendPacket(MagicProcessPacketWriter.CreateCancelTransformation());
    }

    private const int NotTransformed = 0;

    private async Task ExecuteBuffAsync(
        UserSession caster, MagicData magic, int skillId, int targetId, int[] data, MagicCharge charge)
    {
        if (!MagicTypeLookup.TryResolve(gameDataService.MagicType4Table, magic, skillId, out var type4Data))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var target = ResolveBuffTarget(caster, magic, targetId);
        var buffType = (BuffType)type4Data.BuffType;
        var isDebuff = !MagicBuffClassifier.IsBuff(type4Data);
        var cursesAnother = isDebuff && target != null && target.CharacterId != caster.CharacterId;

        if (target == null
            || (cursesAnother && (!PvpRules.CanAttackPlayer(caster, target) || !stealthService.CanSee(caster, target)))
            || (!isDebuff && HoldsBuffOfType(target, buffType))
            || !await charge.TryPayAsync())
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        if (cursesAnother)
        {
            if (target.BlockCurses)
            {
                await SendMagicFailAsync(caster, skillId);
                return;
            }

            if (target.ReflectCurses && Random.Shared.Next(PercentRoll) < CurseReflectChance)
                target = caster;

            if (magic.SuccessRate > 0 && magic.SuccessRate < PercentRoll && Random.Shared.Next(PercentRoll) >= magic.SuccessRate)
            {
                await SendMagicFailAsync(caster, skillId);
                return;
            }
        }

        var buff = new ActiveBuff
        {
            MagicId = skillId,
            CasterId = caster.CharacterId,
            Duration = type4Data.Duration,
            ExpireTicks = DateTime.UtcNow.AddSeconds(type4Data.Duration).Ticks,
            BuffType = buffType,
            SpecialAmount = type4Data.SpecialAmount > 0 ? type4Data.SpecialAmount : type4Data.ExpPct,
            BonusAc = type4Data.Ac,
            BonusAcPct = type4Data.AcPct,
            BonusAttack = type4Data.Attack,
            BonusMagicAttack = type4Data.MagicAttack,
            BonusMaxHp = type4Data.MaxHP,
            BonusMaxHpPct = type4Data.MaxHPPct,
            BonusMaxMp = type4Data.MaxMP,
            BonusMaxMpPct = type4Data.MaxMPPct,
            BonusHitRate = type4Data.HitRate,
            BonusAvoidRate = type4Data.AvoidRate,
            BonusStr = type4Data.Str,
            BonusSta = type4Data.Sta,
            BonusDex = type4Data.Dex,
            BonusIntel = type4Data.Intel,
            BonusCha = type4Data.Cha,
            BonusFireR = type4Data.FireR,
            BonusColdR = type4Data.ColdR,
            BonusLightningR = type4Data.LightningR,
            BonusMagicR = type4Data.MagicR,
            BonusPoisonR = type4Data.PoisonR,
            BonusDiseaseR = type4Data.DiseaseR,
            BonusSpeed = type4Data.Speed,
            BonusAttackSpeed = type4Data.AttackSpeed
        };

        var applied = target.WithLock(recipient =>
        {
            if (!isDebuff && HoldsBuffOfType(recipient, buffType))
                return false;

            if (isDebuff && buffType != BuffType.None)
            {
                foreach (var existing in recipient.ActiveBuffs
                    .Where(entry => entry.Value.BuffType == buffType && !entry.Value.IsExpired)
                    .Select(entry => entry.Key)
                    .ToList())
                {
                    recipient.ActiveBuffs.TryRemove(existing, out _);
                }
            }

            recipient.ActiveBuffs[skillId] = buff;
            recipient.RecalculateStatsWithBuffs(gameDataService);
            return true;
        });

        if (!applied)
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        logger.LogDebug("Buff {SkillId} applied to {Name} type={BuffType} duration={Duration}s",
            skillId, target.Name, buffType, type4Data.Duration);

        await userNotificationService.SendStatUpdateAsync(target);

        var statusType = GetPartyStatusCode(buffType);
        if (statusType > 0 && isDebuff)
            await combatNotificationService.SendPartyStatusUpdateAsync(target, statusType, applied: true);

        await sessionManager.Regions.SendToRegion(
            caster,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                (short)caster.CharacterId,
                (short)target.CharacterId,
                [data[0], SkillSucceeded, data[2], type4Data.Duration, data[4], type4Data.Speed, data[6]]),
            excludeSender: false);
    }

    private static bool HoldsBuffOfType(UserSession session, BuffType buffType) =>
        buffType != BuffType.None
        && session.ActiveBuffs.Any(entry => entry.Value.BuffType == buffType && !entry.Value.IsExpired);

    private UserSession? ResolveBuffTarget(UserSession caster, MagicData magic, int targetId)
    {
        if ((SkillMoral)magic.Moral == SkillMoral.Self || targetId == NoTarget || targetId == caster.CharacterId)
            return caster;

        return sessionManager.GetByCharacterId(targetId);
    }

    private const int NoTarget = -1;

    private async Task ExecuteSpecialAsync(
        UserSession caster, MagicData magic, int skillId, int targetId, MagicCharge charge)
    {
        if (!MagicTypeLookup.TryResolve(gameDataService.MagicType5Table, magic, skillId, out var type5Data))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var special = (SpecialMagicType)type5Data.Type;
        var target = special is SpecialMagicType.ResurrectionSelf or SpecialMagicType.LifeCrystal
            ? caster
            : sessionManager.GetByCharacterId(targetId);
        if (target == null)
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        if (special is SpecialMagicType.Resurrection or SpecialMagicType.ResurrectionSelf or SpecialMagicType.LifeCrystal)
        {
            await ResurrectAsync(caster, magic, skillId, target, type5Data, charge);
            return;
        }

        if (!await charge.TryPayAsync())
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        switch (special)
        {
            case SpecialMagicType.RemoveDot: // clear harmful DOTs (negative tick = damage)
            {
                var harmful = target.ActiveOverTimeEffects
                    .Where(kv => kv.Value.TickAmount < 0)
                    .ToList();
                if (harmful.Count == 0) break;

                // Capture distinct status codes BEFORE removing so we can clear each
                // icon flavor that was actually present (poison + disease + generic).
                var clearedCodes = harmful
                    .Select(kv => kv.Value.PartyStatusCode)
                    .Where(c => c > 0)
                    .Distinct()
                    .ToList();

                foreach (var (id, _) in harmful)
                    target.ActiveOverTimeEffects.TryRemove(id, out _);

                await target.Client.SendPacket(MagicProcessPacketWriter.CreateDurationExpired(200));

                // Drop one status-clear per distinct flavor that was active.
                foreach (var code in clearedCodes)
                    await combatNotificationService.SendPartyStatusUpdateAsync(target, code, applied: false);
                break;
            }

            case SpecialMagicType.RemoveBuff: // clear all type-4 debuffs cast by others
            case SpecialMagicType.RemoveBless: // same shape (single buff dispel)
            {
                if (await ClearDebuffsAsync(target))
                    await userNotificationService.SendStatUpdateAsync(target);
                break;
            }
        }

        await sessionManager.Regions.SendToRegion(
            caster,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                (short)caster.CharacterId,
                targetId,
                [0, 1]),
            excludeSender: false);
    }

    private async Task ResurrectAsync(
        UserSession caster, MagicData magic, int skillId, UserSession target, MagicType5Data type5Data,
        MagicCharge charge)
    {
        var stones = (SpecialMagicType)type5Data.Type == SpecialMagicType.Resurrection
            && magic.UseItem != 0
            && type5Data.NeedStone > NoStones
                ? type5Data.NeedStone
                : NoStones;

        if (target.Hp > 0
            || (stones > NoStones && !magicItemUsageService.CanUseItem(target, magic.UseItem, stones))
            || !await charge.TryPayAsync()
            || (stones > NoStones && !await magicItemUsageService.TryConsumeItemAsync(target, magic.UseItem, stones)))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var lostExperience = target.WithLock(corpse =>
        {
            if (corpse.Hp > 0)
                return (Revived: false, Lost: 0L);

            var lost = corpse.DeathExpLoss;
            corpse.Hp = corpse.MaxHp;
            corpse.Mp = 0;
            corpse.DeathExpLoss = 0;
            return (Revived: true, Lost: lost);
        });

        if (!lostExperience.Revived)
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var recovered = lostExperience.Lost > 0 && type5Data.ExpRecover > 0
            ? lostExperience.Lost * type5Data.ExpRecover / MagicCombatHelper.PercentScale
            : 0;
        if (recovered > 0)
            await playerProgressionService.ChangeExperienceAsync(target, recovered);

        await combatLifecycleService.SendHpChangeAsync(target);
        await combatNotificationService.SendMspChangeAsync(target);

        // Broadcast resurrection to region so other players see the player stand up.
        await sessionManager.Regions.SendToRegion(
            target,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                (short)caster.CharacterId,
                (short)target.CharacterId,
                [0, 1]),
            excludeSender: false);
    }

    private async Task<bool> ClearDebuffsAsync(UserSession target)
    {
        var removed = target.WithLock(session =>
        {
            // A debuff is any type-4 buff cast by someone other than the target itself.
            var debuffIds = session.ActiveBuffs
                .Where(kv => kv.Value.CasterId != session.CharacterId && kv.Value.BuffType != BuffType.None)
                .Select(kv => kv.Key)
                .ToList();

            var cleared = new List<ActiveBuff>();
            foreach (var id in debuffIds)
                if (session.ActiveBuffs.TryRemove(id, out var buff) && buff.BuffType != BuffType.None)
                    cleared.Add(buff);

            if (debuffIds.Count > 0)
            {
                session.RebuildSpecialStates(gameDataService);
                session.RecalculateStatsWithBuffs(gameDataService);
            }

            return (Any: debuffIds.Count > 0, Cleared: cleared);
        });

        foreach (var buff in removed.Cleared)
        {
            await target.Client.SendPacket(
                MagicProcessPacketWriter.CreateDurationExpired((byte)buff.BuffType));

            // Drop the party-panel status icon for cured debuffs.
            var statusType = GetPartyStatusCode(buff.BuffType);
            if (statusType > 0)
                await combatNotificationService.SendPartyStatusUpdateAsync(target, statusType, applied: false);
        }

        return removed.Any;
    }

    private static byte GetPartyStatusCode(BuffType buffType) => (byte)(buffType switch
    {
        BuffType.Blind or BuffType.DisableTargeting or BuffType.Unsight => PartyStatusIcon.Blind,
        _ => PartyStatusIcon.None,
    });

    private async Task ExecuteTransformAsync(
        UserSession caster, MagicData magic, int skillId, int targetId, MagicCharge charge)
    {
        if (!MagicTypeLookup.TryResolve(gameDataService.MagicType6Table, magic, skillId, out var type6Data))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var target = (SkillMoral)magic.Moral == SkillMoral.Self
            ? caster
            : sessionManager.GetByCharacterId(targetId);

        var use = (TransformationUse)type6Data.UserSkillUse;
        if (target == null
            || use == TransformationUse.GuardTower
            || (use == TransformationUse.MovingTower && caster.ZoneId != ZoneDelos)
            || !IsTransformationClassAllowed(caster.Class, type6Data.Class)
            || (type6Data.Nation != (byte)EntityNation.All && (byte)target.Nation != type6Data.Nation))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        if (target.IsTransformed)
        {
            logger.LogWarning(
                "Transformation {SkillId} refused for {Name}: already transformed as {TransformId}",
                skillId, caster.Name, target.TransformId);
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        if (!await charge.TryPayAsync()
            || (type6Data.SkillSuccessRate > 0 && type6Data.SkillSuccessRate < PercentRoll
                && Random.Shared.Next(PercentRoll) >= type6Data.SkillSuccessRate))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var transformed = target.WithLock(recipient =>
        {
            if (recipient.IsTransformed)
                return false;

            recipient.TransformId = type6Data.TransformId;
            recipient.ActiveBuffs[skillId] = new ActiveBuff
            {
                MagicId = skillId,
                CasterId = caster.CharacterId,
                Duration = type6Data.Duration,
                ExpireTicks = DateTime.UtcNow.AddSeconds(type6Data.Duration).Ticks
            };
            return true;
        });

        if (!transformed)
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var statePkt = MovementPacketWriter.Transformation(
            target.CharacterId, (byte)StateChangeType.Transformation, skillId);
        await sessionManager.Regions.SendToRegion(target, statePkt, excludeSender: false);

        int[] effectingData = [0, 1, 0, type6Data.Duration];
        await sessionManager.Regions.SendToRegion(
            target,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                caster.CharacterId,
                target.CharacterId,
                effectingData),
            excludeSender: false);
    }

    private const byte ZoneDelos = (byte)ZoneId.Delos;

    private const byte ZoneForgottenTemple = (byte)ZoneId.ForgottenTemple;
    private const byte ZoneDungeonDefence = (byte)ZoneId.DungeonDefence;

    private async Task ExecuteStealthAsync(
        UserSession caster, MagicData magic, int skillId, int targetId, int[] data, MagicCharge charge)
    {
        if (!MagicTypeLookup.TryResolve(gameDataService.MagicType9Table, magic, skillId, out var type9Data))
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        var target = (SkillMoral)magic.Moral == SkillMoral.Self
            ? caster
            : sessionManager.GetByCharacterId(targetId);

        var stealthType = (MagicStealthType)type9Data.StateChange;
        var applied = target != null && stealthType switch
        {
            MagicStealthType.DispelOnMove or MagicStealthType.DispelOnAttack =>
                await HideAsync(caster, target, stealthType, skillId, type9Data, charge),
            MagicStealthType.SeeInvisible or MagicStealthType.SeeInvisibleParty =>
                await GrantSightAsync(caster, stealthType, skillId, type9Data, data, charge),
            _ => UnhandledStealth(caster, stealthType, skillId),
        };

        if (!applied)
        {
            await SendMagicFailAsync(caster, skillId);
            return;
        }

        if (stealthType is MagicStealthType.DispelOnMove or MagicStealthType.DispelOnAttack)
            await AnnounceStealthAsync(caster, target!, skillId, type9Data, data);
    }

    private async Task<bool> HideAsync(
        UserSession caster, UserSession target, MagicStealthType stealthType, int skillId,
        MagicType9Data type9Data, MagicCharge charge)
    {
        if (caster.ZoneId == ZoneForgottenTemple || caster.ZoneId == ZoneDungeonDefence)
            return false;

        if (target.StealthProhibited || target.IsInvisible || !await charge.TryPayAsync())
            return false;

        await stealthService.HideAsync(target, (InvisibilityType)stealthType);
        AddStealthBuff(target, caster, skillId, type9Data);
        return true;
    }

    private async Task<bool> GrantSightAsync(
        UserSession caster, MagicStealthType stealthType, int skillId, MagicType9Data type9Data,
        int[] data, MagicCharge charge)
    {
        var party = stealthType == MagicStealthType.SeeInvisibleParty && caster.IsInParty
            ? sessionManager.Parties.GetParty(caster.PartyIndex)
            : null;

        if (party == null)
        {
            if (HoldsStealthState(caster, stealthType) || !await charge.TryPayAsync())
                return false;

            await stealthService.GrantSightAsync(caster, type9Data.Radius);
            AddStealthBuff(caster, caster, skillId, type9Data);
            await AnnounceStealthAsync(caster, caster, skillId, type9Data, data);
            return true;
        }

        var members = party.MemberIds
            .Where(memberId => memberId >= 0)
            .Select(memberId => sessionManager.GetByCharacterId(memberId))
            .Where(member => member != null
                && member.ZoneId == caster.ZoneId
                && !HoldsStealthState(member, stealthType))
            .Select(member => member!)
            .ToList();

        if (members.Count == 0 || !await charge.TryPayAsync())
            return false;

        foreach (var member in members)
        {
            await stealthService.GrantSightAsync(member, type9Data.Radius);
            AddStealthBuff(member, caster, skillId, type9Data);
            await AnnounceStealthAsync(caster, member, skillId, type9Data, data);
        }

        return true;
    }

    private bool UnhandledStealth(UserSession caster, MagicStealthType stealthType, int skillId)
    {
        logger.LogDebug(
            "Unhandled stealth state {StealthType} on skill {SkillId} cast by {Name}",
            stealthType, skillId, caster.Name);
        return false;
    }

    private static void AddStealthBuff(
        UserSession target, UserSession caster, int skillId, MagicType9Data type9Data)
    {
        if (type9Data.Duration <= 0)
            return;

        target.ActiveBuffs[skillId] = new ActiveBuff
        {
            MagicId = skillId,
            CasterId = caster.CharacterId,
            Duration = type9Data.Duration,
            ExpireTicks = DateTime.UtcNow.AddSeconds(type9Data.Duration).Ticks
        };
    }

    private bool HoldsStealthState(UserSession session, MagicStealthType stealthType) =>
        session.ActiveBuffs.Keys.Any(activeId =>
            gameDataService.GetMagic(activeId) is { PrimaryType: MagicSkillType.Stealth } active
            && MagicTypeLookup.TryResolve(gameDataService.MagicType9Table, active, activeId, out var active9)
            && (MagicStealthType)active9.StateChange == stealthType);

    private Task AnnounceStealthAsync(
        UserSession caster, UserSession target, int skillId, MagicType9Data type9Data, int[] data) =>
        sessionManager.Regions.SendToRegion(
            caster,
            MagicProcessPacketWriter.Create(
                MagicProcessOpcode.Effecting,
                skillId,
                (short)caster.CharacterId,
                (short)target.CharacterId,
                [data[0], SkillSucceeded, data[2], type9Data.Duration, data[4], data[5], data[6]]),
            excludeSender: false);

    private static Task SendMagicFailAsync(UserSession session, int skillId) =>
        session.Client.SendPacket(MagicProcessPacketWriter.CreateFail(skillId, session.CharacterId));
}
