using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities;

public class RouletteSpin
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public RewardKind Kind { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }
    public DateTime SpunAt { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<RouletteSpin>
    {
        public void Configure(EntityTypeBuilder<RouletteSpin> builder)
        {
            builder.HasKey(p => p.Id);
            builder.HasIndex(p => new { p.CharacterId, p.SpunAt });
            builder.HasOne<Character>()
                .WithMany()
                .HasForeignKey(p => p.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
