using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities.GameData;

public class RewardQuestTargetData
{
    public int Id { get; set; }
    public int QuestId { get; set; }
    public int NpcId { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<RewardQuestTargetData>
    {
        public void Configure(EntityTypeBuilder<RewardQuestTargetData> builder)
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).ValueGeneratedNever();
            builder.HasIndex(p => new { p.QuestId, p.NpcId }).IsUnique();
            builder.HasIndex(p => p.NpcId);
            builder.HasOne<RewardQuestData>()
                .WithMany()
                .HasForeignKey(p => p.QuestId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
