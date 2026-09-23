using System.Text.RegularExpressions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;

namespace LibreKO.Game.World;

public static class CharacterRules
{
    public const int MinNameLength = 1;
    public const int MinRenameLength = 3;
    public const int MaxNameLength = 20;
    public const int RenameScrollItemId = 379090000;
    public const byte MaxFace = 31;
    public const int MaxHairStyle = 6;
    public const int NoSlot = -1;

    private const int HairStyleShift = 24;
    private static readonly TimeSpan NamePatternTimeout = TimeSpan.FromMilliseconds(250);

    public static int StarterStatTotal =>
        ProgressionTable.BaseStatTotal + ProgressionTable.StatPointsForLevel(ProgressionTable.MinLevel);

    public static bool IsValidName(string? name, int minLength, string namePattern)
    {
        if (name is null
            || name.Length < minLength
            || name.Length > MaxNameLength
            || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        try
        {
            return Regex.IsMatch(name, $"^(?:{namePattern})$", RegexOptions.CultureInvariant, NamePatternTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool IsValidAppearance(byte face, int hair) =>
        face <= MaxFace && hair >= 0 && hair >> HairStyleShift <= MaxHairStyle;

    public static bool MeetsClassBaseStats(
        short classId, byte strength, byte stamina, byte dexterity, byte intelligence, byte magic)
    {
        var floor = ProgressionTable.BaseStatsForClass(classId);
        return strength >= floor.Strength
            && stamina >= floor.Stamina
            && dexterity >= floor.Dexterity
            && intelligence >= floor.Intelligence
            && magic >= floor.Magic;
    }

    public static int FindRenameScroll(ItemSlot[] inventory)
    {
        for (var slot = InventoryConstants.SlotMax; slot < InventoryConstants.SlotMax + InventoryConstants.HaveMax; slot++)
        {
            if (inventory[slot].ItemId == RenameScrollItemId && inventory[slot].Count > 0)
                return slot;
        }

        return NoSlot;
    }
}
