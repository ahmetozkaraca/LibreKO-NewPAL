using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Gameplay;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Configuration;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class AccountLockTests : GameTestBase
{
    private const int AccountId = 4711;
    private const int CharacterId = 4712;
    private const int ServerId = 1;
    private const int HandoverProbeMs = 200;
    private const int ShortHandoverSeconds = 1;
    private const int SavedMoney = 4_242;

    [Fact]
    public async Task AcquireAsync_RefusesASecondLiveConnectionForTheSameAccount()
    {
        var (service, _) = CreateService();
        var first = CreateClient(connected: true);
        var second = CreateClient(connected: true);

        (await service.AcquireAsync(first, AccountId)).Granted.Should().BeTrue();

        var refused = await service.AcquireAsync(second, AccountId);
        refused.Granted.Should().BeFalse();
        refused.Occupant.Should().NotBeNull();
        refused.Occupant!.Stage.Should().Be(AccountClaimStage.PreGame);
        service.Owns(second).Should().BeFalse();
        service.Owns(first).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ReportsTheCharacterOfAnInGameOccupant()
    {
        var (service, provider) = CreateService();
        var first = CreateClient(connected: true);
        await service.AcquireAsync(first, AccountId);

        var sessions = provider.GetRequiredService<SessionManager>();
        sessions.CreateSession(first, characterId: 90210, accountId: AccountId).Name = "Aurelia";

        var refused = await service.AcquireAsync(CreateClient(connected: true), AccountId);

        refused.Granted.Should().BeFalse();
        refused.Occupant!.Stage.Should().Be(AccountClaimStage.InGame);
        refused.Occupant.CharacterName.Should().Be("Aurelia");
    }

    [Fact]
    public async Task AcquireAsync_TakesOverAClaimWhoseConnectionIsGone()
    {
        var (service, _) = CreateService();
        var dropped = CreateClient(connected: false);
        var reconnecting = CreateClient(connected: true);

        await service.AcquireAsync(dropped, AccountId);
        var takeover = service.AcquireAsync(reconnecting, AccountId);
        await service.ReleaseAsync(dropped);

        (await takeover).Granted.Should().BeTrue();
        service.Owns(reconnecting).Should().BeTrue();
        service.Owns(dropped).Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_WaitsForADroppedHolderToFinishBeforeHandingOver()
    {
        var (service, _) = CreateService();
        var dropped = CreateClient(connected: false);
        await service.AcquireAsync(dropped, AccountId);

        var takeover = service.AcquireAsync(CreateClient(connected: true), AccountId);
        await Task.Delay(HandoverProbeMs);
        takeover.IsCompleted.Should().BeFalse("the dropped holder is still saving its character");

        await service.ReleaseAsync(dropped);

        (await takeover).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_RefusesWhenTheDroppedHolderNeverFinishesLeaving()
    {
        var (service, _) = CreateService(handoverSeconds: ShortHandoverSeconds);
        var dropped = CreateClient(connected: false);
        await service.AcquireAsync(dropped, AccountId);

        var refused = await service.AcquireAsync(CreateClient(connected: true), AccountId);

        refused.Granted.Should().BeFalse();
        refused.Occupant.Should().NotBeNull();
        service.Owns(dropped).Should().BeTrue();
    }

    [Fact]
    public async Task KickAsync_EvictsTheHolderAndFreesTheAccount()
    {
        var (service, _) = CreateService();
        var holder = CreateClient(connected: true);
        await service.AcquireAsync(holder, AccountId);
        holder.When(client => client.Disconnect()).Do(call => service.ReleaseAsync(holder));

        (await service.KickAsync(AccountId)).Should().Be(AccountKickCode.Done);

        await holder.Received(1).SendPacket(
            Arg.Is<Packet>(p => p.GetOpcode() == (byte)GameOpcodes.GS_KICKOUT),
            Arg.Any<CancellationToken>());
        holder.Received(1).Disconnect();
        service.Owns(holder).Should().BeFalse();

        (await service.AcquireAsync(CreateClient(connected: true), AccountId)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task KickAsync_HandsTheAccountOverOnlyAfterTheHoldersFinalSave()
    {
        var probe = new SaveChangesProbe();
        var (service, provider) = CreateService(probe);
        var holder = CreateClient(connected: true);
        await service.AcquireAsync(holder, AccountId);
        var session = provider.GetRequiredService<SessionManager>().CreateSession(holder, CharacterId, AccountId);
        session.Name = "Aurelia";
        session.Money = SavedMoney;
        var termination = provider.GetRequiredService<ISessionTerminationService>();
        holder.When(client => client.Disconnect()).Do(call => Task.Run(async () =>
        {
            await termination.DisconnectAsync(holder);
            await service.ReleaseAsync(holder);
        }));

        var finalSaveReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeSave = async db =>
        {
            if (!db.ChangeTracker.Entries<Character>().Any(entry => entry.State == EntityState.Modified))
                return;
            finalSaveReached.TrySetResult();
            await releaseFinalSave.Task;
        };

        var kick = service.KickAsync(AccountId);
        await finalSaveReached.Task;
        var login = service.AcquireAsync(CreateClient(connected: true), AccountId);
        await Task.Delay(HandoverProbeMs);

        kick.IsCompleted.Should().BeFalse();
        login.IsCompleted.Should().BeFalse("the account must not change hands before the final save lands");

        releaseFinalSave.SetResult();

        (await kick).Should().Be(AccountKickCode.Done);
        (await login).Granted.Should().BeTrue();
        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Characters
            .AsNoTracking().SingleAsync(c => c.Id == CharacterId);
        stored.Money.Should().Be(SavedMoney);
    }

    [Fact]
    public async Task KickAsync_ReportsNotOnlineWhenNobodyHoldsTheAccount()
    {
        var (service, _) = CreateService();

        (await service.KickAsync(AccountId)).Should().Be(AccountKickCode.NotOnline);
    }

    [Fact]
    public async Task ReleaseAsync_FreesTheAccountForTheNextConnection()
    {
        var (service, _) = CreateService();
        var holder = CreateClient(connected: true);
        await service.AcquireAsync(holder, AccountId);

        await service.ReleaseAsync(holder);

        service.Owns(holder).Should().BeFalse();
        (await service.AcquireAsync(CreateClient(connected: true), AccountId)).Granted.Should().BeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_OfAnEvictedClientLeavesTheNewHolderAlone()
    {
        var (service, _) = CreateService();
        var first = CreateClient(connected: false);
        var second = CreateClient(connected: true);

        await service.AcquireAsync(first, AccountId);
        var takeover = service.AcquireAsync(second, AccountId);
        await service.ReleaseAsync(first);
        await takeover;
        await service.ReleaseAsync(first);

        service.Owns(second).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_MirrorsTheClaimOnTheAccountRow()
    {
        var (service, provider) = CreateService();
        var holder = CreateClient(connected: true);

        await service.AcquireAsync(holder, AccountId);
        (await ReadOnlineServerAsync(provider)).Should().Be(ServerId);

        await service.ReleaseAsync(holder);
        (await ReadOnlineServerAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task ClearOwnClaimsAsync_ClearsRowsLeftBehindByAPreviousRun()
    {
        var (service, provider) = CreateService();
        await using (var scope = provider.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            await accounts.SetOnlineServerAsync(AccountId, ServerId);
        }

        await service.ClearOwnClaimsAsync();

        (await ReadOnlineServerAsync(provider)).Should().BeNull();
    }

    private static async Task<int?> ReadOnlineServerAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.Accounts.FindAsync(AccountId);
        return account?.OnlineServerId;
    }

    private static (AccountLockService Service, ServiceProvider Provider) CreateService(
        SaveChangesProbe? probe = null,
        int handoverSeconds = PlayerSettings.DefaultSessionHandoverTimeoutSeconds)
    {
        var provider = CreateProvider(
            db =>
            {
                db.Accounts.Add(new Account
                {
                    Id = AccountId,
                    Login = "claimant",
                    Password = "hash",
                });
                db.Characters.Add(new Character { Id = CharacterId, AccountId = AccountId, Name = "Aurelia" });
            },
            configureServices: services => probe?.Register(services));

        var servers = Substitute.For<IServerRepository>();
        servers.GetServers().Returns(Task.FromResult(new List<LibreKO.Common.Domain.Entities.Server>
        {
            new() { Id = ServerId, Name = "Beramus Legacy", IpAddress = "127.0.0.1", LanIpAddress = "127.0.0.1", Port = 15001 },
        }));

        var settings = new GameServerSettings { ServerId = ServerId };
        settings.Player.SessionHandoverTimeoutSeconds = handoverSeconds;
        var service = new AccountLockService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            servers,
            provider.GetRequiredService<SessionManager>(),
            Options.Create(settings),
            NullLogger<AccountLockService>.Instance);

        return (service, provider);
    }

    private static IClient CreateClient(bool connected)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.IsConnected.Returns(connected);
        return client;
    }
}
