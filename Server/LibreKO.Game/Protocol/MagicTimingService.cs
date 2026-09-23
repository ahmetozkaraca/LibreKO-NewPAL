using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public enum MagicTimingVerdict
{
    Allowed,
    OnCooldown,
    AlreadyCasting,
    CastTooEarly,
    TooSoonAfterLastSkill,
    NotCast,
}

public interface IMagicTimingService
{
    MagicTimingVerdict CheckCasting(UserSession session, MagicData magic);

    MagicTimingVerdict CheckRelease(UserSession session, MagicData magic);

    bool IsVolleyInFlight(UserSession session, int skillId);

    TimeSpan CastDuration(UserSession session, MagicData magic);

    void OnCastAccepted(UserSession session, MagicData magic);

    void OnVolleyAccepted(UserSession session, MagicData magic, int arrows);

    void OnVolleyHit(UserSession session, int skillId);

    void OnReleaseAccepted(UserSession session, MagicData magic);

    bool OnCastAborted(UserSession session, int skillId);

    void RefundCooldown(UserSession session, int skillId);
}

public sealed class MagicTimingService(TimeProvider? timeProvider = null) : IMagicTimingService
{
    private const int LatencyGraceMs = 250;
    private const int AbandonedCastGraceMs = 3000;
    private const int SkillBurstFloorMs = 250;
    private const int RangedCommitMs = 400;
    private const int PotionSharedCooldownMs = 2000;
    private const byte MoralSelf = 1;
    private const int TenthsPerSecond = 10;
    private const int MillisecondsPerTenth = 1000 / TenthsPerSecond;
    private const int CooldownEntryPruneThreshold = 256;
    private const int StaleAcceptedCastGraces = 10;
    private const int InstantCastMs = 0;
    private const int NoArrows = 0;
    private const int VolleysInFlight = 2;

    public const int MinimumVolley = 1;

    public static readonly TimeSpan ArrowFlightAllowance = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public MagicTimingVerdict CheckCasting(UserSession session, MagicData magic)
    {
        var now = _time.GetUtcNow().UtcTicks;

        if (RemainingCooldownMs(session, magic.Id, now) > 0)
            return MagicTimingVerdict.OnCooldown;

        if (IsPotion(magic) && MillisecondsSince(session.LastPotionTicks, now) < PotionSharedCooldownMs)
            return MagicTimingVerdict.OnCooldown;

        if (!ConsumesAnItem(magic))
        {
            if (CastMilliseconds(session, magic) > InstantCastMs && IsCastInFlight(session, now))
                return MagicTimingVerdict.AlreadyCasting;

            if (MillisecondsSince(session.SkillBurstTicks, now) < SkillBurstFloorMs)
                return MagicTimingVerdict.TooSoonAfterLastSkill;
        }

        return MagicTimingVerdict.Allowed;
    }

    public MagicTimingVerdict CheckRelease(UserSession session, MagicData magic)
    {
        var now = _time.GetUtcNow().UtcTicks;

        if (!IsReleaseOfTheAcceptedCast(session, magic, now, out var accepted))
            return RemainingCooldownMs(session, magic.Id, now) > 0
                ? MagicTimingVerdict.OnCooldown
                : MagicTimingVerdict.NotCast;

        var readyTicks = session.CastingSkillId == magic.Id
            ? session.CastCommitTicks
            : accepted + CommitMilliseconds(session, magic) * TimeSpan.TicksPerMillisecond;

        return now + LatencyGraceMs * TimeSpan.TicksPerMillisecond < readyTicks
            ? MagicTimingVerdict.CastTooEarly
            : MagicTimingVerdict.Allowed;
    }

    public bool IsVolleyInFlight(UserSession session, int skillId)
    {
        if (!session.PendingArrowHits.TryGetValue(skillId, out var volley))
            return false;

        if (_time.GetUtcNow().UtcTicks <= volley.LandsByTicks)
            return volley.Arrows > NoArrows;

        session.PendingArrowHits.TryRemove(skillId, out _);
        return false;
    }

    public TimeSpan CastDuration(UserSession session, MagicData magic) =>
        TimeSpan.FromMilliseconds(CastMilliseconds(session, magic));

    public void OnCastAccepted(UserSession session, MagicData magic)
    {
        var now = _time.GetUtcNow().UtcTicks;
        ArmCooldown(session, magic, now);
        session.AcceptedCasts[magic.Id] = now;
        if (session.AcceptedCasts.Count > CooldownEntryPruneThreshold)
            PruneAcceptedCasts(session, now);

        if (IsPotion(magic))
            session.LastPotionTicks = now;

        if (ConsumesAnItem(magic))
            return;

        session.SkillBurstTicks = now;
        var castMs = CastMilliseconds(session, magic);
        if (castMs <= InstantCastMs)
            return;

        session.CastingSkillId = magic.Id;
        session.CastReadyTicks = now + castMs * TimeSpan.TicksPerMillisecond;
        session.CastCommitTicks = now + CommitMilliseconds(session, magic) * TimeSpan.TicksPerMillisecond;
        session.CastExpireTicks = session.CastReadyTicks + AbandonedCastGraceMs * TimeSpan.TicksPerMillisecond;
    }

    public void OnVolleyAccepted(UserSession session, MagicData magic, int arrows)
    {
        CloseCast(session, magic.Id);

        var volley = Math.Max(MinimumVolley, arrows);
        var landsBy = _time.GetUtcNow().UtcTicks + ArrowFlightAllowance.Ticks;
        session.PendingArrowHits.AddOrUpdate(
            magic.Id,
            new PendingVolley(volley, landsBy),
            (_, outstanding) => new PendingVolley(Math.Min(outstanding.Arrows + volley, volley * VolleysInFlight), landsBy));
    }

    public void OnVolleyHit(UserSession session, int skillId)
    {
        if (!session.PendingArrowHits.TryGetValue(skillId, out var volley))
            return;

        if (volley.Arrows > MinimumVolley)
            session.PendingArrowHits[skillId] = volley with { Arrows = volley.Arrows - 1 };
        else
            session.PendingArrowHits.TryRemove(skillId, out _);
    }

    public void OnReleaseAccepted(UserSession session, MagicData magic)
    {
        var wasCasting = session.CastingSkillId == magic.Id;
        CloseCast(session, magic.Id);
        if (!wasCasting)
            ArmCooldown(session, magic, _time.GetUtcNow().UtcTicks);
    }

    public bool OnCastAborted(UserSession session, int skillId)
    {
        if (!session.AcceptedCasts.ContainsKey(skillId))
            return false;

        if (session.CastingSkillId == skillId)
            session.SkillBurstTicks = 0;

        CloseCast(session, skillId);
        session.SkillCooldowns.TryRemove(skillId, out _);
        return true;
    }

    public void RefundCooldown(UserSession session, int skillId) =>
        session.SkillCooldowns.TryRemove(skillId, out _);

    private static void CloseCast(UserSession session, int skillId)
    {
        session.AcceptedCasts.TryRemove(skillId, out _);
        if (session.CastingSkillId == skillId)
            ClearCast(session);
    }

    private void ArmCooldown(UserSession session, MagicData magic, long now)
    {
        if (magic.ReCastTime <= 0)
            return;

        session.SkillCooldowns[magic.Id] =
            now + magic.ReCastTime * (TimeSpan.TicksPerSecond / TenthsPerSecond);

        if (session.SkillCooldowns.Count > CooldownEntryPruneThreshold)
            PruneCooldowns(session, now);
    }

    private static int CastMilliseconds(UserSession session, MagicData magic) =>
        session.InstantCast ? InstantCastMs : magic.CastTime * MillisecondsPerTenth;

    private static int CommitMilliseconds(UserSession session, MagicData magic)
    {
        var castMs = CastMilliseconds(session, magic);
        return magic.PrimaryType == MagicSkillType.Ranged ? Math.Min(castMs, RangedCommitMs) : castMs;
    }

    private static bool IsReleaseOfTheAcceptedCast(UserSession session, MagicData magic, long now, out long accepted)
    {
        if (!session.AcceptedCasts.TryGetValue(magic.Id, out accepted))
            return false;

        var windowMs = CastMilliseconds(session, magic) + AbandonedCastGraceMs;
        if (MillisecondsSince(accepted, now) <= windowMs)
            return true;

        session.AcceptedCasts.TryRemove(magic.Id, out _);
        return false;
    }

    private static void PruneAcceptedCasts(UserSession session, long now)
    {
        foreach (var (skillId, accepted) in session.AcceptedCasts)
            if (MillisecondsSince(accepted, now) > AbandonedCastGraceMs * StaleAcceptedCastGraces)
                session.AcceptedCasts.TryRemove(skillId, out _);
    }

    private static long RemainingCooldownMs(UserSession session, int skillId, long now)
    {
        if (!session.SkillCooldowns.TryGetValue(skillId, out var readyAt))
            return 0;

        var remaining = (readyAt - now) / TimeSpan.TicksPerMillisecond - LatencyGraceMs;
        if (remaining > 0)
            return remaining;

        session.SkillCooldowns.TryRemove(skillId, out _);
        return 0;
    }

    private static long MillisecondsSince(long ticks, long now) =>
        ticks == 0 ? long.MaxValue : (now - ticks) / TimeSpan.TicksPerMillisecond;

    private static bool IsCastInFlight(UserSession session, long now)
    {
        if (session.CastingSkillId == 0)
            return false;

        if (now < session.CastExpireTicks)
            return true;

        ClearCast(session);
        return false;
    }

    private static bool ConsumesAnItem(MagicData magic) => magic.UseItem != 0;

    private static bool IsPotion(MagicData magic) =>
        ConsumesAnItem(magic)
        && magic.ItemGroup == MagicWeaponRequirement.PotionItemGroup
        && magic.Moral == MoralSelf
        && magic.PrimaryType == MagicSkillType.OverTime
        && magic.CastTime == 0
        && magic.ReCastTime == 0;

    private static void ClearCast(UserSession session)
    {
        session.CastingSkillId = 0;
        session.CastReadyTicks = 0;
        session.CastExpireTicks = 0;
    }

    private static void PruneCooldowns(UserSession session, long now)
    {
        foreach (var (skillId, readyAt) in session.SkillCooldowns)
            if (readyAt <= now)
                session.SkillCooldowns.TryRemove(skillId, out _);
    }
}
