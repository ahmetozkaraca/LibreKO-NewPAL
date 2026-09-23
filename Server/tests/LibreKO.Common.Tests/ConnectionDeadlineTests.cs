using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibreKO.Common.Tests;

public class ConnectionDeadlineTests
{
    private const int ShortTimeoutSeconds = 1;
    private const int LongTimeoutSeconds = 600;
    private const int CloseWaitMs = 6000;
    private const int KeepAliveIntervalMs = 200;

    private sealed class LimitedClientFactory(ConnectionLimitsSettings limits) : IClientFactory
    {
        public IClient Create(Socket socket) => new Client(socket, ServerType.Game, NullLogger<Client>.Instance, limits);
    }

    private sealed class AuthenticatingHandler(bool authenticate) : IPacketHandler
    {
        public Task HandlePacket(IClient client, Packet packet)
        {
            if (authenticate)
                client.AccountId = 1;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AClientThatNeverLogsInIsClosedEvenWhileItKeepsSendingPackets()
    {
        var limits = new ConnectionLimitsSettings
        {
            LoginTimeoutSeconds = ShortTimeoutSeconds,
            IdleTimeoutSeconds = LongTimeoutSeconds,
            PartialFrameTimeoutSeconds = LongTimeoutSeconds,
        };
        var (server, port) = await StartAsync(limits, authenticate: false);

        using var tcp = await ConnectAsync(port);
        var stream = tcp.GetStream();
        var closed = await KeepSendingUntilClosedAsync(stream, CloseWaitMs);

        closed.Should().BeTrue("an unauthenticated connection must not live past the login timeout");
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AnAuthenticatedClientThatKeepsSendingIsNotClosedByTheLoginTimeout()
    {
        var limits = new ConnectionLimitsSettings
        {
            LoginTimeoutSeconds = ShortTimeoutSeconds,
            IdleTimeoutSeconds = LongTimeoutSeconds,
            PartialFrameTimeoutSeconds = LongTimeoutSeconds,
        };
        var (server, port) = await StartAsync(limits, authenticate: true);

        using var tcp = await ConnectAsync(port);
        var closed = await KeepSendingUntilClosedAsync(tcp.GetStream(), ShortTimeoutSeconds * 3000);

        closed.Should().BeFalse();
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASilentAuthenticatedClientIsClosedAfterTheIdleTimeout()
    {
        var limits = new ConnectionLimitsSettings
        {
            LoginTimeoutSeconds = LongTimeoutSeconds,
            IdleTimeoutSeconds = ShortTimeoutSeconds,
            PartialFrameTimeoutSeconds = LongTimeoutSeconds,
        };
        var (server, port) = await StartAsync(limits, authenticate: true);

        using var tcp = await ConnectAsync(port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Frame());

        (await WaitForCloseAsync(stream, CloseWaitMs)).Should().BeTrue();
        await server.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFrameThatNeverCompletesIsClosedAfterThePartialFrameTimeout()
    {
        var limits = new ConnectionLimitsSettings
        {
            LoginTimeoutSeconds = LongTimeoutSeconds,
            IdleTimeoutSeconds = LongTimeoutSeconds,
            PartialFrameTimeoutSeconds = ShortTimeoutSeconds,
        };
        var (server, port) = await StartAsync(limits, authenticate: true);

        using var tcp = await ConnectAsync(port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Frame());
        await stream.WriteAsync(Frame().AsMemory(0, 4));

        (await WaitForCloseAsync(stream, CloseWaitMs)).Should().BeTrue(
            "a client that announces a frame and never finishes it must not hold the buffer forever");
        await server.StopAsync(CancellationToken.None);
    }

    private static async Task<(SocketServer Server, int Port)> StartAsync(ConnectionLimitsSettings limits, bool authenticate)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new SocketServer("127.0.0.1", port, 0, new LimitedClientFactory(limits),
            new AuthenticatingHandler(authenticate), NullLogger<SocketServer>.Instance,
            maxConnectionsPerIp: 50, maxConnectionAttemptsPerWindow: 500);
        _ = server.StartAsync(CancellationToken.None);
        await Task.Yield();
        return (server, port);
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return client;
            }
            catch (SocketException) when (watch.ElapsedMilliseconds < CloseWaitMs)
            {
                client.Dispose();
                await Task.Delay(KeepAliveIntervalMs / 8);
            }
        }
    }

    private static byte[] Frame() => PacketProvider.WrapPacket(new Packet(GameOpcodes.GS_PING));

    private static async Task<bool> KeepSendingUntilClosedAsync(NetworkStream stream, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        var probe = new byte[1];
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                await stream.WriteAsync(Frame());
                using var wait = new CancellationTokenSource(KeepAliveIntervalMs);
                if (await stream.ReadAsync(probe, wait.Token) == 0)
                    return true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> WaitForCloseAsync(NetworkStream stream, int timeoutMs)
    {
        using var wait = new CancellationTokenSource(timeoutMs);
        var probe = new byte[1];
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(probe, wait.Token) == 0)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
