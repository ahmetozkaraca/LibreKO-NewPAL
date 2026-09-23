using System.Collections.Concurrent;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public interface ICharacterUnitOfWork
{
    AppDbContext Db { get; }

    Task CommitAsync();
}

public interface ICharacterStatePersister
{
    Task<bool> SaveAsync(UserSession session, CancellationToken cancellationToken = default);

    Task<bool> SaveFinalAsync(UserSession session, CancellationToken cancellationToken = default);

    Task<bool> RequestSaveAsync(UserSession session);

    Task<TResult> RunAsync<TResult>(UserSession session, TResult refused, Func<ICharacterUnitOfWork, Task<TResult>> work);

    Task SetOnlineStateAsync(int characterId, bool isOnline, CancellationToken cancellationToken = default);

    Task SaveQuestStateAsync(UserSession session, CancellationToken cancellationToken = default);
}

public class CharacterStatePersister(
    IServiceScopeFactory scopeFactory,
    IUserSessionCharacterMapper mapper,
    ILogger<CharacterStatePersister> logger) : ICharacterStatePersister
{
    private const int MaxConcurrentRequestedSaves = 8;

    private readonly SemaphoreSlim _requestedSaveGate = new(MaxConcurrentRequestedSaves);
    private readonly ConcurrentDictionary<int, CharacterSlot> _slots = new();

    public Task<bool> SaveAsync(UserSession session, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(session.CharacterId, () => WriteAsync(session, final: false, cancellationToken), cancellationToken);

    public Task<bool> SaveFinalAsync(UserSession session, CancellationToken cancellationToken = default) =>
        ExclusiveAsync(session.CharacterId, () => WriteAsync(session, final: true, cancellationToken), cancellationToken);

    public Task SaveQuestStateAsync(UserSession session, CancellationToken cancellationToken = default) =>
        RequestSaveAsync(session);

    public Task<bool> RequestSaveAsync(UserSession session)
    {
        var slot = Enter(session.CharacterId);
        PendingSave pending;
        using (slot.Sync.EnterScope())
        {
            if (slot.Pending is { } queued && ReferenceEquals(queued.Session, session))
            {
                slot.Users--;
                return queued.Completion.Task;
            }

            pending = new PendingSave(session);
            slot.Pending = pending;
        }

        _ = RunPendingAsync(slot, pending);
        return pending.Completion.Task;
    }

    public Task<TResult> RunAsync<TResult>(UserSession session, TResult refused, Func<ICharacterUnitOfWork, Task<TResult>> work) =>
        ExclusiveAsync(session.CharacterId, async () =>
        {
            if (session.IsClosing)
                return refused;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await work(new CharacterUnitOfWork(db, session, this));
        }, CancellationToken.None);

    public async Task SetOnlineStateAsync(int characterId, bool isOnline, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var character = await db.Characters.FindAsync([characterId], cancellationToken);
        if (character == null)
            return;

        character.IsOnline = isOnline;
        character.LastOnlineTime = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> WriteAsync(UserSession session, bool final, CancellationToken cancellationToken)
    {
        if (session.IsClosing && !final)
            return false;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await StageAsync(db, session, cancellationToken))
            return false;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> StageAsync(AppDbContext db, UserSession session, CancellationToken cancellationToken)
    {
        var character = await db.Characters.FindAsync([session.CharacterId], cancellationToken);
        if (character == null)
            return false;

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(entry => entry.AccountId == session.AccountId, cancellationToken)
            ?? db.Warehouses.Add(new Warehouse { AccountId = session.AccountId }).Entity;
        var dailyOp = await db.UserDailyOps.FindAsync([session.CharacterId], cancellationToken)
            ?? db.UserDailyOps.Add(new UserDailyOp { CharacterId = session.CharacterId }).Entity;
        var account = await db.Accounts.FindAsync([session.AccountId], cancellationToken);

        session.WithLock(s =>
        {
            mapper.ApplyToCharacter(s, character);
            mapper.ApplyToWarehouse(s, warehouse);
            mapper.ApplyToDailyOps(s, dailyOp);
            if (account != null)
                mapper.ApplyToAccount(s, account);
        });
        return true;
    }

    private async Task RunPendingAsync(CharacterSlot slot, PendingSave pending)
    {
        var session = pending.Session;
        var saved = false;
        try
        {
            await _requestedSaveGate.WaitAsync();
            try
            {
                await slot.Gate.WaitAsync();
                try
                {
                    using (slot.Sync.EnterScope())
                    {
                        if (ReferenceEquals(slot.Pending, pending))
                            slot.Pending = null;
                    }

                    saved = await WriteAsync(session, final: false, CancellationToken.None);
                }
                finally
                {
                    slot.Gate.Release();
                }
            }
            finally
            {
                _requestedSaveGate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Requested save failed for character {CharacterId}", session.CharacterId);
        }
        finally
        {
            Leave(session.CharacterId, slot);
            pending.Completion.TrySetResult(saved);
        }
    }

    private async Task<T> ExclusiveAsync<T>(int characterId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var slot = Enter(characterId);
        try
        {
            await slot.Gate.WaitAsync(cancellationToken);
            try
            {
                return await work();
            }
            finally
            {
                slot.Gate.Release();
            }
        }
        finally
        {
            Leave(characterId, slot);
        }
    }

    private CharacterSlot Enter(int characterId)
    {
        while (true)
        {
            var slot = _slots.GetOrAdd(characterId, static _ => new CharacterSlot());
            using var scope = slot.Sync.EnterScope();
            if (slot.Retired)
                continue;

            slot.Users++;
            return slot;
        }
    }

    private void Leave(int characterId, CharacterSlot slot)
    {
        using var scope = slot.Sync.EnterScope();
        if (--slot.Users > 0)
            return;

        slot.Retired = true;
        _slots.TryRemove(new KeyValuePair<int, CharacterSlot>(characterId, slot));
    }

    private sealed class CharacterSlot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Lock Sync = new();
        public int Users;
        public bool Retired;
        public PendingSave? Pending;
    }

    private sealed class PendingSave(UserSession session)
    {
        public UserSession Session { get; } = session;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CharacterUnitOfWork(AppDbContext db, UserSession session, CharacterStatePersister persister)
        : ICharacterUnitOfWork
    {
        public AppDbContext Db => db;

        public async Task CommitAsync()
        {
            await persister.StageAsync(db, session, CancellationToken.None);
            await db.SaveChangesAsync();
        }
    }
}
