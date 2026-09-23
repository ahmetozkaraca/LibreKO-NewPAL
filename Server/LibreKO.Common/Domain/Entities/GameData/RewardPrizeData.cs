using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities.GameData;

public class RewardPrizeData
{
    public int Id { get; set; }
    public PrizePool Pool { get; set; }
    public byte MinLevel { get; set; }
    public byte MaxLevel { get; set; }
    public int Weight { get; set; }
    public RewardKind Kind { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }

    public bool AcceptsLevel(byte level) => level >= MinLevel && level <= MaxLevel;

    internal class EntityConfiguration : IEntityTypeConfiguration<RewardPrizeData>
    {
        public void Configure(EntityTypeBuilder<RewardPrizeData> builder)
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).ValueGeneratedNever();
            builder.HasIndex(p => p.Pool);
        }
    }
}
