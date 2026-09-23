using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public sealed record MagicReagent(IReadOnlyList<int> RequiredItems, int ConsumedItem, int Count);

public sealed record MagicCost(int Mana, int Health, int Gold, IReadOnlyList<MagicReagent> Reagents);

public interface IMagicCostService
{
    MagicCost CostOf(MagicData magic);
    MagicCost VolleyCostOf(MagicData magic, int arrows);
    bool CanAfford(UserSession caster, MagicCost cost);
    Task<bool> TryPayAsync(UserSession caster, MagicCost cost);
}

public sealed class MagicCostService(
    IGameDataService gameDataService,
    IMagicItemUsageService magicItemUsageService,
    ICombatNotificationService combatNotificationService,
    IUserNotificationService userNotificationService) : IMagicCostService
{
    public const int DisguiseScrollItem = 381001000;

    private const int SingleItem = 1;
    private const int NothingConsumed = 0;
    private const int NoItem = 0;
    private const int NoGold = 0;
    private const int NoCost = 0;

    public MagicCost CostOf(MagicData magic) =>
        new(Math.Max(NoCost, (int)magic.Msp), Math.Max(NoCost, (int)magic.Hp), GoldOf(magic), ReagentsOf(magic));

    public MagicCost VolleyCostOf(MagicData magic, int arrows) =>
        new(
            Math.Max(NoCost, (int)magic.Msp),
            Math.Max(NoCost, (int)magic.Hp),
            NoGold,
            magic.UseItem == NoItem ? [] : [new MagicReagent([magic.UseItem], magic.UseItem, arrows)]);

    public bool CanAfford(UserSession caster, MagicCost cost) =>
        caster.WithLock(session => HasFunds(session, cost) && (cost.Reagents.Count == 0 || AffordableReagent(session, cost) != null));

    public async Task<bool> TryPayAsync(UserSession caster, MagicCost cost)
    {
        var takenSlots = caster.WithLock(session => Take(session, cost));
        if (takenSlots == null)
            return false;

        if (cost.Mana > 0)
            await combatNotificationService.SendMspChangeAsync(caster);
        if (cost.Health > 0)
            await combatNotificationService.SendHpChangeAsync(caster);
        if (cost.Gold > 0)
            await userNotificationService.SendGoldLossAsync(caster, cost.Gold);

        await magicItemUsageService.SendItemChangesAsync(caster, takenSlots);
        return true;
    }

    private IReadOnlyList<int>? Take(UserSession session, MagicCost cost)
    {
        if (!HasFunds(session, cost))
            return null;

        var reagent = AffordableReagent(session, cost);
        if (cost.Reagents.Count > 0 && reagent == null)
            return null;

        session.Mp -= (short)cost.Mana;
        if (cost.Health > 0)
            session.ApplyDamage(cost.Health);
        session.Money -= cost.Gold;

        return reagent == null ? [] : magicItemUsageService.TakeItem(session, reagent.ConsumedItem, reagent.Count);
    }

    private static bool HasFunds(UserSession session, MagicCost cost) =>
        session.Mp >= cost.Mana
        && (cost.Health == NoCost || session.Hp > cost.Health)
        && session.Money >= cost.Gold;

    private MagicReagent? AffordableReagent(UserSession session, MagicCost cost) =>
        cost.Reagents.FirstOrDefault(reagent =>
            reagent.RequiredItems.All(item => magicItemUsageService.CanUseItem(session, item))
            && magicItemUsageService.CanUseItem(session, reagent.ConsumedItem, reagent.Count));

    private int GoldOf(MagicData magic)
    {
        if (!HasType(magic, MagicSkillType.OverTime)
            || !MagicTypeLookup.TryResolve(gameDataService.MagicType3Table, magic, magic.Id, out var type3Data))
            return NoGold;

        return (MagicDirectType)type3Data.DirectType is MagicDirectType.HealthPurchase or MagicDirectType.ManaPurchase
            ? Math.Max(NoGold, (int)type3Data.TimeDamage)
            : NoGold;
    }

    private IReadOnlyList<MagicReagent> ReagentsOf(MagicData magic)
    {
        if (IsPaidByTheTarget(magic))
            return [];

        if (IsMonsterDisguise(magic))
        {
            var consumesTheGem = magic.UseItem != NoItem;
            return
            [
                new MagicReagent(
                    [.. new[] { magic.BeforeAction, magic.UseItem }.Where(item => item != NoItem)],
                    consumesTheGem ? magic.ConsumedItem : NoItem,
                    consumesTheGem ? SingleItem : NothingConsumed),
                new MagicReagent([DisguiseScrollItem], DisguiseScrollItem, SingleItem),
            ];
        }

        return magic.UseItem == NoItem
            ? []
            : [new MagicReagent([magic.UseItem], magic.ConsumedItem, SingleItem)];
    }

    private bool IsPaidByTheTarget(MagicData magic) =>
        magic.PrimaryType == MagicSkillType.Special
        && MagicTypeLookup.TryResolve(gameDataService.MagicType5Table, magic, magic.Id, out var type5Data)
        && (SpecialMagicType)type5Data.Type == SpecialMagicType.Resurrection
        && type5Data.NeedStone > 0;

    private bool IsMonsterDisguise(MagicData magic) =>
        magic.PrimaryType == MagicSkillType.Transform
        && MagicTypeLookup.TryResolve(gameDataService.MagicType6Table, magic, magic.Id, out var type6Data)
        && (TransformationUse)type6Data.UserSkillUse == TransformationUse.Monster;

    private static bool HasType(MagicData magic, MagicSkillType type) =>
        magic.PrimaryType == type || magic.SecondaryType == type;
}
