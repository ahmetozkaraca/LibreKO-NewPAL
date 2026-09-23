using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public interface IRewardStateService
{
    Task<bool> EnsureLoadedAsync(UserSession session);
    Task<T> RunExclusiveAsync<T>(UserSession session, T unavailable, Func<RewardState, Task<T>> operation);
    Task<bool> SaveProgressAsync(UserSession session);
    void SaveProgressInBackground(UserSession session);
    Task FlushAsync(UserSession session);
    Task<bool> CommitAsync(UserSession session, string operation, Func<bool> apply, Func<IRewardStateRepository, Task<bool>> stage, Action undo);
}

public sealed class RewardStateService(
    IServiceScopeFactory scopeFactory,
    IGameDataService gameData,
    ICharacterStatePersister persister,
    TimeProvider time,
    ILogger<RewardStateService> logger) : IRewardStateService
{
    public async Task<bool> EnsureLoadedAsync(UserSession session)
    {
        if (session.Rewards.IsLoaded)
            return true;

        return await RunExclusiveAsync(session, false, _ => Task.FromResult(true));
    }

    public async Task<T> RunExclusiveAsync<T>(UserSession session, T unavailable, Func<RewardState, Task<T>> operation)
    {
        var state = session.Rewards;
        await state.Gate.WaitAsync();
        try
        {
            if (!state.IsLoaded && !await TryLoadAsync(session, state))
                return unavailable;

            return await operation(state);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> SaveProgressAsync(UserSession session)
    {
        var state = session.Rewards;
        var rows = session.WithLock(_ =>
        {
            var dirty = new List<CharacterRewardQuest>(state.DirtyQuests.Count);
            foreach (var key in state.DirtyQuests)
            {
                if (state.Quests.TryGetValue(key, out var progress))
                    dirty.Add(ProgressRow(session.CharacterId, key, progress));
            }

            state.DirtyQuests.Clear();
            return dirty;
        });

        if (rows.Count == 0)
            return true;

        if (await WriteAsync("progress save", session.CharacterId, async repository =>
            {
                await repository.SaveQuestProgressAsync(rows);
                return true;
            }))
        {
            return true;
        }

        session.WithLock(_ =>
        {
            foreach (var row in rows)
                state.DirtyQuests.Add(new RewardQuestKey(row.QuestId, row.PeriodStart));
        });
        return false;
    }

    public void SaveProgressInBackground(UserSession session) => _ = FlushAsync(session);

    public async Task FlushAsync(UserSession session)
    {
        if (!session.Rewards.IsLoaded)
            return;

        try
        {
            await RunExclusiveAsync(session, false, _ => SaveProgressAsync(session));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reward progress flush failed for {Name} ({CharacterId})", session.Name, session.CharacterId);
        }
    }

    public Task<bool> CommitAsync(UserSession session, string operation, Func<bool> apply,
        Func<IRewardStateRepository, Task<bool>> stage, Action undo) =>
        persister.RunAsync(session, false, async unit =>
        {
            if (!apply())
                return false;

            try
            {
                if (await stage(new RewardStateRepository(unit.Db)))
                {
                    await unit.CommitAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reward {Operation} failed for {Name} ({CharacterId})",
                    operation, session.Name, session.CharacterId);
            }

            undo();
            return false;
        });

    private async Task<bool> TryLoadAsync(UserSession session, RewardState state)
    {
        try
        {
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRewardStateRepository>();
            var progress = await repository.GetQuestProgressAsync(session.CharacterId, OldestCurrentPeriod(today));
            var coins = await repository.GetEventCoinsAsync(session.CharacterId);
            var spins = await repository.GetRecentSpinsAsync(session.CharacterId, RewardState.RecentSpinCapacity);
            var claims = await repository.GetDailyClaimsAsync(session.AccountId, today);

            session.WithLock(_ =>
            {
                foreach (var row in progress)
                {
                    state.Quests[new RewardQuestKey(row.QuestId, row.PeriodStart)] = new RewardQuestProgress
                    {
                        Kills = row.Kills,
                        AcceptedAt = row.AcceptedAt,
                        ClaimedAt = row.ClaimedAt,
                    };
                }

                state.EventCoins = coins;
                state.RecentSpins.AddRange(spins.Select(spin =>
                    new RoulettePrizeRecord(spin.Kind, spin.ItemId, spin.Count, spin.SpunAt)));
                foreach (var pool in claims)
                    state.DailyClaims[pool] = today;
            });

            state.IsLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load reward state for {Name} ({CharacterId})", session.Name, session.CharacterId);
            return false;
        }
    }

    private DateOnly OldestCurrentPeriod(DateOnly today) =>
        gameData.RewardQuestTable.Values
            .Select(quest => quest.CurrentPeriod(today))
            .OfType<DateOnly>()
            .Where(period => period != RewardQuestData.PermanentPeriod)
            .DefaultIfEmpty(today)
            .Min();

    private async Task<bool> WriteAsync(string operation, int characterId, Func<IRewardStateRepository, Task<bool>> write)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            return await write(scope.ServiceProvider.GetRequiredService<IRewardStateRepository>());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reward {Operation} failed for character {CharacterId}", operation, characterId);
            return false;
        }
    }

    private static CharacterRewardQuest ProgressRow(int characterId, RewardQuestKey key, RewardQuestProgress progress) =>
        new()
        {
            CharacterId = characterId,
            QuestId = key.QuestId,
            PeriodStart = key.PeriodStart,
            Kills = progress.Kills,
            AcceptedAt = progress.AcceptedAt,
        };
}
