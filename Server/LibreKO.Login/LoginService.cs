using System.Net;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Logging;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Login.Configuration;
using LibreKO.Login.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Login.Protocol.Writers;

namespace LibreKO.Login;

public sealed record LoginOutcome(Packet Response, LoginResult Result, int AccountId);

public interface ILoginService
{
    Task<Packet> VersionCheckAsync();
    Task<Packet> DownloadInfoAsync(short version);
    Task<LoginOutcome> LoginAsync(string login, string password, IPAddress? address, LoginOpcodes responseOpcode = LoginOpcodes.LS_LOGIN, LoginRequestFlags flags = LoginRequestFlags.None);
    Task<Packet> ServerListAsync(short echo);
    Task<Packet> NewsAsync();
    Task<Packet> UnknownF7Async();
    Task<Packet> LauncherNewsAsync();
    Task<Packet> SocketListAsync();
}

public class LoginService(
    IAccountRepository accountRepository,
    IServerRepository serverRepository,
    IPatchRepository patchRepository,
    IKingRepository kingRepository,
    LoginAttemptLimiter loginAttempts,
    AccountCreationThrottle creationThrottle,
    IOptions<LoginServerSettings> settings,
    ILogger<LoginService> logger) : ILoginService
{
    private const string NewsTitle = "LoginNotice";
    private const string NewsBody = "<empty>";

    private static readonly SemaphoreSlim AccountCreationGate = new(1, 1);

    public Task<Packet> VersionCheckAsync()
    {
        var packet = LoginPacketWriter.VersionCheck((short)settings.Value.Version);

        return Task.FromResult(packet);
    }

    public async Task<Packet> DownloadInfoAsync(short version)
    {
        var ftpSettings = settings.Value.Ftp;
        var patchList = await patchRepository.GetPatchList();
        var fileNames = patchList
            .Where(patch => patch.FileId > version)
            .Select(patch => patch.FileName)
            .ToList();

        return LoginPacketWriter.DownloadInfo(ftpSettings.Url, ftpSettings.Path, fileNames);
    }

    public async Task<LoginOutcome> LoginAsync(string login, string password, IPAddress? address, LoginOpcodes responseOpcode = LoginOpcodes.LS_LOGIN, LoginRequestFlags flags = LoginRequestFlags.None)
    {
        var loggedLogin = LogSanitizer.Clean(login);

        if (!AccountCredentialRules.IsAcceptableLogin(login) || !AccountCredentialRules.IsAcceptablePassword(password))
        {
            loginAttempts.RecordFailure(address, login);
            logger.LogWarning("Login attempt with malformed credentials for {Login} from {Address}", loggedLogin, address);
            return Rejected(responseOpcode, LoginResult.InvalidPassword);
        }

        var admission = await loginAttempts.AdmitAsync(address, login);
        if (admission == null)
        {
            logger.LogDebug("Refused login for {Login} from {Address}: too many recent failures", loggedLogin, address);
            return Rejected(responseOpcode, LoginResult.InvalidPassword);
        }

        Account? account;
        using (admission)
        {
            account = await accountRepository.GetByLogin(login);
            if (account == null && settings.Value.Account.AutoCreate)
                account = await CreateAccountAsync(login, password, address);

            var verified = PasswordHasher.Verify(password, account?.Password);
            if (account == null || !verified)
            {
                loginAttempts.RecordFailure(address, login);
                logger.LogWarning("Failed login for {Login} from {Address}", loggedLogin, address);
                return Rejected(responseOpcode, LoginResult.InvalidPassword);
            }

            loginAttempts.RecordSuccess(address, login);
        }

        if (PasswordHasher.NeedsRehash(account.Password))
        {
            await accountRepository.UpdatePasswordAsync(account, PasswordHasher.Hash(password));
            logger.LogInformation("Upgraded the stored password of account {AccountId}", account.Id);
        }

        if (account.Authority == AccountAuthority.Banned)
        {
            logger.LogWarning("Banned account login attempt: {Login}", loggedLogin);
            return Rejected(responseOpcode, LoginResult.AccountBlocked);
        }

        if (account.OnlineServerId is { } onlineServerId)
        {
            if (!flags.HasFlag(LoginRequestFlags.IgnoreOnlineClaim))
            {
                var occupiedServer = (await serverRepository.GetServers())
                    .FirstOrDefault(server => server.Id == onlineServerId);
                logger.LogInformation(
                    "Account {Login} is already connected to server {ServerId} since {Since:u}",
                    loggedLogin, onlineServerId, account.OnlineSince);
                return new LoginOutcome(
                    LoginPacketWriter.LoginOccupied(responseOpcode, LoginResult.AlreadyInGame, occupiedServer),
                    LoginResult.AlreadyInGame,
                    0);
            }

            logger.LogInformation(
                "Account {Login} signs in past the claim of server {ServerId} at the player's request; the claim stays with that server",
                loggedLogin, onlineServerId);
        }

        logger.LogInformation("Account logged in successfully: {Login}", loggedLogin);
        return new LoginOutcome(
            LoginPacketWriter.LoginSucceeded(
                responseOpcode, LoginResult.Success, account.RemainingPremiumHours, login, account.Language),
            LoginResult.Success,
            account.Id);
    }

    private async Task<Account?> CreateAccountAsync(string login, string password, IPAddress? address)
    {
        var loggedLogin = LogSanitizer.Clean(login);
        if (!AccountCredentialRules.IsValidNewLogin(login))
        {
            logger.LogWarning("Refused to auto-create account {Login}: invalid name", loggedLogin);
            return null;
        }

        if (!creationThrottle.TryReserve(address))
        {
            logger.LogWarning("Refused to auto-create account {Login}: {Address} created too many accounts", loggedLogin, address);
            return null;
        }

        var candidate = new Account
        {
            Login = login,
            Password = PasswordHasher.Hash(password),
            Authority = AccountAuthority.Normal,
            Nation = AccountNation.None,
            AccessDate = DateTime.UtcNow
        };

        await AccountCreationGate.WaitAsync();
        try
        {
            var existing = await accountRepository.GetByLogin(login);
            if (existing != null)
                return existing;

            if (await accountRepository.TryCreateAsync(candidate))
            {
                logger.LogInformation("Auto-created account {Login} (accountId={AccountId})", loggedLogin, candidate.Id);
                return candidate;
            }
        }
        finally
        {
            AccountCreationGate.Release();
        }

        return await accountRepository.GetByLogin(login);
    }

    private static LoginOutcome Rejected(LoginOpcodes responseOpcode, LoginResult result) =>
        new(LoginPacketWriter.LoginRejected(responseOpcode, result), result, 0);

    public async Task<Packet> ServerListAsync(short echo)
    {
        var servers = await serverRepository.GetServers();
        var kings = await kingRepository.GetKingsAsync();
        var karus = kings.FirstOrDefault(king => king.Nation == (byte)AccountNation.Karus);
        var elMorad = kings.FirstOrDefault(king => king.Nation == (byte)AccountNation.ElMorad);

        var entries = servers
            .Select(server => new LoginPacketWriter.ServerListEntry(
                Id: (short)server.Id,
                GroupName: server.Group?.Name ?? string.Empty,
                Name: server.Name,
                GroupId: (short)(server.GroupId ?? 0),
                Category: server.Category,
                Address: server.IpAddress,
                LanAddress: server.LanIpAddress,
                OnlinePlayers: (short)server.OnlinePlayers,
                MaxPlayers: (short)server.MaxPlayers,
                FreePlayerCap: (short)(server.FreePlayerCap > 0 ? server.FreePlayerCap : server.MaxPlayers),
                Port: server.Port,
                KarusKing: karus?.KingName ?? string.Empty,
                KarusNotice: karus?.Notice ?? string.Empty,
                ElMoradKing: elMorad?.KingName ?? string.Empty,
                ElMoradNotice: elMorad?.Notice ?? string.Empty))
            .ToList();

        return LoginPacketWriter.ServerList(echo, entries);
    }

    public Task<Packet> NewsAsync()
    {
        return Task.FromResult(LoginPacketWriter.News(NewsTitle, NewsBody));
    }

    public Task<Packet> UnknownF7Async()
    {
        return Task.FromResult(LoginPacketWriter.UnknownF7());
    }

    public Task<Packet> LauncherNewsAsync()
    {
        return Task.FromResult(LoginPacketWriter.LauncherNews([]));
    }

    public Task<Packet> SocketListAsync()
    {
        return Task.FromResult(LoginPacketWriter.SocketList());
    }
}
