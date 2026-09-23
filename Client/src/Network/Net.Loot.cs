using System;
using System.Collections.Generic;

namespace LibreKO.Network;

public partial class Net
{
    public const int GoldItemId = 900000000;

    public const int LootMaxItems = 8;
    public const int LootWireSlots = 12;
    public const byte LootBundleListed = 1;
    public const byte LootBundleRefused = 2;

    public const float LootRange = 11f;

    public event Action<int, int>? LootDropEvent;

    public event Action<int, List<LootEntry>>? LootContentsEvent;

    public event Action<int, int, int>? LootTakenEvent;

    public event Action<byte>? LootFailEvent;

    public event Action<int>? LootRefusedEvent;

    private void HandleItemDrop(Packet p)
    {
        if (p.RemainingBytes < 9) return;
        int sourceId = p.ReadInt();
        int bundleId = p.ReadInt();
        bool hasItems = p.ReadByte() != 0;
        if (hasItems) LootDropEvent?.Invoke(sourceId, bundleId);
    }

    private void HandleBundleOpen(Packet p)
    {
        if (p.RemainingBytes < 5) return;
        int bundleId = p.ReadInt();
        byte state = p.ReadByte();
        if (state == LootBundleRefused)
        {
            LootRefusedEvent?.Invoke(bundleId);
            return;
        }

        var entries = new List<LootEntry>(LootMaxItems);
        if (state == LootBundleListed)
        {
            for (int i = 0; i < LootWireSlots && p.RemainingBytes >= 6; i++)
            {
                int itemId = p.ReadInt();
                int count = p.ReadUShort();
                if (itemId != 0) entries.Add(new LootEntry(itemId, count));
            }
        }
        LootContentsEvent?.Invoke(bundleId, entries);
    }
}
