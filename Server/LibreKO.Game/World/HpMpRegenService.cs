using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.World;

public class HpMpRegenService(
    SessionManager sessionManager,
    ICombatNotificationService combatNotificationService,
    TimeProvider timeProvider,
    ILogger<HpMpRegenService> logger) : BackgroundService
{
    private static readonly TimeSpan RestAfterCombat = TimeSpan.FromSeconds(VitalsRegenCalculator.IntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HP/MP regen service started ({Interval}s interval)",
            VitalsRegenCalculator.IntervalSeconds);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(VitalsRegenCalculator.IntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessTickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in HP/MP regen tick");
            }
        }
    }

    public async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        foreach (var session in sessionManager.GetAll())
            await RegenerateAsync(session, now, stoppingToken);
    }

    private async Task RegenerateAsync(UserSession session, long now, CancellationToken stoppingToken)
    {
        if (session.Hp <= 0 || session.IsWarping)
            return;

        var resting = session.IsSitting && now - session.CombatActions.LastActionTicks >= RestAfterCombat.Ticks;
        var battle = sessionManager.Battle;
        var amounts = VitalsRegenCalculator.Calculate(new RegenSubject(
            session.Level,
            session.Hp,
            session.MaxHp,
            session.Mp,
            session.MaxMp,
            session.Class,
            session.ZoneId,
            resting,
            session.IsGM,
            battle.BattleOpen == BattleZoneManager.SNOW_BATTLE));

        var hpChanged = amounts.Hp > 0 && session.Heal(amounts.Hp) > 0;
        var mpChanged = amounts.Mp > 0 && session.WithLock(s =>
        {
            if (s.Hp <= 0 || s.Mp >= s.MaxMp)
                return false;

            s.Mp = (short)Math.Min(s.MaxMp, s.Mp + amounts.Mp);
            return true;
        });

        if (hpChanged)
            await session.Client.SendPacket(
                VitalsPacketWriter.HpChange(session.MaxHp, session.Hp, VitalsPacketWriter.NoAttacker),
                stoppingToken);

        if (mpChanged)
            await session.Client.SendPacket(
                VitalsPacketWriter.MpChange(session.MaxMp, session.Mp), stoppingToken);

        if ((hpChanged || mpChanged) && session.IsInParty)
            await combatNotificationService.SendPartyHpUpdateAsync(session);
    }
}
