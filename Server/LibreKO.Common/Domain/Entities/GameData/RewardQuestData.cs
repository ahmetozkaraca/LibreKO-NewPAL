using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LibreKO.Common.Domain.Entities.GameData;

public class RewardQuestData
{
    public const int TitleMaxLength = 64;

    public static readonly DateOnly PermanentPeriod = DateOnly.FromDateTime(DateTime.UnixEpoch);

    private const int DaysPerWeek = 7;
    private const int FirstMonth = 1;
    private const int LastMonth = 12;
    private const int FirstDayOfMonth = 1;

    public int Id { get; set; }
    public QuestBoard Board { get; set; }
    public string Title { get; set; } = string.Empty;
    public byte MinLevel { get; set; }
    public byte MaxLevel { get; set; }
    public QuestRecurrence Recurrence { get; set; }
    public int KillCount { get; set; }
    public bool PartyShared { get; set; }
    public byte StartMonth { get; set; }
    public byte StartDay { get; set; }
    public short DurationDays { get; set; }

    public bool AcceptsLevel(byte level) => level >= MinLevel && level <= MaxLevel;

    public DateOnly? CurrentPeriod(DateOnly today)
    {
        DateOnly? windowStart = null;
        if (DurationDays > 0)
        {
            windowStart = ActiveWindowStart(today);
            if (windowStart == null)
                return null;
        }

        return Recurrence switch
        {
            QuestRecurrence.Daily => today,
            QuestRecurrence.Weekly => today.AddDays(-DaysSinceMonday(today)),
            _ => windowStart ?? PermanentPeriod,
        };
    }

    private static int DaysSinceMonday(DateOnly day) =>
        ((int)day.DayOfWeek - (int)DayOfWeek.Monday + DaysPerWeek) % DaysPerWeek;

    private DateOnly? ActiveWindowStart(DateOnly today)
    {
        if (StartMonth is < FirstMonth or > LastMonth)
            return null;

        var start = WindowStart(today.Year);
        if (start > today)
            start = WindowStart(today.Year - 1);

        return today < start.AddDays(DurationDays) ? start : null;
    }

    private DateOnly WindowStart(int year) =>
        new(year, StartMonth, Math.Clamp(StartDay, FirstDayOfMonth, DateTime.DaysInMonth(year, StartMonth)));

    internal class EntityConfiguration : IEntityTypeConfiguration<RewardQuestData>
    {
        public void Configure(EntityTypeBuilder<RewardQuestData> builder)
        {
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Id).ValueGeneratedNever();
            builder.Property(p => p.Title).HasMaxLength(TitleMaxLength);
        }
    }
}
