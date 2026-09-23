using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities;

public class CharacterRewardQuest
{
    public int CharacterId { get; set; }
    public int QuestId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public int Kills { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<CharacterRewardQuest>
    {
        public void Configure(EntityTypeBuilder<CharacterRewardQuest> builder)
        {
            builder.HasKey(p => new { p.CharacterId, p.QuestId, p.PeriodStart });
            builder.Property(p => p.ClaimedAt).IsConcurrencyToken();
            builder.HasOne<Character>()
                .WithMany()
                .HasForeignKey(p => p.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
