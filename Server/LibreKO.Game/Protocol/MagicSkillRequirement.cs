using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public enum MagicAccess : byte
{
    Granted,
    Unavailable,
    Forged,
}

public static class MagicSkillRequirement
{
    public const int MasteryPointSlots = 9;
    public const int ItemSkillFirstId = 400000;

    private const int NationBuffFirstTree = 4000;
    private const int NationBuffEndTree = 10000;
    private const int FirstMasteryTree = 5;
    private const int LastMasteryTree = 8;
    private const int SkillIdsPerClass = 1000;
    private const int NoClassGroup = 0;
    private const int BeginnerTier = 0;
    private const int NoviceTier = 1;
    private const int MasterTier = 2;

    public static int MasteryTreeOf(int skillTree)
    {
        if (skillTree is >= NationBuffFirstTree and < NationBuffEndTree)
            return 0;

        var digit = skillTree % 10;
        return digit is >= FirstMasteryTree and <= LastMasteryTree ? digit : 0;
    }

    public static bool IsMet(MagicData magic, int level, byte[] skillPoints)
    {
        if (magic.SkillLevel <= 0)
            return true;

        var tree = MasteryTreeOf(magic.Skill);
        if (tree == 0)
            return level >= magic.SkillLevel;

        return tree < skillPoints.Length && skillPoints[tree] >= magic.SkillLevel;
    }

    public static MagicAccess AccessFor(MagicData magic, short casterClass)
    {
        var skillClass = (short)(magic.Id / SkillIdsPerClass);
        if (IsPlayerClass(skillClass))
            return IsOnClassLine(skillClass, casterClass) ? MagicAccess.Granted : MagicAccess.Forged;

        if (magic.UseItem != 0)
            return MagicAccess.Granted;

        return magic.Id >= ItemSkillFirstId ? MagicAccess.Unavailable : MagicAccess.Forged;
    }

    public static string Describe(MagicData magic)
    {
        var tree = MasteryTreeOf(magic.Skill);
        return tree == 0
            ? $"character level {magic.SkillLevel}"
            : $"{magic.SkillLevel} points in mastery {tree}";
    }

    private static bool IsPlayerClass(short classId) =>
        ClassIdHelper.GetNation(classId) is AccountNation.Karus or AccountNation.ElMorad
        && ClassIdHelper.GroupOf(classId) != NoClassGroup;

    private static bool IsOnClassLine(short skillClass, short casterClass) =>
        IsPlayerClass(casterClass)
        && ClassIdHelper.GetNation(skillClass) == ClassIdHelper.GetNation(casterClass)
        && ClassIdHelper.GroupOf(skillClass) == ClassIdHelper.GroupOf(casterClass)
        && TierOf(skillClass) <= TierOf(casterClass);

    private static int TierOf(short classId) =>
        ClassIdHelper.IsMastered(classId) ? MasterTier
        : ClassIdHelper.IsNovice(classId) ? NoviceTier
        : BeginnerTier;
}
