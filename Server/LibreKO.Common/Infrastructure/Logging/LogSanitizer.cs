using System.Globalization;

namespace LibreKO.Common.Infrastructure.Logging;

public static class LogSanitizer
{
    public const int DefaultMaxLength = 64;

    private const char Replacement = '?';
    private const string TruncationMarker = "...";

    public static string Clean(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var kept = Math.Min(value.Length, maxLength);
        var cleaned = string.Create(kept, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = IsSafe(source[i]) ? source[i] : Replacement;
        });

        return kept < value.Length ? cleaned + TruncationMarker : cleaned;
    }

    private static bool IsSafe(char c) =>
        !char.IsControl(c)
        && char.GetUnicodeCategory(c) is not (UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Format);
}
