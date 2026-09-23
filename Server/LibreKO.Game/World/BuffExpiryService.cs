using LibreKO.Game.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public class BuffExpiryService(
    SessionManager sessionManager,
    IMagicExecutionService magicExecutionService,
    ICombatLifecycleService combatLifecycleService,
    ICombatNotificationService combatNotificationService,
    ILogger<BuffExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Buff expiry service started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessTickAsync(DateTime.UtcNow.Ticks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in buff expiry tick");
            }
        }
    }

    public async Task ProcessTickAsync(long nowTicks)
    {
        foreach (var session in sessionManager.GetAll())
        {
            await ProcessOverTimeEffectsAsync(session, nowTicks);

            if (session.ActiveBuffs.Count == 0)
                continue;

            var expired = session.ActiveBuffs
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var skillId in expired)
                await magicExecutionService.CancelAsync(session, skillId);
        }

        foreach (var npc in sessionManager.Regions.GetAllNpcs())
            await ProcessOverTimeEffectsAsync(npc, nowTicks);
    }

    private async Task ProcessOverTimeEffectsAsync(UserSession session, long nowTicks)
    {
        if (session.ActiveOverTimeEffects.Count == 0)
            return;

        if (session.Hp <= 0)
        {
            session.ActiveOverTimeEffects.Clear();
            return;
        }

        var expiredSkillIds = new List<int>();
        foreach (var (skillId, effect) in session.ActiveOverTimeEffects.ToList())
        {
            while (effect.TickCount < effect.TickLimit && effect.NextTickTicks <= nowTicks)
            {
                if (effect.TickAmount < 0)
                {
                    var killer = sessionManager.GetByCharacterId(effect.CasterId);
                    var damage = killer != null
                        ? GmMode.Dealt(killer, session.Hp, -effect.TickAmount)
                        : -effect.TickAmount;
                    var outcome = session.ApplyDamage(GmMode.Taken(session, damage));
                    if (outcome.Dealt > 0)
                        await combatLifecycleService.SendHpChangeAsync(
                            session,
                            killer?.CharacterId ?? CombatLifecycleService.NoKillerId);

                    if (outcome.Killed)
                        await combatLifecycleService.HandlePlayerDeathAsync(session, killer);

                    if (session.Hp <= 0)
                    {
                        expiredSkillIds.Add(skillId);
                        break;
                    }
                }
                else if (effect.TickAmount > 0 && session.Heal(effect.TickAmount) > 0)
                {
                    await combatLifecycleService.SendHpChangeAsync(session);
                }

                effect.TickCount++;
                effect.NextTickTicks += TimeSpan.FromSeconds(effect.TickIntervalSeconds).Ticks;
            }

            if (effect.TickCount >= effect.TickLimit)
                expiredSkillIds.Add(skillId);
        }

        // Track which icon flavors were active before removal so we can clear each
        // one ONLY if no other active DOT of the same flavor remains on the target.
        var clearedCodes = new HashSet<byte>();
        foreach (var skillId in expiredSkillIds)
        {
            if (session.ActiveOverTimeEffects.TryRemove(skillId, out var removed)
                && removed.TickAmount < 0
                && removed.PartyStatusCode > 0)
            {
                clearedCodes.Add(removed.PartyStatusCode);
            }
        }
        if (clearedCodes.Count == 0) return;

        // For each flavor that just lost an effect, only clear the panel icon if
        // no remaining DOT carries the same code. Otherwise the player still has
        // (e.g.) poison from a different stack and the icon must stay lit.
        foreach (var code in clearedCodes)
        {
            var stillActive = session.ActiveOverTimeEffects.Any(kv =>
                kv.Value.TickAmount < 0 && kv.Value.PartyStatusCode == code);
            if (!stillActive)
                await combatNotificationService.SendPartyStatusUpdateAsync(session, code, applied: false);
        }
    }

    private async Task ProcessOverTimeEffectsAsync(NpcInstance npc, long nowTicks)
    {
        if (npc.ActiveOverTimeEffects.Count == 0)
            return;

        if (!npc.IsAlive)
        {
            npc.ActiveOverTimeEffects.Clear();
            return;
        }

        var expiredSkillIds = new List<int>();
        foreach (var (skillId, effect) in npc.ActiveOverTimeEffects.ToList())
        {
            while (effect.TickCount < effect.TickLimit && effect.NextTickTicks <= nowTicks)
            {
                if (effect.TickAmount < 0)
                {
                    var killer = ClaimantOf(npc, effect.CasterId);
                    var damage = killer != null
                        ? GmMode.Dealt(killer, npc.Hp, -effect.TickAmount)
                        : -effect.TickAmount;
                    var outcome = npc.ApplyDamage(damage);

                    if (killer != null)
                    {
                        npc.RecordDamage(effect.CasterId, outcome.Dealt, killer, id => sessionManager.GetByCharacterId(id));
                        await combatLifecycleService.SendNpcTargetHpAsync(killer, npc, outcome.Dealt);
                    }
                    else
                    {
                        npc.RecordDamage(effect.CasterId, outcome.Dealt);
                    }

                    if (outcome.Killed)
                    {
                        if (killer != null)
                            await combatLifecycleService.HandleNpcDeathAsync(npc, killer);
                        else
                            await combatLifecycleService.HandleUnclaimedNpcDeathAsync(npc);
                    }

                    if (!npc.IsAlive)
                    {
                        expiredSkillIds.Add(skillId);
                        break;
                    }
                }
                else if (effect.TickAmount > 0)
                {
                    npc.Heal(effect.TickAmount);
                }

                effect.TickCount++;
                effect.NextTickTicks += TimeSpan.FromSeconds(effect.TickIntervalSeconds).Ticks;
            }

            if (effect.TickCount >= effect.TickLimit)
                expiredSkillIds.Add(skillId);
        }

        foreach (var skillId in expiredSkillIds)
            npc.ActiveOverTimeEffects.TryRemove(skillId, out _);
    }

    private UserSession? ClaimantOf(NpcInstance npc, int casterId)
    {
        var caster = sessionManager.GetByCharacterId(casterId);
        if (caster != null)
            return caster;

        var damagers = npc.WithLock(target =>
            new[] { target.TopDamagerCharId }.Concat(target.DamageMap.Keys).ToArray());
        return damagers
            .Select(sessionManager.GetByCharacterId)
            .FirstOrDefault(damager => damager != null);
    }
}
