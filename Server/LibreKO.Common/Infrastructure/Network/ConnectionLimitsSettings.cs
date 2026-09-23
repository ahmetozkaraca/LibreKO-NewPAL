namespace LibreKO.Common.Infrastructure.Network;

public class ConnectionLimitsSettings
{
    public const int Disabled = 0;

    public int MaxConnectionsPerIp { get; set; } = 10;
    public int ConnectionRateWindowSeconds { get; set; } = 10;
    public int MaxConnectionAttemptsPerWindow { get; set; } = 20;
    public int LoginTimeoutSeconds { get; set; } = 60;
    public int IdleTimeoutSeconds { get; set; } = 600;
    public int PartialFrameTimeoutSeconds { get; set; } = 30;
    public int MaxLoginFailuresPerConnection { get; set; } = 3;
    public int MaxLoginFailuresPerAccountAndIp { get; set; } = 10;

    [Obsolete("Renamed to " + nameof(MaxLoginFailuresPerAccountAndIp) + "; kept so existing configuration still binds.")]
    public int MaxLoginFailuresPerAccount
    {
        get => MaxLoginFailuresPerAccountAndIp;
        set => MaxLoginFailuresPerAccountAndIp = value;
    }

    public int MaxLoginFailuresPerIp { get; set; } = 30;
    public int AccountSlowdownFailures { get; set; } = 20;
    public int AccountSlowdownMilliseconds { get; set; } = 1000;
    public int LoginFailureWindowSeconds { get; set; } = 900;

    public TimeSpan LoginTimeout => AsTimeout(LoginTimeoutSeconds);
    public TimeSpan IdleTimeout => AsTimeout(IdleTimeoutSeconds);
    public TimeSpan PartialFrameTimeout => AsTimeout(PartialFrameTimeoutSeconds);
    public TimeSpan LoginFailureWindow => NonNegative(TimeSpan.FromSeconds(LoginFailureWindowSeconds));
    public TimeSpan AccountSlowdown => NonNegative(TimeSpan.FromMilliseconds(AccountSlowdownMilliseconds));

    private static TimeSpan AsTimeout(int seconds) =>
        seconds > Disabled ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;

    private static TimeSpan NonNegative(TimeSpan span) => span > TimeSpan.Zero ? span : TimeSpan.Zero;
}

public interface IConnectionLimitsOwner
{
    ConnectionLimitsSettings Connections { get; }
}
