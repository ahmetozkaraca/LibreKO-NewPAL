using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Login.Configuration;
using LibreKO.Login.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LibreKO.Login.Tests;

public abstract class LoginSocketTest(IReadOnlyDictionary<string, string?> settingOverrides)
    : ServerTest(settingOverrides: settingOverrides)
{
    protected const int CloseWaitMs = 5000;
    protected const string Password = "SECRET";
    protected const int ClaimingServerId = 3;

    protected readonly LogRecorder Logs = new();

    protected override void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
    {
        services.AddSingleton<ILoggerProvider>(Logs);
    }

    protected static Packet LoginRequest(string login, string password, LoginRequestFlags flags = LoginRequestFlags.None)
    {
        var packet = new Packet(LoginOpcodes.LS_LOGIN);
        packet.WriteString(login);
        packet.WriteString(password);
        if (flags != LoginRequestFlags.None)
        {
            packet.WriteUInt(GameplayProtocol.AccountLockMagic);
            packet.WriteByte(GameplayProtocol.ExtensionVersion);
            packet.WriteByte((byte)flags);
        }

        return packet;
    }

    protected static async Task<Packet?> ExchangeAsync(NetworkStream stream, Packet request)
    {
        var response = await WriteHexAsync(stream, Convert.ToHexString(PacketProvider.WrapPacket(request, asClient: true)));
        if (response.Length == 0)
            return null;

        return await PacketProvider.ReadFromStream(new MemoryStream(Convert.FromHexString(response)), CancellationToken.None);
    }

    protected async Task<byte> LoginResultAsync(string login, string password, LoginRequestFlags flags = LoginRequestFlags.None)
    {
        var (tcp, stream) = await ConnectAsync();
        using (tcp)
        {
            var response = await ExchangeAsync(stream, LoginRequest(login, password, flags));
            response.Should().NotBeNull();
            response!.ReadShort();
            return response.ReadByte();
        }
    }

    protected static async Task<bool> WaitForCloseAsync(NetworkStream stream)
    {
        using var wait = new CancellationTokenSource(CloseWaitMs);
        var buffer = new byte[1024];
        try
        {
            while (await stream.ReadAsync(buffer, wait.Token) > 0)
            {
            }

            return true;
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

    protected async Task<Account> ReadAccountAsync(string login)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Accounts.AsNoTracking().SingleAsync(account => account.Login == login);
    }

    protected async Task<int> CountAccountsAsync(string login)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Accounts.CountAsync(account => account.Login == login);
    }

    protected sealed class LogRecorder : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToArray();
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(this);

        public void Dispose()
        {
        }

        private void Add(string message)
        {
            lock (_messages)
                _messages.Add(message);
        }

        private sealed class Recorder(LogRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Add(formatter(state, exception));
        }
    }
}

public class LoginCredentialTests() : LoginSocketTest(new Dictionary<string, string?>
{
    ["Account:AutoCreate"] = "false",
    ["Connections:MaxLoginFailuresPerConnection"] = "2",
    ["Connections:MaxLoginFailuresPerAccountAndIp"] = "3",
})
{
    private const int AttackingAddresses = 6;
    private static readonly IPAddress OwnerAddress = IPAddress.Parse("198.51.100.7");

    protected override void SeedDatabase(AppDbContext db)
    {
        db.Accounts.AddRange(
            new Account { Login = "legacy", Password = Password },
            new Account { Login = "hashed", Password = PasswordHasher.Hash(Password) },
            new Account { Login = "claimed", Password = PasswordHasher.Hash(Password), OnlineServerId = ClaimingServerId },
            new Account { Login = "victim", Password = PasswordHasher.Hash(Password) });
        db.SaveChanges();
    }

    [Fact]
    public async Task AnUnknownAccountAndAWrongPasswordGetTheSameRefusal()
    {
        await StartServerAsync();

        var unknown = await LoginResultAsync("nobody", Password);
        var wrongPassword = await LoginResultAsync("hashed", "WRONG");

        unknown.Should().Be(wrongPassword, "a different answer tells an attacker which accounts exist");
        unknown.Should().Be((byte)LoginResult.InvalidPassword);
    }

    [Fact]
    public async Task ALegacyPlaintextPasswordIsRehashedOnTheNextLogin()
    {
        await StartServerAsync();

        (await LoginResultAsync("legacy", Password)).Should().Be((byte)LoginResult.Success);

        var account = await ReadAccountAsync("legacy");
        account.Password.Should().NotBe(Password);
        PasswordHasher.Verify(Password, account.Password).Should().BeTrue();
        PasswordHasher.NeedsRehash(account.Password).Should().BeFalse();
    }

    [Fact]
    public async Task SigningInPastAnOnlineClaimLeavesThatServersClaimAlone()
    {
        await StartServerAsync();

        (await LoginResultAsync("claimed", Password)).Should().Be((byte)LoginResult.AlreadyInGame);
        (await LoginResultAsync("claimed", Password, LoginRequestFlags.IgnoreOnlineClaim))
            .Should().Be((byte)LoginResult.Success);

        (await ReadAccountAsync("claimed")).OnlineServerId.Should().Be(ClaimingServerId,
            "only the game server that wrote the claim may clear it");
    }

    [Fact]
    public async Task AnAddressIsLockedOutOfAnAccountAfterRepeatedFailuresEvenForTheRightPassword()
    {
        await StartServerAsync();

        for (var attempt = 0; attempt < 3; attempt++)
            (await LoginResultAsync("victim", "GUESS" + attempt)).Should().Be((byte)LoginResult.InvalidPassword);

        (await LoginResultAsync("victim", Password)).Should().Be((byte)LoginResult.InvalidPassword,
            "the lockout has to hold before the password is even checked");
        (await LoginResultAsync("hashed", Password)).Should().Be((byte)LoginResult.Success,
            "other accounts stay reachable");
    }

    [Fact]
    public async Task FailuresFromOtherAddressesNeverLockTheOwnerOut()
    {
        for (var attempt = 0; attempt < AttackingAddresses; attempt++)
            (await ServiceLoginAsync("victim", "GUESS", IPAddress.Parse($"203.0.113.{attempt + 1}")))
                .Should().Be(LoginResult.InvalidPassword);

        (await ServiceLoginAsync("victim", Password, OwnerAddress)).Should().Be(LoginResult.Success,
            "guessing from elsewhere must not keep the owner out of the account");
    }

    private async Task<LoginResult> ServiceLoginAsync(string login, string password, IPAddress address)
    {
        using var scope = host.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ILoginService>().LoginAsync(login, password, address);
        return outcome.Result;
    }

    [Fact]
    public async Task AConnectionIsClosedOnceItHasUsedUpItsFailedLogins()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        (await ExchangeAsync(stream, LoginRequest("hashed", "WRONG1"))).Should().NotBeNull();
        (await ExchangeAsync(stream, LoginRequest("hashed", "WRONG2"))).Should().NotBeNull();

        await stream.WriteAsync(PacketProvider.WrapPacket(LoginRequest("hashed", "WRONG3"), asClient: true));

        (await WaitForCloseAsync(stream)).Should().BeTrue("one connection must not keep guessing passwords forever");
    }

    [Fact]
    public async Task AnUnprotectedPacketAfterTheHandshakeClosesTheConnection()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        (await ExchangeAsync(stream, new Packet(LoginOpcodes.LS_CRYPTION))).Should().NotBeNull();
        await stream.WriteAsync(PacketProvider.WrapPacket(new Packet(LoginOpcodes.LS_VERSION_REQ), asClient: true));

        (await WaitForCloseAsync(stream)).Should().BeTrue();
    }

    [Fact]
    public async Task ASecondHandshakeClosesTheConnection()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        var reply = await ExchangeAsync(stream, new Packet(LoginOpcodes.LS_CRYPTION));
        reply.Should().NotBeNull();
        var seed = reply!.ReadBytes(reply.ReadByte());

        var again = LoginSeedCipher.Protect(new Packet(LoginOpcodes.LS_CRYPTION).GetBytes(), seed);
        var protectedAgain = new Packet(again[0]);
        protectedAgain.WriteBytes(again[1..]);
        await stream.WriteAsync(PacketProvider.WrapPacket(protectedAgain, asClient: true));

        (await WaitForCloseAsync(stream)).Should().BeTrue();
    }

    [Fact]
    public async Task AFloodOfRequestsClosesTheConnection()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        var news = PacketProvider.WrapPacket(new Packet(LoginOpcodes.LS_NEWS), asClient: true);
        var burst = Enumerable.Repeat(news, (int)LoginOpcodePolicies.ClientTotal.Capacity * 2).SelectMany(frame => frame).ToArray();
        await stream.WriteAsync(burst);

        (await WaitForCloseAsync(stream)).Should().BeTrue();
    }

    [Fact]
    public async Task LineBreaksInALoginNameNeverReachTheLog()
    {
        await StartServerAsync();

        await LoginResultAsync("nobody\r\n[00:00:00 INF] Account logged in successfully: admin", Password);

        var mentions = Logs.Messages.Where(message => message.Contains("nobody")).ToList();
        mentions.Should().NotBeEmpty();
        mentions.Should().NotContain(message => message.Contains('\n') || message.Contains('\r'));
    }
}

public class LoginTimingTests() : LoginSocketTest(new Dictionary<string, string?>
{
    ["Account:AutoCreate"] = "false",
})
{
    private const int Samples = 3;
    private const double MinimumCostRatio = 0.3;

    protected override void SeedDatabase(AppDbContext db)
    {
        db.Accounts.Add(new Account { Login = "hashed", Password = PasswordHasher.Hash(Password) });
        db.SaveChanges();
    }

    [Fact]
    public async Task AnUnknownAccountTakesAsLongToRefuseAsAWrongPassword()
    {
        await MeasureAsync("hashed", "WARMUP");

        var wrongPassword = await MeasureAsync("hashed", "WRONG");
        var unknownAccount = await MeasureAsync("nobody", Password);

        unknownAccount.Should().BeGreaterThan((long)(wrongPassword * MinimumCostRatio),
            "skipping the password hash for unknown accounts tells an attacker which accounts exist");
    }

    private async Task<long> MeasureAsync(string login, string password)
    {
        var samples = new long[Samples];
        for (var i = 0; i < samples.Length; i++)
        {
            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ILoginService>();
            var started = Stopwatch.GetTimestamp();
            var outcome = await service.LoginAsync(login, password, IPAddress.Loopback);
            samples[i] = Stopwatch.GetTimestamp() - started;
            outcome.Result.Should().Be(LoginResult.InvalidPassword);
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }
}

public class LoginTimeoutTests() : LoginSocketTest(new Dictionary<string, string?>
{
    ["Account:AutoCreate"] = "false",
    ["Connections:LoginTimeoutSeconds"] = "1",
})
{
    private const int PastTheTimeoutMs = 2500;

    protected override void SeedDatabase(AppDbContext db)
    {
        db.Accounts.Add(new Account { Login = "player", Password = PasswordHasher.Hash(Password) });
        db.SaveChanges();
    }

    [Fact]
    public async Task AConnectionThatNeverLogsInIsClosed()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        (await ExchangeAsync(stream, new Packet(LoginOpcodes.LS_VERSION_REQ))).Should().NotBeNull();

        (await WaitForCloseAsync(stream)).Should().BeTrue();
    }

    [Fact]
    public async Task ALoggedInConnectionOutlivesTheLoginTimeout()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        var login = await ExchangeAsync(stream, LoginRequest("player", Password));
        login!.ReadShort();
        login.ReadByte().Should().Be((byte)LoginResult.Success);

        await Task.Delay(PastTheTimeoutMs);

        var request = new Packet(LoginOpcodes.LS_SERVERLIST);
        request.WriteShort(1);
        (await ExchangeAsync(stream, request)).Should().NotBeNull("a signed-in player may sit on the server list");
    }
}

public class LoginScreenTests() : LoginSocketTest(new Dictionary<string, string?>
{
    ["Account:AutoCreate"] = "false",
})
{
    private const int SweepsToWaitMs = 2500;
    private static readonly TimeSpan PastTheDeadline = TimeSpan.FromSeconds(1);

    private readonly ManualClock _clock = new();

    protected override void ConfigureServices(HostBuilderContext ctx, IServiceCollection services)
    {
        base.ConfigureServices(ctx, services);
        services.AddSingleton<TimeProvider>(_clock);
    }

    protected override void SeedDatabase(AppDbContext db)
    {
        db.Accounts.Add(new Account { Login = "player", Password = PasswordHasher.Hash(Password) });
        db.SaveChanges();
    }

    [Fact]
    public async Task APlayerMayTakeTheirTimeOnTheLoginScreen()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        (await ExchangeAsync(stream, new Packet(LoginOpcodes.LS_VERSION_REQ))).Should().NotBeNull();
        _clock.Advance(new ConnectionLimitsSettings().LoginTimeout * 2);
        await Task.Delay(SweepsToWaitMs);

        var login = await ExchangeAsync(stream, LoginRequest("player", Password));
        login.Should().NotBeNull("the client connects before the player has typed the credentials");
        login!.ReadShort();
        login.ReadByte().Should().Be((byte)LoginResult.Success);
    }

    [Fact]
    public async Task AConnectionLeftOnTheLoginScreenIsClosedEventually()
    {
        await StartServerAsync();
        var (tcp, stream) = await ConnectAsync();
        using var _ = tcp;

        (await ExchangeAsync(stream, new Packet(LoginOpcodes.LS_VERSION_REQ))).Should().NotBeNull();
        _clock.Advance(TimeSpan.FromSeconds(LoginServerSettings.LoginScreenTimeoutSeconds) + PastTheDeadline);

        (await WaitForCloseAsync(stream)).Should().BeTrue();
    }
}

public class AccountCreationTests() : LoginSocketTest(new Dictionary<string, string?>
{
    ["Account:AutoCreate"] = "true",
    ["Account:MaxCreatedPerIp"] = "3",
})
{
    private const int ConcurrentLogins = 5;

    [Fact]
    public async Task ConcurrentLoginsForANewNameCreateASingleAccount()
    {
        await StartServerAsync();

        await Task.WhenAll(Enumerable.Range(0, ConcurrentLogins).Select(_ => LoginResultAsync("newbie", Password)));

        (await CountAccountsAsync("newbie")).Should().Be(1);
    }

    [Fact]
    public async Task AnAddressCanOnlyCreateAFewAccountsPerWindow()
    {
        await StartServerAsync();

        for (var i = 0; i < 3; i++)
            (await LoginResultAsync($"fresh{i}", Password)).Should().Be((byte)LoginResult.Success);

        (await LoginResultAsync("fresh3", Password)).Should().NotBe((byte)LoginResult.Success);
        (await CountAccountsAsync("fresh3")).Should().Be(0);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("tab\tname")]
    [InlineData("waytoolongaccountname123")]
    public async Task AnInvalidNameIsNeverCreated(string login)
    {
        await StartServerAsync();

        (await LoginResultAsync(login, Password)).Should().NotBe((byte)LoginResult.Success);
        (await CountAccountsAsync(login)).Should().Be(0);
    }

    [Fact]
    public async Task AnEmptyPasswordNeverCreatesAnAccount()
    {
        await StartServerAsync();

        (await LoginResultAsync("nopassword", string.Empty)).Should().NotBe((byte)LoginResult.Success);
        (await CountAccountsAsync("nopassword")).Should().Be(0);
    }
}
