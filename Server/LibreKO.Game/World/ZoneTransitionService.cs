using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using LibreKO.Game.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.World;

public enum ZoneEntryResult : byte
{
    Allowed,
    LevelTooLow,
    LevelTooHigh,
    NoNationalPoints,
    ZoneFull,
    NotQualified,
    Busy,
}

public interface IZoneTransitionService
{
    Task<bool> ChangeZoneAsync(UserSession session, byte newZone, float x, float z);
    Task<ZoneEntryResult> EnterZoneAsync(UserSession session, byte newZone, float x, float z, int fee);
    ZoneEntryResult CanEnterZone(UserSession session, byte zoneId);
    bool TakeArrivalSnapshot(UserSession session);
    Task CompleteArrivalAsync(UserSession session);
    Task CompleteOverdueArrivalsAsync();
    Task SendZoneAbilityAsync(UserSession session);
    Task RefreshArenaAsync(UserSession session);
}

public class ZoneTransitionService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    TimeWeatherBroadcastService timeWeather,
    ICollectionRaceService collectionRaceService,
    InstanceRoomRegistry instanceRooms,
    IWorldVisibilityService worldVisibilityService,
    IOptions<GameServerSettings> settings,
    TimeProvider time,
    ILogger<ZoneTransitionService> logger) : IZoneTransitionService
{
    public const int NoFee = 0;

    private const byte ZoneAbilityUpdate = 1;
    private const byte DefaultTariff = 10;
    private const short NoBindPoint = -1;

    private const float MoradonTownX = 816f;
    private const float MoradonTownZ = 532f;

    public async Task<bool> ChangeZoneAsync(UserSession session, byte newZone, float x, float z)
        => await TransferAsync(session, newZone, x, z, NoFee, voluntary: false) == ZoneEntryResult.Allowed;

    public async Task<ZoneEntryResult> EnterZoneAsync(UserSession session, byte newZone, float x, float z, int fee)
    {
        var verdict = CanEnterZone(session, newZone);
        return verdict == ZoneEntryResult.Allowed
            ? await TransferAsync(session, newZone, x, z, fee, voluntary: true)
            : verdict;
    }

    public ZoneEntryResult CanEnterZone(UserSession session, byte zoneId)
    {
        if (!IsKnownZone(zoneId))
            return ZoneEntryResult.NotQualified;

        if (session.IsGM)
            return ZoneEntryResult.Allowed;

        var entry = ZoneRules.EntryFor(zoneId);
        var access = entry.Access switch
        {
            ZoneAccess.Open => ZoneEntryResult.Allowed,
            ZoneAccess.Nation when entry.Owner == session.Nation => ZoneEntryResult.Allowed,
            ZoneAccess.Battlefield => BattlefieldEntry(session, zoneId),
            _ => ZoneEntryResult.NotQualified,
        };

        if (access != ZoneEntryResult.Allowed)
            return access;

        if (session.Level < entry.MinLevel)
            return ZoneEntryResult.LevelTooLow;

        if (session.Level > entry.MaxLevel)
            return ZoneEntryResult.LevelTooHigh;

        return entry.NeedsNationalPoints && session.Loyalty <= 0
            ? ZoneEntryResult.NoNationalPoints
            : ZoneEntryResult.Allowed;
    }

    public bool TakeArrivalSnapshot(UserSession session) => session.WithLock(s =>
    {
        var pending = s.Travel.ArrivalSnapshotPending;
        s.Travel.ArrivalSnapshotPending = false;
        return pending;
    });

    public async Task CompleteArrivalAsync(UserSession session)
    {
        var arrived = session.WithLock(s =>
        {
            var warping = s.IsWarping;
            s.IsWarping = false;
            return warping;
        });

        if (!arrived)
            return;

        await worldVisibilityService.BroadcastUserInOutAsync(session, InOutType.Warp);
        await collectionRaceService.SyncPlayerAsync(session);
    }

    public async Task CompleteOverdueArrivalsAsync()
    {
        var now = time.GetTimestamp();
        foreach (var session in sessionManager.GetAll())
        {
            if (session.IsWarping && !IsArrivalPending(session, now))
                await CompleteArrivalAsync(session);
        }
    }

    public Task RefreshArenaAsync(UserSession session)
        => ArenaZones.GetArenaId(session.ZoneId, session.X, session.Z) == session.ArenaId
            ? Task.CompletedTask
            : SendZoneAbilityAsync(session);

    public async Task SendZoneAbilityAsync(UserSession session)
    {
        session.ArenaId = ArenaZones.GetArenaId(session.ZoneId, session.X, session.Z);
        var rule = ZoneRules.For(session.ZoneId);
        var zoneType = GetZoneAbilityType(session.ZoneId, session.ArenaId);
        var kingData = gameDataService.KingSystemTable.TryGetValue((byte)session.Nation, out var data) ? data : null;
        var tariff = (ushort)(kingData?.TerritoryTariff ?? DefaultTariff);

        await session.Client.SendPacket(ZoneChangePacketWriter.ZoneAbility(
            ZoneAbilityUpdate,
            rule.Flags.HasFlag(ZoneFlags.TradeOtherNation),
            (byte)zoneType,
            rule.Flags.HasFlag(ZoneFlags.TalkOtherNation),
            tariff));
    }

    private async Task<ZoneEntryResult> TransferAsync(
        UserSession session, byte newZone, float x, float z, int fee, bool voluntary)
    {
        if (!IsKnownZone(newZone) || !ReferenceEquals(sessionManager.GetByCharacterId(session.CharacterId), session))
            return ZoneEntryResult.NotQualified;

        if (x == 0f && z == 0f)
            (x, z) = ResolveZeroCoords(newZone, session.Nation);

        var y = ResolveTargetHeight(newZone, x, z);
        var now = time.GetTimestamp();
        List<UserSession> watchers = [];

        var outcome = session.WithLock(s =>
        {
            if (voluntary && (s.Hp <= 0 || s.Trade.LocksInventory || IsArrivalPending(s, now) || !RegionManager.IsInWorld(s)))
                return ZoneEntryResult.Busy;

            if (s.Money < fee)
                return ZoneEntryResult.NotQualified;

            s.Money -= fee;
            watchers = [.. sessionManager.Regions.GetUsersAroundRegistration(s).Where(viewer => StealthSight.CanSee(viewer, s))];
            sessionManager.Regions.RemoveFromRegion(s);
            if (s.Room != 0 && !instanceRooms.Holds(s.Room, newZone))
                instanceRooms.Leave(s);

            s.ZoneId = newZone;
            s.X = x;
            s.Z = z;
            s.Y = y;
            s.Quest.BindPoint = NoBindPoint;
            s.MovePending = false;
            s.IsWarping = true;
            s.Travel.WarpStartedAt = now;
            s.Travel.ArrivalSnapshotPending = true;
            s.Travel.Offer = null;

            sessionManager.Regions.AddToRegion(s);
            return ZoneEntryResult.Allowed;
        });

        if (outcome != ZoneEntryResult.Allowed)
            return outcome;

        var outPacket = VisibilityPacketWriter.UserOut(session.CharacterId);
        foreach (var watcher in watchers)
            await watcher.Client.SendPacket(outPacket);

        sessionManager.Regions.DropAggroOn(session.CharacterId);

        logger.LogDebug("ZoneChange for {Name}: zone={Zone} X={X} Z={Z} Y={Y} PosX={PosX} PosZ={PosZ} PosY={PosY}",
            session.Name, newZone, x, z, y, session.GetPosX, session.GetPosZ, session.GetPosY);

        await session.Client.SendPacket(ZoneChangePacketWriter.Teleport(
            (short)newZone,
            (ushort)session.GetPosX, (ushort)session.GetPosZ, (ushort)session.GetPosY,
            (byte)session.Nation));

        await SendZoneAbilityAsync(session);
        await session.Client.SendPacket(timeWeather.BuildWeatherPacketFor(session.ZoneId));
        await collectionRaceService.SyncPlayerAsync(session);
        return ZoneEntryResult.Allowed;
    }

    private ZoneEntryResult BattlefieldEntry(UserSession session, byte zoneId)
    {
        var battle = sessionManager.Battle;
        if (!battle.IsBattleActive || battle.BattleZone != zoneId)
            return ZoneEntryResult.NotQualified;

        var compatriots = sessionManager.GetAll()
            .Count(user => user.ZoneId == zoneId && user.Nation == session.Nation);
        return compatriots >= BattleZoneManager.MAX_BATTLE_ZONE_USERS
            ? ZoneEntryResult.ZoneFull
            : ZoneEntryResult.Allowed;
    }

    private bool IsArrivalPending(UserSession session, long now)
        => session.IsWarping
            && time.GetElapsedTime(session.Travel.WarpStartedAt, now).TotalSeconds
                < settings.Value.AntiCheat.Travel.ZoneChangeTimeoutSeconds;

    private bool IsKnownZone(byte zoneId)
        => gameDataService.ZoneInfoTable is not { Count: > 0 } zones || zones.ContainsKey(zoneId);

    private float ResolveTargetHeight(short zoneId, float x, float z)
        => Math.Max(0f, sessionManager.Maps?.GetGroundHeight(zoneId, x, z) ?? 0f);

    private (float X, float Z) ResolveZeroCoords(byte zoneId, AccountNation nation)
    {
        var startPosition = gameDataService.GetStartPosition(zoneId);
        if (startPosition != null)
        {
            if (startPosition.BaseX(nation) != 0 || startPosition.BaseZ(nation) != 0)
            {
                var (spawnX, spawnZ) = startPosition.RandomSpawn(nation);
                logger.LogDebug("ResolveZeroCoords: zone {Zone} → start_position ({X}, {Z})",
                    zoneId, spawnX, spawnZ);
                return (spawnX, spawnZ);
            }
        }

        if (gameDataService.ZoneInfoTable.TryGetValue(zoneId, out var zone)
            && (zone.InitX != 0 || zone.InitZ != 0))
        {
            return (zone.InitX, zone.InitZ);
        }

        logger.LogWarning("ResolveZeroCoords: no coords for zone {Zone} — using Moradon fallback", zoneId);
        return (MoradonTownX, MoradonTownZ);
    }

    public static ZoneAbilityType GetZoneAbilityType(byte zoneId, byte arenaId)
        => arenaId != ArenaZones.NoArena
            ? ZoneAbilityType.FreeForAll
            : ZoneRules.For(zoneId).Ability;
}
