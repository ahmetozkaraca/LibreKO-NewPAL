using LibreKO.Common.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public class NpcRespawnService(
    SessionManager sessionManager,
    SummonQuota summonQuota,
    INpcLifecycleService lifecycle,
    ILogger<NpcRespawnService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NPC respawn service started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in NPC respawn tick");
            }
        }
    }

    public async Task TickAsync()
    {
        var now = DateTime.UtcNow.Ticks;
        var npcsToRespawn = sessionManager.Regions.GetDeadNpcsReadyToRespawn(now).ToList();

        foreach (var npc in npcsToRespawn)
        {
            npc.Respawn();

            if (!NpcWorldFilter.ShouldSpawnNormally(npc))
                continue;

            sessionManager.Regions.UpdateNpcRegion(npc);

            var pkt = Protocol.NpcPacketMapper.BuildInOutPacket(npc, InOutType.In);
            await sessionManager.Regions.BroadcastFromNpc(npc, pkt);
        }

        if (npcsToRespawn.Count > 0)
            logger.LogDebug("Respawned {Count} NPCs", npcsToRespawn.Count);

        foreach (var summon in summonQuota.TakeExpired())
            await lifecycle.DespawnAsync(summon);

        foreach (var corpse in sessionManager.Regions.GetDeadNpcsReadyToRetire(now).ToList())
            sessionManager.Regions.RemoveNpc(corpse);

        sessionManager.Regions.CleanupExpiredBundles();
    }

}
