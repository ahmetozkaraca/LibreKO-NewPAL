using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace LibreKO.Common.Infrastructure.Persistence;

public class RewardStateRepository(AppDbContext context) : IRewardStateRepository
{
    public async Task<IReadOnlyList<CharacterRewardQuest>> GetQuestProgressAsync(int characterId, DateOnly since) =>
        await context.CharacterRewardQuests.AsNoTracking()
            .Where(row => row.CharacterId == characterId
                && (row.PeriodStart >= since || row.PeriodStart == RewardQuestData.PermanentPeriod))
            .ToListAsync();

    public async Task<int> GetEventCoinsAsync(int characterId) =>
        await context.EventCoinWallets.AsNoTracking()
            .Where(wallet => wallet.CharacterId == characterId)
            .Select(wallet => wallet.Coins)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<RouletteSpin>> GetRecentSpinsAsync(int characterId, int count) =>
        await context.RouletteSpins.AsNoTracking()
            .Where(spin => spin.CharacterId == characterId)
            .OrderByDescending(spin => spin.SpunAt)
            .ThenByDescending(spin => spin.Id)
            .Take(count)
            .ToListAsync();

    public async Task<IReadOnlyList<PrizePool>> GetDailyClaimsAsync(int accountId, DateOnly day) =>
        await context.DailyRewardClaims.AsNoTracking()
            .Where(claim => claim.AccountId == accountId && claim.Day == day)
            .Select(claim => claim.Pool)
            .ToListAsync();

    public async Task SaveQuestProgressAsync(IReadOnlyCollection<CharacterRewardQuest> progress)
    {
        foreach (var row in progress)
        {
            var stored = await FindQuestAsync(row);
            if (stored == null)
            {
                context.CharacterRewardQuests.Add(row);
                continue;
            }

            if (stored.ClaimedAt != null)
                continue;

            stored.Kills = Math.Max(stored.Kills, row.Kills);
            stored.AcceptedAt ??= row.AcceptedAt;
        }

        await context.SaveChangesAsync();
    }

    public async Task<bool> StageQuestClaimAsync(CharacterRewardQuest claim, int eventCoins)
    {
        var stored = await FindQuestAsync(claim);
        if (stored == null)
        {
            context.CharacterRewardQuests.Add(claim);
        }
        else if (stored.ClaimedAt != null)
        {
            return false;
        }
        else
        {
            stored.Kills = Math.Max(stored.Kills, claim.Kills);
            stored.AcceptedAt ??= claim.AcceptedAt;
            stored.ClaimedAt = claim.ClaimedAt;
        }

        if (eventCoins > 0)
            await AddEventCoinsAsync(claim.CharacterId, eventCoins);

        return true;
    }

    public async Task<bool> StageRouletteSpinAsync(RouletteSpin spin, int cost)
    {
        var wallet = await context.EventCoinWallets.FindAsync(spin.CharacterId);
        if (wallet == null || wallet.Coins < cost)
            return false;

        wallet.Coins -= cost;
        context.RouletteSpins.Add(spin);
        return true;
    }

    public async Task<bool> StageDailyRewardClaimAsync(DailyRewardClaim claim)
    {
        var claimed = await context.DailyRewardClaims.AnyAsync(stored =>
            stored.AccountId == claim.AccountId && stored.Pool == claim.Pool && stored.Day == claim.Day);
        if (claimed)
            return false;

        context.DailyRewardClaims.Add(claim);
        return true;
    }

    private async Task AddEventCoinsAsync(int characterId, int coins)
    {
        var wallet = await context.EventCoinWallets.FindAsync(characterId);
        if (wallet == null)
        {
            context.EventCoinWallets.Add(new EventCoinWallet { CharacterId = characterId, Coins = coins });
            return;
        }

        wallet.Coins = (int)Math.Min((long)wallet.Coins + coins, int.MaxValue);
    }

    private ValueTask<CharacterRewardQuest?> FindQuestAsync(CharacterRewardQuest row) =>
        context.CharacterRewardQuests.FindAsync(row.CharacterId, row.QuestId, row.PeriodStart);
}
