using System.Collections.Frozen;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

internal static class MagicWeaponRequirement
{
    public const byte NoWeaponNeeded = 9;
    public const byte PotionItemGroup = NoWeaponNeeded;

    private const byte AnyWeapon = 0;
    private const int KindsPerGroup = 10;
    private const byte ThrowingSpearKind = 130;

    private static readonly FrozenSet<byte> WeaponKinds = new[]
    {
        (byte)ItemKind.Dagger,
        (byte)ItemKind.SwordOneHand,
        (byte)ItemKind.SwordTwoHand,
        (byte)ItemKind.AxeOneHand,
        (byte)ItemKind.AxeTwoHand,
        (byte)ItemKind.ClubOneHand,
        (byte)ItemKind.ClubTwoHand,
        (byte)ItemKind.SpearOneHand,
        (byte)ItemKind.SpearTwoHand,
        (byte)ItemKind.Bow,
        (byte)ItemKind.Crossbow,
        (byte)ItemKind.LongBow,
        (byte)ItemKind.Staff,
        ThrowingSpearKind,
        (byte)ItemKind.Jamadhar,
        (byte)ItemKind.Mace,
    }.ToFrozenSet();

    public static bool IsMet(MagicData magic, UserSession caster, IGameDataService gameData)
    {
        if (magic.ItemGroup == NoWeaponNeeded)
            return true;

        var right = KindIn(caster, InventoryConstants.RightHand, gameData);
        var left = KindIn(caster, InventoryConstants.LeftHand, gameData);
        if (!WeaponKinds.Contains(right) && !WeaponKinds.Contains(left))
            return false;

        return magic.ItemGroup == AnyWeapon
            || magic.ItemGroup == right / KindsPerGroup
            || magic.ItemGroup == left / KindsPerGroup;
    }

    private static byte KindIn(UserSession caster, int slot, IGameDataService gameData)
    {
        var item = caster.GetEquippedItem(slot);
        return item.IsEmpty ? (byte)ItemKind.None : gameData.GetItem(item.ItemId)?.Kind ?? (byte)ItemKind.None;
    }
}
