using FluentAssertions;
using LibreKO.Common.Enums;
using LibreKO.Game.World;

namespace LibreKO.Game.Tests;

public class NpcRetirementTests
{
    private const byte Zone = 21;
    private static readonly long DeathTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void ADeadNeverRespawningNpcIsRetiredOnceItsCorpseHasLingered()
    {
        var regions = new SessionManager().Regions;
        var summon = Kill(regions.SpawnNpc(Npc(NpcRespawnType.Never)));

        regions.GetDeadNpcsReadyToRetire(DeathTicks + RegionManager.CorpseLinger.Ticks - 1).Should().BeEmpty();

        var retired = regions.GetDeadNpcsReadyToRetire(DeathTicks + RegionManager.CorpseLinger.Ticks).ToList();
        retired.Should().ContainSingle().Which.Should().BeSameAs(summon);

        regions.RemoveNpc(summon);

        regions.GetNpc(summon.UniqueId).Should().BeNull();
        regions.GetAllNpcsInZone(Zone).Should().NotContain(summon);
        regions.GetAllNpcs().Should().NotContain(summon);
    }

    [Fact]
    public void ARespawningNpcIsNeverRetired()
    {
        var regions = new SessionManager().Regions;
        var monster = Kill(regions.SpawnNpc(Npc(NpcRespawnType.Normal)));

        regions.GetDeadNpcsReadyToRetire(DeathTicks + RegionManager.CorpseLinger.Ticks * 100).Should().BeEmpty();
        regions.GetNpc(monster.UniqueId).Should().BeSameAs(monster);
    }

    [Fact]
    public void ALivingNeverRespawningNpcStays()
    {
        var regions = new SessionManager().Regions;
        var summon = regions.SpawnNpc(Npc(NpcRespawnType.Never));

        regions.GetDeadNpcsReadyToRetire(DeathTicks + RegionManager.CorpseLinger.Ticks * 100).Should().BeEmpty();
        regions.GetNpc(summon.UniqueId).Should().BeSameAs(summon);
    }

    private static NpcInstance Npc(NpcRespawnType respawn) => new()
    {
        NpcId = 750,
        ZoneId = Zone,
        X = 100,
        Z = 100,
        SpawnX = 100,
        SpawnZ = 100,
        Hp = 100,
        MaxHp = 100,
        IsMonster = true,
        RespawnType = respawn,
    };

    private static NpcInstance Kill(NpcInstance npc)
    {
        npc.ApplyDamage(npc.Hp);
        npc.TryBeginDeath(DeathTicks);
        return npc;
    }
}
