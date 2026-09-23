using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;

namespace LibreKO.Game.Protocol;

public interface IStealthService
{
    Task HideAsync(UserSession session, InvisibilityType invisibility);
    Task RevealAsync(UserSession session, InvisibilityType dispelledBy);
    Task GrantSightAsync(UserSession session, short radius);
    Task ClearSightAsync(UserSession session);
    Task EndAsync(UserSession session, MagicStealthType stealthType);
    bool CanSee(UserSession viewer, UserSession target);
}

public class StealthService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IWorldVisibilityService worldVisibilityService) : IStealthService
{
    private const short NoSightRadius = 0;

    public async Task HideAsync(UserSession session, InvisibilityType invisibility)
    {
        session.Invisibility = invisibility;
        await BroadcastVisibilityAsync(session);
        await worldVisibilityService.HideFromAsync(session, UnawareOf(session));
    }

    public async Task RevealAsync(UserSession session, InvisibilityType dispelledBy)
    {
        if (session.Invisibility == InvisibilityType.None)
            return;

        if (dispelledBy != InvisibilityType.None && session.Invisibility != dispelledBy)
            return;

        foreach (var skillId in ActiveStealthSkills(session))
            session.ActiveBuffs.TryRemove(skillId, out _);

        session.Invisibility = InvisibilityType.None;
        await AnnounceVisibleAsync(session);
        await session.Client.SendPacket(
            MagicProcessPacketWriter.CreateDurationExpired(DurationExpiredCode.Stealth));
    }

    public async Task GrantSightAsync(UserSession session, short radius)
    {
        var revealed = sessionManager.Regions.GetNearbyUsers(session)
            .Where(user => user.IsInvisible && !StealthSight.Detects(session, user))
            .ToList();

        session.SightRadius = radius;
        await session.Client.SendPacket(StealthPacketWriter.Sight(radius));

        foreach (var user in revealed)
            await worldVisibilityService.ShowToAsync(user, [session]);
    }

    public async Task ClearSightAsync(UserSession session)
    {
        var obscured = session.SightRadius == NoSightRadius
            ? []
            : sessionManager.Regions.GetNearbyUsers(session)
                .Where(user => user.IsInvisible && !StealthSight.DetectsUnaided(session, user))
                .ToList();

        session.SightRadius = NoSightRadius;
        await session.Client.SendPacket(StealthPacketWriter.NoSight());

        foreach (var user in obscured)
            await worldVisibilityService.HideFromAsync(user, [session]);
    }

    public async Task EndAsync(UserSession session, MagicStealthType stealthType)
    {
        switch (stealthType)
        {
            case MagicStealthType.DispelOnMove:
            case MagicStealthType.DispelOnAttack:
                if (session.IsInvisible)
                    await BroadcastVisibilityAsync(session);
                else
                    await AnnounceVisibleAsync(session);
                await session.Client.SendPacket(
                    MagicProcessPacketWriter.CreateDurationExpired(DurationExpiredCode.Stealth));
                break;

            case MagicStealthType.SeeInvisible:
            case MagicStealthType.SeeInvisibleParty:
                await ClearSightAsync(session);
                await session.Client.SendPacket(
                    MagicProcessPacketWriter.CreateDurationExpired(DurationExpiredCode.Sight));
                break;
        }
    }

    private async Task AnnounceVisibleAsync(UserSession session)
    {
        await worldVisibilityService.ShowToAsync(session, UnawareOf(session));
        await BroadcastVisibilityAsync(session);
    }

    private List<UserSession> UnawareOf(UserSession session) =>
        sessionManager.Regions.GetNearbyUsers(session)
            .Where(viewer => !StealthSight.Detects(viewer, session))
            .ToList();

    public bool CanSee(UserSession viewer, UserSession target) => StealthSight.CanTarget(viewer, target);

    private Task BroadcastVisibilityAsync(UserSession session) =>
        sessionManager.Regions.SendToRegion(
            session,
            MovementPacketWriter.StateChange(
                session.CharacterId, (byte)StateChangeType.Stealth, (byte)session.Invisibility),
            excludeSender: false);

    private List<int> ActiveStealthSkills(UserSession session) =>
        session.ActiveBuffs.Keys
            .Where(skillId => StealthTypeOf(skillId)
                is MagicStealthType.DispelOnMove or MagicStealthType.DispelOnAttack)
            .ToList();

    private MagicStealthType StealthTypeOf(int skillId)
    {
        var magic = gameDataService.GetMagic(skillId);
        if (magic?.PrimaryType != MagicSkillType.Stealth
            || !MagicTypeLookup.TryResolve(gameDataService.MagicType9Table, magic, skillId, out var type9Data))
            return MagicStealthType.None;

        return (MagicStealthType)type9Data.StateChange;
    }
}
