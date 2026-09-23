using FluentAssertions;
using LibreKO.Game.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LibreKO.Game.Tests;

public class GameServerSettingsTests
{
    [Fact]
    public void TheDefaultSettingsPassValidation()
    {
        var read = () => Resolve(_ => { });

        read.Should().NotThrow();
    }

    [Fact]
    public void AnOutOfRangeAntiCheatValueIsRefusedAtStartup()
    {
        var read = () => Resolve(settings => settings.AntiCheat.KickScore = 0);

        read.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(nameof(AntiCheatSettings.KickScore));
    }

    [Fact]
    public void AnOutOfRangeMovementValueIsRefusedAtStartup()
    {
        var read = () => Resolve(settings => settings.AntiCheat.Movement.SpeedTolerance = 0);

        read.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(nameof(MovementCheckSettings.SpeedTolerance));
    }

    [Fact]
    public void AnOutOfRangeHandoverTimeoutIsRefusedAtStartup()
    {
        var read = () => Resolve(settings =>
            settings.Player.SessionHandoverTimeoutSeconds = PlayerSettings.MaxSessionHandoverTimeoutSeconds + 1);

        read.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(nameof(PlayerSettings.SessionHandoverTimeoutSeconds));
    }

    private static GameServerSettings Resolve(Action<GameServerSettings> configure)
    {
        var services = new ServiceCollection();
        services.AddOptions<GameServerSettings>()
            .Configure(settings =>
            {
                settings.BindPort = 1;
                configure(settings);
            })
            .ValidateDataAnnotations();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<GameServerSettings>>().Value;
    }
}
