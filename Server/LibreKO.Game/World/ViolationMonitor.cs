using System.Collections.Concurrent;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LibreKO.Game.World;

public enum ViolationKind : byte
{
    RateLimit,
    InvalidState,
    InvalidRequest,
    OutOfRange,
    SpeedHack,
    Teleport,
    ServerOnlyOpcode,
    ForgedEvent,
}

public interface IViolationMonitor
{
    void Report(IClient client, ViolationKind kind, string detail);
    void Report(UserSession session, ViolationKind kind, string detail);
    int ScoreOf(Guid clientId);
    void Forget(Guid clientId);
}

public sealed class ViolationMonitor(
    IOptions<GameServerSettings> settings,
    SessionManager sessionManager,
    TimeProvider time,
    ILogger<ViolationMonitor> logger) : IViolationMonitor
{
    public const int RateLimitWeight = 1;
    public const int InvalidStateWeight = 2;
    public const int InvalidRequestWeight = 5;
    public const int OutOfRangeWeight = 5;
    public const int SpeedHackWeight = 10;
    public const int TeleportWeight = 25;
    public const int ServerOnlyOpcodeWeight = 25;
    public const int ForgedEventWeight = 50;

    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, Record> _records = new();

    public static int WeightOf(ViolationKind kind) => kind switch
    {
        ViolationKind.RateLimit => RateLimitWeight,
        ViolationKind.InvalidState => InvalidStateWeight,
        ViolationKind.InvalidRequest => InvalidRequestWeight,
        ViolationKind.OutOfRange => OutOfRangeWeight,
        ViolationKind.SpeedHack => SpeedHackWeight,
        ViolationKind.Teleport => TeleportWeight,
        ViolationKind.ServerOnlyOpcode => ServerOnlyOpcodeWeight,
        ViolationKind.ForgedEvent => ForgedEventWeight,
        _ => RateLimitWeight,
    };

    public void Report(UserSession session, ViolationKind kind, string detail)
        => Report(session.Client, kind, detail);

    public void Report(IClient client, ViolationKind kind, string detail)
    {
        var options = settings.Value.AntiCheat;
        var session = sessionManager.GetByClientId(client.Id);
        var now = time.GetUtcNow();
        var record = _records.GetOrAdd(client.Id, _ => new Record());

        double score;
        int suppressed = 0;
        bool shouldLog;
        bool shouldKick;
        using (record.Sync.EnterScope())
        {
            score = Decayed(record, now, options.ScoreDecayPerMinute) + WeightOf(kind);
            record.Score = score;
            record.Updated = now;

            shouldLog = now - record.LastLogged >= LogInterval;
            if (shouldLog)
            {
                record.LastLogged = now;
                suppressed = record.Suppressed;
                record.Suppressed = 0;
            }
            else
            {
                record.Suppressed++;
            }

            shouldKick = options.KickEnabled
                && score >= options.KickScore
                && session?.IsGM != true
                && !record.Kicked;
            if (shouldKick)
                record.Kicked = true;
        }

        if (shouldLog)
            logger.LogWarning(
                "Violation {Kind} by {Name} (account {AccountId}, client {ClientId}): {Detail}; score {Score:F0}, {Suppressed} reports suppressed",
                kind, session?.Name ?? "-", client.AccountId, client.Id, detail, score, suppressed);

        if (!shouldKick)
            return;

        logger.LogWarning(
            "Disconnecting {Name} (account {AccountId}, client {ClientId}): violation score {Score:F0} reached {Limit}",
            session?.Name ?? "-", client.AccountId, client.Id, score, options.KickScore);
        client.Disconnect();
    }

    public int ScoreOf(Guid clientId)
    {
        if (!_records.TryGetValue(clientId, out var record))
            return 0;

        using (record.Sync.EnterScope())
            return (int)Decayed(record, time.GetUtcNow(), settings.Value.AntiCheat.ScoreDecayPerMinute);
    }

    public void Forget(Guid clientId) => _records.TryRemove(clientId, out _);

    private static double Decayed(Record record, DateTimeOffset now, int decayPerMinute)
    {
        if (record.Updated == default)
            return 0;

        var minutes = Math.Max(0, (now - record.Updated).TotalMinutes);
        return Math.Max(0, record.Score - minutes * decayPerMinute);
    }

    private sealed class Record
    {
        public readonly Lock Sync = new();
        public double Score;
        public DateTimeOffset Updated;
        public DateTimeOffset LastLogged;
        public int Suppressed;
        public bool Kicked;
    }
}
