using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public static class RewardQuestTitles
{
    public const string ClaimedSuffix = " (Claimed)";

    public static string ForEventBoard(RewardQuestView view) =>
        view.Claimed ? view.Title + ClaimedSuffix : view.Title + Progress(view);

    public static string ForDailyBoard(RewardQuestView view)
    {
        if (view.Claimed)
            return view.Title;

        var title = view.Title + Progress(view);
        return view.LevelLocked && !view.Claimable ? $"{title} [Lv {view.MinLevel}-{view.MaxLevel}]" : title;
    }

    private static string Progress(RewardQuestView view)
    {
        var parts = new List<string>();
        if (view.KillCount > 0)
            parts.Add($"{Math.Min(view.Kills, view.KillCount)}/{view.KillCount}");
        if (view.ItemsRequired > 0)
            parts.Add($"{view.ItemsHeld}/{view.ItemsRequired}");

        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }
}
