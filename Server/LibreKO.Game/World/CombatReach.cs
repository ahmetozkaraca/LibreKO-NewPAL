using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;

namespace LibreKO.Game.World;

public static class CombatReach
{
    public const float MeleeReach = 3f;
    public const float DefaultSkillRange = 7f;
    public const float PlayerBodyRadius = 1f;
    public const float ClientRangeSlack = 1f;
    public const float ImplausibleSlack = 20f;
    public const float TargetDriftPerSecond = 12f;

    private const float WeaponRangeScale = 10f;

    public static float RangeOf(ItemData? weapon) => weapon == null ? 0f : weapon.Range / WeaponRangeScale;

    public static ItemData? RangedWeapon(UserSession session, IGameDataService gameData)
    {
        var left = ItemIn(session, InventoryConstants.LeftHand, gameData);
        if (left?.Category is ItemKind.Bow or ItemKind.LongBow)
            return left;

        var right = ItemIn(session, InventoryConstants.RightHand, gameData);
        return right?.Category == ItemKind.Crossbow ? right : null;
    }

    public static ItemData? HeldWeapon(UserSession session, IGameDataService gameData)
    {
        var right = session.GetEquippedItem(InventoryConstants.RightHand);
        var hand = right.IsEmpty ? session.GetEquippedItem(InventoryConstants.LeftHand) : right;
        return hand.IsEmpty ? null : gameData.GetItem(hand.ItemId);
    }

    public static float BasicAttackReach(UserSession session, IGameDataService gameData)
    {
        var ranged = RangedWeapon(session, gameData);
        var weaponRange = RangeOf(ranged ?? HeldWeapon(session, gameData));
        return Math.Max(MeleeReach, weaponRange);
    }

    public static float BodyRadius(NpcInstance npc) => npc.BulkRadius;

    private static ItemData? ItemIn(UserSession session, int slot, IGameDataService gameData)
    {
        var item = session.GetEquippedItem(slot);
        return item.IsEmpty ? null : gameData.GetItem(item.ItemId);
    }
}
