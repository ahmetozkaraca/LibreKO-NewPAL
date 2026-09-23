using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace LibreKO.Common.Tests;

public class ClientProtocolTests
{
    private const string Secret = "HUNTER2SECRET";
    private static readonly byte[] Seed = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] WrongSeed = [8, 7, 6, 5, 4, 3, 2, 1];

    private sealed class RecordingLogger : ILogger<Client>
    {
        private readonly List<string> _messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToArray();
            }
        }
    }

    private sealed class Connection : IDisposable
    {
        public required Client Client { get; init; }
        public required TcpClient Peer { get; init; }
        public required TcpListener Listener { get; init; }

        public Task SendAsync(byte[] frame) => Peer.GetStream().WriteAsync(frame).AsTask();

        public void Dispose()
        {
            Client.Dispose();
            Peer.Dispose();
            Listener.Stop();
        }
    }

    [Theory]
    [InlineData(ServerType.Login, (byte)LoginOpcodes.LS_LOGIN)]
    [InlineData(ServerType.Login, (byte)LoginOpcodes.LS_MGAME_LOGIN)]
    [InlineData(ServerType.Game, (byte)GameOpcodes.GS_LOGIN)]
    [InlineData(ServerType.Game, (byte)GameOpcodes.GS_KICKOUT)]
    public async Task TraceLoggingNeverWritesTheCredentialsOfALoginPacket(ServerType serverType, byte opcode)
    {
        var logger = new RecordingLogger();
        using var connection = await ConnectAsync(serverType, logger);

        var login = new Packet(opcode);
        login.WriteString("user");
        login.WriteString(Secret);
        await connection.SendAsync(PacketProvider.WrapPacket(login, asClient: true));

        var received = await connection.Client.ReceivePacket();

        received.GetOpcode().Should().Be(opcode);
        logger.Messages.Should().NotContain(message =>
            message.Contains(Convert.ToHexString(Encoding.ASCII.GetBytes(Secret))) || message.Contains(Secret));
    }

    [Fact]
    public async Task TraceLoggingStillShowsOrdinaryPackets()
    {
        var logger = new RecordingLogger();
        using var connection = await ConnectAsync(ServerType.Game, logger);

        var move = new Packet(GameOpcodes.GS_ROTATE);
        move.WriteShort(0x1234);
        await connection.SendAsync(PacketProvider.WrapPacket(move, asClient: true));

        await connection.Client.ReceivePacket();

        logger.Messages.Should().Contain(message => message.Contains(Convert.ToHexString(move.GetBytes())));
    }

    [Theory]
    [InlineData((byte)LoginOpcodes.LS_VERSION_REQ)]
    [InlineData((byte)LoginOpcodes.LS_LOGIN)]
    public async Task AnUnprotectedPacketAfterTheLoginHandshakeIsRefused(byte opcode)
    {
        using var connection = await ConnectAsync(ServerType.Login, new RecordingLogger());
        connection.Client.EnableLoginCrypto(Seed);

        var plain = new Packet(opcode);
        if (opcode == (byte)LoginOpcodes.LS_LOGIN)
        {
            plain.WriteString("user");
            plain.WriteString(Secret);
        }

        await connection.SendAsync(PacketProvider.WrapPacket(plain, asClient: true));

        var receive = () => connection.Client.ReceivePacket();
        await receive.Should().ThrowAsync<InvalidDataException>(
            "the stock client protects every packet once it has the seed, so a plain one is forged");
    }

    [Fact]
    public async Task AProtectedPacketStillArrivesAfterTheHandshake()
    {
        using var connection = await ConnectAsync(ServerType.Login, new RecordingLogger());
        connection.Client.EnableLoginCrypto(Seed);

        await connection.SendAsync(Protected(new Packet(LoginOpcodes.LS_VERSION_REQ), Seed));

        (await connection.Client.ReceivePacket()).GetOpcode().Should().Be((byte)LoginOpcodes.LS_VERSION_REQ);
    }

    [Fact]
    public async Task APacketProtectedWithTheWrongSeedIsAProtocolFault()
    {
        using var connection = await ConnectAsync(ServerType.Login, new RecordingLogger());
        connection.Client.EnableLoginCrypto(Seed);

        await connection.SendAsync(Protected(new Packet(LoginOpcodes.LS_VERSION_REQ), WrongSeed));

        var receive = () => connection.Client.ReceivePacket();
        await receive.Should().ThrowAsync<InvalidDataException>(
            "undecryptable client bytes are a malformed packet, not a server error");
    }

    [Fact]
    public async Task AnEncryptedPacketCannotBeReplayedWithSequenceZero()
    {
        var publicKey = new BigInteger(123456789);
        var cipher = new PacketCipher(publicKey);
        using var connection = await ConnectAsync(ServerType.Game, new RecordingLogger());
        connection.Client.EnableCrypto(publicKey);

        var packet = new Packet(GameOpcodes.GS_ROTATE);
        packet.WriteShort(0);
        await connection.SendAsync(PacketProvider.WrapPacket(packet, cipher, sequenceId: 1, asClient: true));
        await connection.Client.ReceivePacket();

        await connection.SendAsync(PacketProvider.WrapPacket(packet, cipher, sequenceId: 0, asClient: true));

        var replay = () => connection.Client.ReceivePacket();
        await replay.Should().ThrowAsync<InvalidDataException>();
    }

    private static byte[] Protected(Packet packet, byte[] seed)
    {
        var payload = LoginSeedCipher.Protect(packet.GetBytes(), seed);
        var wrapped = new Packet(payload[0]);
        wrapped.WriteBytes(payload[1..]);
        return PacketProvider.WrapPacket(wrapped, asClient: true);
    }

    private static async Task<Connection> ConnectAsync(ServerType serverType, ILogger<Client> logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var peer = new TcpClient(AddressFamily.InterNetwork);
        var connect = peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var socket = await listener.AcceptSocketAsync();
        await connect;

        return new Connection
        {
            Client = new Client(socket, serverType, logger),
            Peer = peer,
            Listener = listener,
        };
    }
}
