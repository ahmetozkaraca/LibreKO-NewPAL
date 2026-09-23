using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities;

public class EventCoinWallet
{
    public int CharacterId { get; set; }
    public int Coins { get; set; }

    internal class EntityConfiguration : IEntityTypeConfiguration<EventCoinWallet>
    {
        public void Configure(EntityTypeBuilder<EventCoinWallet> builder)
        {
            builder.HasKey(p => p.CharacterId);
            builder.Property(p => p.CharacterId).ValueGeneratedNever();
            builder.Property(p => p.Coins).IsConcurrencyToken();
            builder.HasOne<Character>()
                .WithOne()
                .HasForeignKey<EventCoinWallet>(p => p.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
