using System.Collections.Concurrent;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.World;

public sealed record AccountOccupant(
    int ServerId,
    string ServerName,
    string CharacterName,
    AccountClaimStage Stage);

public sealed record AccountLockResult(bool Granted, AccountOccupant? Occupant);

public interface IAccountLockService
{
    Task<AccountLockResult> AcquireAsync(IClient client, int accountId);
    Task<AccountKickCode> KickAsync(int accountId);
    bool Owns(IClient client);
    IClient? HolderOf(int accountId);
    Task ReleaseAsync(IClient client);
    Task ClearOwnClaimsAsync();
}

public sealed class AccountClaim(int accountId, IClient client)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _evicted;

    public int AccountId { get; } = accountId;
    public IClient Client { get; } = client;
    public bool IsEvicted => Volatile.Read(ref _evicted) != 0;
    public Task Released => _released.Task;

    public bool TryEvict() => Interlocked.Exchange(ref _evicted, 1) == 0;

    public void MarkReleased() => _released.TrySetResult();
}

public class AccountLockService(
    IServiceScopeFactory scopeFactory,
    IServerRepository serverRepository,
    SessionManager sessionManager,
    IOptions<GameServerSettings> settings,
    ILogger<AccountLockService> logger) : IAccountLockService
{
    private const int EvictionGraceMs = 500;

    private readonly ConcurrentDictionary<int, AccountClaim> _claims = new();
    private readonly ConcurrentDictionary<Guid, int> _accountByClient = new();

    private TimeSpan HandoverTimeout => TimeSpan.FromSeconds(settings.Value.Player.SessionHandoverTimeoutSeconds);

    public async Task<AccountLockResult> AcquireAsync(IClient client, int accountId)
    {
        var wanted = new AccountClaim(accountId, client);

        while (true)
        {
            var current = _claims.GetOrAdd(accountId, wanted);
            if (ReferenceEquals(current, wanted) || (current.Client.Id == client.Id && !current.IsEvicted))
                break;

            if (current.Client.Id == client.Id || (current.Client.IsConnected && !current.IsEvicted))
                return new AccountLockResult(false, await DescribeAsync(current));

            logger.LogInformation(
                "Account {AccountId}: waiting for client {ClientId} to finish leaving before handing the account over",
                accountId, current.Client.Id);
            if (!await WaitForReleaseAsync(current))
                return new AccountLockResult(false, await DescribeAsync(current));
        }

        _accountByClient[client.Id] = accountId;
        await SetOnlineAsync(accountId);
        return new AccountLockResult(true, null);
    }

    public async Task<AccountKickCode> KickAsync(int accountId)
    {
        if (!_claims.TryGetValue(accountId, out var claim))
        {
            await ClearOnlineAsync(accountId);
            return AccountKickCode.NotOnline;
        }

        await EvictAsync(claim);
        return AccountKickCode.Done;
    }

    public bool Owns(IClient client)
        => _accountByClient.TryGetValue(client.Id, out var accountId)
           && _claims.TryGetValue(accountId, out var claim)
           && claim.Client.Id == client.Id
           && !claim.IsEvicted;

    public IClient? HolderOf(int accountId)
        => _claims.TryGetValue(accountId, out var claim) ? claim.Client : null;

    public async Task ReleaseAsync(IClient client)
    {
        if (!_accountByClient.TryRemove(client.Id, out var accountId))
            return;

        if (!_claims.TryGetValue(accountId, out var claim) || claim.Client.Id != client.Id)
            return;

        await ClearOnlineAsync(accountId);
        _claims.TryRemove(new KeyValuePair<int, AccountClaim>(accountId, claim));
        claim.MarkReleased();
    }

    public async Task ClearOwnClaimsAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            var cleared = await accounts.ClearOnlineServerForServerAsync(settings.Value.ServerId);
            if (cleared > 0)
                logger.LogInformation("Cleared {Count} stale account claim(s) left by a previous run", cleared);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear stale account claims for server {ServerId}", settings.Value.ServerId);
        }
    }

    private async Task EvictAsync(AccountClaim claim)
    {
        if (claim.TryEvict())
        {
            logger.LogInformation(
                "Evicting account {AccountId} (client {ClientId}) for a takeover",
                claim.AccountId, claim.Client.Id);

            await claim.Client.SendPacket(SessionPacketWriter.KickResult(AccountKickCode.Evicted));

            var evicted = claim.Client;
            evicted.ExpectedClose = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(EvictionGraceMs);
                evicted.Disconnect();
            });
        }

        if (!await WaitForReleaseAsync(claim))
            logger.LogWarning(
                "Account {AccountId}: client {ClientId} has not finished leaving; its claim stays until it does",
                claim.AccountId, claim.Client.Id);
    }

    private async Task<bool> WaitForReleaseAsync(AccountClaim claim)
    {
        try
        {
            await claim.Released.WaitAsync(HandoverTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<AccountOccupant> DescribeAsync(AccountClaim claim)
    {
        var serverId = settings.Value.ServerId;
        var name = string.Empty;
        try
        {
            var servers = await serverRepository.GetServers();
            name = servers.FirstOrDefault(s => s.Id == serverId)?.Name ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve this server's name for an occupancy reply");
        }

        var session = sessionManager.GetByClientId(claim.Client.Id);
        return new AccountOccupant(
            serverId,
            name,
            session?.Name ?? string.Empty,
            session != null ? AccountClaimStage.InGame : AccountClaimStage.PreGame);
    }

    private async Task SetOnlineAsync(int accountId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            await accounts.SetOnlineServerAsync(accountId, settings.Value.ServerId);
        }
        catch (ObjectDisposedException)
        {
            logger.LogDebug("Account {AccountId} came online as the host was shutting down", accountId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mark account {AccountId} online", accountId);
        }
    }

    private async Task ClearOnlineAsync(int accountId)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            await accounts.ClearOnlineServerAsync(accountId);
        }
        catch (ObjectDisposedException)
        {
            logger.LogDebug(
                "Account {AccountId} disconnected after the host shut down; the bulk claim clear covers it",
                accountId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear the online flag for account {AccountId}", accountId);
        }
    }
}
