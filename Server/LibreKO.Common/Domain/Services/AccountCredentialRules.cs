namespace LibreKO.Common.Domain.Services;

public static class AccountCredentialRules
{
    public const int MaxLoginLength = 50;
    public const int MaxNewLoginLength = 20;
    public const int MaxPasswordLength = 128;

    private const char FirstPrintable = ' ';
    private const char LastPrintable = '~';
    private static readonly char[] NewLoginPunctuation = ['_', '.', '-'];

    public static bool IsAcceptableLogin(string? login) =>
        !string.IsNullOrEmpty(login) && login.Length <= MaxLoginLength && login.All(IsPrintable);

    public static bool IsAcceptablePassword(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length <= MaxPasswordLength && password.All(IsPrintable);

    public static bool IsValidNewLogin(string? login) =>
        !string.IsNullOrEmpty(login)
        && login.Length <= MaxNewLoginLength
        && login.All(c => char.IsAsciiLetterOrDigit(c) || NewLoginPunctuation.Contains(c));

    private static bool IsPrintable(char c) => c is >= FirstPrintable and <= LastPrintable;
}
