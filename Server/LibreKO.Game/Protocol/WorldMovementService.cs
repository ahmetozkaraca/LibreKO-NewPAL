using System.Linq;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public readonly record struct WarpListEntry(
    short WarpId,
    string Name,
    string Announce,
    short ZoneId,
    short MaxUsers,
    int Fee);

public interface IWorldMovementService
{
    Task HandleMoveAsync(IClient client, Packet packet);
    Task HandleRotateAsync(IClient client, Packet packet);
    Task HandleStateChangeAsync(IClient client, Packet packet);
    Task HandleHomeAsync(IClient client);
    Task WarpAsync(UserSession session, ushort posX, ushort posZ);
    Task HandleRecvWarpAsync(IClient client, Packet packet);
    Task HandleWarpListAsync(IClient client, Packet packet);
    Task HandleZoneChangeAsync(IClient client, Packet packet);
    Task HandleStealthAsync(IClient client, Packet packet);
    Task OfferWarpListAsync(UserSession session, WarpSource source, IReadOnlyCollection<WarpListEntry> warps);
    Task RefreshRegionAsync(UserSession session);
}

public class WorldMovementService(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IZoneTransitionService zoneTransitionService,
    IUserNotificationService userNotificationService,
    ICombatNotificationService combatNotificationService,
    ICombatLifecycleService combatLifecycleService,
    IWorldVisibilityService worldVisibilityService,
    IMiningPacketCoordinator miningPacketCoordinator,
    IStealthService stealthService,
    IMovementValidator movementValidator,
    IViolationMonitor violations,
    IOptions<GameServerSettings> settings,
    TimeProvider time,
    ILogger<WorldMovementService> logger) : IWorldMovementService
{
    private const byte MoveEchoFinish = 0;
    private const byte MoveEchoStart = 1;
    private const byte MoveEchoMove = 3;

    private const int MoveBodySize = 9;
    private const int MoveOriginSize = 6;
    private const short MaxMoveSpeed = 90;

    private const float MoveSpeedScale = 100f;
    private const float PositionScale = 10f;

    private const byte ZoneChangeEvent = 1;
    private const byte DamageEvent = 3;
    private const short DamageZoneHp = 10;

    public const float ZoneGateRetrySeconds = 3f;
    private const string ZoneGateLevelTooLowNotice = "You are not experienced enough to pass this way.";
    private const string ZoneGateLevelTooHighNotice = "Your level is too high to pass this way.";
    private const string ZoneGateNoNationalPointsNotice = "You need national points to pass this way.";
    private const string ZoneGateFullNotice = "The land beyond is full.";
    private const string ZoneGateClosedNotice = "This way is closed to you.";
    private const string TownRecallRefusedNotice = "You cannot return to town right now.";

    private const int StateChangeBodySize = 2;
    private const byte StealthCancelRequest = 0;
    private const int RecallHealthDivisor = 2;

    public async Task HandleMoveAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.IsWarping || session.Hp <= 0 || packet.RemainingBytes < MoveBodySize)
            return;

        var willX = packet.ReadUShort();
        var willZ = packet.ReadUShort();
        var willY = packet.ReadUShort();
        var speed = packet.ReadShort();
        var echo = packet.ReadByte();

        ushort curX = willX, curZ = willZ;
        if (packet.RemainingBytes >= MoveOriginSize)
        {
            curX = packet.ReadUShort();
            curZ = packet.ReadUShort();
            _ = packet.ReadUShort();
        }

        if (echo is not MoveEchoFinish and not MoveEchoStart and not MoveEchoMove)
            return;

        if (speed is > MaxMoveSpeed or < -MaxMoveSpeed)
        {
            violations.Report(session, ViolationKind.InvalidRequest, $"sent a move at wire speed {speed}");
            return;
        }

        if (speed != 0 && echo != MoveEchoFinish)
            (willX, willZ) = LeadDestination(willX, willZ, curX, curZ, speed);

        var zoneId = session.ZoneId;
        var newX = willX / PositionScale;
        var newZ = willZ / PositionScale;
        if (sessionManager.Maps != null && !sessionManager.Maps.IsValidPosition(zoneId, newX, newZ))
            return;

        var step = new MoveStep(zoneId, newX, SettleHeight(zoneId, newX, newZ, willY / PositionScale), newZ,
            willX, willZ, speed, echo);
        var move = session.WithLock(s => CommitMove(s, step));
        switch (move.Verdict)
        {
            case MoveVerdict.Ignore:
                return;
            case MoveVerdict.Reject:
                await client.SendPacket(MovementPacketWriter.Warp(ToWire(move.X), ToWire(move.Z)));
                return;
        }

        if (move.Displaced)
            await stealthService.RevealAsync(session, InvisibilityType.DispelOnMove);

        if (move.StoodUp)
            await sessionManager.Regions.SendToRegion(
                session,
                MovementPacketWriter.StateChange(
                    session.CharacterId, (byte)StateChangeType.Pose, (byte)UserPoseState.Standing),
                excludeSender: false);

        session.MovePending = true;

        await zoneTransitionService.RefreshArenaAsync(session);

        if (session.IsGathering)
            await miningPacketCoordinator.StopGatheringAsync(session);

        await RefreshRegionAsync(session);
        await RunTileEventAsync(session, zoneId, newX, newZ);
    }

    private MoveOutcome CommitMove(UserSession session, MoveStep step)
    {
        if (session.IsWarping || session.Hp <= 0 || session.ZoneId != step.ZoneId)
            return MoveOutcome.Ignored;

        var verdict = movementValidator.Check(session, step.X, step.Z);
        if (verdict != MoveVerdict.Accept)
            return new MoveOutcome(verdict, session.X, session.Z, Displaced: false, StoodUp: false);

        var displaced = step.WillX != session.MoveOldWillX || step.WillZ != session.MoveOldWillZ;
        var stoodUp = session.IsSitting;

        session.X = step.X;
        session.Y = step.Y;
        session.Z = step.Z;
        session.IsSitting = false;
        session.MoveOldEcho = step.Echo;
        session.MoveOldSpeed = step.Speed;
        session.MoveOldWillX = step.WillX;
        session.MoveOldWillY = ToWire(step.Y);
        session.MoveOldWillZ = step.WillZ;

        return new MoveOutcome(MoveVerdict.Accept, step.X, step.Z, displaced, stoodUp);
    }

    private float SettleHeight(byte zoneId, float x, float z, float claimedY)
    {
        if (sessionManager.Maps?.GetGroundHeight(zoneId, x, z) is not { } ground)
            return claimedY;

        var limits = settings.Value.AntiCheat.Travel;
        var settled = Math.Clamp(claimedY, ground - limits.MaxDepthBelowGround, ground + limits.MaxHeightAboveGround);
        return Math.Max(0f, settled);
    }

    private async Task RunTileEventAsync(UserSession session, byte zoneId, float x, float z)
    {
        if (session.ZoneId != zoneId)
            return;

        var gameEvent = sessionManager.Maps?.CheckEvent(zoneId, x, z);
        if (gameEvent == null)
            return;

        switch (gameEvent.Type)
        {
            case ZoneChangeEvent:
                await PassZoneGateAsync(session, (byte)gameEvent.Exec1, gameEvent.Exec2, gameEvent.Exec3);
                break;

            case DamageEvent:
                var outcome = session.ApplyDamage(GmMode.Taken(session, DamageZoneHp));
                if (outcome == DamageOutcome.None)
                    break;

                await combatNotificationService.SendHpChangeAsync(session);
                if (outcome.Killed)
                    await combatLifecycleService.HandlePlayerDeathAsync(session, killer: null);
                break;
        }
    }

    private async Task PassZoneGateAsync(UserSession session, byte zoneId, float x, float z)
    {
        var now = time.GetTimestamp();
        if (now < session.Travel.ZoneGateRetryAt)
            return;

        var result = await zoneTransitionService.EnterZoneAsync(session, zoneId, x, z, ZoneTransitionService.NoFee);
        if (result is ZoneEntryResult.Allowed or ZoneEntryResult.Busy)
            return;

        session.Travel.ZoneGateRetryAt = now + Ticks(ZoneGateRetrySeconds);
        await session.Client.SendPacket(ChatPacketWriter.SystemNotice((byte)session.Nation, result switch
        {
            ZoneEntryResult.LevelTooLow => ZoneGateLevelTooLowNotice,
            ZoneEntryResult.LevelTooHigh => ZoneGateLevelTooHighNotice,
            ZoneEntryResult.NoNationalPoints => ZoneGateNoNationalPointsNotice,
            ZoneEntryResult.ZoneFull => ZoneGateFullNotice,
            _ => ZoneGateClosedNotice,
        }));
    }

    public async Task RefreshRegionAsync(UserSession session)
    {
        var now = time.GetTimestamp();
        var cooldown = Ticks(settings.Value.AntiCheat.Travel.RegionChangeCooldownSeconds);
        var previous = session.WithLock(s =>
        {
            if (!RegionManager.IsInWorld(s) || now < s.Travel.RegionChangeAllowedAt)
                return ((int X, int Z)?)null;

            var origin = (s.RegionX, s.RegionZ);
            if (!sessionManager.Regions.UpdateRegion(s))
                return null;

            s.Travel.RegionChangeAllowedAt = now + cooldown;
            return origin;
        });

        if (previous is not { } origin)
            return;

        await worldVisibilityService.BroadcastRegionTransitionAsync(session, origin.X, origin.Z);
        await worldVisibilityService.SendRegionUserListAsync(session);
        await worldVisibilityService.SendNpcRegionListAsync(session);
    }

    private static (ushort X, ushort Z) LeadDestination(
        ushort willX, ushort willZ, ushort curX, ushort curZ, short speed)
    {
        var stepX = (willX - curX) / PositionScale;
        var stepZ = (willZ - curZ) / PositionScale;
        var length = MathF.Sqrt(stepX * stepX + stepZ * stepZ);
        if (length <= 0f)
            return (willX, willZ);

        var lead = speed / MoveSpeedScale;
        var leadX = willX + stepX / length * lead * PositionScale;
        var leadZ = willZ + stepZ / length * lead * PositionScale;

        return ((ushort)Math.Clamp(leadX, 0f, ushort.MaxValue),
                (ushort)Math.Clamp(leadZ, 0f, ushort.MaxValue));
    }

    public async Task HandleRotateAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 2)
            return;

        session.Direction = packet.ReadShort();

        var result = MovementPacketWriter.Rotate(session.CharacterId, session.Direction);
        await sessionManager.Regions.SendToRegion(session, result);
    }

    public async Task HandleStateChangeAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < StateChangeBodySize)
            return;

        var type = packet.ReadByte();
        var value = packet.RemainingBytes >= sizeof(int) ? packet.ReadInt() : packet.ReadByte();

        switch (ClassifyStateChange(type, value))
        {
            case StateChangeOrigin.ServerOwned:
                violations.Report(session, ViolationKind.InvalidRequest, $"sent server-owned state change {type} with value {value}");
                return;
            case StateChangeOrigin.Unsupported:
                return;
        }

        if (session.Hp <= 0)
            return;

        if (type == (byte)StateChangeType.Pose)
            session.IsSitting = value == (byte)UserPoseState.Sitting;
        else
            session.InCombatStance = value == (byte)CombatStanceState.Ready;

        var result = MovementPacketWriter.StateChange(session.CharacterId, type, value);
        await sessionManager.Regions.SendToRegion(session, result, excludeSender: false);
    }

    private enum StateChangeOrigin : byte
    {
        Player,
        ServerOwned,
        Unsupported,
    }

    private static StateChangeOrigin ClassifyStateChange(byte type, int value) => (StateChangeType)type switch
    {
        StateChangeType.Pose => value is (byte)UserPoseState.Standing or (byte)UserPoseState.Sitting
            ? StateChangeOrigin.Player
            : StateChangeOrigin.Unsupported,
        StateChangeType.CombatStance => value is (byte)CombatStanceState.Relaxed or (byte)CombatStanceState.Ready
            ? StateChangeOrigin.Player
            : StateChangeOrigin.Unsupported,
        StateChangeType.Abnormal or StateChangeType.Visibility or StateChangeType.Stealth
            or StateChangeType.Transformation => StateChangeOrigin.ServerOwned,
        _ => StateChangeOrigin.Unsupported,
    };

    public async Task HandleHomeAsync(IClient client)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var startPos = gameDataService.GetStartPosition(session.ZoneId);
        if (startPos == null || !TryBeginTownRecall(session))
        {
            await client.SendPacket(ChatPacketWriter.SystemNotice((byte)session.Nation, TownRecallRefusedNotice));
            return;
        }

        var (x, z) = startPos.RandomSpawn(session.Nation);

        await WarpAsync(session, ToWire(x), ToWire(z));
    }

    private bool TryBeginTownRecall(UserSession session)
    {
        var now = time.GetTimestamp();
        var cooldown = Ticks(settings.Value.AntiCheat.Travel.TownRecallCooldownSeconds);
        return session.WithLock(s =>
        {
            if (s.Hp <= 0 || s.Hp < s.MaxHp / RecallHealthDivisor || s.IsWarping || !s.CanTeleport
                || now < s.Travel.TownRecallAllowedAt)
                return false;

            s.Travel.TownRecallAllowedAt = now + cooldown;
            return true;
        });
    }

    public async Task WarpAsync(UserSession session, ushort posX, ushort posZ)
    {
        var realX = posX / PositionScale;
        var realZ = posZ / PositionScale;

        await session.Client.SendPacket(MovementPacketWriter.Warp(posX, posZ));

        await worldVisibilityService.BroadcastUserInOutAsync(session, InOutType.Out);
        sessionManager.Regions.DropAggroOn(session.CharacterId);

        var inWorld = session.WithLock(s =>
        {
            s.X = realX;
            s.Y = ResolveTargetHeight(s.ZoneId, realX, realZ);
            s.Z = realZ;
            if (!RegionManager.IsInWorld(s))
                return false;

            sessionManager.Regions.UpdateRegion(s);
            return true;
        });

        if (!inWorld)
            return;

        await zoneTransitionService.RefreshArenaAsync(session);

        await worldVisibilityService.BroadcastUserInOutAsync(session, InOutType.Warp);
        await worldVisibilityService.SendNpcRegionListAsync(session);
        await worldVisibilityService.SendRegionUserListAsync(session);
    }

    public async Task HandleRecvWarpAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || !session.IsGM || packet.RemainingBytes < 4)
            return;

        await WarpAsync(session, packet.ReadUShort(), packet.ReadUShort());
    }

    private static ushort ToWire(float coordinate)
        => (ushort)Math.Clamp(coordinate * PositionScale, 0f, ushort.MaxValue);

    public async Task HandleWarpListAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < sizeof(short))
            return;

        var sourceId = packet.ReadShort();

        if (packet.RemainingBytes < sizeof(short))
        {
            await OfferKeeperWarpsAsync(session, sourceId);
            return;
        }

        await SelectWarpAsync(session, packet.ReadShort());
    }

    public async Task HandleZoneChangeAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < sizeof(byte))
            return;

        switch ((ZoneChangeSubOpcode)packet.ReadByte())
        {
            case ZoneChangeSubOpcode.Loading:
                if (!zoneTransitionService.TakeArrivalSnapshot(session))
                    return;

                await worldVisibilityService.SendNearbyUsersToClientAsync(session);
                await client.SendPacket(ZoneChangePacketWriter.Ready());
                break;

            case ZoneChangeSubOpcode.Ready:
                await zoneTransitionService.CompleteArrivalAsync(session);
                break;
        }
    }

    public async Task HandleStealthAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < sizeof(byte))
            return;

        if (packet.ReadByte() != StealthCancelRequest)
            return;

        await stealthService.RevealAsync(session, InvisibilityType.None);
    }

    public async Task OfferWarpListAsync(
        UserSession session, WarpSource source, IReadOnlyCollection<WarpListEntry> warps)
    {
        var expiresAt = time.GetTimestamp() + Ticks(settings.Value.AntiCheat.Travel.WarpOfferSeconds);
        var offer = new WarpOffer(source, warps.Select(warp => warp.WarpId).ToHashSet(), expiresAt);
        session.WithLock(s => s.Travel.Offer = offer);

        var entries = warps
            .Select(warp => new WarpListPacketWriter.Entry(
                warp.WarpId, warp.Name, warp.Announce, warp.ZoneId, warp.MaxUsers,
                (uint)Math.Max(0, warp.Fee)))
            .ToList();

        await session.Client.SendPacket(WarpListPacketWriter.Menu(entries));
    }

    private async Task OfferKeeperWarpsAsync(UserSession session, short npcId)
    {
        var npc = gameDataService.GetNpc(npcId, isMonster: false);
        if (npc == null || !npc.IsNpc)
            return;

        var keeper = sessionManager.Regions.GetNpc(session.Quest.EventNpcUniqueId);
        if (keeper == null || keeper.NpcId != npcId || !Reach.CanInteract(session, keeper))
            return;

        var warps = sessionManager.Maps?.GetWarpList(session.ZoneId, npc.Group) ?? [];
        if (warps.Count == 0)
            return;

        await OfferWarpListAsync(
            session,
            new KeeperWarpSource(keeper),
            [.. warps.Select(warp => new WarpListEntry(
                warp.WarpId,
                warp.Name,
                string.Empty,
                warp.Zone,
                WarpListPacketWriter.NoUserLimit,
                (int)warp.Fee))]);
    }

    private async Task SelectWarpAsync(UserSession session, short warpId)
    {
        var warp = sessionManager.Maps?.GetWarp(session.ZoneId, warpId);
        if (warp == null || !HoldsWarpOffer(session, warpId)
            || (warp.Nation != (short)EntityNation.All && warp.Nation != (short)session.Nation))
        {
            await RefuseWarpAsync(session, ZoneEntryResult.NotQualified);
            return;
        }

        var fee = (int)Math.Min(warp.Fee, int.MaxValue);
        var destinationZoneId = ResolveWarpDestinationZone(session.ZoneId, warp.Zone);
        var (targetX, targetZ) = ResolveWarpArrival(session.ZoneId, destinationZoneId, warp);

        var result = destinationZoneId == session.ZoneId
            ? await WarpWithinZoneAsync(session, targetX, targetZ, fee)
            : await zoneTransitionService.EnterZoneAsync(session, (byte)destinationZoneId, targetX, targetZ, fee);

        if (result != ZoneEntryResult.Allowed)
        {
            await RefuseWarpAsync(session, result);
            return;
        }

        logger.LogDebug("Warp {Name} to zone {Zone} via warp '{WarpName}': X={X} Z={Z}",
            session.Name, destinationZoneId, warp.Name, targetX, targetZ);

        session.WithLock(s => s.Travel.Offer = null);
        if (fee > ZoneTransitionService.NoFee)
            await userNotificationService.SendGoldLossAsync(session, fee);
    }

    private bool HoldsWarpOffer(UserSession session, short warpId)
    {
        var offer = session.Travel.Offer;
        return offer != null
            && time.GetTimestamp() < offer.ExpiresAt
            && offer.WarpIds.Contains(warpId)
            && session.Hp > 0
            && !session.IsWarping
            && offer.Source.IsWithinReach(session);
    }

    private async Task<ZoneEntryResult> WarpWithinZoneAsync(UserSession session, float x, float z, int fee)
    {
        var charged = session.WithLock(s =>
        {
            if (s.Hp <= 0 || s.IsWarping || s.Trade.LocksInventory || !RegionManager.IsInWorld(s))
                return ZoneEntryResult.Busy;

            if (s.Money < fee)
                return ZoneEntryResult.NotQualified;

            s.Money -= fee;
            return ZoneEntryResult.Allowed;
        });

        if (charged != ZoneEntryResult.Allowed)
            return charged;

        await session.Client.SendPacket(WarpListPacketWriter.Arrived());
        await WarpAsync(session, ToWire(x), ToWire(z));
        return ZoneEntryResult.Allowed;
    }

    private static Task RefuseWarpAsync(UserSession session, ZoneEntryResult result)
        => session.Client.SendPacket(WarpListPacketWriter.Result(result switch
        {
            ZoneEntryResult.LevelTooLow => WarpListPacketWriter.ResultLevelTooLow,
            ZoneEntryResult.LevelTooHigh => WarpListPacketWriter.ResultLevelRangeOnly,
            ZoneEntryResult.NoNationalPoints => WarpListPacketWriter.ResultNoNationalPoints,
            ZoneEntryResult.ZoneFull => WarpListPacketWriter.ResultServerFull,
            _ => WarpListPacketWriter.ResultNotQualified,
        }));

    private static (float X, float Z) ApplyWarpRadius(float x, float z, float radius)
    {
        if (radius <= 0)
            return (x, z);

        var offsetX = Random.Shared.NextSingle() * radius * 2;
        if (offsetX < radius)
            offsetX = -offsetX;

        var offsetZ = Random.Shared.NextSingle() * radius * 2;
        if (offsetZ < radius)
            offsetZ = -offsetZ;

        return (x + offsetX, z + offsetZ);
    }

    private short ResolveWarpDestinationZone(short currentZoneId, short targetZoneId)
    {
        if (targetZoneId == currentZoneId || !SharesMapFile(currentZoneId, targetZoneId))
            return targetZoneId;

        if (!gameDataService.ZoneInfoTable.TryGetValue(currentZoneId, out var currentZone)
            || !gameDataService.ZoneInfoTable.TryGetValue(targetZoneId, out var targetZone))
            return targetZoneId;

        if (!string.Equals(
                NormalizeMapFamily(currentZone.MapName),
                NormalizeMapFamily(targetZone.MapName),
                StringComparison.OrdinalIgnoreCase))
            return targetZoneId;

        return currentZoneId;
    }

    private bool SharesMapFile(short zoneId, short otherZoneId)
    {
        if (zoneId == otherZoneId)
            return true;

        if (!gameDataService.ZoneInfoTable.TryGetValue(zoneId, out var zone)
            || !gameDataService.ZoneInfoTable.TryGetValue(otherZoneId, out var otherZone))
            return false;

        var smdName = zone.SmdName?.Trim();
        return !string.IsNullOrEmpty(smdName)
            && string.Equals(smdName, otherZone.SmdName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private (float X, float Z) ResolveWarpArrival(short fromZoneId, short destinationZoneId, WarpInfo warp)
    {
        if (!SharesMapFile(fromZoneId, destinationZoneId)
            && SharesMapFile(destinationZoneId, BattleZoneManager.ZONE_MORADON))
            return (0f, 0f);

        return warp.X == 0f && warp.Z == 0f
            ? (0f, 0f)
            : ApplyWarpRadius(warp.X, warp.Z, warp.Radius);
    }

    private float ResolveTargetHeight(short zoneId, float x, float z)
        => Math.Max(0f, sessionManager.Maps?.GetGroundHeight(zoneId, x, z) ?? 0f);

    private long Ticks(float seconds) => (long)(seconds * time.TimestampFrequency);

    private static string NormalizeMapFamily(string mapName)
    {
        var normalized = (mapName ?? string.Empty).Trim();
        foreach (var suffix in SharedMapVariantSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return normalized[..^suffix.Length].TrimEnd();
        }

        return normalized;
    }

    private static readonly string[] SharedMapVariantSuffixes =
    [
        " VIII",
        " VII",
        " III",
        " II",
        " IV",
        " VI",
        " IX",
        " V",
        " X",
        " I"
    ];

    private readonly record struct MoveStep(
        byte ZoneId, float X, float Y, float Z, ushort WillX, ushort WillZ, short Speed, byte Echo);

    private readonly record struct MoveOutcome(MoveVerdict Verdict, float X, float Z, bool Displaced, bool StoodUp)
    {
        public static readonly MoveOutcome Ignored = new(MoveVerdict.Ignore, default, default, Displaced: false, StoodUp: false);
    }
}
