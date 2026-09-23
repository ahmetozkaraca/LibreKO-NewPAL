using LibreKO.Common.Domain.Entities.GameData;

namespace LibreKO.Common.Infrastructure.Persistence.Seed.Entities;

public class SellingGroupItemSeed : SnapshotJsonSeed<SellingGroupItemData>
{
    protected override string JsonFileName => "SellingGroupItems.json";
}
