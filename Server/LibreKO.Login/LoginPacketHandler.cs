using System.Collections.Concurrent;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Login.Configuration;
using LibreKO.Login.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Login.Protocol.Writers;

namespace LibreKO.Login;

public class LoginPacketHandler(
    IServiceProvider serviceProvider,
    IOptions<LoginServerSettings> settings,
    TimeProvider time,
    ILogger<LoginPacketHandler> logger) : IPacketHandler
{
    private readonly ConnectionRateLimiter<LoginOpcodes> _rateLimiter =
        new(LoginOpcodePolicies.ClientTotal, LoginOpcodePolicies.LimitOf, time);
    private readonly ConcurrentDictionary<Guid, int> _credentialFailures = new();

    public async Task HandlePacket(IClient client, Packet packet)
    {
        var opcode = (LoginOpcodes)packet.GetOpcode();
        if (!_rateLimiter.TryAcquire(client.Id, opcode))
            throw new InvalidDataException($"Rate limit exceeded for {opcode}");

        if (opcode == LoginOpcodes.LS_CRYPTION)
        {
            if (client.IsCryptoEnabled)
                throw new InvalidDataException("Repeated login handshake");

            await HandleCryptoHandshakeAsync(client);
            return;
        }

        if (opcode == LoginOpcodes.LS_OTP)
            return;

        using var scope = serviceProvider.CreateScope();
        var loginService = scope.ServiceProvider.GetRequiredService<ILoginService>();

        var response = opcode is LoginOpcodes.LS_LOGIN or LoginOpcodes.LS_MGAME_LOGIN
            ? await LoginAsync(client, loginService, packet, opcode)
            : await DispatchCommand(loginService, opcode, packet);
        if (response != null)
        {
            await client.SendPacket(response);
        }
    }

    public Task OnClientDisconnected(IClient client)
    {
        _rateLimiter.Forget(client.Id);
        _credentialFailures.TryRemove(client.Id, out _);
        return Task.CompletedTask;
    }

    private static async Task HandleCryptoHandshakeAsync(IClient client)
    {
        var (keyBytes, _) = PacketCipher.GeneratePublicKey();
        var resp = LoginPacketWriter.Cryption(keyBytes);
        await client.SendPacket(resp);

        client.EnableLoginCrypto(keyBytes);
    }

    private static Task<Packet> DispatchCommand(ILoginService loginService, LoginOpcodes opcode, Packet packet)
    {
        return opcode switch
        {
            LoginOpcodes.LS_VERSION_REQ => loginService.VersionCheckAsync(),
            LoginOpcodes.LS_DOWNLOADINFO_REQ => loginService.DownloadInfoAsync(packet.ReadShort()),
            LoginOpcodes.LS_SERVERLIST => loginService.ServerListAsync(packet.ReadShort()),
            LoginOpcodes.LS_NEWS => loginService.NewsAsync(),
            LoginOpcodes.LS_UNKNOWN_F7 => loginService.UnknownF7Async(),
            LoginOpcodes.LS_LAUNCHER_NEWS => loginService.LauncherNewsAsync(),
            LoginOpcodes.LS_SOCKET_LIST => loginService.SocketListAsync(),
            _ => throw new InvalidDataException($"Unsupported opcode: {opcode}"),
        };
    }

    private async Task<Packet?> LoginAsync(IClient client, ILoginService loginService, Packet packet, LoginOpcodes responseOpcode)
    {
        var login = packet.ReadString();
        var password = packet.ReadString();
        var flags = ReadLoginFlags(packet);

        var limit = settings.Value.Connections.MaxLoginFailuresPerConnection;
        if (limit > 0 && _credentialFailures.GetValueOrDefault(client.Id) >= limit)
        {
            logger.LogWarning("Disconnecting client {ClientId} after {Failures} failed logins on one connection",
                client.Id, limit);
            client.Disconnect();
            return null;
        }

        var outcome = await loginService.LoginAsync(login, password, client.RemoteAddress, responseOpcode, flags);
        if (outcome.Result == LoginResult.Success)
            client.AccountId = outcome.AccountId;
        else if (outcome.Result == LoginResult.InvalidPassword)
            _credentialFailures.AddOrUpdate(client.Id, 1, (_, failures) => failures + 1);

        return outcome.Response;
    }

    private static LoginRequestFlags ReadLoginFlags(Packet packet)
    {
        const int extensionBytes = 6;
        if (packet.RemainingBytes < extensionBytes
            || packet.ReadUInt() != GameplayProtocol.AccountLockMagic
            || packet.ReadByte() != GameplayProtocol.ExtensionVersion)
            return LoginRequestFlags.None;

        return (LoginRequestFlags)packet.ReadByte();
    }
}
