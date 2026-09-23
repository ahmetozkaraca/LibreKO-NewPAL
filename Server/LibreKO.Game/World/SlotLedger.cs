using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;

namespace LibreKO.Game.World;

public sealed class SlotLedger
{
    private readonly List<Entry> _entries = [];

    public SlotLedger Touch(ItemSlot slot, IReadOnlyList<ItemSlot> home)
    {
        if (!_entries.Exists(entry => ReferenceEquals(entry.Slot, slot)))
            _entries.Add(new Entry(slot, home, ItemStack.Of(slot)));
        return this;
    }

    public ItemStack Before(ItemSlot slot) =>
        _entries.Find(entry => ReferenceEquals(entry.Slot, slot))?.Before ?? default;

    public void Settle()
    {
        foreach (var entry in _entries)
            entry.After = ItemStack.Of(entry.Slot);
    }

    public bool Revert(IGameDataService gameData)
    {
        var reversals = new List<(Entry Entry, Reversal Reversal)>(_entries.Count);
        foreach (var entry in _entries)
        {
            var current = ItemStack.Of(entry.Slot);
            if (Plan(current, entry.Before, entry.After ?? current, itemId => ItemTransfer.IsStackable(gameData.GetItem(itemId))) is not { } reversal)
                return false;

            reversals.Add((entry, reversal));
        }

        var claimed = new HashSet<ItemSlot>(_entries.Select(entry => entry.Slot), ReferenceEqualityComparer.Instance);
        var displaced = new List<(ItemSlot Slot, ItemStack Stack)>();
        foreach (var (entry, reversal) in reversals.Where(pair => !pair.Reversal.Displaced.IsEmpty))
        {
            var free = entry.Home.FirstOrDefault(slot => slot.IsEmpty && !claimed.Contains(slot));
            if (free == null)
                return false;

            claimed.Add(free);
            displaced.Add((free, reversal.Displaced));
        }

        foreach (var (entry, reversal) in reversals)
            reversal.Kept.WriteTo(entry.Slot);
        foreach (var (slot, stack) in displaced)
            stack.WriteTo(slot);

        _entries.Clear();
        return true;
    }

    private static Reversal? Plan(ItemStack current, ItemStack before, ItemStack after, Func<int, bool> isStackable)
    {
        if (current == after)
            return new Reversal(before, default);

        if (after.IsEmpty)
            return before.IsEmpty ? new Reversal(current, default) : GiveBack(current, before, isStackable);

        if (before.IsEmpty || before.SharesIdentityWith(after))
            return Recount(current, before, after);

        if (!current.SharesIdentityWith(after) || current.Count < after.Count)
            return null;

        var remaining = current with { Count = (ushort)(current.Count - after.Count) };
        return remaining.Count == 0 ? new Reversal(before, default) : new Reversal(remaining, before);
    }

    private static Reversal GiveBack(ItemStack current, ItemStack before, Func<int, bool> isStackable) =>
        isStackable(before.ItemId)
        && current.SharesIdentityWith(before)
        && current.Count + before.Count <= InventoryConstants.MaxStackCount
            ? new Reversal(current with { Count = (ushort)(current.Count + before.Count) }, default)
            : new Reversal(current, before);

    private static Reversal? Recount(ItemStack current, ItemStack before, ItemStack after)
    {
        if (!current.IsEmpty && !current.SharesIdentityWith(after))
            return null;

        var count = current.Count - after.Count + before.Count;
        if (count is < 0 or > InventoryConstants.MaxStackCount)
            return null;

        var basis = current.IsEmpty ? before : current;
        return new Reversal(count == 0 ? default : basis with { Count = (ushort)count }, default);
    }

    private readonly record struct Reversal(ItemStack Kept, ItemStack Displaced);

    private sealed class Entry(ItemSlot slot, IReadOnlyList<ItemSlot> home, ItemStack before)
    {
        public ItemSlot Slot { get; } = slot;
        public IReadOnlyList<ItemSlot> Home { get; } = home;
        public ItemStack Before { get; } = before;
        public ItemStack? After { get; set; }
    }
}
