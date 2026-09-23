using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.Protocol;

public interface ICombatPacketCoordinator
{
    Task HandleAttackAsync(IClient client, Packet packet);
    Task HandleRegeneAsync(IClient client, Packet packet);
    Task HandleSkillDataAsync(IClient client, Packet packet);
    Task HandleTargetHpAsync(IClient client, Packet packet);
}

public class CombatPacketCoordinator(
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IMagicItemUsageService magicItemUsageService,
    ICombatLifecycleService combatLifecycleService,
    IStealthService stealthService,
    TimeProvider timeProvider,
    ILogger<CombatPacketCoordinator> logger) : ICombatPacketCoordinator
{
    private const int AttackRequestBytes = 10;
    private const int TargetHpRequestBytes = 5;
    private const int PercentScale = 100;
    private const int SlowestAttackSpeedPercent = 25;
    private const int FastestAttackSpeedPercent = 200;
    private const int SwingBurst = 1;
    private const int VisibleRegionSpan = 3;
    private const float TargetHpPollRange = RegionManager.RegionSize * VisibleRegionSpan;

    private static readonly TimeSpan SwingIntervalFloor = TimeSpan.FromMilliseconds(800);

    public async Task HandleAttackAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < AttackRequestBytes)
            return;

        _ = packet.ReadByte();
        _ = packet.ReadByte();
        var targetId = packet.ReadInt();

        if (session.WeaponsDisabled || session.IsBlinded
            || UserSessionMagicState.DisguiseForbidsAttack(session, gameDataService))
            return;

        if (!TryTakeSwing(session, timeProvider.GetUtcNow().UtcTicks))
        {
            logger.LogDebug("Swing by {Name} refused: faster than its attack speed allows", session.Name);
            return;
        }

        await stealthService.RevealAsync(session, InvisibilityType.None);

        var isBowAttack = CombatReach.HeldWeapon(session, gameDataService)?.IsBow() == true;
        if (isBowAttack && !magicItemUsageService.HasArrows(session))
        {
            logger.LogDebug("Attack rejected for {Name}: bow equipped with no arrows", session.Name);
            return;
        }

        var reach = CombatReach.BasicAttackReach(session, gameDataService) + Reach.LatencyAllowance;
        var attackResult = sessionManager.GetByCharacterId(targetId) is { } target
            ? await StrikePlayerAsync(session, target, reach)
            : await StrikeNpcAsync(session, targetId, reach);

        if (attackResult != AttackResult.Failed && isBowAttack)
            _ = await magicItemUsageService.TryConsumeArrowAsync(session);

        var result = AttackPacketWriter.Create(
            AttackPacketWriter.TypeMelee, attackResult, session.CharacterId, targetId);
        await sessionManager.Regions.SendToRegion(session, result, excludeSender: false);
    }

    private async Task<AttackResult> StrikePlayerAsync(UserSession session, UserSession target, float reach)
    {
        if (!Reach.Within(session, target, reach + CombatReach.PlayerBodyRadius)
            || !PvpRules.CanAttackPlayer(session, target)
            || !stealthService.CanSee(session, target))
        {
            logger.LogDebug(
                "PvP swing by {Name} on {Target} refused: arena={A}/{B} zone={Zone}/{TargetZone} invisible={Invisible}",
                session.Name, target.Name, session.ArenaId, target.ArenaId, session.ZoneId, target.ZoneId,
                target.IsInvisible);
            return AttackResult.Failed;
        }

        var damage = GmMode.Taken(target, GmMode.Dealt(session, target.Hp,
            PhysicalDamageCalculator.Calculate(session, PhysicalDefender.Of(target), gameDataService)));
        var outcome = target.ApplyDamage(damage);
        if (outcome.Dealt <= 0)
            return AttackResult.Failed;

        await combatLifecycleService.SendHpChangeAsync(target, session.CharacterId);
        await combatLifecycleService.SendPlayerTargetHpAsync(session, target, outcome.Dealt);
        if (!outcome.Killed)
            return AttackResult.Succeeded;

        await combatLifecycleService.HandlePlayerDeathAsync(target, session);
        return AttackResult.TargetDead;
    }

    private async Task<AttackResult> StrikeNpcAsync(UserSession session, int targetId, float reach)
    {
        var npcTarget = sessionManager.Regions.GetNpc(targetId);
        if (npcTarget == null
            || !npcTarget.IsAlive
            || !Reach.Within(session, npcTarget, reach + CombatReach.BodyRadius(npcTarget))
            || !NpcHostility.IsAttackableBy(npcTarget, session))
            return AttackResult.Failed;

        combatLifecycleService.SetNpcAggro(npcTarget, session);

        var outcome = npcTarget.ApplyDamage(GmMode.Dealt(session, npcTarget.Hp, PhysicalDamageCalculator.Calculate(
            session, PhysicalDefender.Of(npcTarget), gameDataService)));
        if (outcome.Dealt <= 0)
            return AttackResult.Failed;

        npcTarget.RecordDamage(session.CharacterId, outcome.Dealt, session, id => sessionManager.GetByCharacterId(id));
        await combatLifecycleService.SendNpcTargetHpAsync(session, npcTarget, outcome.Dealt);
        if (!outcome.Killed)
            return AttackResult.Succeeded;

        await combatLifecycleService.HandleNpcDeathAsync(npcTarget, session);
        return AttackResult.TargetDead;
    }

    private static bool TryTakeSwing(UserSession session, long now)
    {
        var interval = SwingInterval(session).Ticks;
        return session.WithLock(attacker =>
        {
            var pacing = attacker.CombatActions;
            if (now < pacing.NextSwingTicks - interval * SwingBurst)
                return false;

            pacing.NextSwingTicks = Math.Max(pacing.NextSwingTicks, now) + interval;
            pacing.LastActionTicks = now;
            attacker.IsSitting = false;
            return true;
        });
    }

    private static TimeSpan SwingInterval(UserSession session) =>
        SwingIntervalFloor * PercentScale
        / Math.Clamp((int)session.AttackSpeedAmount, SlowestAttackSpeedPercent, FastestAttackSpeedPercent);

    public async Task HandleRegeneAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        await combatLifecycleService.HandleRegeneAsync(client, session, packet.ReadByte());
    }

    public async Task HandleSkillDataAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var subOpcode = packet.ReadByte();
        if (subOpcode == (byte)SkillBarSubOpcode.Save)
        {
            var count = packet.ReadShort();
            if (count <= 0 || count > SkillBarRequest.MaxSlots)
                return;

            var data = new byte[count * SkillBarRequest.BytesPerSlot];
            for (var index = 0; index < count; index++)
            {
                BitConverter.TryWriteBytes(
                    data.AsSpan(index * SkillBarRequest.BytesPerSlot), packet.ReadInt());
            }

            session.SkillData = data;
            logger.LogDebug("Skill data saved for {Name}: {Count} skills", session.Name, count);

            await client.SendPacket(SkillDataPacketWriter.Slots(data));
            return;
        }

        if (subOpcode == (byte)SkillBarSubOpcode.Load)
            await client.SendPacket(SkillDataPacketWriter.Slots(session.SkillData));
    }

    public async Task HandleTargetHpAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < TargetHpRequestBytes)
            return;

        var targetId = packet.ReadInt();
        var echo = packet.ReadByte();

        var target = sessionManager.GetByCharacterId(targetId);
        if (target != null)
        {
            if (!Reach.Within(session, target, TargetHpPollRange) || !MayObserve(session, target))
                return;

            await client.SendPacket(BuildTargetHpPacket(targetId, echo, target.MaxHp, target.Hp, 0));
            return;
        }

        var npc = sessionManager.Regions.GetNpc(targetId);
        if (npc == null || !npc.IsAlive || !Reach.Within(session, npc, TargetHpPollRange))
            return;

        var npcResult = BuildTargetHpPacket(targetId, echo, npc.MaxHp, npc.Hp, 0);
        await client.SendPacket(npcResult);
    }

    private bool MayObserve(UserSession viewer, UserSession target) =>
        !target.IsInvisible || !PvpRules.IsEnemy(viewer, target) || stealthService.CanSee(viewer, target);

    private static Packet BuildTargetHpPacket(int targetId, byte echo, int maxHp, int hp, int damage)
    {
        return TargetHpPacketWriter.Polled(targetId, echo, maxHp, hp, damage);
    }
}
