namespace LibreKO.Common.Infrastructure.Network;

public class ConnectionLimitsSettings
{
    public int MaxConnectionsPerIp { get; set; } = 10;
    public int ConnectionRateWindowSeconds { get; set; } = 10;
    public int MaxConnectionAttemptsPerWindow { get; set; } = 20;
    public int LoginTimeoutSeconds { get; set; } = 60;
    public int IdleTimeoutSeconds { get; set; } = 600;
    public int PartialFrameTimeoutSeconds { get; set; } = 30;
    public int MaxLoginFailuresPerConnection { get; set; } = 3;
    public int MaxLoginFailuresPerAccount { get; set; } = 10;
    public int MaxLoginFailuresPerIp { get; set; } = 30;
    public int LoginFailureWindowSeconds { get; set; } = 900;

    public TimeSpan LoginTimeout => AsTimeout(LoginTimeoutSeconds);
    public TimeSpan IdleTimeout => AsTimeout(IdleTimeoutSeconds);
    public TimeSpan PartialFrameTimeout => AsTimeout(PartialFrameTimeoutSeconds);
    public TimeSpan LoginFailureWindow => TimeSpan.FromSeconds(Math.Max(0, LoginFailureWindowSeconds));

    private static TimeSpan AsTimeout(int seconds) =>
        seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.MaxValue;
}
