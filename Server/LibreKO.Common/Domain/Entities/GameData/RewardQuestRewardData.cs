using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities.GameData;

public class RewardQuestRewardData
{
    public int Id { get; set; }
    public int QuestId { get; set; }
    public RewardKind Kind { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<RewardQuestRewardData>
    {
        public void Configure(EntityTypeBuilder<RewardQuestRewardData> builder)
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).ValueGeneratedNever();
            builder.HasIndex(p => p.QuestId);
            builder.HasOne<RewardQuestData>()
                .WithMany()
                .HasForeignKey(p => p.QuestId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
