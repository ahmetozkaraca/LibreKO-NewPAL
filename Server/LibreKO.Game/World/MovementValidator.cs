using LibreKO.Game.Configuration;
using Microsoft.Extensions.Options;

namespace LibreKO.Game.World;

public enum MoveVerdict : byte
{
    Accept,
    Ignore,
    Reject,
}

public sealed class MovementCheckState
{
    public bool Initialized;
    public float X;
    public float Z;
    public long Timestamp;
    public float Budget;
    public long GraceUntil;
}

public interface IMovementValidator
{
    MoveVerdict Check(UserSession session, float x, float z);
}

public sealed class MovementValidator(
    IOptions<GameServerSettings> settings,
    IViolationMonitor violations,
    TimeProvider time) : IMovementValidator
{
    public const float TeleportDistance = 50f;

    private const float PositionEpsilon = 0.05f;
    private const float PercentScale = 100f;

    public MoveVerdict Check(UserSession session, float x, float z)
    {
        var options = settings.Value.AntiCheat.Movement;
        var state = session.MoveCheck;
        var now = time.GetTimestamp();

        if (!options.Enabled || session.IsGM)
        {
            Commit(state, x, z, now, 0f);
            return MoveVerdict.Accept;
        }

        var maxSpeed = MaxSpeed(session, options);
        if (!state.Initialized || Relocated(session, state))
        {
            state.Budget = state.Initialized ? 0f : maxSpeed * options.BurstSeconds;
            state.Initialized = true;
            state.X = session.X;
            state.Z = session.Z;
            state.Timestamp = now;
            state.GraceUntil = now + Ticks(options.RelocationGraceSeconds);
        }

        var elapsed = (float)time.GetElapsedTime(state.Timestamp, now).TotalSeconds;
        var budget = Math.Min(state.Budget + maxSpeed * elapsed, maxSpeed * options.BurstSeconds);
        var distance = MathF.Sqrt(Reach.DistanceSquared(state.X, state.Z, x, z));

        if (distance <= budget + options.SlackMeters)
        {
            Commit(state, x, z, now, Math.Max(budget - distance, -options.SlackMeters));
            return MoveVerdict.Accept;
        }

        state.Timestamp = now;
        state.Budget = Math.Max(budget, 0f);
        if (now < state.GraceUntil)
            return MoveVerdict.Ignore;

        state.GraceUntil = now + Ticks(options.RelocationGraceSeconds);
        violations.Report(
            session,
            distance > TeleportDistance ? ViolationKind.Teleport : ViolationKind.SpeedHack,
            $"moved {distance:F1}m with a budget of {budget:F1}m in zone {session.ZoneId}");
        return MoveVerdict.Reject;
    }

    private float MaxSpeed(UserSession session, MovementCheckSettings options)
    {
        var speed = options.RunSpeed * session.SpeedAmount / PercentScale * options.SpeedTolerance;
        return settings.Value.PublicDemo.GrantGameMasterSpeedToEveryone
            ? speed * options.GameMasterSpeedMultiplier
            : speed;
    }

    private static bool Relocated(UserSession session, MovementCheckState state)
        => MathF.Abs(session.X - state.X) > PositionEpsilon || MathF.Abs(session.Z - state.Z) > PositionEpsilon;

    private static void Commit(MovementCheckState state, float x, float z, long now, float budget)
    {
        state.Initialized = true;
        state.X = x;
        state.Z = z;
        state.Timestamp = now;
        state.Budget = budget;
    }

    private long Ticks(float seconds) => (long)(seconds * time.TimestampFrequency);
}
