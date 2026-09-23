using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public enum MagicTargetVerdict : byte
{
    Valid,
    Refused,
    Implausible,
}

public readonly record struct MagicTargetCheck(MagicTargetVerdict Verdict, MagicCastBinding? Binding, float Distance)
{
    public const float NoDistance = 0f;

    public static MagicTargetCheck Refused { get; } = new(MagicTargetVerdict.Refused, null, NoDistance);
}

public interface IMagicTargetingService
{
    MagicTargetCheck CheckCast(UserSession caster, MagicData magic, int targetId, int[] data);
    MagicTargetCheck CheckRelease(UserSession caster, MagicData magic, MagicCastBinding binding, float driftSeconds);
    float CastRange(UserSession caster, MagicData magic);
}

public sealed class MagicTargetingService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IStealthService stealthService) : IMagicTargetingService
{
    public const int AreaTargetId = -1;

    private const int CentreXSlot = 0;
    private const int CentreYSlot = 1;
    private const int CentreZSlot = 2;
    private const int NoCoordinate = 0;
    private const float PercentScale = 100f;
    private const float NoDrift = 0f;
    private const float UnscaledRange = 1f;
    private const short NoClan = 0;

    public MagicTargetCheck CheckCast(UserSession caster, MagicData magic, int targetId, int[] data)
    {
        if (targetId == AreaTargetId)
            return CheckArea(caster, magic, data, NoDrift);

        if ((SkillMoral)magic.Moral == SkillMoral.Self || targetId == caster.CharacterId)
            return CheckSelf(caster, magic);

        return caster.IsBlinded ? MagicTargetCheck.Refused : CheckTarget(caster, magic, targetId, data, NoDrift);
    }

    public MagicTargetCheck CheckRelease(UserSession caster, MagicData magic, MagicCastBinding binding, float driftSeconds)
    {
        var drift = driftSeconds * CombatReach.TargetDriftPerSecond;
        if (binding.TargetId == AreaTargetId)
            return CheckArea(caster, magic, binding.Data, drift);

        return binding.TargetId == caster.CharacterId
            ? CheckSelf(caster, magic)
            : caster.IsBlinded ? MagicTargetCheck.Refused : CheckTarget(caster, magic, binding.TargetId, binding.Data, drift);
    }

    public float CastRange(UserSession caster, MagicData magic)
    {
        if (magic.Range > 0)
            return magic.Range;

        if (magic.PrimaryType == MagicSkillType.Melee || magic.SecondaryType == MagicSkillType.Melee)
            return Math.Max(CombatReach.MeleeReach, CombatReach.RangeOf(CombatReach.HeldWeapon(caster, gameDataService)));

        if (magic.PrimaryType == MagicSkillType.Ranged || magic.SecondaryType == MagicSkillType.Ranged)
        {
            var bowRange = CombatReach.RangeOf(CombatReach.RangedWeapon(caster, gameDataService));
            if (bowRange > 0)
                return Math.Max(CombatReach.DefaultSkillRange, bowRange * RangeScaleOf(magic));
        }

        return CombatReach.DefaultSkillRange;
    }

    private MagicTargetCheck CheckTarget(UserSession caster, MagicData magic, int targetId, int[] data, float drift)
    {
        var player = sessionManager.GetByCharacterId(targetId);
        if (player != null)
            return CheckPlayer(caster, magic, player, data, drift);

        var npc = sessionManager.Regions.GetNpc(targetId);
        return npc == null ? MagicTargetCheck.Refused : CheckNpc(caster, magic, npc, data, drift);
    }

    private static MagicTargetCheck CheckSelf(UserSession caster, MagicData magic) =>
        (SkillMoral)magic.Moral is SkillMoral.FriendExceptMe or SkillMoral.CorpseFriend || IsHostile(magic)
            ? MagicTargetCheck.Refused
            : new MagicTargetCheck(MagicTargetVerdict.Valid, new MagicCastBinding(caster.CharacterId, EmptyPayload()), MagicTargetCheck.NoDistance);

    private MagicTargetCheck CheckPlayer(UserSession caster, MagicData magic, UserSession target, int[] data, float drift)
    {
        if (target.ZoneId != caster.ZoneId || !Accepts(caster, magic, target))
            return MagicTargetCheck.Refused;

        var reach = CastRange(caster, magic) + CombatReach.PlayerBodyRadius + drift;
        return Measure(caster, target.X, target.Z, reach,
            new MagicCastBinding(target.CharacterId, TargetedPayload(caster, magic, data, drift)));
    }

    private MagicTargetCheck CheckNpc(UserSession caster, MagicData magic, NpcInstance npc, int[] data, float drift)
    {
        if (npc.ZoneId != caster.ZoneId || !npc.IsAlive || !IsHostile(magic) || !NpcHostility.IsAttackableBy(npc, caster))
            return MagicTargetCheck.Refused;

        var reach = CastRange(caster, magic) + CombatReach.BodyRadius(npc) + drift;
        return Measure(caster, npc.X, npc.Z, reach,
            new MagicCastBinding(npc.UniqueId, TargetedPayload(caster, magic, data, drift)));
    }

    private MagicTargetCheck CheckArea(UserSession caster, MagicData magic, int[] data, float drift)
    {
        if (!HasCentre(data))
            return new MagicTargetCheck(MagicTargetVerdict.Valid, new MagicCastBinding(AreaTargetId, EmptyPayload()), MagicTargetCheck.NoDistance);

        return Measure(caster, data[CentreXSlot], data[CentreZSlot], CastRange(caster, magic) + drift,
            new MagicCastBinding(AreaTargetId, CentreOf(data)));
    }

    private int[] TargetedPayload(UserSession caster, MagicData magic, int[] data, float drift) =>
        HasCentre(data)
        && Reach.Within(caster.X, caster.Z, data[CentreXSlot], data[CentreZSlot], Allowance(CastRange(caster, magic) + drift))
            ? CentreOf(data)
            : EmptyPayload();

    private static bool HasCentre(int[] data) =>
        data.Length > CentreZSlot && (data[CentreXSlot] != NoCoordinate || data[CentreZSlot] != NoCoordinate);

    private static int[] CentreOf(int[] data)
    {
        var centre = EmptyPayload();
        centre[CentreXSlot] = data[CentreXSlot];
        centre[CentreYSlot] = data[CentreYSlot];
        centre[CentreZSlot] = data[CentreZSlot];
        return centre;
    }

    private static int[] EmptyPayload() => new int[MagicProcessPacketWriter.PayloadSlotCount];

    private static float Allowance(float reach) => reach + CombatReach.ClientRangeSlack + Reach.LatencyAllowance;

    private static MagicTargetCheck Measure(UserSession caster, float x, float z, float reach, MagicCastBinding binding)
    {
        var allowed = Allowance(reach);
        var distance = MathF.Sqrt(Reach.DistanceSquared(caster.X, caster.Z, x, z));
        if (distance <= allowed)
            return new MagicTargetCheck(MagicTargetVerdict.Valid, binding, distance);

        return new MagicTargetCheck(
            distance > allowed + CombatReach.ImplausibleSlack ? MagicTargetVerdict.Implausible : MagicTargetVerdict.Refused,
            null,
            distance);
    }

    private bool Accepts(UserSession caster, MagicData magic, UserSession target)
    {
        var moral = (SkillMoral)magic.Moral;
        if (moral == SkillMoral.CorpseFriend)
            return target.Hp <= 0 && IsAlly(caster, target);

        if (target.Hp <= 0)
            return false;

        if (moral == SkillMoral.All)
            return stealthService.CanTarget(caster, target);

        if (IsHostile(magic))
            return moral != SkillMoral.Npc && PvpRules.CanAttackPlayer(caster, target) && stealthService.CanTarget(caster, target);

        return moral switch
        {
            SkillMoral.Party or SkillMoral.PartyAll => PvpRules.SharesPartyWith(caster, target),
            SkillMoral.Clan or SkillMoral.ClanAll => caster.KnightsId > NoClan && caster.KnightsId == target.KnightsId,
            SkillMoral.FriendWithMe or SkillMoral.FriendExceptMe or SkillMoral.AreaFriend or SkillMoral.AreaAll
                => IsAlly(caster, target),
            _ => false,
        };
    }

    private static bool IsAlly(UserSession caster, UserSession target) =>
        caster.Nation == target.Nation && !PvpRules.CanAttackPlayer(caster, target);

    private static bool IsHostile(MagicData magic) =>
        (SkillMoral)magic.Moral is SkillMoral.None or SkillMoral.Npc or SkillMoral.Enemy or SkillMoral.All
            or SkillMoral.AreaEnemy or SkillMoral.SelfArea or SkillMoral.CorpseEnemy or SkillMoral.SiegeWeapon;

    private float RangeScaleOf(MagicData magic) =>
        MagicTypeLookup.TryResolve(gameDataService.MagicType2Table, magic, magic.Id, out var type2Data)
        && type2Data.AddRange > 0
            ? type2Data.AddRange / PercentScale
            : UnscaledRange;
}
