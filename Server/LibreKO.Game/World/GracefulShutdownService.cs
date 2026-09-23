using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public sealed class GracefulShutdownService(
    SessionManager sessionManager,
    ISessionTerminationService sessionTerminationService,
    IAccountLockService accountLockService,
    ILogger<GracefulShutdownService> logger) : IHostedService
{
    private const int MaxConcurrentLogouts = 32;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var sessions = sessionManager.GetAll().ToArray();
        if (sessions.Length > 0)
        {
            logger.LogInformation("Graceful shutdown: logging out {SessionCount} active session(s)", sessions.Length);

            await Parallel.ForEachAsync(
                sessions,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentLogouts },
                async (session, _) => await LogoutAsync(session, cancellationToken));
        }

        await accountLockService.ClearOwnClaimsAsync();
    }

    private async Task LogoutAsync(UserSession session, CancellationToken cancellationToken)
    {
        try
        {
            await sessionTerminationService.LogoutAsync(session.Client, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Graceful shutdown failed to log out {Name} (CharId={CharId})",
                session.Name,
                session.CharacterId);
        }
        finally
        {
            session.Client.Disconnect();
        }
    }
}
