using FluentAssertions;
using LibreKO.Common.Infrastructure.Logging;

namespace LibreKO.Common.Tests;

public class LogSanitizerTests
{
    private const char NullCharacter = (char)0x0000;
    private const char EscapeCharacter = (char)0x001B;
    private const char LineSeparator = (char)0x2028;
    private const char RightToLeftOverride = (char)0x202E;

    [Fact]
    public void Clean_RemovesLineBreaksSoAClientCannotForgeLogLines()
    {
        var cleaned = LogSanitizer.Clean("user\r\n[12:00:00 INF] Account logged in successfully: admin");

        cleaned.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public void Clean_ReplacesOtherControlAndFormattingCharacters()
    {
        var hostile = $"a{NullCharacter}b{EscapeCharacter}c{LineSeparator}d{RightToLeftOverride}e";

        LogSanitizer.Clean(hostile).Should().Be("a?b?c?d?e");
    }

    [Fact]
    public void Clean_TruncatesLongValues()
    {
        var cleaned = LogSanitizer.Clean(new string('x', short.MaxValue));

        cleaned.Length.Should().BeLessThan(LogSanitizer.DefaultMaxLength * 2);
        cleaned.Should().StartWith(new string('x', LogSanitizer.DefaultMaxLength));
    }

    [Fact]
    public void Clean_KeepsOrdinaryTextAndHandlesNull()
    {
        LogSanitizer.Clean("Warrior_01").Should().Be("Warrior_01");
        LogSanitizer.Clean(null).Should().BeEmpty();
    }
}
