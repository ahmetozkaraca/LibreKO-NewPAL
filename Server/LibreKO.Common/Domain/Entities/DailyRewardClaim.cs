using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities;

public class DailyRewardClaim
{
    public int AccountId { get; set; }
    public PrizePool Pool { get; set; }
    public DateOnly Day { get; set; }
    public int CharacterId { get; set; }
    public RewardKind Kind { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }
    public DateTime ClaimedAt { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<DailyRewardClaim>
    {
        public void Configure(EntityTypeBuilder<DailyRewardClaim> builder)
        {
            builder.HasKey(p => new { p.AccountId, p.Pool, p.Day });
            builder.HasOne<Account>()
                .WithMany()
                .HasForeignKey(p => p.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
