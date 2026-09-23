using System.Collections.Generic;

namespace LibreKO.Domain;

public readonly record struct PusPurchaseLine(int CatalogEntryId, int Count);

public static class PusPurchase
{
    public const int MaxCountPerRequest = byte.MaxValue;

    public static Queue<PusPurchaseLine> Plan(IEnumerable<PusPurchaseLine> basket)
    {
        var requests = new Queue<PusPurchaseLine>();
        foreach (var line in basket)
        {
            for (int left = line.Count; left > 0; left -= MaxCountPerRequest)
                requests.Enqueue(line with { Count = System.Math.Min(left, MaxCountPerRequest) });
        }
        return requests;
    }
}
