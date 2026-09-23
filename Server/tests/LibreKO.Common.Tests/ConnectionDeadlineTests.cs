using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibreKO.Common.Tests;

public class ConnectionDeadlineTests
{
    private const int TimeoutSeconds = 60;
    private const int CloseWaitMs = 6000;
    private const int SweepsToWaitMs = 2500;
    private const int AdvanceStepMs = 100;
    private const int PartialFrameBytes = 4;
    private static readonly TimeSpan PastTheTimeout = TimeSpan.FromSeconds(TimeoutSeconds + 1);
    private static readonly TimeSpan WithinTheTimeout = TimeSpan.FromSeconds(TimeoutSeconds - 1);

    private sealed class LimitedClientFactory(ConnectionLimitsSettings limits, TimeProvider time) : IClientFactory
    {
        public IClient Create(Socket socket) => new Client(socket, ServerType.Game, NullLogger<Client>.Instance, limits, time);
    }

    private sealed class AuthenticatingHandler(bool authenticate) : IPacketHandler
    {
        private readonly SemaphoreSlim _received = new(0);

        public Task HandlePacket(IClient client, Packet packet)
        {
            if (authenticate)
                client.AccountId = 1;
            _received.Release();
            return Task.CompletedTask;
        }

        public Task WaitForPacketAsync() => _received.WaitAsync(CloseWaitMs);
    }

    private static ConnectionLimitsSettings Only(Action<ConnectionLimitsSettings> configure)
    {
        var limits = new ConnectionLimitsSettings
        {
            LoginTimeoutSeconds = ConnectionLimitsSettings.Disabled,
            IdleTimeoutSeconds = ConnectionLimitsSettings.Disabled,
            PartialFrameTimeoutSeconds = ConnectionLimitsSettings.Disabled,
        };
        configure(limits);
        return limits;
    }

    [Fact]
    public async Task AClientThatNeverLogsInIsClosedEvenWhileItKeepsSendingPackets()
    {
        var clock = new ManualClock();
        await using var harness = await Harness.StartAsync(Only(limits => limits.LoginTimeoutSeconds = TimeoutSeconds), clock, authenticate: false);
        using var tcp = await harness.ConnectAsync();
        var stream = tcp.GetStream();

        await harness.SendAsync(stream);
        clock.Advance(WithinTheTimeout);
        await harness.SendAsync(stream);
        clock.Advance(PastTheTimeout - WithinTheTimeout);

        (await WaitForCloseAsync(stream, CloseWaitMs)).Should().BeTrue(
            "an unauthenticated connection must not live past the login timeout");
    }

    [Fact]
    public async Task AnAuthenticatedClientIsNotClosedByTheLoginTimeout()
    {
        var clock = new ManualClock();
        await using var harness = await Harness.StartAsync(Only(limits => limits.LoginTimeoutSeconds = TimeoutSeconds), clock, authenticate: true);
        using var tcp = await harness.ConnectAsync();
        var stream = tcp.GetStream();

        await harness.SendAsync(stream);
        clock.Advance(PastTheTimeout);

        (await WaitForCloseAsync(stream, SweepsToWaitMs)).Should().BeFalse();
    }

    [Fact]
    public async Task WithTheLoginTimeoutDisabledAClientMayWaitBeforeLoggingIn()
    {
        var clock = new ManualClock();
        var limits = Only(limits => limits.IdleTimeoutSeconds = TimeoutSeconds * TimeoutSeconds);
        await using var harness = await Harness.StartAsync(limits, clock, authenticate: false);
        using var tcp = await harness.ConnectAsync();
        var stream = tcp.GetStream();

        await harness.SendAsync(stream);
        clock.Advance(new ConnectionLimitsSettings().LoginTimeout + PastTheTimeout);

        (await WaitForCloseAsync(stream, SweepsToWaitMs)).Should().BeFalse(
            "a player typing credentials on the login screen sends nothing until the login itself");
    }

    [Fact]
    public async Task ASilentAuthenticatedClientIsClosedAfterTheIdleTimeout()
    {
        var clock = new ManualClock();
        await using var harness = await Harness.StartAsync(Only(limits => limits.IdleTimeoutSeconds = TimeoutSeconds), clock, authenticate: true);
        using var tcp = await harness.ConnectAsync();
        var stream = tcp.GetStream();

        await harness.SendAsync(stream);
        clock.Advance(WithinTheTimeout);
        (await WaitForCloseAsync(stream, SweepsToWaitMs)).Should().BeFalse();

        clock.Advance(PastTheTimeout - WithinTheTimeout);
        (await WaitForCloseAsync(stream, CloseWaitMs)).Should().BeTrue();
    }

    [Fact]
    public async Task AFrameThatNeverCompletesIsClosedAfterThePartialFrameTimeout()
    {
        var clock = new ManualClock();
        await using var harness = await Harness.StartAsync(Only(limits => limits.PartialFrameTimeoutSeconds = TimeoutSeconds), clock, authenticate: true);
        using var tcp = await harness.ConnectAsync();
        var stream = tcp.GetStream();

        await harness.SendAsync(stream);
        await stream.WriteAsync(Frame().AsMemory(0, PartialFrameBytes));

        (await AdvanceUntilClosedAsync(stream, clock, PastTheTimeout)).Should().BeTrue(
            "a client that announces a frame and never finishes it must not hold the buffer forever");
    }

    private sealed class Harness(SocketServer server, AuthenticatingHandler handler, int port) : IAsyncDisposable
    {
        public static async Task<Harness> StartAsync(ConnectionLimitsSettings limits, TimeProvider time, bool authenticate)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var handler = new AuthenticatingHandler(authenticate);
            var server = new SocketServer("127.0.0.1", port, 0, new LimitedClientFactory(limits, time),
                handler, NullLogger<SocketServer>.Instance,
                maxConnectionsPerIp: 50, maxConnectionAttemptsPerWindow: 500);
            _ = server.StartAsync(CancellationToken.None);
            await Task.Yield();
            return new Harness(server, handler, port);
        }

        public async Task<TcpClient> ConnectAsync()
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
                    await Task.Delay(AdvanceStepMs);
                }
            }
        }

        public async Task SendAsync(NetworkStream stream)
        {
            await stream.WriteAsync(Frame());
            await handler.WaitForPacketAsync();
        }

        public async ValueTask DisposeAsync() => await server.StopAsync(CancellationToken.None);
    }

    private static byte[] Frame() => PacketProvider.WrapPacket(new Packet(GameOpcodes.GS_PING));

    private static async Task<bool> AdvanceUntilClosedAsync(NetworkStream stream, ManualClock clock, TimeSpan step)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < CloseWaitMs)
        {
            clock.Advance(step);
            if (await WaitForCloseAsync(stream, AdvanceStepMs * 10))
                return true;
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
