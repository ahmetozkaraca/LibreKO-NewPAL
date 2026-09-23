using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class CombatAuthorityTests : GameTestBase
{
    private const byte Moradon = 21;
    private const byte RonarkLand = BattleZoneManager.ZONE_RONARK_LAND;
    private const byte NoWeaponNeeded = MagicWeaponRequirement.NoWeaponNeeded;
    private const short WarriorBeginner = 101;
    private const short WarriorNovice = 105;
    private const short RogueBeginner = 102;
    private const short MageNovice = 109;
    private const short MageMaster = 110;
    private const short PriestNovice = 111;
    private const int Guard = 101007;
    private const int Heal = 111509;
    private const int SlowHeal = 111510;
    private const int Nuke = 110501;
    private const int AreaNuke = 110502;
    private const int Curse = 110503;
    private const int PriestGreaterHeal = 111554;
    private const int MonsterSkill = 300431;
    private const int ArrowShot = 102500;
    private const int SelfRevival = 480004;
    private const int RevivalScroll = 800039000;
    private const int StealthPotion = 490300;
    private const int StealthPotionItem = 800300000;
    private const int ArmourPotion = 490301;
    private const int ArmourPotionItem = 800301000;
    private const int GoldHeal = 490302;
    private const int GoldHealToken = 800302000;
    private const int Frenzy = 105510;
    private const int Bash = 101501;
    private const int SummonFriend = 109004;
    private const int Escape = 109035;
    private const int SightSkill = 107715;
    private const int Resurrect = 111520;
    private const int Blessing = 111530;
    private const int ResurrectMana = 30;
    private const int RevivalStone = 379006000;
    private const short RequiredStones = 3;
    private const int NukeMana = 50;
    private const short NukeRecast = 100;
    private const byte AnyRegene = 1;
    private const int Arrow = 391010000;
    private const int Sword = 110110001;
    private const int ArrowKind = 120;
    private const int GoldHealPrice = 3500;
    private static readonly TimeSpan BurstFloor = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SwingFloor = TimeSpan.FromMilliseconds(800);

    private sealed class Catalog
    {
        public Dictionary<int, MagicData> Magic { get; } = [];
        public Dictionary<int, MagicType2Data> Type2 { get; } = [];
        public Dictionary<int, MagicType3Data> Type3 { get; } = [];
        public Dictionary<int, MagicType4Data> Type4 { get; } = [];
        public Dictionary<int, MagicType5Data> Type5 { get; } = [];
        public Dictionary<int, MagicType8Data> Type8 { get; } = [];
        public Dictionary<int, MagicType9Data> Type9 { get; } = [];
        public Dictionary<int, ItemData> Items { get; } = [];
    }

    private sealed record Harness(ServiceProvider Provider, ManualClock Clock, Catalog Data) : IDisposable
    {
        public IMagicPacketCoordinator Magic => Provider.GetRequiredService<IMagicPacketCoordinator>();
        public ICombatPacketCoordinator Combat => Provider.GetRequiredService<ICombatPacketCoordinator>();
        public SessionManager Sessions => Provider.GetRequiredService<SessionManager>();
        public int Score(IClient client) => Provider.GetRequiredService<IViolationMonitor>().ScoreOf(client.Id);
        public void Dispose() => Provider.Dispose();
    }

    [Fact]
    public async Task SwingsFasterThanTheAttackCadenceAreDropped()
    {
        using var harness = CreateHarness();
        var (attacker, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 101, 100);

        for (var swing = 0; swing < 5; swing++)
            await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));

        Attacks(sent).Should().Be(2, "a burst of one extra swing absorbs latency, the rest are refused");

        harness.Clock.Advance(SwingFloor);
        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));
        Attacks(sent).Should().Be(3);
    }

    [Fact]
    public async Task AnAttackSpeedDebuffStretchesTheSwingInterval()
    {
        using var harness = CreateHarness();
        var (attacker, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        attacker.AttackSpeedAmount = 50;
        var worm = Monster(harness, Moradon, 101, 100);

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));
        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));
        harness.Clock.Advance(SwingFloor);
        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));

        Attacks(sent).Should().Be(2, "at half attack speed the next swing is due after twice the interval");
    }

    [Fact]
    public async Task ASwingFromBeyondWeaponReachMissesWhateverDistanceTheClientClaims()
    {
        using var harness = CreateHarness();
        var (_, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 120, 100);

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));

        worm.Hp.Should().Be(worm.MaxHp);
        LastAttackResult(sent).Should().Be(AttackResult.Failed);
    }

    [Fact]
    public async Task APlayerWhoseWeaponsAreDisabledCannotSwing()
    {
        using var harness = CreateHarness();
        var (attacker, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        attacker.WeaponsDisabled = true;
        var worm = Monster(harness, Moradon, 101, 100);

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));

        Attacks(sent).Should().Be(0);
        worm.Hp.Should().Be(worm.MaxHp);
    }

    [Fact]
    public async Task AStealthedEnemyCannotBeHitWithoutSight()
    {
        using var harness = CreateHarness();
        var (_, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, RonarkLand, 100, 100);
        var (enemy, _, _) = Player(harness, 2, 201, AccountNation.ElMorad, RonarkLand, 101, 100);
        enemy.Invisibility = InvisibilityType.DispelOnAttack;

        await harness.Combat.HandleAttackAsync(client, Swing(enemy.CharacterId));

        enemy.Hp.Should().Be(enemy.MaxHp);
        LastAttackResult(sent).Should().Be(AttackResult.Failed);
    }

    [Fact]
    public async Task SwingingStandsThePlayerUpAndBroadcastsNoClientCriticalFlag()
    {
        using var harness = CreateHarness();
        var (attacker, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        attacker.IsSitting = true;
        var worm = Monster(harness, Moradon, 101, 100);

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId, critical: 1));

        attacker.IsSitting.Should().BeFalse();
        var broadcast = sent.Last(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_ATTACK);
        broadcast.GetBytes()[^1].Should().Be(AttackPacketWriter.NoCritical);
    }

    [Fact]
    public async Task TargetHpIsRefusedForAnotherZoneAFarTargetAndAnUnseenEnemy()
    {
        using var harness = CreateHarness();
        var (viewer, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, RonarkLand, 100, 100);
        var (elsewhere, _, _) = Player(harness, 2, 201, AccountNation.ElMorad, Moradon, 100, 100);
        var (far, _, _) = Player(harness, 3, 201, AccountNation.ElMorad, RonarkLand, 900, 900);
        var (hidden, _, _) = Player(harness, 4, 201, AccountNation.ElMorad, RonarkLand, 105, 100);
        hidden.Invisibility = InvisibilityType.DispelOnMove;
        var (visible, _, _) = Player(harness, 5, 201, AccountNation.ElMorad, RonarkLand, 105, 100);

        foreach (var target in new[] { elsewhere, far, hidden, visible })
            await harness.Combat.HandleTargetHpAsync(client, TargetHpPoll(target.CharacterId));

        sent.Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_TARGET_HP)
            .Select(packet => BitConverter.ToInt32(packet.GetBytes(), 1))
            .Should().Equal(visible.CharacterId);
    }

    [Fact]
    public async Task ACancelAfterTheSkillFiredDoesNotRefundItsCooldown()
    {
        using var harness = CreateHarness();
        GuardSkill(harness.Data);
        var (warrior, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);

        await harness.Magic.CastAsync(client, Guard, warrior.CharacterId, warrior.CharacterId);
        warrior.ActiveBuffs.Should().ContainKey(Guard);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Cancel, Guard, warrior.CharacterId, warrior.CharacterId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Fail, Guard, warrior.CharacterId, warrior.CharacterId);

        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Guard, warrior.CharacterId, warrior.CharacterId);

        warrior.SkillCooldowns.Should().ContainKey(Guard);
        warrior.ActiveBuffs.Should().NotContainKey(Guard, "the recast is still on cooldown");
    }

    [Fact]
    public async Task AnEffectingWithoutAnAcceptedCastingDoesNothing()
    {
        using var harness = CreateHarness();
        HealSkills(harness.Data);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        priest.Hp = 100;

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, Heal, priest.CharacterId, priest.CharacterId);

        priest.Hp.Should().Be(100);
        priest.Mp.Should().Be(priest.MaxMp);
    }

    [Fact]
    public async Task AReleaseBeforeTheCastTimeIsRefusedUntilTheCastCompletes()
    {
        using var harness = CreateHarness();
        HealSkills(harness.Data);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        priest.Hp = 100;

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, SlowHeal, priest.CharacterId, priest.CharacterId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, SlowHeal, priest.CharacterId, priest.CharacterId);
        priest.Hp.Should().Be(100);

        harness.Clock.Advance(TimeSpan.FromSeconds(1.5));
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, SlowHeal, priest.CharacterId, priest.CharacterId);
        priest.Hp.Should().Be(200);
    }

    [Fact]
    public async Task AnInstantCastBuffLetsATimedSkillReleaseAtOnce()
    {
        using var harness = CreateHarness();
        HealSkills(harness.Data);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        priest.Hp = 100;
        priest.InstantCast = true;

        await harness.Magic.CastAsync(client, SlowHeal, priest.CharacterId, priest.CharacterId);

        priest.Hp.Should().Be(200);
    }

    [Fact]
    public async Task AFailNeverExecutesAnOverTimeSkill()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 105, 100);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Fail, Nuke, mage.CharacterId, worm.UniqueId);

        worm.Hp.Should().Be(worm.MaxHp);
        worm.ActiveOverTimeEffects.Should().BeEmpty();
    }

    [Fact]
    public async Task AMonsterSkillIsRefusedAndReported()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[MonsterSkill] = new MagicData
        {
            Id = MonsterSkill, Type1 = 3, Moral = 10, Range = 20, ItemGroup = NoWeaponNeeded
        };
        var (mage, client, sent) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, MonsterSkill, mage.CharacterId, -1);

        harness.Score(client).Should().Be(ViolationMonitor.ForgedEventWeight);
        Replies(sent).Should().Equal(MagicProcessOpcode.Fail);
    }

    [Fact]
    public async Task AMageCannotCastAPriestHealButANoviceKeepsItsBeginnerSkills()
    {
        using var harness = CreateHarness();
        HealSkills(harness.Data);
        GuardSkill(harness.Data);
        harness.Data.Magic[PriestGreaterHeal] = HealRow(PriestGreaterHeal, castTenths: 0);
        var (mage, mageClient, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        mage.Hp = 100;
        var (warrior, warriorClient, _) = Player(harness, 2, WarriorNovice, AccountNation.Karus, Moradon, 100, 100);

        await harness.Magic.SendAsync(mageClient, MagicProcessOpcode.Casting, PriestGreaterHeal, mage.CharacterId, mage.CharacterId);
        await harness.Magic.CastAsync(warriorClient, Guard, warrior.CharacterId, warrior.CharacterId);

        harness.Score(mageClient).Should().Be(ViolationMonitor.ForgedEventWeight);
        warrior.ActiveBuffs.Should().ContainKey(Guard);
        harness.Score(warriorClient).Should().Be(0);
    }

    [Fact]
    public async Task AHealIsRefusedForAnotherZoneAnEnemyAndAnAllyOutOfRange()
    {
        using var harness = CreateHarness();
        HealSkills(harness.Data);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        var (elsewhere, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, RonarkLand, 100, 100);
        var (enemy, _, _) = Player(harness, 3, 201, AccountNation.ElMorad, Moradon, 101, 100);
        var (distant, _, _) = Player(harness, 4, WarriorBeginner, AccountNation.Karus, Moradon, 140, 100);
        var (near, _, _) = Player(harness, 5, WarriorBeginner, AccountNation.Karus, Moradon, 110, 100);

        foreach (var target in new[] { elsewhere, enemy, distant, near })
        {
            target.Hp = 100;
            harness.Clock.Advance(BurstFloor);
            await harness.Magic.CastAsync(client, Heal, priest.CharacterId, target.CharacterId);
        }

        elsewhere.Hp.Should().Be(100);
        enemy.Hp.Should().Be(100);
        distant.Hp.Should().Be(100);
        near.Hp.Should().Be(200);
    }

    [Fact]
    public async Task AnAreaCentreFarBeyondRangeIsRefusedAndReported()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 400, 100);

        await harness.Magic.CastAsync(client, AreaNuke, mage.CharacterId, MagicTargetingService.AreaTargetId, 400, 0, 100);

        worm.Hp.Should().Be(worm.MaxHp);
        harness.Score(client).Should().Be(ViolationMonitor.OutOfRangeWeight);
    }

    [Fact]
    public async Task ADeadPlayerCannotCast()
    {
        using var harness = CreateHarness();
        GuardSkill(harness.Data);
        var (warrior, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        warrior.Hp = 0;

        await harness.Magic.CastAsync(client, Guard, warrior.CharacterId, warrior.CharacterId);

        warrior.ActiveBuffs.Should().BeEmpty();
    }

    [Fact]
    public async Task SelfRevivalNeedsAndSpendsItsScroll()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[SelfRevival] = new MagicData
        {
            Id = SelfRevival, Type1 = 5, Moral = 1, UseItem = RevivalScroll, ReCastTime = 2, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type5[SelfRevival] = new MagicType5Data
        {
            Id = SelfRevival, Type = (byte)SpecialMagicType.ResurrectionSelf, ExpRecover = 100
        };
        harness.Data.Items[RevivalScroll] = Consumable(RevivalScroll);
        var (corpse, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        corpse.Hp = 0;

        await harness.Magic.CastAsync(client, SelfRevival, corpse.CharacterId, corpse.CharacterId);
        corpse.Hp.Should().Be(0, "the scroll is not carried");

        Carry(corpse, RevivalScroll);
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, SelfRevival, corpse.CharacterId, corpse.CharacterId);

        corpse.Hp.Should().Be(corpse.MaxHp);
        corpse.Inventory[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AStealthPotionIsNotFree()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[StealthPotion] = new MagicData
        {
            Id = StealthPotion, Type1 = 9, Moral = 1, UseItem = StealthPotionItem, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type9[StealthPotion] = new MagicType9Data
        {
            Id = StealthPotion, StateChange = (byte)MagicStealthType.DispelOnMove, Duration = 30
        };
        harness.Data.Items[StealthPotionItem] = Consumable(StealthPotionItem);
        var (rogue, client, _) = Player(harness, 1, RogueBeginner, AccountNation.Karus, Moradon, 100, 100);

        await harness.Magic.CastAsync(client, StealthPotion, rogue.CharacterId, rogue.CharacterId);
        rogue.Invisibility.Should().Be(InvisibilityType.None);

        Carry(rogue, StealthPotionItem);
        await harness.Magic.CastAsync(client, StealthPotion, rogue.CharacterId, rogue.CharacterId);

        rogue.Invisibility.Should().Be(InvisibilityType.DispelOnMove);
        rogue.Inventory[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task APotionIsKeptWhenItsBuffIsRefused()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[ArmourPotion] = new MagicData
        {
            Id = ArmourPotion, Type1 = 4, Moral = 1, UseItem = ArmourPotionItem, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type4[ArmourPotion] = new MagicType4Data
        {
            Id = ArmourPotion, BuffType = (byte)BuffType.Ac, Ac = 30, Duration = 60
        };
        harness.Data.Items[ArmourPotionItem] = Consumable(ArmourPotionItem);
        var (warrior, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        Carry(warrior, ArmourPotionItem);
        warrior.ActiveBuffs[Guard] = new ActiveBuff
        {
            MagicId = Guard, CasterId = warrior.CharacterId, BuffType = BuffType.Ac,
            ExpireTicks = DateTime.UtcNow.AddMinutes(1).Ticks
        };

        await harness.Magic.CastAsync(client, ArmourPotion, warrior.CharacterId, warrior.CharacterId);

        warrior.ActiveBuffs.Should().NotContainKey(ArmourPotion);
        warrior.Inventory[InventoryConstants.InventoryStart].Count.Should().Be(1);
    }

    [Fact]
    public async Task AnArrowSkillOnlyHitsForArrowsPaidInFlight()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[ArrowShot] = new MagicData
        {
            Id = ArrowShot, Type1 = 2, Moral = 7, UseItem = Arrow, Msp = 10, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type2[ArrowShot] = new MagicType2Data
        {
            Id = ArrowShot, HitType = 1, HitRate = 101, AddDamage = 100, NeedArrow = 1
        };
        harness.Data.Items[Arrow] = new ItemData { Num = Arrow, Kind = ArrowKind, Countable = 1, Duration = 1, ReqLevelMax = 83 };
        var (archer, client, _) = Player(harness, 1, RogueBeginner, AccountNation.Karus, Moradon, 100, 100);
        Carry(archer, Arrow, 10);
        var worm = Monster(harness, Moradon, 105, 100);

        await harness.Magic.CastAsync(client, ArrowShot, archer.CharacterId, worm.UniqueId);
        worm.Hp.Should().Be(worm.MaxHp, "no volley was paid for");
        archer.Mp.Should().Be(archer.MaxMp);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Flying, ArrowShot, archer.CharacterId, worm.UniqueId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, ArrowShot, archer.CharacterId, worm.UniqueId);
        var afterOneArrow = worm.Hp;
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, ArrowShot, archer.CharacterId, worm.UniqueId);

        afterOneArrow.Should().BeLessThan(worm.MaxHp);
        worm.Hp.Should().Be(afterOneArrow, "one arrow was paid for");
        archer.Inventory[InventoryConstants.InventoryStart].Count.Should().Be(9);
        archer.Mp.Should().Be((short)(archer.MaxMp - 10));
    }

    [Fact]
    public async Task ABlindedPlayerCannotSwing()
    {
        using var harness = CreateHarness();
        var (attacker, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        attacker.IsBlinded = true;
        var worm = Monster(harness, Moradon, 101, 100);

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));

        Attacks(sent).Should().Be(0);
        worm.Hp.Should().Be(worm.MaxHp);
        harness.Score(client).Should().Be(0, "the stock client does not know it is blind");
    }

    [Fact]
    public async Task ABlindedPlayerCannotAimASkillAtSomeoneElse()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 105, 100);
        mage.IsBlinded = true;

        await harness.Magic.CastAsync(client, Nuke, mage.CharacterId, worm.UniqueId);

        worm.Hp.Should().Be(worm.MaxHp);
        worm.ActiveOverTimeEffects.Should().BeEmpty();

        mage.IsBlinded = false;
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.Magic.CastAsync(client, Nuke, mage.CharacterId, worm.UniqueId);

        (worm.Hp < worm.MaxHp || worm.ActiveOverTimeEffects.Any()).Should().BeTrue("sight restores targeting");
    }

    [Fact]
    public async Task ABlindedPlayerCanStillBuffThemselves()
    {
        using var harness = CreateHarness();
        GuardSkill(harness.Data);
        var (warrior, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        warrior.IsBlinded = true;

        await harness.Magic.CastAsync(client, Guard, warrior.CharacterId, warrior.CharacterId);

        warrior.ActiveBuffs.Should().ContainKey(Guard);
    }

    [Fact]
    public async Task ACurseCannotLandOnAPlayerThatMayNotBeAttacked()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, RonarkLand, 100, 100);
        var (ally, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, RonarkLand, 102, 100);

        await harness.Magic.CastAsync(client, Curse, mage.CharacterId, ally.CharacterId);

        ally.ActiveBuffs.Should().BeEmpty();
    }

    [Fact]
    public async Task AHostileSkillNeedsSightOfAStealthedTarget()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        harness.Data.Magic[SightSkill] = new MagicData { Id = SightSkill, Type1 = 9, Moral = 1 };
        harness.Data.Type9[SightSkill] = new MagicType9Data
        {
            Id = SightSkill, StateChange = (byte)MagicStealthType.SeeInvisible, Radius = 25, Duration = 60
        };
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, RonarkLand, 100, 100);
        var (enemy, _, _) = Player(harness, 2, 201, AccountNation.ElMorad, RonarkLand, 105, 100);
        enemy.Invisibility = InvisibilityType.DispelOnAttack;

        await harness.Magic.CastAsync(client, Nuke, mage.CharacterId, enemy.CharacterId);
        enemy.Hp.Should().Be(enemy.MaxHp);

        await harness.Provider.GetRequiredService<IStealthService>()
            .GrantSightAsync(mage, harness.Data.Type9[SightSkill].Radius);
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Nuke, mage.CharacterId, enemy.CharacterId);
        enemy.Hp.Should().BeLessThan(enemy.MaxHp);
    }

    [Fact]
    public async Task TeleportsRespectPartyMembershipAndNoRecall()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[SummonFriend] = new MagicData
        {
            Id = SummonFriend, Type1 = 8, Moral = (byte)SkillMoral.Party, Range = 10000, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Magic[Escape] = new MagicData
        {
            Id = Escape, Type1 = 8, Moral = (byte)SkillMoral.PartyAll, Range = 10000, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type8[SummonFriend] = new MagicType8Data { Id = SummonFriend, WarpType = (byte)MagicWarpType.SummonInZone };
        harness.Data.Type8[Escape] = new MagicType8Data { Id = Escape, WarpType = (byte)MagicWarpType.BindPoint };
        var (mage, client, _) = Player(harness, 1, MageNovice, AccountNation.Karus, Moradon, 100, 100);
        var (stranger, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, Moradon, 300, 300);
        var (member, _, _) = Player(harness, 3, WarriorBeginner, AccountNation.Karus, Moradon, 400, 400);
        PartyUp(harness.Sessions, mage, member);

        await harness.Magic.CastAsync(client, SummonFriend, mage.CharacterId, stranger.CharacterId);
        stranger.X.Should().Be(300, "only a party member can be summoned");

        member.CanTeleport = false;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, SummonFriend, mage.CharacterId, member.CharacterId);
        member.X.Should().Be(400, "a no-recall curse pins the member in place");

        mage.CanTeleport = false;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Escape, mage.CharacterId, mage.CharacterId);
        mage.X.Should().Be(100);
    }

    [Fact]
    public async Task ASkillNeedsItsWeaponAndItsHealthCost()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[Bash] = new MagicData { Id = Bash, Type1 = 4, Moral = 1, ItemGroup = 0 };
        harness.Data.Type4[Bash] = new MagicType4Data { Id = Bash, BuffType = (byte)BuffType.Damage, Attack = 110, Duration = 30 };
        harness.Data.Magic[Frenzy] = new MagicData { Id = Frenzy, Type1 = 4, Moral = 1, Hp = 50, ItemGroup = NoWeaponNeeded };
        harness.Data.Type4[Frenzy] = new MagicType4Data { Id = Frenzy, BuffType = (byte)BuffType.AttackSpeed, AttackSpeed = 120, Duration = 30 };
        harness.Data.Items[Sword] = new ItemData { Num = Sword, Kind = (byte)ItemKind.SwordOneHand, Duration = 100 };
        var (warrior, client, _) = Player(harness, 1, WarriorNovice, AccountNation.Karus, Moradon, 100, 100);

        await harness.Magic.CastAsync(client, Bash, warrior.CharacterId, warrior.CharacterId);
        warrior.ActiveBuffs.Should().NotContainKey(Bash, "the skill needs a weapon in hand");

        warrior.Inventory[InventoryConstants.RightHand].ItemId = Sword;
        warrior.Inventory[InventoryConstants.RightHand].Durability = 100;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Bash, warrior.CharacterId, warrior.CharacterId);
        warrior.ActiveBuffs.Should().ContainKey(Bash);

        warrior.Hp = 50;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Frenzy, warrior.CharacterId, warrior.CharacterId);
        warrior.ActiveBuffs.Should().NotContainKey(Frenzy, "paying 50 health would kill the caster");

        warrior.Hp = 200;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Frenzy, warrior.CharacterId, warrior.CharacterId);
        warrior.ActiveBuffs.Should().ContainKey(Frenzy);
        warrior.Hp.Should().Be(150);
    }

    [Fact]
    public async Task AGoldHealNeedsTheGold()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[GoldHeal] = new MagicData
        {
            Id = GoldHeal, Type1 = 3, Moral = 1, UseItem = GoldHealToken, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type3[GoldHeal] = new MagicType3Data
        {
            Id = GoldHeal, DirectType = (byte)MagicDirectType.HealthPurchase, FirstDamage = 300, TimeDamage = GoldHealPrice
        };
        harness.Data.Items[GoldHealToken] = Consumable(GoldHealToken);
        var (warrior, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        Carry(warrior, GoldHealToken, 2);
        warrior.Hp = 100;
        warrior.Money = GoldHealPrice - 1;

        await harness.Magic.CastAsync(client, GoldHeal, warrior.CharacterId, warrior.CharacterId);
        warrior.Hp.Should().Be(100);
        warrior.Money.Should().Be(GoldHealPrice - 1);

        warrior.Money = GoldHealPrice;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, GoldHeal, warrior.CharacterId, warrior.CharacterId);
        warrior.Hp.Should().Be(400);
        warrior.Money.Should().Be(0);
    }

    [Fact]
    public async Task CastingStandsThePlayerUpAndOnlyTheServersDataIsRebroadcast()
    {
        using var harness = CreateHarness();
        GuardSkill(harness.Data);
        var (warrior, client, sent) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        warrior.IsSitting = true;

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, Guard, warrior.CharacterId, warrior.CharacterId,
            111, 222, 333, 999, 5, 6, 7);

        warrior.IsSitting.Should().BeFalse();
        var casting = ReadMagicProcessPacket(sent.Single(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MAGIC_PROCESS));
        casting.ProcessOpcode.Should().Be(MagicProcessOpcode.Casting);
        casting.Data.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public async Task SittingRegenPausesRightAfterAFightAndSkipsTheDead()
    {
        using var harness = CreateHarness();
        var (fighter, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        var (corpse, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 101, 100);
        fighter.Hp = 100;
        corpse.Hp = 0;
        corpse.IsSitting = true;
        var regen = new HpMpRegenService(
            harness.Sessions,
            harness.Provider.GetRequiredService<ICombatNotificationService>(),
            harness.Clock,
            harness.Provider.GetRequiredService<ILogger<HpMpRegenService>>());

        await harness.Combat.HandleAttackAsync(client, Swing(worm.UniqueId));
        fighter.IsSitting = true;
        await regen.ProcessTickAsync(CancellationToken.None);
        fighter.Hp.Should().Be(100, "a player who just swung is not resting");
        corpse.Hp.Should().Be(0);

        harness.Clock.Advance(TimeSpan.FromSeconds(VitalsRegenCalculator.IntervalSeconds));
        await regen.ProcessTickAsync(CancellationToken.None);
        fighter.Hp.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task AMonsterKilledByADotWhileEveryDamagerIsOfflineStillDies()
    {
        using var harness = CreateHarness();
        var worm = Monster(harness, Moradon, 100, 100);
        worm.Hp = 10;
        worm.ActiveOverTimeEffects[Nuke] = new ActiveOverTimeEffect
        {
            MagicId = Nuke, CasterId = 424242, TickAmount = -50, TickLimit = 5, NextTickTicks = DateTime.UtcNow.Ticks
        };
        var expiry = new BuffExpiryService(
            harness.Sessions,
            harness.Provider.GetRequiredService<IMagicExecutionService>(),
            harness.Provider.GetRequiredService<ICombatLifecycleService>(),
            harness.Provider.GetRequiredService<ICombatNotificationService>(),
            harness.Provider.GetRequiredService<ILogger<BuffExpiryService>>());

        await expiry.ProcessTickAsync(DateTime.UtcNow.Ticks);

        worm.IsDead.Should().BeTrue("it must leave the world and come back through the respawn timer");
        worm.State.Should().Be(NpcState.Dead);
    }

    [Fact]
    public async Task ABoltsFlightIsRebroadcastForItsBoundCastAndChargesNothing()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        harness.Data.Magic[Nuke].Msp = NukeMana;
        var (mage, client, sent) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 105, 100);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Flying, Nuke, mage.CharacterId, worm.UniqueId);
        Replies(sent).Should().Equal(MagicProcessOpcode.Fail);
        sent.Clear();

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, Nuke, mage.CharacterId, worm.UniqueId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Flying, Nuke, mage.CharacterId, worm.UniqueId);
        Replies(sent).Should().Equal(MagicProcessOpcode.Casting, MagicProcessOpcode.Flying);
        mage.Mp.Should().Be(mage.MaxMp);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, Nuke, mage.CharacterId, worm.UniqueId);
        worm.Hp.Should().BeLessThan(worm.MaxHp);
        mage.Mp.Should().Be((short)(mage.MaxMp - NukeMana));
    }

    [Fact]
    public async Task ABoltFliesOnlyOncePerCast()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, sent) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 105, 100);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, Nuke, mage.CharacterId, worm.UniqueId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Flying, Nuke, mage.CharacterId, worm.UniqueId);
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Flying, Nuke, mage.CharacterId, worm.UniqueId);

        Replies(sent).Should().Equal(MagicProcessOpcode.Casting, MagicProcessOpcode.Flying, MagicProcessOpcode.Fail);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, Nuke, mage.CharacterId, worm.UniqueId);
        worm.Hp.Should().BeLessThan(worm.MaxHp, "a refused repeat flight leaves the cast itself intact");
    }

    [Fact]
    public async Task ATargetedAreaSpellKeepsACentreWithinItsRange()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var target = Monster(harness, Moradon, 110, 100);
        var bystander = Monster(harness, Moradon, 113, 100);

        await harness.Magic.CastAsync(client, AreaNuke, mage.CharacterId, target.UniqueId, 111, 0, 100);

        target.Hp.Should().BeLessThan(target.MaxHp);
        bystander.Hp.Should().BeLessThan(bystander.MaxHp);
    }

    [Fact]
    public async Task ATargetedAreaSpellDropsACentreBeyondItsRange()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var target = Monster(harness, Moradon, 110, 100);
        var distant = Monster(harness, Moradon, 160, 100);

        await harness.Magic.CastAsync(client, AreaNuke, mage.CharacterId, target.UniqueId, 160, 0, 100);

        target.Hp.Should().BeLessThan(target.MaxHp);
        distant.Hp.Should().Be(distant.MaxHp);
    }

    [Fact]
    public async Task ARefusedReleaseHandsBackTheCooldown()
    {
        using var harness = CreateHarness();
        NukeSkills(harness.Data);
        harness.Data.Magic[Nuke].ReCastTime = NukeRecast;
        var (mage, client, _) = Player(harness, 1, MageMaster, AccountNation.Karus, Moradon, 100, 100);
        var worm = Monster(harness, Moradon, 105, 100);

        await harness.Magic.SendAsync(client, MagicProcessOpcode.Casting, Nuke, mage.CharacterId, worm.UniqueId);
        worm.X = 200;
        await harness.Magic.SendAsync(client, MagicProcessOpcode.Effecting, Nuke, mage.CharacterId, worm.UniqueId);
        worm.Hp.Should().Be(worm.MaxHp, "the target walked out of range");
        mage.SkillCooldowns.Should().NotContainKey(Nuke);

        worm.X = 105;
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Nuke, mage.CharacterId, worm.UniqueId);
        worm.Hp.Should().BeLessThan(worm.MaxHp);
    }

    [Fact]
    public async Task AResurrectionIsRefusedWhileAnotherIsUnderwayAndChargesNothing()
    {
        using var harness = CreateHarness();
        ResurrectSkill(harness.Data);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        var (corpse, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, Moradon, 102, 100);
        corpse.Hp = 0;
        corpse.TryClaimRevival().Should().BeTrue();

        await harness.Magic.CastAsync(client, Resurrect, priest.CharacterId, corpse.CharacterId);
        corpse.Hp.Should().Be(0);
        priest.Mp.Should().Be(priest.MaxMp);

        corpse.ReleaseRevival();
        harness.Clock.Advance(BurstFloor);
        await harness.Magic.CastAsync(client, Resurrect, priest.CharacterId, corpse.CharacterId);
        corpse.Hp.Should().Be(corpse.MaxHp);
        priest.Mp.Should().Be((short)(priest.MaxMp - ResurrectMana));
    }

    [Fact]
    public async Task ARegeneWaitsForAResurrectionThatIsUnderway()
    {
        using var harness = CreateHarness();
        var (corpse, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        corpse.Hp = 0;
        corpse.TryClaimRevival();
        var lifecycle = harness.Provider.GetRequiredService<ICombatLifecycleService>();

        await lifecycle.HandleRegeneAsync(client, corpse, AnyRegene);
        corpse.Hp.Should().Be(0);

        corpse.ReleaseRevival();
        await lifecycle.HandleRegeneAsync(client, corpse, AnyRegene);
        corpse.Hp.Should().Be(corpse.MaxHp);
    }

    [Fact]
    public async Task ARegeneHoldsTheCorpseSoNoResurrectionLandsMidway()
    {
        using var harness = CreateHarness();
        var (corpse, client, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        corpse.Hp = 0;
        bool? claimedMidway = null;
        client.SendPacket(Arg.Is<Packet>(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_REGENE), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                claimedMidway = corpse.TryClaimRevival();
                return Task.CompletedTask;
            });

        await harness.Provider.GetRequiredService<ICombatLifecycleService>().HandleRegeneAsync(client, corpse, AnyRegene);

        claimedMidway.Should().BeFalse("the respawn owns the corpse until it stands up");
        corpse.Hp.Should().Be(corpse.MaxHp);
        corpse.Hp = 0;
        corpse.TryClaimRevival().Should().BeTrue("the claim is handed back once the respawn is done");
    }

    [Fact]
    public async Task AResurrectionWithoutItsStonesChargesNothingAndFreesTheCorpse()
    {
        using var harness = CreateHarness();
        ResurrectSkill(harness.Data);
        harness.Data.Magic[Resurrect].UseItem = RevivalStone;
        harness.Data.Type5[Resurrect].NeedStone = RequiredStones;
        harness.Data.Items[RevivalStone] = Consumable(RevivalStone);
        var (priest, client, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        var (corpse, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, Moradon, 102, 100);
        corpse.Hp = 0;

        await harness.Magic.CastAsync(client, Resurrect, priest.CharacterId, corpse.CharacterId);

        corpse.Hp.Should().Be(0);
        priest.Mp.Should().Be(priest.MaxMp);
        corpse.TryClaimRevival().Should().BeTrue();
    }

    [Fact]
    public async Task ABuffNeverLandsOnACorpse()
    {
        using var harness = CreateHarness();
        harness.Data.Magic[Blessing] = new MagicData
        {
            Id = Blessing, Type1 = 4, Moral = (byte)SkillMoral.FriendWithMe, Range = 25, ItemGroup = NoWeaponNeeded
        };
        harness.Data.Type4[Blessing] = new MagicType4Data { Id = Blessing, BuffType = (byte)BuffType.Ac, Ac = 50, Duration = 60 };
        var (priest, _, _) = Player(harness, 1, PriestNovice, AccountNation.Karus, Moradon, 100, 100);
        var (corpse, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.Karus, Moradon, 102, 100);
        corpse.Hp = 0;

        await harness.Provider.GetRequiredService<IMagicStatusEffectService>().ExecuteAsync(
            priest, harness.Data.Magic[Blessing], MagicSkillType.Buff, Blessing, corpse.CharacterId,
            new int[MagicProcessPacketWriter.PayloadSlotCount], MagicCharge.Prepaid);

        corpse.ActiveBuffs.Should().BeEmpty();
    }

    [Fact]
    public async Task APvpDeathClosesTheVictimsStall()
    {
        using var harness = CreateHarness();
        var (victim, _, _) = Player(harness, 1, WarriorBeginner, AccountNation.Karus, Moradon, 100, 100);
        var (killer, _, _) = Player(harness, 2, WarriorBeginner, AccountNation.ElMorad, Moradon, 102, 100);
        victim.Trade.MerchantState = MerchantMode.Selling;
        victim.Hp = 0;

        await harness.Provider.GetRequiredService<ICombatLifecycleService>().HandlePlayerDeathAsync(victim, killer);

        victim.Trade.IsMerchanting.Should().BeFalse();
    }

    private static Harness CreateHarness()
    {
        var clock = new ManualClock();
        var catalog = new Catalog();
        var provider = CreateProvider(
            _ => { },
            gameData =>
            {
                gameData.GetMagic(Arg.Any<int>()).Returns(call => catalog.Magic.GetValueOrDefault(call.Arg<int>()));
                gameData.GetItem(Arg.Any<int>()).Returns(call => catalog.Items.GetValueOrDefault(call.Arg<int>()));
                gameData.MagicType1Table.Returns(new Dictionary<int, MagicType1Data>());
                gameData.MagicType2Table.Returns(catalog.Type2);
                gameData.MagicType3Table.Returns(catalog.Type3);
                gameData.MagicType4Table.Returns(catalog.Type4);
                gameData.MagicType5Table.Returns(catalog.Type5);
                gameData.MagicType6Table.Returns(new Dictionary<int, MagicType6Data>());
                gameData.MagicType7Table.Returns(new Dictionary<int, MagicType7Data>());
                gameData.MagicType8Table.Returns(catalog.Type8);
                gameData.MagicType9Table.Returns(catalog.Type9);
                gameData.GetStartPosition(Moradon).Returns(new StartPositionData
                {
                    ZoneId = Moradon, KarusX = 700, KarusZ = 700, ElmoradX = 700, ElmoradZ = 700
                });
            },
            configureServices: services => services.AddSingleton<TimeProvider>(clock));
        return new Harness(provider, clock, catalog);
    }

    private static void GuardSkill(Catalog data)
    {
        data.Magic[Guard] = new MagicData
        {
            Id = Guard, Type1 = 4, Moral = 1, ReCastTime = 100, Msp = 10, ItemGroup = NoWeaponNeeded
        };
        data.Type4[Guard] = new MagicType4Data { Id = Guard, BuffType = (byte)BuffType.Ac, Ac = 50, Duration = 60 };
    }

    private static void ResurrectSkill(Catalog data)
    {
        data.Magic[Resurrect] = new MagicData
        {
            Id = Resurrect, Type1 = 5, Moral = (byte)SkillMoral.CorpseFriend, Range = 25, Msp = ResurrectMana,
            ItemGroup = NoWeaponNeeded
        };
        data.Type5[Resurrect] = new MagicType5Data { Id = Resurrect, Type = (byte)SpecialMagicType.Resurrection };
    }

    private static MagicData HealRow(int id, int castTenths) => new()
    {
        Id = id, Type1 = 3, Moral = 2, Range = 25, Msp = 20, CastTime = (byte)castTenths, ItemGroup = NoWeaponNeeded
    };

    private static void HealSkills(Catalog data)
    {
        data.Magic[Heal] = HealRow(Heal, castTenths: 0);
        data.Magic[SlowHeal] = HealRow(SlowHeal, castTenths: 15);
        data.Type3[Heal] = new MagicType3Data { Id = Heal, DirectType = (byte)MagicDirectType.Health, FirstDamage = 100 };
        data.Type3[SlowHeal] = new MagicType3Data { Id = SlowHeal, DirectType = (byte)MagicDirectType.Health, FirstDamage = 100 };
        data.Type3[PriestGreaterHeal] = new MagicType3Data { Id = PriestGreaterHeal, DirectType = (byte)MagicDirectType.Health, FirstDamage = 100 };
    }

    private static void NukeSkills(Catalog data)
    {
        data.Magic[Nuke] = new MagicData { Id = Nuke, Type1 = 3, Moral = 7, Range = 25, ItemGroup = NoWeaponNeeded };
        data.Magic[AreaNuke] = new MagicData { Id = AreaNuke, Type1 = 3, Moral = 10, Range = 25, ItemGroup = NoWeaponNeeded };
        data.Magic[Curse] = new MagicData { Id = Curse, Type1 = 4, Moral = 7, Range = 25, ItemGroup = NoWeaponNeeded };
        data.Type3[Nuke] = new MagicType3Data { Id = Nuke, DirectType = (byte)MagicDirectType.Health, FirstDamage = -300 };
        data.Type3[AreaNuke] = new MagicType3Data
        {
            Id = AreaNuke, DirectType = (byte)MagicDirectType.Health, FirstDamage = -300, Radius = 5
        };
        data.Type4[Curse] = new MagicType4Data { Id = Curse, BuffType = (byte)BuffType.Speed, Speed = 50, Duration = 30 };
    }

    private static ItemData Consumable(int itemId) => new()
    {
        Num = itemId,
        Countable = 1,
        Duration = 1,
        ReqLevelMax = 83
    };

    private static void Carry(UserSession session, int itemId, ushort count = 1)
    {
        session.Inventory[InventoryConstants.InventoryStart].ItemId = itemId;
        session.Inventory[InventoryConstants.InventoryStart].Count = count;
        session.Inventory[InventoryConstants.InventoryStart].Durability = 1;
    }

    private static (UserSession Session, IClient Client, List<Packet> Sent) Player(
        Harness harness, int characterId, short classId, AccountNation nation, byte zone, float x, float z)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var session = harness.Sessions.CreateSession(client, characterId, characterId + 100);
        session.Name = $"Player{characterId}";
        session.Class = classId;
        session.Level = 60;
        session.Nation = nation;
        session.ZoneId = zone;
        session.X = x;
        session.Z = z;
        session.MaxHp = 1000;
        session.Hp = 1000;
        session.MaxMp = 1000;
        session.Mp = 1000;
        session.Stats.TotalHit = 300;
        session.Stats.TotalHitrate = 100;
        harness.Sessions.Regions.AddToRegion(session);
        return (session, client, sent);
    }

    private static NpcInstance Monster(Harness harness, byte zone, float x, float z) =>
        harness.Sessions.Regions.SpawnNpc(new NpcInstance
        {
            IsMonster = true,
            NpcId = 750,
            Name = "Worm",
            ZoneId = zone,
            X = x,
            Z = z,
            SpawnX = x,
            SpawnZ = z,
            Hp = 100000,
            MaxHp = 100000,
            Ac = 0,
            EvadeRate = 1
        });

    private static void PartyUp(SessionManager sessions, UserSession leader, UserSession member)
    {
        var party = sessions.Parties.CreateParty((short)leader.CharacterId);
        party.MemberIds[1] = (short)member.CharacterId;
        leader.PartyIndex = party.Index;
        leader.IsPartyLeader = true;
        member.PartyIndex = party.Index;
    }

    private static Packet Swing(int targetId, byte critical = 0)
    {
        var packet = new Packet(GameOpcodes.GS_ATTACK);
        packet.WriteByte(AttackPacketWriter.TypeMelee);
        packet.WriteByte(0);
        packet.WriteInt(targetId);
        packet.WriteShort(100);
        packet.WriteShort(0);
        packet.WriteByte(critical);
        packet.WriteByte(0);
        return packet;
    }

    private static Packet TargetHpPoll(int targetId)
    {
        var packet = new Packet(GameOpcodes.GS_TARGET_HP);
        packet.WriteInt(targetId);
        packet.WriteByte(TargetHpPacketWriter.EchoPoll);
        return packet;
    }

    private static int Attacks(List<Packet> sent) =>
        sent.Count(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_ATTACK);

    private static AttackResult LastAttackResult(List<Packet> sent) =>
        (AttackResult)sent.Last(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_ATTACK).GetBytes()[2];

    private static List<MagicProcessOpcode> Replies(List<Packet> sent) => sent
        .Where(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MAGIC_PROCESS)
        .Select(packet => (MagicProcessOpcode)packet.GetBytes()[1])
        .ToList();
}
