using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol.Writers;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public readonly record struct RewardQuestView(
    int QuestId,
    string Title,
    bool Accepted,
    bool Claimed,
    bool Claimable,
    bool LevelLocked,
    byte MinLevel,
    byte MaxLevel,
    int Kills,
    int KillCount,
    int ItemsHeld,
    int ItemsRequired);

public interface IRewardQuestService
{
    Task<IReadOnlyList<RewardQuestView>> ListAsync(UserSession session, QuestBoard board);
    Task<RewardOutcome> AcceptAsync(UserSession session, int questId);
    Task<RewardOutcome> ClaimAsync(UserSession session, QuestBoard board, int questId);
    Task RecordKillAsync(NpcInstance npc, UserSession recipient);
}

public sealed class RewardQuestService(
    IGameDataService gameData,
    IRewardStateService states,
    IRewardGrantService grants,
    SessionManager sessionManager,
    IViolationMonitor violations,
    TimeProvider time,
    ILogger<RewardQuestService> logger) : IRewardQuestService
{
    public const float PartyCreditRange = 50f;

    private const string EventCompletedNotice = "{0} is complete. Claim your reward in the Event Quests window.";
    private const string DailyCompletedNotice = "{0} is complete. Claim your reward in the Daily Quests window.";

    public async Task<IReadOnlyList<RewardQuestView>> ListAsync(UserSession session, QuestBoard board)
    {
        if (!await states.EnsureLoadedAsync(session))
            return [];

        var today = Today();
        var quests = QuestsOn(board);
        return session.WithLock(s => Describe(s, board, quests, today));
    }

    public async Task<RewardOutcome> AcceptAsync(UserSession session, int questId)
    {
        if (!gameData.RewardQuestTable.TryGetValue(questId, out var quest) || quest.Board != QuestBoard.Event)
        {
            violations.Report(session, ViolationKind.ForgedEvent, $"accepted unknown event quest {questId}");
            return RewardOutcome.Refused;
        }

        return await states.RunExclusiveAsync(session, RewardOutcome.Unavailable, state => AcceptAsync(session, state, quest));
    }

    public async Task<RewardOutcome> ClaimAsync(UserSession session, QuestBoard board, int questId)
    {
        if (!gameData.RewardQuestTable.TryGetValue(questId, out var quest) || quest.Board != board)
        {
            violations.Report(session, ViolationKind.ForgedEvent, $"claimed unknown {board} quest {questId}");
            return RewardOutcome.Refused;
        }

        return await states.RunExclusiveAsync(session, RewardOutcome.Unavailable, state => ClaimAsync(session, state, quest));
    }

    public async Task RecordKillAsync(NpcInstance npc, UserSession recipient)
    {
        if (!npc.IsMonster)
            return;

        var targeted = gameData.RewardQuestTargetsByNpc[npc.NpcId]
            .Select(target => gameData.RewardQuestTable.GetValueOrDefault(target.QuestId))
            .OfType<RewardQuestData>()
            .Where(quest => quest.KillCount > 0)
            .Distinct()
            .ToList();
        if (targeted.Count == 0)
            return;

        await CreditAsync(recipient, targeted);

        var shared = targeted.Where(quest => quest.PartyShared).ToList();
        if (shared.Count == 0)
            return;

        foreach (var member in PartyMembersNear(recipient, npc))
            await CreditAsync(member, shared);
    }

    private async Task<RewardOutcome> AcceptAsync(UserSession session, RewardState state, RewardQuestData quest)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var period = quest.CurrentPeriod(DateOnly.FromDateTime(now));
        var key = default(RewardQuestKey);
        var repeated = false;
        var refusal = session.WithLock<ViolationKind?>(s =>
        {
            var offered = state.OfferedAccepts.Contains(quest.Id);
            if (period is not { } start || !quest.AcceptsLevel(s.Level))
                return offered ? ViolationKind.InvalidState : ViolationKind.ForgedEvent;

            key = new RewardQuestKey(quest.Id, start);
            var progress = state.ProgressOf(key);
            if (progress is { IsAccepted: true } or { IsClaimed: true })
            {
                repeated = true;
                return ViolationKind.InvalidState;
            }

            state.Track(key).AcceptedAt = now;
            state.DirtyQuests.Add(key);
            return null;
        });

        if (refusal is { } violation)
        {
            if (!repeated)
                violations.Report(session, violation, $"accept of event quest {quest.Id} refused");
            return RewardOutcome.Refused;
        }

        if (await states.SaveProgressAsync(session))
            return RewardOutcome.Succeeded;

        session.WithLock(_ =>
        {
            var progress = state.Track(key);
            progress.AcceptedAt = null;
            if (progress.Kills == 0)
            {
                state.Quests.Remove(key);
                state.DirtyQuests.Remove(key);
            }
        });
        return RewardOutcome.Unavailable;
    }

    private async Task<RewardOutcome> ClaimAsync(UserSession session, RewardState state, RewardQuestData quest)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var period = quest.CurrentPeriod(DateOnly.FromDateTime(now));
        var requirements = RequirementsOf(quest);
        var rewards = gameData.RewardQuestRewardsByQuest[quest.Id]
            .Select(reward => new RewardLine(reward.Kind, reward.ItemId, reward.Count))
            .ToList();
        var attempted = false;
        var verdict = RewardVerdict.Unavailable;
        var key = default(RewardQuestKey);
        RewardGrant? grant = null;
        RewardQuestProgress? progress = null;

        var committed = await states.CommitAsync(session, "quest claim",
            () =>
            {
                attempted = true;
                verdict = session.WithLock(s =>
                {
                    var offered = state.OfferedClaimsOn(quest.Board).Contains(quest.Id);
                    var stale = offered ? ViolationKind.InvalidState : ViolationKind.ForgedEvent;
                    if (period is not { } start)
                        return RewardVerdict.Violated(stale);

                    key = new RewardQuestKey(quest.Id, start);
                    progress = state.ProgressOf(key);
                    if (progress?.IsClaimed == true)
                        return RewardVerdict.Repeated;
                    if (!IsEarned(s, quest, progress, HasItems(s, requirements)))
                        return RewardVerdict.Violated(stale);

                    grant = grants.Plan(s, requirements, rewards);
                    if (!grant.IsReady)
                        return RewardVerdict.Blocked(grant.Status);

                    grants.Apply(s, grant);
                    progress = state.Track(key);
                    progress.ClaimedAt = now;
                    return RewardVerdict.Succeeded;
                });
                return verdict.Outcome == RewardOutcome.Succeeded;
            },
            db => db.StageQuestClaimAsync(new CharacterRewardQuest
            {
                CharacterId = session.CharacterId,
                QuestId = quest.Id,
                PeriodStart = key.PeriodStart,
                Kills = progress!.Kills,
                AcceptedAt = progress.AcceptedAt,
                ClaimedAt = now,
            }, grant!.EventCoins),
            () => session.WithLock(s =>
            {
                grants.Revert(s, grant!);
                progress!.ClaimedAt = null;
            }));

        if (!attempted)
            return RewardOutcome.Unavailable;

        if (verdict.Violation is { } violation)
        {
            violations.Report(session, violation, $"claim of {quest.Board} quest {quest.Id} refused");
            return RewardOutcome.Refused;
        }

        if (verdict.Outcome != RewardOutcome.Succeeded)
        {
            if (grant?.Status == RewardGrantStatus.UnknownItem)
                logger.LogWarning("{Board} quest {QuestId} names an item that does not exist", quest.Board, quest.Id);
            return verdict.Outcome;
        }

        if (!committed)
        {
            logger.LogWarning("{Name} could not record the claim of {Board} quest {QuestId}; the reward was withdrawn",
                session.Name, quest.Board, quest.Id);
            return RewardOutcome.Unavailable;
        }

        session.WithLock(_ => state.DirtyQuests.Remove(key));
        await grants.NotifyAsync(session, grant!);
        logger.LogInformation("{Name} claimed {Board} quest {QuestId} for period {Period}",
            session.Name, quest.Board, quest.Id, key.PeriodStart);
        return RewardOutcome.Succeeded;
    }

    private List<RewardQuestView> Describe(UserSession session, QuestBoard board, IReadOnlyList<RewardQuestData> quests, DateOnly today)
    {
        var state = session.Rewards;
        var offeredClaims = state.OfferedClaimsOn(board);
        offeredClaims.Clear();
        if (board == QuestBoard.Event)
            state.OfferedAccepts.Clear();

        var views = new List<RewardQuestView>(quests.Count);
        foreach (var quest in quests)
        {
            if (quest.CurrentPeriod(today) is not { } period)
                continue;

            var progress = state.ProgressOf(new RewardQuestKey(quest.Id, period));
            var accepted = progress?.IsAccepted == true;
            var claimed = progress?.IsClaimed == true;
            if (board == QuestBoard.Event && !accepted && !claimed && !quest.AcceptsLevel(session.Level))
                continue;

            var requirements = RequirementsOf(quest);
            var claimable = !claimed && IsEarned(session, quest, progress, HasItems(session, requirements));
            if (claimable)
                offeredClaims.Add(quest.Id);
            if (board == QuestBoard.Event && !accepted && !claimed)
                state.OfferedAccepts.Add(quest.Id);

            views.Add(new RewardQuestView(
                quest.Id,
                quest.Title,
                accepted,
                claimed,
                claimable,
                !quest.AcceptsLevel(session.Level),
                quest.MinLevel,
                quest.MaxLevel,
                progress?.Kills ?? 0,
                quest.KillCount,
                requirements.Sum(requirement => Math.Min(grants.CountItem(session, requirement.ItemId), requirement.Count)),
                requirements.Sum(requirement => requirement.Count)));
        }

        return views;
    }

    private static bool IsEarned(UserSession session, RewardQuestData quest, RewardQuestProgress? progress, bool hasItems)
    {
        var kills = progress?.Kills ?? 0;
        if (kills < quest.KillCount || !hasItems)
            return false;

        return quest.Board == QuestBoard.Event
            ? progress?.IsAccepted == true
            : quest.AcceptsLevel(session.Level) || kills > 0;
    }

    private bool HasItems(UserSession session, IReadOnlyList<ItemRequirement> requirements) =>
        requirements.All(requirement => grants.CountItem(session, requirement.ItemId) >= requirement.Count);

    private async Task CreditAsync(UserSession session, IReadOnlyList<RewardQuestData> quests)
    {
        if (!await states.EnsureLoadedAsync(session))
            return;

        var today = Today();
        var (changed, completed) = session.WithLock(s => Advance(s, quests, today));
        if (!changed)
            return;

        states.SaveProgressInBackground(session);
        foreach (var quest in completed)
        {
            var notice = quest.Board == QuestBoard.Event ? EventCompletedNotice : DailyCompletedNotice;
            await session.Client.SendPacket(ChatPacketWriter.SystemNotice((byte)session.Nation, string.Format(notice, quest.Title)));
        }
    }

    private static (bool Changed, List<RewardQuestData> Completed) Advance(
        UserSession session, IReadOnlyList<RewardQuestData> quests, DateOnly today)
    {
        var state = session.Rewards;
        var changed = false;
        var completed = new List<RewardQuestData>();
        foreach (var quest in quests)
        {
            if (quest.CurrentPeriod(today) is not { } period)
                continue;

            var key = new RewardQuestKey(quest.Id, period);
            var progress = state.ProgressOf(key);
            if (progress?.IsClaimed == true || (progress?.Kills ?? 0) >= quest.KillCount)
                continue;

            var eligible = quest.Board == QuestBoard.Event
                ? progress?.IsAccepted == true
                : quest.AcceptsLevel(session.Level);
            if (!eligible)
                continue;

            progress ??= state.Track(key);
            progress.Kills++;
            state.DirtyQuests.Add(key);
            changed = true;
            if (progress.Kills == quest.KillCount)
                completed.Add(quest);
        }

        return (changed, completed);
    }

    private List<UserSession> PartyMembersNear(UserSession recipient, NpcInstance npc)
    {
        var members = new List<UserSession>();
        if (!recipient.IsInParty || sessionManager.Parties.GetParty(recipient.PartyIndex) is not { } party)
            return members;

        foreach (var memberId in party.MemberIds.ToArray())
        {
            if (memberId < 0 || memberId == recipient.CharacterId)
                continue;

            var member = sessionManager.GetByCharacterId(memberId);
            if (member != null && member.Hp > 0 && Reach.Within(member, npc, PartyCreditRange))
                members.Add(member);
        }

        return members;
    }

    private List<RewardQuestData> QuestsOn(QuestBoard board) =>
        gameData.RewardQuestTable.Values
            .Where(quest => quest.Board == board)
            .OrderBy(quest => quest.Id)
            .ToList();

    private List<ItemRequirement> RequirementsOf(RewardQuestData quest) =>
        gameData.RewardQuestItemsByQuest[quest.Id]
            .Select(item => new ItemRequirement(item.ItemId, item.Count))
            .ToList();

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
