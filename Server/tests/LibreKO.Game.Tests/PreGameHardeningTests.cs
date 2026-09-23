using System.Diagnostics;
using System.Net;
using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Configuration;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class PreGameHardeningTests : GameTestBase
{
    private const string Login = "player";
    private const string Password = "SECRET";
    private const string CharacterName = "Hero";
    private const short WarriorClass = 101;
    private const byte TuarekRace = 1;
    private const int CompressiblePadding = 700;
    private const int Flood = 20;
    private const int TimingSamples = 3;
    private const double MinimumCostRatio = 0.3;

    [Fact]
    public async Task ACompressedPacketIsRefusedInsteadOfUnpacked()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, PasswordHasher.Hash(Password)),
            configureServices: UseClock(new ManualClock()));
        var (client, sent) = CreateClient();

        var login = LoginRequest(GameOpcodes.GS_LOGIN, Login, Password);
        login.WriteBytes(new byte[CompressiblePadding]);
        var compressed = login.CompressIfNeeded();
        compressed.GetOpcode().Should().Be((byte)GameOpcodes.GS_COMPRESS_PACKET);
        compressed.ResetOffset();

        await provider.GetRequiredService<IPacketHandler>().HandlePacket(client, compressed);

        sent.Should().BeEmpty("the stock client never compresses, so the wrapped login must not run");
        client.AccountId.Should().Be(0);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.ForgedEventWeight);
    }

    [Fact]
    public async Task CharacterSelectRequestsAreRateLimitedPerConnection()
    {
        var clock = new ManualClock();
        using var provider = CreateProvider(db => SeedPlayer(db, Password), configureServices: UseClock(clock));
        var (client, sent) = CreateClient();
        client.AccountId = await GetAccountIdAsync(provider, Login);
        var handler = provider.GetRequiredService<IPacketHandler>();

        for (var i = 0; i < Flood; i++)
            await handler.HandlePacket(client, CharacterListRequest());

        var listing = PreGameOpcodePolicies.LimitOf(GameOpcodes.GS_ALLCHAR_INFO_REQ);
        sent.Should().HaveCount((int)listing.Capacity);

        clock.Advance(TimeSpan.FromSeconds(1 / listing.RefillPerSecond));
        await handler.HandlePacket(client, CharacterListRequest());

        sent.Should().HaveCount((int)listing.Capacity + 1);
    }

    [Fact]
    public async Task HairChangesAtCharacterSelectAreThrottled()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password), configureServices: UseClock(new ManualClock()));
        var (client, sent) = CreateClient();
        client.AccountId = await GetAccountIdAsync(provider, Login);
        var handler = provider.GetRequiredService<IPacketHandler>();

        for (var i = 0; i < Flood; i++)
        {
            var request = new Packet(GameOpcodes.GS_CHANGE_HAIR);
            request.WriteByte(1);
            request.WriteSByteString(CharacterName);
            request.WriteByte(1);
            request.WriteInt(i);
            await handler.HandlePacket(client, request);
        }

        sent.Should().HaveCount((int)PreGameOpcodePolicies.LimitOf(GameOpcodes.GS_CHANGE_HAIR).Capacity,
            "every hair change is a database write");
    }

    [Theory]
    [InlineData((byte)GameOpcodes.GS_LOGIN)]
    [InlineData((byte)GameOpcodes.GS_KICKOUT)]
    public async Task AConnectionThatKeepsFailingCredentialsIsDisconnected(byte opcode)
    {
        var clock = new ManualClock();
        using var provider = CreateProvider(db => SeedPlayer(db, PasswordHasher.Hash(Password)),
            configureServices: UseClock(clock));
        var (client, sent) = CreateClient();
        var handler = provider.GetRequiredService<IPacketHandler>();
        var limit = new GameServerSettings().Connections.MaxLoginFailuresPerConnection;
        var spacing = TimeSpan.FromSeconds(1 / PreGameOpcodePolicies.LimitOf((GameOpcodes)opcode).RefillPerSecond);

        for (var attempt = 0; attempt < limit; attempt++)
        {
            clock.Advance(spacing);
            await handler.HandlePacket(client, LoginRequest((GameOpcodes)opcode, Login, "WRONG" + attempt));
        }

        sent.Should().HaveCount(limit);
        client.DidNotReceive().Disconnect();

        clock.Advance(spacing);
        await handler.HandlePacket(client, LoginRequest((GameOpcodes)opcode, Login, Password));

        client.Received(1).Disconnect();
        sent.Should().HaveCount(limit, "the attempt after the limit is not even evaluated");
        client.AccountId.Should().Be(0);
    }

    [Fact]
    public async Task AnAccountUnderAttackIsLockedAcrossConnectionsUntilTheWindowPasses()
    {
        const int accountLimit = 3;
        var clock = new ManualClock();
        using var provider = CreateProvider(
            db => SeedPlayer(db, PasswordHasher.Hash(Password)),
            configureSettings: settings => settings.Connections.MaxLoginFailuresPerAccount = accountLimit,
            configureServices: UseClock(clock));
        var handler = provider.GetRequiredService<IPacketHandler>();

        for (var attempt = 0; attempt < accountLimit; attempt++)
        {
            var (attacker, _) = CreateClient(IPAddress.Parse($"203.0.113.{attempt + 1}"));
            await handler.HandlePacket(attacker, LoginRequest(GameOpcodes.GS_LOGIN, Login, "GUESS" + attempt));
        }

        var (owner, ownerSent) = CreateClient(IPAddress.Parse("198.51.100.7"));
        await handler.HandlePacket(owner, LoginRequest(GameOpcodes.GS_LOGIN, Login, Password));

        owner.AccountId.Should().Be(0);
        ownerSent.Single().ReadByte().Should().Be(byte.MaxValue);

        clock.Advance(new GameServerSettings().Connections.LoginFailureWindow);
        var (later, _) = CreateClient(IPAddress.Parse("198.51.100.7"));
        await handler.HandlePacket(later, LoginRequest(GameOpcodes.GS_LOGIN, Login, Password));

        later.AccountId.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnUnknownAccountTakesAsLongToRefuseAsAWrongPassword()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, PasswordHasher.Hash(Password)));
        await MeasureLoginAsync(provider, Login, "WARMUP");

        var wrongPassword = await MeasureLoginAsync(provider, Login, "WRONG");
        var unknownAccount = await MeasureLoginAsync(provider, "nobody", Password);

        unknownAccount.Should().BeGreaterThan((long)(wrongPassword * MinimumCostRatio),
            "skipping the password hash for unknown accounts tells an attacker which accounts exist");
    }

    [Fact]
    public async Task ALegacyPlaintextPasswordIsRehashedOnGameLogin()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var (client, _) = CreateClient();

        await provider.GetRequiredService<IPacketHandler>()
            .HandlePacket(client, LoginRequest(GameOpcodes.GS_LOGIN, Login, Password));

        client.AccountId.Should().BeGreaterThan(0);
        var stored = (await ReadAccountAsync(provider)).Password;
        stored.Should().NotBe(Password);
        PasswordHasher.Verify(Password, stored).Should().BeTrue();
    }

    [Fact]
    public async Task ClientStringsAreSanitizedBeforeTheyAreLogged()
    {
        var logs = new LogRecorder();
        using var provider = CreateProvider(db => SeedPlayer(db, Password),
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        var (client, _) = CreateClient();

        await provider.GetRequiredService<IPacketHandler>().HandlePacket(client,
            LoginRequest(GameOpcodes.GS_LOGIN, "evil\r\n[00:00:00 INF] GM admin logged in", Password));

        var mentions = logs.Messages.Where(message => message.Contains("evil")).ToList();
        mentions.Should().NotBeEmpty();
        mentions.Should().NotContain(message => message.Contains('\n') || message.Contains('\r'));
    }

    [Fact]
    public async Task UnhandledPreGamePacketsAreLoggedOnceWithoutTheirPayload()
    {
        var logs = new LogRecorder();
        using var provider = CreateProvider(db => SeedPlayer(db, Password), configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(new ManualClock());
            services.AddSingleton<ILoggerProvider>(logs);
        });
        var (client, _) = CreateClient();
        client.AccountId = await GetAccountIdAsync(provider, Login);
        var handler = provider.GetRequiredService<IPacketHandler>();
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };

        for (var i = 0; i < 3; i++)
        {
            var packet = new Packet(GameOpcodes.GS_MOVE);
            packet.WriteBytes(payload);
            await handler.HandlePacket(client, packet);
        }

        var warnings = logs.Messages.Where(message => message.Contains("Unhandled opcode")).ToList();
        warnings.Should().ContainSingle();
        warnings.Should().NotContain(message => message.Contains(Convert.ToHexString(payload)));
    }

    [Fact]
    public async Task GameStart_ARepeatedLoadIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password), configureServices: UseClock(new ManualClock()));
        var (client, sent) = await CreateSelectedClientAsync(provider);
        var handler = provider.GetRequiredService<IPacketHandler>();

        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));

        sent.Count(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MYINFO).Should().Be(1);
        provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id)
            .Should().Be(ViolationMonitor.InvalidStateWeight);
    }

    [Fact]
    public async Task GameStart_ReadyBeforeLoadIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var (client, sent) = await CreateSelectedClientAsync(provider);

        await provider.GetRequiredService<IPacketHandler>().HandlePacket(client, GameStartRequest(GameStartSubOpcode.Ready));

        provider.GetRequiredService<SessionManager>().GetByClientId(client.Id).Should().BeNull();
        sent.Should().BeEmpty();
    }

    [Fact]
    public async Task GameStart_ARepeatedReadyIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var (client, _) = await CreateSelectedClientAsync(provider);
        var handler = provider.GetRequiredService<IPacketHandler>();
        var viewerPackets = AddViewer(provider);

        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));
        viewerPackets.Clear();
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Ready));
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Ready));

        viewerPackets.Count(IsRespawn).Should().Be(1, "a second ready must not rebroadcast or rewrite the character row");
    }

    [Fact]
    public async Task GameStart_ReadyIsAcceptedAgainAfterReturningToCharacterSelect()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var (client, _) = await CreateSelectedClientAsync(provider);
        var handler = provider.GetRequiredService<IPacketHandler>();
        var characterId = client.CharacterId;
        var viewerPackets = AddViewer(provider);

        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Ready));
        await provider.GetRequiredService<ISessionTerminationService>().LogoutAsync(client);

        client.CharacterId = characterId;
        viewerPackets.Clear();
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));
        await handler.HandlePacket(client, GameStartRequest(GameStartSubOpcode.Ready));

        viewerPackets.Count(IsRespawn).Should().Be(1);
    }

    [Fact]
    public async Task GameStart_ABannedAccountCannotEnterTheWorld()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var (client, sent) = await CreateSelectedClientAsync(provider);
        await SetAuthorityAsync(provider, AccountAuthority.Banned);

        await provider.GetRequiredService<IPacketHandler>().HandlePacket(client, GameStartRequest(GameStartSubOpcode.Load));

        provider.GetRequiredService<SessionManager>().GetByClientId(client.Id).Should().BeNull();
        sent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MYINFO);
        client.Received(1).Disconnect();
    }

    [Fact]
    public async Task CharacterSelect_ABannedAccountIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password, AccountAuthority.Banned));
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>().SelectCharacterAsync(accountId, Login, CharacterName, 1);

        result.CharacterId.Should().Be(0);
        result.Packet.ReadByte().Should().Be((byte)SelectCharacterResult.Failed);
    }

    [Fact]
    public async Task CharacterSelect_ADeletedCharacterIsRefused()
    {
        using var provider = CreateProvider(db =>
        {
            SeedPlayer(db, Password);
            db.Characters.Single().DeletionTime = DateTime.UtcNow;
        });
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>().SelectCharacterAsync(accountId, Login, CharacterName, 1);

        result.CharacterId.Should().Be(0);
    }

    [Fact]
    public async Task CharacterSelect_AcceptsTheAccountNameInAnyCase()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>()
            .SelectCharacterAsync(accountId, Login.ToUpperInvariant(), CharacterName, 1);

        result.CharacterId.Should().BeGreaterThan(0, "the database matches logins without regard to case");
    }

    [Theory]
    [InlineData((byte)50, (byte)65, (byte)60, (byte)75, (byte)50)]
    [InlineData((byte)65, (byte)65, (byte)50, (byte)70, (byte)50)]
    public async Task Create_AStatSplitBelowTheClassBaseIsRefused(byte strength, byte stamina, byte dexterity, byte intelligence, byte magic)
    {
        using var provider = CreateStarterProvider();
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>().CreateCharacterAsync(
            accountId, 1, "Newcomer", TuarekRace, WarriorClass, 0, 0, strength, stamina, dexterity, intelligence, magic);

        result.ReadByte().Should().Be((byte)CreateCharacterResult.StatTooLow);
    }

    [Theory]
    [InlineData((byte)200, 0)]
    [InlineData((byte)0, 0x7F000000)]
    [InlineData((byte)0, -1)]
    public async Task Create_AnAppearanceTheClientCannotProduceIsRefused(byte face, int hair)
    {
        using var provider = CreateStarterProvider();
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>().CreateCharacterAsync(
            accountId, 1, "Newcomer", TuarekRace, WarriorClass, face, hair, 75, 65, 60, 50, 50);

        result.ReadByte().Should().NotBe((byte)CreateCharacterResult.Success);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("Name1")]
    [InlineData("Evil\r\nName")]
    public async Task Create_ANameOutsideTheNamePatternIsRefused(string name)
    {
        using var provider = CreateStarterProvider();
        var accountId = await GetAccountIdAsync(provider, Login);

        var result = await provider.GetRequiredService<IPreGameService>().CreateCharacterAsync(
            accountId, 1, name, TuarekRace, WarriorClass, 0, 0, 75, 65, 60, 50, 50);

        result.ReadByte().Should().Be((byte)CreateCharacterResult.InvalidName);
    }

    [Fact]
    public async Task Rename_WithoutTheScrollIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password));

        (await RenameAsync(provider, "Renamed")).Should().BeFalse();
        (await ReadCharacterAsync(provider)).Name.Should().Be(CharacterName);
    }

    [Fact]
    public async Task Rename_ConsumesOneScroll()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password, scrolls: 2));

        (await RenameAsync(provider, "Renamed")).Should().BeTrue();

        var character = await ReadCharacterAsync(provider);
        character.Name.Should().Be("Renamed");
        ScrollCount(character).Should().Be(1);
    }

    [Theory]
    [InlineData("Ab")]
    [InlineData("Bad1")]
    [InlineData("Two Words")]
    public async Task Rename_ToAnInvalidNameIsRefused(string newName)
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password, scrolls: 1));

        (await RenameAsync(provider, newName)).Should().BeFalse();
        ScrollCount(await ReadCharacterAsync(provider)).Should().Be(1);
    }

    [Fact]
    public async Task Rename_AClanMemberIsRefused()
    {
        using var provider = CreateProvider(db =>
        {
            SeedPlayer(db, Password, scrolls: 1);
            db.Characters.Single().KnightsId = 77;
        });

        (await RenameAsync(provider, "Renamed")).Should().BeFalse();
    }

    [Fact]
    public async Task Rename_AClanChiefIsRefused()
    {
        using var provider = CreateProvider(db => SeedPlayer(db, Password, scrolls: 1));
        provider.GetRequiredService<SessionManager>().Knights.AddClan(88, new KnightsEntity
        {
            Id = 88,
            Name = "Guardians",
            Chief = CharacterName,
        });

        (await RenameAsync(provider, "Renamed")).Should().BeFalse("clan leadership is stored by name");
    }

    [Fact]
    public async Task Rename_AKingIsRefused()
    {
        using var provider = CreateProvider(
            db => SeedPlayer(db, Password, scrolls: 1),
            gameData => gameData.KingSystemTable.Returns(new Dictionary<byte, KingSystemData>
            {
                [(byte)AccountNation.Karus] = new() { Nation = (byte)AccountNation.Karus, KingName = CharacterName },
            }));

        (await RenameAsync(provider, "Renamed")).Should().BeFalse("the crown is stored by name");
    }

    [Fact]
    public async Task Rename_AKingElectionCandidateIsRefused()
    {
        using var provider = CreateProvider(db =>
        {
            SeedPlayer(db, Password, scrolls: 1);
            db.KingElectionList.Add(new KingElectionList
            {
                Nation = (byte)AccountNation.Karus,
                Type = KingPacketConstants.ElectionListCandidate,
                Name = CharacterName,
            });
        });

        (await RenameAsync(provider, "Renamed")).Should().BeFalse("votes are counted by candidate name");
    }

    private static void SeedPlayer(AppDbContext db, string password, AccountAuthority authority = AccountAuthority.Normal, ushort scrolls = 0)
    {
        db.Accounts.Add(new Account
        {
            Login = Login,
            Password = password,
            Nation = AccountNation.Karus,
            Authority = authority,
        });
        db.SaveChanges();

        var inventory = new byte[InventoryConstants.InventoryTotal * UserSessionBinaryState.BytesPerItem];
        if (scrolls > 0)
        {
            var offset = InventoryConstants.SlotMax * UserSessionBinaryState.BytesPerItem;
            BitConverter.TryWriteBytes(inventory.AsSpan(offset), CharacterRules.RenameScrollItemId);
            BitConverter.TryWriteBytes(inventory.AsSpan(offset + sizeof(int) + sizeof(short)), scrolls);
        }

        db.Characters.Add(new Character
        {
            AccountId = db.Accounts.Single(account => account.Login == Login).Id,
            Slot = 0,
            Name = CharacterName,
            Race = TuarekRace,
            Class = WarriorClass,
            Face = 2,
            Hair = 3,
            Level = 10,
            Hp = 100,
            Mp = 100,
            MapId = 1,
            X = 10,
            Z = 20,
            Items = inventory,
            SkillPointData = new byte[MyInfoSkillDataSize],
        });
        db.SaveChanges();
    }

    private static ServiceProvider CreateStarterProvider() => CreateProvider(
        db => SeedPlayer(db, Password),
        gameData =>
        {
            gameData.GetCoefficient(WarriorClass).Returns(new CoefficientData { ClassId = WarriorClass });
            gameData.GetStartPosition(Arg.Any<short>()).Returns(new StartPositionData { ZoneId = (short)ZoneId.Moradon });
        });

    private static Action<IServiceCollection> UseClock(ManualClock clock) =>
        services => services.AddSingleton<TimeProvider>(clock);

    private static async Task<long> MeasureLoginAsync(ServiceProvider provider, string login, string password)
    {
        var samples = new long[TimingSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            using var scope = provider.CreateScope();
            var preGameService = scope.ServiceProvider.GetRequiredService<IPreGameService>();
            var started = Stopwatch.GetTimestamp();
            var result = await preGameService.LoginAsync(login, password);
            samples[i] = Stopwatch.GetTimestamp() - started;
            result.Success.Should().BeFalse();
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static (IClient Client, List<Packet> Sent) CreateClient(IPAddress? address = null)
    {
        var sent = new List<Packet>();
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.RemoteAddress.Returns(address);
        client.SendPacket(Arg.Do<Packet>(packet => sent.Add(ClonePacket(packet))), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return (client, sent);
    }

    private static async Task<(IClient Client, List<Packet> Sent)> CreateSelectedClientAsync(ServiceProvider provider)
    {
        var (client, sent) = CreateClient();
        client.AccountId = await GetAccountIdAsync(provider, Login);
        client.CharacterId = await GetCharacterIdAsync(provider, CharacterName);
        (await provider.GetRequiredService<IAccountLockService>().AcquireAsync(client, client.AccountId))
            .Granted.Should().BeTrue();
        return (client, sent);
    }

    private static List<Packet> AddViewer(ServiceProvider provider)
    {
        var (viewerClient, viewerPackets) = CreateClient();
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var viewer = sessionManager.CreateSession(viewerClient, characterId: 9001, accountId: 9002);
        viewer.Name = "Viewer";
        viewer.ZoneId = 1;
        viewer.X = 10;
        viewer.Z = 20;
        sessionManager.Regions.AddToRegion(viewer);
        return viewerPackets;
    }

    private static bool IsRespawn(Packet packet)
    {
        if (packet.GetOpcode() != (byte)GameOpcodes.GS_USER_INOUT)
            return false;

        packet.ResetOffset();
        return packet.ReadByte() == (byte)InOutType.Respawn;
    }

    private static Packet LoginRequest(GameOpcodes opcode, string login, string password)
    {
        var packet = new Packet(opcode);
        packet.WriteString(login);
        packet.WriteString(password);
        return packet;
    }

    private static Packet CharacterListRequest()
    {
        var packet = new Packet(GameOpcodes.GS_ALLCHAR_INFO_REQ);
        packet.WriteByte((byte)AllCharacterInfoOpcode.CharacterList);
        return packet;
    }

    private static Packet GameStartRequest(GameStartSubOpcode subOpcode)
    {
        var packet = new Packet(GameOpcodes.GS_GAMESTART);
        packet.WriteByte((byte)subOpcode);
        return packet;
    }

    private static async Task<bool> RenameAsync(ServiceProvider provider, string newName)
    {
        using var scope = provider.CreateScope();
        var accountId = await GetAccountIdAsync(provider, Login);
        var response = await scope.ServiceProvider.GetRequiredService<IPreGameService>()
            .ChangeSelectingCharacterNameAsync(accountId, 1, CharacterName, newName);

        response.ReadByte().Should().Be((byte)AllCharacterInfoOpcode.NameChange);
        return response.ReadByte() == (byte)SelectingCharacterNameChangeResult.Success;
    }

    private static int ScrollCount(Character character)
    {
        var inventory = new ItemSlot[InventoryConstants.InventoryTotal];
        for (var i = 0; i < inventory.Length; i++)
            inventory[i] = new ItemSlot();

        UserSessionBinaryState.LoadItems(inventory, character.Items);
        return inventory.Where(slot => slot.ItemId == CharacterRules.RenameScrollItemId).Sum(slot => slot.Count);
    }

    private static async Task<Account> ReadAccountAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.AsNoTracking()
            .SingleAsync(account => account.Login == Login);
    }

    private static async Task<Character> ReadCharacterAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Characters.AsNoTracking().SingleAsync();
    }

    private static async Task SetAuthorityAsync(ServiceProvider provider, AccountAuthority authority)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.SingleAsync(entry => entry.Login == Login);
        account.Authority = authority;
        await db.SaveChangesAsync();
    }

    private sealed class LogRecorder : ILoggerProvider
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
