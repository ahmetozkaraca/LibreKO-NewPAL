using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IKnightsRuntimeService
{
    bool IsClanLeader(UserSession session);
    bool CanAdmitCandidates(UserSession session);
    void ClearClanState(UserSession session);
    bool TryLeaveClan(UserSession member, short clanId);
    Task<bool> CanPromoteToViceChiefAsync(IKnightsRepository repo, short knightsId, string targetName);
    Task SyncCharacterAsync(IKnightsRepository repo, UserSession session, bool includeMoney = false, bool includeLoyalty = false);
    Task NotifyOnlineClanMembersAsync(short clanId, Packet packet);
}

public class KnightsRuntimeService(SessionManager sessionManager) : IKnightsRuntimeService
{
    private const int MaxViceChiefs = 3;

    public bool IsClanLeader(UserSession session)
    {
        return session.KnightsId > 0 && session.KnightsFame == KnightsManager.ChiefFame;
    }

    public bool CanAdmitCandidates(UserSession session)
    {
        return session.KnightsId > 0 && session.KnightsFame is > 0 and <= 3;
    }

    public void ClearClanState(UserSession session)
    {
        // Clan rank is persisted through the shared fame field, so clear both when leaving the clan.
        session.KnightsId = 0;
        session.KnightsFame = 0;
        session.KnightsName = string.Empty;
        session.Fame = 0;
    }

    public bool TryLeaveClan(UserSession member, short clanId)
        => member.WithLock(leaver =>
        {
            if (clanId <= 0 || leaver.KnightsId != clanId)
                return false;

            ClearClanState(leaver);
            return true;
        });

    public async Task<bool> CanPromoteToViceChiefAsync(
        IKnightsRepository repo, short knightsId, string targetName)
    {
        var members = await repo.GetCharactersByClanAsync(knightsId);
        if (members.Any(m => m.Fame == KnightsManager.ViceChiefFame
                             && m.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
            return true;

        return members.Count(m => m.Fame == KnightsManager.ViceChiefFame) < MaxViceChiefs;
    }

    public async Task SyncCharacterAsync(IKnightsRepository repo, UserSession session, bool includeMoney = false, bool includeLoyalty = false)
    {
        var fame = session.KnightsId > 0 ? session.KnightsFame : session.Fame;
        await repo.SyncCharacterClanStateAsync(
            session.CharacterId,
            session.KnightsId,
            fame,
            includeMoney ? session.Money : null,
            includeLoyalty ? session.Loyalty : null);
    }

    public async Task NotifyOnlineClanMembersAsync(short clanId, Packet packet)
    {
        foreach (var member in sessionManager.GetAll())
        {
            if (member.KnightsId != clanId)
                continue;

            try
            {
                await member.Client.SendPacket(packet);
            }
            catch
            {
                // Ignore per-member delivery failures.
            }
        }
    }

}
