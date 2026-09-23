using System.Collections.Concurrent;
using LibreKO.Common.Enums;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Logging;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Configuration;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game;

public class GamePacketHandler(
    IServiceProvider serviceProvider,
    SessionManager sessionManager,
    ISessionTerminationService sessionTerminationService,
    IAccountLockService accountLockService,
    IZoneTransitionService zoneTransitionService,
    IAdminPacketCoordinator adminPacketCoordinator,
    IAdminPanelPacketCoordinator adminPanelPacketCoordinator,
    IChatPacketCoordinator chatPacketCoordinator,
    IMiscPacketCoordinator miscPacketCoordinator,
    IWorldPacketCoordinator worldPacketCoordinator,
    IShoppingMallPacketCoordinator shoppingMallPacketCoordinator,
    ISavedMagicService savedMagicService,
    IInGameOpcodeRouter opcodeRouter,
    ICollectionRaceService collectionRaceService,
    IMailService mailService,
    IPacketGuard packetGuard,
    IViolationMonitor violationMonitor,
    LoginAttemptLimiter loginAttempts,
    IOptions<GameServerSettings> settings,
    TimeProvider time,
    ILogger<GamePacketHandler> logger) : IPacketHandler
{
    private const int PingMinIntervalMs = 200;
    private const int PingMaxEchoBytes = 8;
    private const char CommandArgumentSeparator = ' ';
    private static readonly TimeSpan UnhandledWarningInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, long> _lastPingTicks = new();
    private readonly ConcurrentDictionary<Guid, ConnectionState> _connections = new();
    private readonly ConnectionRateLimiter<GameOpcodes> _preGameLimiter =
        new(PreGameOpcodePolicies.ClientTotal, PreGameOpcodePolicies.LimitOf, time);

    public async Task OnClientDisconnected(IClient client)
    {
        _lastPingTicks.TryRemove(client.Id, out _);
        _connections.TryRemove(client.Id, out _);
        _preGameLimiter.Forget(client.Id);
        packetGuard.Forget(client.Id);
        violationMonitor.Forget(client.Id);
        await sessionTerminationService.DisconnectAsync(client);
        await accountLockService.ReleaseAsync(client);
    }

    private async Task HandlePingAsync(IClient client, Packet packet)
    {
        var now = Environment.TickCount64;
        if (now - _lastPingTicks.GetValueOrDefault(client.Id) < PingMinIntervalMs)
            return;
        _lastPingTicks[client.Id] = now;

        var echoBytes = Math.Min(packet.RemainingBytes, PingMaxEchoBytes);
        await client.SendPacket(SessionPacketWriter.Pong(
            echoBytes > 0 ? packet.ReadBytes(echoBytes) : []));
    }

    public async Task HandlePacket(IClient client, Packet packet)
    {
        var opcode = (GameOpcodes)packet.GetOpcode();

        if (opcode == GameOpcodes.GS_COMPRESS_PACKET)
        {
            violationMonitor.Report(client, ViolationKind.ForgedEvent, "sent a compressed packet");
            return;
        }

        if (opcode == GameOpcodes.GS_PING)
        {
            await HandlePingAsync(client, packet);
            return;
        }

        // In-game traffic (movement, combat, …) is the overwhelming majority, and its
        // handlers are singleton coordinators that scope their own DB work. Skip the
        // per-packet DI scope + DbContext entirely for it — only the rare per-connection
        // auth / pre-game / game-start paths need scoped services.
        if (client.AccountId != 0 && client.CharacterId != 0)
        {
            await HandleInGamePacket(client, packet, opcode);
            return;
        }

        if (!_preGameLimiter.TryAcquire(client.Id, opcode))
        {
            violationMonitor.Report(client, ViolationKind.RateLimit, $"exceeded the pre-game rate limit for {opcode}");
            return;
        }

        if (client.AccountId == 0)
        {
            await HandleUnauthenticatedPacket(client, packet, opcode);
            return;
        }

        // CharacterId == 0: pre-game (character select, etc.)
        using var scope = serviceProvider.CreateScope();
        var preGamePacketCoordinator = scope.ServiceProvider.GetRequiredService<IPreGamePacketCoordinator>();
        await HandlePreGamePacket(client, packet, opcode, preGamePacketCoordinator);
    }

    private async Task HandleUnauthenticatedPacket(IClient client, Packet packet, GameOpcodes opcode)
    {
        if (opcode == GameOpcodes.GS_VERSION_CHECK)
        {
            await client.SendPacket(SessionPacketWriter.VersionCheck((short)settings.Value.Version));
            return;
        }

        if (opcode is not (GameOpcodes.GS_LOGIN or GameOpcodes.GS_KICKOUT))
            return;

        var login = packet.ReadString();
        var password = packet.ReadString();
        if (CredentialAttemptsExhausted(client))
            return;

        using var scope = serviceProvider.CreateScope();
        var preGameService = scope.ServiceProvider.GetRequiredService<IPreGameService>();
        var auth = await AuthenticateAsync(client, login, password, preGameService);

        if (opcode == GameOpcodes.GS_LOGIN)
            await CompleteLoginAsync(client, auth);
        else
            await CompleteKickOutAsync(client, login, auth);
    }

    private async Task<GameLoginResult> AuthenticateAsync(
        IClient client, string login, string password, IPreGameService preGameService)
    {
        using var admission = await loginAttempts.AdmitAsync(client.RemoteAddress, login);
        if (admission == null)
        {
            logger.LogDebug("Refused credentials for {Login} from client {ClientId}: too many recent failures",
                LogSanitizer.Clean(login), client.Id);
            RecordCredentialFailure(client, login);
            return GameLoginResult.Denied;
        }

        var auth = await preGameService.LoginAsync(login, password);
        if (auth.Success)
            loginAttempts.RecordSuccess(client.RemoteAddress, login);
        else
            RecordCredentialFailure(client, login);

        return auth;
    }

    private bool CredentialAttemptsExhausted(IClient client)
    {
        var limit = settings.Value.Connections.MaxLoginFailuresPerConnection;
        if (limit <= 0 || StateOf(client).CredentialFailures < limit)
            return false;

        logger.LogWarning("Disconnecting client {ClientId} after {Failures} failed logins on one connection",
            client.Id, limit);
        client.Disconnect();
        return true;
    }

    private void RecordCredentialFailure(IClient client, string login)
    {
        StateOf(client).CredentialFailures++;
        loginAttempts.RecordFailure(client.RemoteAddress, login);
    }

    private ConnectionState StateOf(IClient client) => _connections.GetOrAdd(client.Id, _ => new ConnectionState());

    private async Task HandlePreGamePacket(IClient client, Packet packet, GameOpcodes opcode, IPreGamePacketCoordinator preGamePacketCoordinator)
    {
        var response = await preGamePacketCoordinator.HandleAsync(client, packet, opcode);

        if (response != null)
        {
            await client.SendPacket(response);
            return;
        }

        if (!ShouldSuppressPreGameWarning(opcode, packet))
            WarnUnhandledPreGamePacket(client, packet);
    }

    private void WarnUnhandledPreGamePacket(IClient client, Packet packet)
    {
        var state = StateOf(client);
        var now = time.GetTimestamp();
        if (state.LastUnhandledWarning != 0 && time.GetElapsedTime(state.LastUnhandledWarning, now) < UnhandledWarningInterval)
            return;

        state.LastUnhandledWarning = now;
        logger.LogWarning(
            "Unhandled opcode 0x{Opcode:X2} ({Length} bytes) from client {ClientId} (pre-game)",
            packet.GetOpcode(),
            packet.GetLength(),
            client.Id);
    }

    private async Task CompleteLoginAsync(IClient client, GameLoginResult response)
    {
        if (!response.Success)
        {
            await client.SendPacket(SessionPacketWriter.LoginDenied());
            return;
        }

        var claim = await accountLockService.AcquireAsync(client, response.AccountId);
        if (!claim.Granted && claim.Occupant != null)
        {
            logger.LogInformation(
                "Refused login for account {AccountId} from client {ClientId}: already connected ({Stage})",
                response.AccountId, client.Id, claim.Occupant.Stage);

            await client.SendPacket(SessionPacketWriter.LoginDeniedAccountInUse(claim.Occupant));
            return;
        }

        client.AccountId = response.AccountId;

        await client.SendPacket(SessionPacketWriter.LoginAccepted((byte)response.Nation));
        await client.SendPacket(SessionPacketWriter.LoginFollowUp());
    }

    private async Task CompleteKickOutAsync(IClient client, string login, GameLoginResult auth)
    {
        if (!auth.Success)
        {
            logger.LogWarning("Rejected kick request for '{Login}' from client {ClientId}", LogSanitizer.Clean(login), client.Id);
            await client.SendPacket(SessionPacketWriter.KickResult(AccountKickCode.Rejected));
            return;
        }

        var code = await accountLockService.KickAsync(auth.AccountId);
        logger.LogInformation("Kick request for account {AccountId} from client {ClientId}: {Code}",
            auth.AccountId, client.Id, code);

        await client.SendPacket(SessionPacketWriter.KickResult(code));
    }

    private static bool ShouldSuppressPreGameWarning(GameOpcodes opcode, Packet packet)
    {
        if (opcode is GameOpcodes.GS_SPEEDHACK_CHECK or GameOpcodes.GS_HACKTOOL or GameOpcodes.GS_REPORT_BUG)
            return true;

        if (opcode != GameOpcodes.GS_ALLCHAR_INFO_REQ || packet.GetLength() <= 0)
            return false;

        return packet.GetData()[0] is (byte)AllCharacterInfoOpcode.ArrangeOpen or (byte)AllCharacterInfoOpcode.ArrangeReceive;
    }

    private async Task HandleInGamePacket(
        IClient client,
        Packet packet,
        GameOpcodes opcode)
    {
        switch (opcode)
        {
            case GameOpcodes.GS_GAMESTART or GameOpcodes.GS_CHAT when !packetGuard.Admit(client, opcode):
                return;

            case GameOpcodes.GS_GAMESTART:
                {
                    // Rare (per login) — the only in-game opcode needing scoped services.
                    using var scope = serviceProvider.CreateScope();
                    var preGameService = scope.ServiceProvider.GetRequiredService<IPreGameService>();
                    var gameSessionInitializer = scope.ServiceProvider.GetRequiredService<IGameSessionInitializer>();
                    await HandleGameStartAsync(client, packet, preGameService, gameSessionInitializer);
                }
                return;

            case GameOpcodes.GS_CHAT:
                await HandleChatAsync(client, packet);
                return;
        }

        var handler = opcodeRouter.Resolve(opcode);
        if (handler != null)
        {
            await handler(client, packet);
        }
        else
        {
            logger.LogDebug("Unhandled in-game opcode 0x{Opcode:X2} from client {ClientId}", packet.GetOpcode(), client.Id);
        }
    }

    private async Task HandleGameStartAsync(
        IClient client,
        Packet packet,
        IPreGameService preGameService,
        IGameSessionInitializer gameSessionInitializer)
    {
        var subOpcode = (GameStartSubOpcode)packet.ReadByte();

        if (!accountLockService.Owns(client))
        {
            logger.LogWarning("Ignoring game start from client {ClientId}: it no longer holds its account claim", client.Id);
            return;
        }

        var session = sessionManager.GetByClientId(client.Id);
        var state = StateOf(client);
        if (subOpcode == GameStartSubOpcode.Load && session == null)
        {
            await LoadWorldAsync(client, preGameService, gameSessionInitializer);
        }
        else if (subOpcode == GameStartSubOpcode.Ready && (session == null || !ReferenceEquals(state.EnteredWorld, session)))
        {
            session ??= await InitializeSessionAsync(client, gameSessionInitializer);
            if (session == null)
                return;

            state.EnteredWorld = session;
            await EnterWorldAsync(client, session, preGameService);
        }
        else
        {
            violationMonitor.Report(client, ViolationKind.InvalidState, $"sent game start {subOpcode} out of sequence");
        }
    }

    private async Task LoadWorldAsync(
        IClient client,
        IPreGameService preGameService,
        IGameSessionInitializer gameSessionInitializer)
    {
        var session = await InitializeSessionAsync(client, gameSessionInitializer);
        if (session == null)
            return;

        logger.LogDebug(
            "GameStart subOp=1: {Name} (id={Id}) entered zone={Zone} pos=({X},{Z}) region=({RX},{RZ})",
            session.Name, session.CharacterId, session.ZoneId, session.X, session.Z, session.RegionX, session.RegionZ);

        var responses = await preGameService.GameStartAsync(
            client.CharacterId, client.AccountId, (byte)GameStartSubOpcode.Load, session);
        foreach (var response in responses)
        {
            await client.SendPacket(response);

            if (response.GetOpcode() != (byte)GameOpcodes.GS_MYINFO)
                continue;

            await zoneTransitionService.SendZoneAbilityAsync(session);
            await adminPanelPacketCoordinator.SendGrantAsync(session);
            await worldPacketCoordinator.SendRegionUserListAsync(session);
            await worldPacketCoordinator.SendNpcRegionListAsync(session);
        }
    }

    private async Task<UserSession?> InitializeSessionAsync(IClient client, IGameSessionInitializer gameSessionInitializer)
    {
        var session = await gameSessionInitializer.InitializeAsync(client);
        if (session != null)
            return session;

        logger.LogWarning("Closing client {ClientId}: character {CharacterId} cannot enter the game",
            client.Id, client.CharacterId);
        client.Disconnect();
        return null;
    }

    private async Task EnterWorldAsync(IClient client, UserSession session, IPreGameService preGameService)
    {
        await preGameService.GameStartAsync(
            client.CharacterId, client.AccountId, (byte)GameStartSubOpcode.Ready, session);

        logger.LogDebug(
            "GameStart subOp=2: {Name} (id={Id}) ready in zone={Zone} pos=({X},{Z}) region=({RX},{RZ}) — broadcasting Respawn",
            session.Name, session.CharacterId, session.ZoneId, session.X, session.Z, session.RegionX, session.RegionZ);

        await zoneTransitionService.SendZoneAbilityAsync(session);
        if (session.AccountStatus != 0 || session.PremiumType != 0 || session.PremiumTime > 0)
            await miscPacketCoordinator.SendPremiumInfoAsync(session);
        await worldPacketCoordinator.BroadcastUserInOutAsync(session, InOutType.Respawn);
        await shoppingMallPacketCoordinator.SendUnreadAsync(session);
        await savedMagicService.RecastAsync(session);
        await collectionRaceService.SyncPlayerAsync(session);
        await mailService.SendUnreadAsync(session);

        if (session.Hp <= 0)
            await SendReconnectDeathStateAsync(session);
    }

    private static async Task SendReconnectDeathStateAsync(UserSession session)
    {
        await session.Client.SendPacket(DeathPacketWriter.PlayerDeath(
            session.CharacterId, DeathPacketWriter.NoKiller));
    }

    private async Task HandleChatAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var chatType = packet.ReadByte();
        var message = packet.ReadString();

        if (message.StartsWith('+') && (session.IsGM || adminPacketCoordinator.IsOpenToEveryone(message)))
        {
            logger.LogInformation("Chat command {Command} from {Name}", LogSanitizer.Clean(CommandWordOf(message)), session.Name);
            await adminPacketCoordinator.HandleGmCommandAsync(session, message);
            return;
        }

        logger.LogDebug("Chat from {Name}: IsGM={IsGM}, message={Message}",
            session.Name, session.IsGM, LogSanitizer.Clean(message));
        await chatPacketCoordinator.HandleAsync(session, chatType, message);
    }

    private static string CommandWordOf(string message)
    {
        var separator = message.IndexOf(CommandArgumentSeparator);
        return separator < 0 ? message : message[..separator];
    }

    private sealed class ConnectionState
    {
        public int CredentialFailures;
        public long LastUnhandledWarning;
        public UserSession? EnteredWorld;
    }
}
