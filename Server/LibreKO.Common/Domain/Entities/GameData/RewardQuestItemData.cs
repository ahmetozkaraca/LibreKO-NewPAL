using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities.GameData;

public class RewardQuestItemData
{
    public int Id { get; set; }
    public int QuestId { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<RewardQuestItemData>
    {
        public void Configure(EntityTypeBuilder<RewardQuestItemData> builder)
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).ValueGeneratedNever();
            builder.HasIndex(p => new { p.QuestId, p.ItemId }).IsUnique();
            builder.HasOne<RewardQuestData>()
                .WithMany()
                .HasForeignKey(p => p.QuestId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
