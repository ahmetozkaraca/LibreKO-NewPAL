using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Enums;

namespace LibreKO.Common.Domain.Services;

public interface IRewardStateRepository
{
    Task<IReadOnlyList<CharacterRewardQuest>> GetQuestProgressAsync(int characterId, DateOnly since);
    Task<int> GetEventCoinsAsync(int characterId);
    Task<IReadOnlyList<RouletteSpin>> GetRecentSpinsAsync(int characterId, int count);
    Task<IReadOnlyList<PrizePool>> GetDailyClaimsAsync(int accountId, DateOnly day);
    Task SaveQuestProgressAsync(IReadOnlyCollection<CharacterRewardQuest> progress);
    Task<bool> StageQuestClaimAsync(CharacterRewardQuest claim, int eventCoins);
    Task<bool> StageRouletteSpinAsync(RouletteSpin spin, int cost);
    Task<bool> StageDailyRewardClaimAsync(DailyRewardClaim claim);
}
