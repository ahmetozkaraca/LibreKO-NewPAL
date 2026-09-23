using System.Linq.Expressions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.Protocol;

public interface IKingSystemRuntimeService
{
    KingSystemData? GetKingData(AccountNation nation);
    bool IsKing(UserSession session, KingSystemData? kingData);
    Task<bool> DismissUnknownKingAsync(KingSystemData kingData);
    Task PersistKingPropertyAsync<TProperty>(
        KingSystemData kingData,
        Expression<Func<KingSystemData, TProperty>> propertySelector);
    Task BroadcastToNationAsync(AccountNation nation, Packet packet);
}

public class KingSystemRuntimeService(
    SessionManager sessionManager,
    IServiceScopeFactory scopeFactory,
    IGameDataService gameDataService,
    ILogger<KingSystemRuntimeService> logger) : IKingSystemRuntimeService
{
    public KingSystemData? GetKingData(AccountNation nation)
    {
        return gameDataService.KingSystemTable.TryGetValue((byte)nation, out var kingData) ? kingData : null;
    }

    public bool IsKing(UserSession session, KingSystemData? kingData)
    {
        var kingName = kingData?.KingName?.Trim();
        return kingData != null
            && kingData.Nation == (byte)session.Nation
            && !string.IsNullOrEmpty(kingName)
            && string.Equals(kingName, session.Name, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DismissUnknownKingAsync(KingSystemData kingData)
    {
        var kingName = kingData.KingName?.Trim();
        if (string.IsNullOrEmpty(kingName))
            return false;

        using (var scope = scopeFactory.CreateScope())
        {
            var characters = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();
            if (await characters.ExistsInNationAsync(kingName, (AccountNation)kingData.Nation))
                return false;
        }

        logger.LogWarning(
            "Nation {Nation} names {KingName} as its king, but no character of that nation carries the name; the throne is left empty",
            kingData.Nation, kingName);
        kingData.KingName = string.Empty;
        await PersistKingPropertyAsync(kingData, entry => entry.KingName);
        return true;
    }

    public async Task PersistKingPropertyAsync<TProperty>(
        KingSystemData kingData,
        Expression<Func<KingSystemData, TProperty>> propertySelector)
    {
        logger.LogInformation("King property persisted for nation {Nation}", kingData.Nation);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Attach(kingData);
        db.Entry(kingData).Property(propertySelector).IsModified = true;
        await db.SaveChangesAsync();
    }

    public async Task BroadcastToNationAsync(AccountNation nation, Packet packet)
    {
        foreach (var session in sessionManager.GetAll())
        {
            if (session.Nation != nation)
                continue;

            try
            {
                await session.Client.SendPacket(packet);
            }
            catch
            {
                // Ignore broadcast failures per recipient.
            }
        }
    }
}
