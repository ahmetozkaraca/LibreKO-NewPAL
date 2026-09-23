using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class ClanWarehouseTests : GameTestBase
{
    private const short ClanId = 700;
    private const int MemberId = 9100;
    private const string MemberName = "Banker";
    private const int CrateId = 389010000;
    private const int UntradeableId = 389020000;
    private const byte RaceTradeable = 1;
    private const byte RaceUntradeable = 20;
    private const byte Zone = 21;
    private const float Here = 100f;
    private const float KeeperOffset = 2f;
    private const float FarAway = 60f;
    private const int ClanBankNpcId = 19999;
    private const byte LeaderFame = 1;
    private const byte TraineeFame = 5;
    private const int StartingMoney = 50_000;
    private const int GoldDeposit = 1_500;
    private const ushort CrateCount = 7;
    private const ushort ArrivedMeanwhile = 3;
    private const byte FirstSlot = 0;
    private const byte SecondSlot = 1;

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, FarAway)]
    public async Task Deposit_RequiresAWarehouseKeeperWithinReach(bool keeperClicked, float walkedAway)
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame, keeperClicked);
        member.X += walkedAway;
        var bag = Stock(member, CrateId, CrateCount);

        await Route(provider, member, Deposit(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Failed);
        bag.Count.Should().Be(CrateCount);
    }

    [Theory]
    [InlineData(ItemFlag.Bound)]
    [InlineData(ItemFlag.Sealed)]
    [InlineData(ItemFlag.Rented)]
    [InlineData(ItemFlag.Duplicate)]
    [InlineData(ItemFlag.CharacterSeal)]
    public async Task Deposit_RefusesItemsThatCannotChangeHands(ItemFlag flag)
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        var bag = Stock(member, CrateId, CrateCount);
        bag.Flag = (byte)flag;

        await Route(provider, member, Deposit(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Failed);
        bag.Count.Should().Be(CrateCount);
    }

    [Fact]
    public async Task Deposit_RefusesAnItemItsTableMarksUntradeable()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        var bag = Stock(member, UntradeableId, 1);

        await Route(provider, member, Deposit(UntradeableId, 1));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Failed);
        bag.ItemId.Should().Be(UntradeableId);
    }

    [Fact]
    public async Task Deposit_CommitsTheInventoryWithTheClanWarehouse()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        Stock(member, CrateId, CrateCount);

        await Route(provider, member, Deposit(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Succeeded);
        var character = await StoredCharacterAsync(provider);
        StoredSlots(character.Items, InventoryConstants.InventoryTotal)[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
        var clan = await StoredClanAsync(provider);
        StoredSlots(clan.ClanWarehouseItems, KnightsManager.ClanWarehouseSlots)[0].Count.Should().Be(CrateCount);
    }

    [Fact]
    public async Task GoldDeposit_CommitsTheMoneyWithTheClanGold()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);

        await Route(provider, member, Deposit(InventoryConstants.ItemGold, GoldDeposit));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Succeeded);
        (await StoredCharacterAsync(provider)).Money.Should().Be(StartingMoney - GoldDeposit);
        (await StoredClanAsync(provider)).ClanWarehouseGold.Should().Be(GoldDeposit);
    }

    [Fact]
    public async Task Deposit_RefusesWhileTheInventoryIsLocked()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        var bag = Stock(member, CrateId, CrateCount);
        member.Trade.ExchangeUser = MemberId + 1;
        member.Trade.ExchangeStarted = true;

        await Route(provider, member, Deposit(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Failed);
        bag.Count.Should().Be(CrateCount);
    }

    [Fact]
    public async Task Withdraw_IsLimitedToTheLeaderAndAssistants()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, TraineeFame);
        var vault = provider.GetRequiredService<SessionManager>().Knights.GetClanWarehouse(ClanId)!;
        vault[0].ItemId = CrateId;
        vault[0].Count = CrateCount;

        await Route(provider, member, Withdraw(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Output).Should().Be((byte)ClanWarehouseResult.Failed);
        vault[0].Count.Should().Be(CrateCount);
        member.Inventory[InventoryConstants.InventoryStart].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task AClanUpdateElsewhereDoesNotRevertTheClanWarehouse()
    {
        using var provider = Provider();
        var (member, _) = Member(provider, LeaderFame);
        await Route(provider, member, Deposit(InventoryConstants.ItemGold, GoldDeposit));

        await using (var scope = provider.CreateAsyncScope())
        {
            var stale = new KnightsEntity { Id = ClanId, Name = "Keepers", Chief = MemberName, Notice = "Rally at dusk" };
            await scope.ServiceProvider.GetRequiredService<IKnightsRepository>().UpdateAsync(stale);
        }

        var clan = await StoredClanAsync(provider);
        clan.Notice.Should().Be("Rally at dusk");
        clan.ClanWarehouseGold.Should().Be(GoldDeposit);
    }

    [Fact]
    public async Task Deposit_PutsBothSidesBackWhenTheCommitFails()
    {
        var probe = new SaveChangesProbe
        {
            FailWhen = db => db.ChangeTracker.Entries<KnightsEntity>().Any(entry => entry.State == EntityState.Modified),
        };
        using var provider = Provider(probe);
        var (member, sent) = Member(provider, LeaderFame);
        var bag = Stock(member, CrateId, CrateCount);

        await Route(provider, member, Deposit(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Input).Should().Be((byte)ClanWarehouseResult.Failed);
        bag.ItemId.Should().Be(CrateId);
        bag.Count.Should().Be(CrateCount);
        provider.GetRequiredService<SessionManager>().Knights.GetClanWarehouse(ClanId)![0].IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Withdraw_UndoKeepsWhatArrivedDuringTheCommit()
    {
        var probe = new SaveChangesProbe();
        using var provider = Provider(probe);
        var (member, sent) = Member(provider, LeaderFame);
        var vault = provider.GetRequiredService<SessionManager>().Knights.GetClanWarehouse(ClanId)!;
        vault[0].ItemId = CrateId;
        vault[0].Count = CrateCount;
        var bag = member.Inventory[InventoryConstants.InventoryStart];
        probe.BeforeSave = _ =>
        {
            member.WithLock(s => s.Inventory[InventoryConstants.InventoryStart].Count += ArrivedMeanwhile);
            return Task.CompletedTask;
        };
        probe.FailWhen = _ => true;

        await Route(provider, member, Withdraw(CrateId, CrateCount));

        Result(sent, WarehouseSubOpcode.Output).Should().Be((byte)ClanWarehouseResult.Failed);
        vault[0].Count.Should().Be(CrateCount);
        bag.ItemId.Should().Be(CrateId);
        bag.Count.Should().Be(ArrivedMeanwhile);
    }

    [Fact]
    public async Task Move_ShiftsAVaultSlotAndCommitsIt()
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        var vault = provider.GetRequiredService<SessionManager>().Knights.GetClanWarehouse(ClanId)!;
        vault[FirstSlot].ItemId = CrateId;
        vault[FirstSlot].Count = CrateCount;

        await Route(provider, member, Shift(WarehouseSubOpcode.Move, CrateId, FirstSlot, SecondSlot));

        Result(sent, WarehouseSubOpcode.Move).Should().Be((byte)ClanWarehouseResult.Succeeded);
        vault[FirstSlot].IsEmpty.Should().BeTrue();
        vault[SecondSlot].Count.Should().Be(CrateCount);
        var stored = StoredSlots((await StoredClanAsync(provider)).ClanWarehouseItems, KnightsManager.ClanWarehouseSlots);
        stored[SecondSlot].ItemId.Should().Be(CrateId);
    }

    [Theory]
    [InlineData(LeaderFame, true)]
    [InlineData(TraineeFame, false)]
    public async Task Move_IsLimitedToTheLeaderAndAssistants(byte fame, bool moved)
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, fame);
        var vault = provider.GetRequiredService<SessionManager>().Knights.GetClanWarehouse(ClanId)!;
        vault[FirstSlot].ItemId = CrateId;
        vault[FirstSlot].Count = CrateCount;

        await Route(provider, member, Shift(WarehouseSubOpcode.Move, CrateId, FirstSlot, SecondSlot));

        Result(sent, WarehouseSubOpcode.Move).Should().Be(moved ? (byte)ClanWarehouseResult.Succeeded : (byte)ClanWarehouseResult.Failed);
        vault[SecondSlot].IsEmpty.Should().Be(!moved);
    }

    [Theory]
    [InlineData(CrateId, true)]
    [InlineData(UntradeableId, false)]
    public async Task InventoryMove_OnlyMovesTheNamedItemIntoAnEmptySlot(int namedItem, bool moved)
    {
        using var provider = Provider();
        var (member, sent) = Member(provider, LeaderFame);
        Stock(member, CrateId, CrateCount);

        await Route(provider, member, Shift(WarehouseSubOpcode.InventoryMove, namedItem, FirstSlot, SecondSlot));

        Result(sent, WarehouseSubOpcode.InventoryMove).Should().Be(moved ? (byte)ClanWarehouseResult.Succeeded : (byte)ClanWarehouseResult.Failed);
        member.Inventory[InventoryConstants.InventoryStart + SecondSlot].IsEmpty.Should().Be(!moved);
        member.Inventory[InventoryConstants.InventoryStart + FirstSlot].IsEmpty.Should().Be(moved);
    }

    private static Packet Shift(WarehouseSubOpcode sub, int itemId, byte source, byte destination)
    {
        var packet = new Packet(GameOpcodes.GS_CLAN_WAREHOUSE);
        packet.WriteByte((byte)sub);
        packet.WriteInt(0);
        packet.WriteInt(itemId);
        packet.WriteByte(0);
        packet.WriteByte(source);
        packet.WriteByte(destination);
        return packet;
    }

    private static ServiceProvider Provider(SaveChangesProbe? probe = null) => CreateProvider(
        db =>
        {
            db.Characters.Add(new Character
            {
                Id = MemberId,
                AccountId = MemberId,
                Name = MemberName,
                Money = StartingMoney,
                KnightsId = ClanId,
                Items = SavedBag(CrateId, CrateCount),
            });
            db.Knights.Add(new KnightsEntity { Id = ClanId, Name = "Keepers", Chief = MemberName });
        },
        gameData =>
        {
            gameData.GetItem(CrateId).Returns(new ItemData { Num = CrateId, Race = RaceTradeable, Countable = 1, Weight = 1 });
            gameData.GetItem(UntradeableId).Returns(new ItemData { Num = UntradeableId, Race = RaceUntradeable, Weight = 1 });
        },
        configureServices: services => probe?.Register(services));

    private static (UserSession Session, List<Packet> Sent) Member(ServiceProvider provider, byte fame, bool keeperClicked = true)
    {
        var sessionManager = provider.GetRequiredService<SessionManager>();
        sessionManager.Knights.AddClan(ClanId, new KnightsEntity { Id = ClanId, Name = "Keepers", Chief = MemberName });

        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(sent.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var session = sessionManager.CreateSession(client, MemberId, MemberId);
        session.Name = MemberName;
        session.Money = StartingMoney;
        session.MaxHp = 100;
        session.Hp = 100;
        session.ZoneId = Zone;
        session.X = Here;
        session.Z = Here;
        session.KnightsId = ClanId;
        session.KnightsFame = fame;

        var keeper = sessionManager.Regions.SpawnNpc(new NpcInstance
        {
            NpcId = ClanBankNpcId,
            Name = "Clan Bank",
            NpcType = NpcData.TypeWarehouse,
            ZoneId = Zone,
            X = Here + KeeperOffset,
            Z = Here,
            Hp = 100,
            MaxHp = 100,
        });
        if (keeperClicked)
            session.Quest.EventNpcUniqueId = keeper.UniqueId;
        return (session, sent);
    }

    private static ItemSlot Stock(UserSession session, int itemId, ushort count)
    {
        var slot = session.Inventory[InventoryConstants.InventoryStart];
        slot.ItemId = itemId;
        slot.Count = count;
        slot.Durability = 1;
        return slot;
    }

    private static Packet Deposit(int itemId, int count) => Transfer(WarehouseSubOpcode.Input, itemId, count);

    private static Packet Withdraw(int itemId, int count) => Transfer(WarehouseSubOpcode.Output, itemId, count);

    private static Packet Transfer(WarehouseSubOpcode sub, int itemId, int count)
    {
        var packet = new Packet(GameOpcodes.GS_CLAN_WAREHOUSE);
        packet.WriteByte((byte)sub);
        packet.WriteInt(0);
        packet.WriteInt(itemId);
        packet.WriteByte(0);
        packet.WriteByte(0);
        packet.WriteByte(0);
        packet.WriteInt(count);
        return packet;
    }

    private static Task Route(ServiceProvider provider, UserSession session, Packet packet)
    {
        packet.ResetOffset();
        return provider.GetRequiredService<IClanWarehousePacketCoordinator>().HandleAsync(session.Client, packet);
    }

    private static byte Result(List<Packet> sent, WarehouseSubOpcode sub)
    {
        var packet = sent.Last(p => p.GetOpcode() == (byte)GameOpcodes.GS_CLAN_WAREHOUSE && p.GetData()[0] == (byte)sub);
        packet.ResetOffset();
        packet.ReadByte();
        return packet.ReadByte();
    }

    private static async Task<Character> StoredCharacterAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Characters
            .AsNoTracking().SingleAsync(c => c.Id == MemberId);
    }

    private static async Task<KnightsEntity> StoredClanAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Knights
            .AsNoTracking().SingleAsync(k => k.Id == ClanId);
    }

    private static byte[] SavedBag(int itemId, ushort count)
    {
        var slots = Enumerable.Range(0, InventoryConstants.InventoryTotal).Select(_ => new ItemSlot()).ToArray();
        slots[InventoryConstants.InventoryStart].ItemId = itemId;
        slots[InventoryConstants.InventoryStart].Count = count;
        return UserSessionBinaryState.SerializeItems(slots);
    }

    private static ItemSlot[] StoredSlots(byte[] blob, int length)
    {
        var slots = Enumerable.Range(0, length).Select(_ => new ItemSlot()).ToArray();
        UserSessionBinaryState.LoadSlots(slots, blob);
        return slots;
    }
}
