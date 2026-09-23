using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;

namespace LibreKO.Login.Tests;

public class ShippedSettingsTests
{
    private const string Information = "Information";

    [Theory]
    [InlineData("LibreKO.Login")]
    [InlineData("LibreKO.Game")]
    public void TheShippedSettingsLogAtInformation(string project)
    {
        var logging = Read(project, "appsettings.json").GetProperty("Logging");

        logging.GetProperty("FileLevel").GetString().Should().Be(Information,
            "Trace logs every packet, and packet dumps are no place for player traffic");
        logging.GetProperty("LogLevel").GetProperty("Default").GetString().Should().Be(Information);
    }

    [Fact]
    public void AProductionLoginServerDoesNotCreateAccountsOnFirstLogin()
    {
        Read("LibreKO.Login", "appsettings.json")
            .GetProperty("LoginServer").GetProperty("Account").GetProperty("AutoCreate").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public void TheDevelopmentLoginServerStillCreatesAccountsOnFirstLogin()
    {
        Read("LibreKO.Login", "appsettings.Development.json")
            .GetProperty("LoginServer").GetProperty("Account").GetProperty("AutoCreate").GetBoolean()
            .Should().BeTrue("the local start scripts run with DOTNET_ENVIRONMENT=Development and rely on it");
    }

    private static JsonElement Read(string project, string fileName, [CallerFilePath] string source = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", project, fileName));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
