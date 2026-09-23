using LibreKO.Domain;
using LibreKO.Network;
using Xunit;

namespace LibreKO.Tests;

public class UpgradeOutcomeTests
{
    private const byte ItemsDoNotMatch = 4;
    private const int Sword = 110110001;
    private const int Downgraded = Sword - 1;
    private const int Scroll = 379021000;
    private const short Worn = 40;
    private const short Full = 100;
    private const short Scrolls = 3;
    private const int ItemPosition = 0;
    private const int ScrollPosition = 1;
    private const int NoPosition = -1;

    private static int Abs(int position) => InventoryConstants.InventoryStart + position;

    private static ItemSlot[] Bag()
    {
        var bag = new ItemSlot[InventoryConstants.InventoryTotal];
        bag[Abs(ItemPosition)] = new ItemSlot { ItemId = Sword, Count = 1, Durability = Worn };
        bag[Abs(ScrollPosition)] = new ItemSlot { ItemId = Scroll, Count = Scrolls };
        return bag;
    }

    private static UpgradeSlotResult[] Slots(int resultItemId) =>
    [
        new(resultItemId, ItemPosition),
        new(Scroll, ScrollPosition),
        .. Enumerable.Repeat(new UpgradeSlotResult(0, NoPosition), 8),
    ];

    private static short FullDurability(int _) => Full;

    [Fact]
    public void ARefusedUpgradeTouchesNothing()
    {
        var changes = UpgradeOutcome.Changes(Bag(), ItemsDoNotMatch, Slots(Sword), FullDurability);

        Assert.Empty(changes);
    }

    [Fact]
    public void AFailureThatKeepsTheItemLeavesItInTheBag()
    {
        var changes = UpgradeOutcome.Changes(Bag(), UpgradeOutcome.Failed, Slots(Downgraded), FullDurability);

        Assert.Equal(Downgraded, changes[Abs(ItemPosition)].ItemId);
        Assert.Equal(Worn, changes[Abs(ItemPosition)].Durability);
        Assert.Equal(Scrolls - 1, changes[Abs(ScrollPosition)].Count);
    }

    [Fact]
    public void AFailureThatDestroysTheItemEmptiesItsSlot()
    {
        var changes = UpgradeOutcome.Changes(Bag(), UpgradeOutcome.Failed, Slots(0), FullDurability);

        Assert.True(changes[Abs(ItemPosition)].IsEmpty);
    }

    [Fact]
    public void ASuccessRestoresFullDurabilityAndConsumesTheScroll()
    {
        var changes = UpgradeOutcome.Changes(Bag(), UpgradeOutcome.Succeeded, Slots(Sword + 1), FullDurability);

        Assert.Equal(Full, changes[Abs(ItemPosition)].Durability);
        Assert.Equal(Scrolls - 1, changes[Abs(ScrollPosition)].Count);
    }
}
