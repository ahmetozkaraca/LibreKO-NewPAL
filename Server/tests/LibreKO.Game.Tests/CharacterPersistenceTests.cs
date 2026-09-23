using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Configuration;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class CharacterPersistenceTests : GameTestBase
{
    private const int AccountId = 7100;
    private const int CharacterId = 7200;
    private const string CharacterName = "Keeper";
    private const int PotionId = 389010000;
    private const short QuestId = 1234;
    private const byte QuestCompleted = 2;
    private const int AutoSaveDefaultSeconds = 120;
    private const long SweepMoment = 1_000;
    private const long ExpiredAt = 500;
    private const int LockHoldMs = 200;
    private const long DeathPenalty = 4_000;
    private const int RequestBurst = 5;

    [Fact]
    public async Task ASaveWritesEveryRowOfTheCharacterInOneCommit()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var session = Online(provider);
        session.Money = 4_321;
        Stock(session, InventoryConstants.InventoryStart, PotionId, 2);
        session.WarehouseMoney = 77;
        session.KnightCash = 55;
        var commitsBefore = probe.Saves;

        (await Persister(provider).SaveAsync(session)).Should().BeTrue();

        probe.Saves.Should().Be(commitsBefore + 1);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var character = await db.Characters.SingleAsync(c => c.Id == CharacterId);
        character.Money.Should().Be(4_321);
        StoredSlot(character.Items, InventoryConstants.InventoryStart).Count.Should().Be(2);
        (await db.Warehouses.SingleAsync(w => w.AccountId == AccountId)).Money.Should().Be(77);
        (await db.Accounts.SingleAsync(a => a.Id == AccountId)).KnightCash.Should().Be(55);
    }

    [Fact]
    public async Task AnOlderSnapshotNeverLandsAfterANewerOne()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var session = Online(provider);
        var firstCommitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        probe.BeforeSave = async _ =>
        {
            if (Interlocked.Increment(ref commits) != 1)
                return;
            firstCommitReached.SetResult();
            await releaseFirstCommit.Task;
        };

        session.Money = 100;
        var older = Persister(provider).SaveAsync(session);
        await firstCommitReached.Task;
        session.Money = 200;
        var newer = Persister(provider).SaveAsync(session);
        await Task.Delay(LockHoldMs);
        releaseFirstCommit.SetResult();
        await Task.WhenAll(older, newer);

        (await StoredCharacterAsync(provider)).Money.Should().Be(200);
    }

    [Fact]
    public async Task ASaveThatArrivesAfterTheFinalSaveIsDropped()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.Money = 1_000;

        await provider.GetRequiredService<ISessionTerminationService>().LogoutAsync(session.Client);
        session.Money = 999_999;
        await Persister(provider).SaveAsync(session);

        (await StoredCharacterAsync(provider)).Money.Should().Be(1_000);
    }

    [Fact]
    public async Task ATakeoverSavesTheSessionItEvicts()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.Money = 2_500;
        Stock(session, InventoryConstants.InventoryStart, PotionId, 3);

        await provider.GetRequiredService<ISessionTerminationService>().EvictForTakeoverAsync(session);

        var character = await StoredCharacterAsync(provider);
        character.Money.Should().Be(2_500);
        StoredSlot(character.Items, InventoryConstants.InventoryStart).Count.Should().Be(3);
        provider.GetRequiredService<SessionManager>().GetByCharacterId(CharacterId).Should().BeNull();
    }

    [Fact]
    public async Task AQuestSaveCommitsTheInventoryWithTheQuestState()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.Quest.QuestMap[QuestId] = QuestCompleted;
        Stock(session, InventoryConstants.InventoryStart, PotionId, 1);

        await Persister(provider).RequestSaveAsync(session);

        var character = await StoredCharacterAsync(provider);
        character.QuestData.Should().Equal(session.SerializeQuestData());
        StoredSlot(character.Items, InventoryConstants.InventoryStart).ItemId.Should().Be(PotionId);
    }

    [Fact]
    public void AutoSaveRunsEveryTwoMinutesByDefault()
    {
        new PlayerSettings().AutoSaveDelaySeconds.Should().Be(AutoSaveDefaultSeconds);
    }

    [Fact]
    public async Task AutoSaveAlsoSavesDeadCharacters()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.Hp = 0;
        session.DeathExpLoss = DeathPenalty;
        var autoSave = new AutoSaveService(
            provider.GetRequiredService<SessionManager>(),
            Persister(provider),
            Options.Create(new GameServerSettings()),
            NullLogger<AutoSaveService>.Instance);

        (await autoSave.SaveAllAsync(CancellationToken.None)).Should().Be(1);

        var character = await StoredCharacterAsync(provider);
        character.Hp.Should().Be(0);
        character.DeathExpLoss.Should().Be(DeathPenalty);
    }

    [Fact]
    public async Task RepeatedSaveRequestsCoalesceIntoOneFollowUpSave()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var session = Online(provider);
        var firstCommitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        probe.BeforeSave = async _ =>
        {
            if (Interlocked.Increment(ref commits) != 1)
                return;
            firstCommitReached.SetResult();
            await releaseFirstCommit.Task;
        };
        var persister = Persister(provider);

        session.Money = 100;
        var first = persister.RequestSaveAsync(session);
        await firstCommitReached.Task;
        session.Money = 300;
        var followUps = Enumerable.Range(0, RequestBurst).Select(_ => persister.RequestSaveAsync(session)).ToArray();
        releaseFirstCommit.SetResult();
        await first;
        await Task.WhenAll(followUps);

        probe.Saves.Should().Be(2);
        (await StoredCharacterAsync(provider)).Money.Should().Be(300);
    }

    [Fact]
    public async Task PacketsFromAClosingSessionAreIgnored()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.TryBeginClosing();
        var packet = new Packet(GameOpcodes.GS_UPGRADE_NOTICE);
        packet.WriteInt(PotionId);
        packet.ResetOffset();

        await provider.GetRequiredService<IInGameOpcodeRouter>().Resolve(GameOpcodes.GS_UPGRADE_NOTICE)!(session.Client, packet);

        session.WatchedUpgradeItem.Should().Be(0);
    }

    [Fact]
    public async Task TheFinalSaveWaitsForACommitAlreadyUnderWay()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var session = Online(provider);
        Stock(session, InventoryConstants.InventoryStart, PotionId, 3);
        var unitCommitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnitCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.BeforeSave = async _ =>
        {
            if (unitCommitReached.TrySetResult())
                await releaseUnitCommit.Task;
        };

        var unit = Persister(provider).RunAsync(session, false, async work =>
        {
            session.WithLock(s => s.Inventory[InventoryConstants.InventoryStart].Clear());
            await work.CommitAsync();
            return true;
        });
        await unitCommitReached.Task;
        var logout = provider.GetRequiredService<ISessionTerminationService>().LogoutAsync(session.Client);
        await Task.Delay(LockHoldMs);
        logout.IsCompleted.Should().BeFalse();

        releaseUnitCommit.SetResult();
        (await unit).Should().BeTrue();
        await logout;

        StoredSlot((await StoredCharacterAsync(provider)).Items, InventoryConstants.InventoryStart).IsEmpty.Should().BeTrue();
        (await Persister(provider).RunAsync(session, false, _ => Task.FromResult(true))).Should().BeFalse();
    }

    [Fact]
    public async Task ShutdownSavesEveryoneAndClosesTheirConnections()
    {
        using var provider = Provider(new SaveChangesProbe());
        var session = Online(provider);
        session.Money = 3_333;
        var shutdown = new GracefulShutdownService(
            provider.GetRequiredService<SessionManager>(),
            provider.GetRequiredService<ISessionTerminationService>(),
            Substitute.For<IAccountLockService>(),
            NullLogger<GracefulShutdownService>.Instance);

        await shutdown.StopAsync(CancellationToken.None);

        (await StoredCharacterAsync(provider)).Money.Should().Be(3_333);
        session.Client.Received(1).Disconnect();
    }

    [Fact]
    public async Task MarkingACharacterOfflineOnlyTouchesTheOnlineColumns()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var written = new List<string>();
        probe.BeforeSave = db =>
        {
            written.AddRange(db.ChangeTracker.Entries<Character>()
                .SelectMany(entry => entry.Properties.Where(property => property.IsModified))
                .Select(property => property.Metadata.Name));
            return Task.CompletedTask;
        };

        await Persister(provider).SetOnlineStateAsync(CharacterId, isOnline: false);

        written.Should().NotBeEmpty();
        written.Should().OnlyContain(name => name == nameof(Character.IsOnline) || name == nameof(Character.LastOnlineTime));
    }

    [Fact]
    public async Task TheExpirySweepWaitsForThePlayersLock()
    {
        using var provider = CreateProvider(_ => { });
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = sessionManager.CreateSession(client, CharacterId, AccountId);
        var rented = session.Inventory[InventoryConstants.InventoryStart];
        rented.ItemId = PotionId;
        rented.Count = 1;
        rented.ExpiresAt = ExpiredAt;
        var service = new ItemExpiryService(sessionManager, NullLogger<ItemExpiryService>.Instance);

        Task sweep = Task.CompletedTask;
        var clearedWhileLocked = true;
        session.WithLock(_ =>
        {
            sweep = Task.Run(() => service.ProcessTickAsync(SweepMoment));
            Thread.Sleep(LockHoldMs);
            clearedWhileLocked = rented.IsEmpty;
        });
        await sweep;

        clearedWhileLocked.Should().BeFalse();
        rented.IsEmpty.Should().BeTrue();
        sent.Should().ContainSingle(p => p.GetOpcode() == (byte)GameOpcodes.GS_ITEM_COUNT_CHANGE);
    }

    private static ServiceProvider Provider(SaveChangesProbe probe) => CreateProvider(
        db =>
        {
            db.Accounts.Add(new Account { Id = AccountId, Login = "keeper", Password = "pw", Nation = AccountNation.Karus });
            db.Characters.Add(new Character
            {
                Id = CharacterId,
                AccountId = AccountId,
                Name = CharacterName,
                Race = 1,
                Class = 101,
                Level = 10,
                Hp = 100,
                Mp = 100,
                MapId = 21,
                Items = new byte[InventoryConstants.InventoryTotal * UserSessionBinaryState.BytesPerItem],
                SkillPointData = new byte[9],
            });
            db.Warehouses.Add(new Warehouse { AccountId = AccountId });
            db.UserDailyOps.Add(new UserDailyOp { CharacterId = CharacterId });
        },
        configureServices: probe.Register);

    private static UserSession Online(ServiceProvider provider)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId = CharacterId;
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, CharacterId, AccountId);
        session.Name = CharacterName;
        session.Class = 101;
        session.Level = 10;
        session.ZoneId = 21;
        session.MaxHp = 100;
        session.Hp = 100;
        return session;
    }

    private static void Stock(UserSession session, int slot, int itemId, ushort count)
    {
        session.Inventory[slot].ItemId = itemId;
        session.Inventory[slot].Count = count;
    }

    private static ICharacterStatePersister Persister(ServiceProvider provider) =>
        provider.GetRequiredService<ICharacterStatePersister>();

    private static async Task<Character> StoredCharacterAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Characters.AsNoTracking().SingleAsync(c => c.Id == CharacterId);
    }

    private static ItemSlot StoredSlot(byte[] items, int slot)
    {
        var slots = Enumerable.Range(0, InventoryConstants.InventoryTotal).Select(_ => new ItemSlot()).ToArray();
        UserSessionBinaryState.LoadItems(slots, items);
        return slots[slot];
    }
}
