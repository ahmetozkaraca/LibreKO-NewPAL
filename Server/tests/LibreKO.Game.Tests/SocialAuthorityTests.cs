using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using LibreKO.Game.Scripting;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class SocialAuthorityTests : GameTestBase
{
    private const byte MarketRegister = 1;
    private const byte MarketOpen = 4;
    private const byte AllAds = 0;
    private const byte Selling = 1;
    private const int AdvertisedItem = 110_110_001;
    private const byte FriendReport = 2;
    private const short QuestId = 500;
    private const int AchievementId = 42;
    private const int AchievementReward = 389_010_000;

    [Fact]
    public async Task AWhisperIsRefusedOnceTheTargetBlocksWhispers()
    {
        using var provider = CreateProvider(_ => { });
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var (sender, senderClient, _) = CreatePlayer(sessionManager, 1200, "Sender");
        var (target, targetClient, targetSent) = CreatePlayer(sessionManager, 1201, "Target");
        var social = provider.GetRequiredService<ISocialPacketCoordinator>();

        await social.HandleChatTargetAsync(senderClient, ChatTarget(ChatTargetPacketWriter.TypeWhisper, "Target"));
        await social.HandleChatTargetAsync(targetClient, BlockWhispers());
        targetSent.Clear();

        await provider.GetRequiredService<IChatPacketCoordinator>()
            .HandleAsync(sender, (byte)ChatType.Private, "hello");

        target.BlockPrivateChat.Should().BeTrue();
        targetSent.Should().NotContain(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT);
    }

    [Fact]
    public async Task ASellerKeepsOnlyAFewAdsOnTheMarketBoard()
    {
        using var provider = CreateProvider(_ => { }, configureServices: services => services.AddSingleton<TimeProvider>(new ManualClock()));
        var (_, client, sent) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 1202, "Seller");
        var misc = provider.GetRequiredService<IMiscPacketCoordinator>();

        for (var ad = 0; ad < 10; ad++)
            await misc.HandleMarketBbsAsync(client, RegisterAd(count: 1));

        sent.Count(IsAcceptedRegistration).Should().BeLessThan(10);
        sent.Count(IsAcceptedRegistration).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MarketAdsExpireAndRejectEmptyStacks()
    {
        var clock = new ManualClock();
        using var provider = CreateProvider(_ => { }, configureServices: services => services.AddSingleton<TimeProvider>(clock));
        var (_, client, sent) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 1203, "Seller");
        var misc = provider.GetRequiredService<IMiscPacketCoordinator>();

        await misc.HandleMarketBbsAsync(client, RegisterAd(count: 0));
        sent.Count(IsAcceptedRegistration).Should().Be(0);

        await misc.HandleMarketBbsAsync(client, RegisterAd(count: 5));
        sent.Count(IsAcceptedRegistration).Should().Be(1);

        clock.Advance(TimeSpan.FromDays(8));
        sent.Clear();
        await misc.HandleMarketBbsAsync(client, OpenBoard());

        var list = sent.Single(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_MARKET_BBS);
        list.ResetOffset();
        list.ReadByte().Should().Be(MarketOpen);
        list.ReadByte().Should().Be(MarketBbsPacketWriter.Succeeded);
        list.ReadUShort().Should().Be(0);
    }

    [Fact]
    public async Task AFriendReportOnlyDescribesRealFriends()
    {
        using var provider = CreateProvider(db =>
        {
            db.Characters.AddRange(
                new Character { Id = 1204, AccountId = 1, Name = "Me", MapId = 1 },
                new Character { Id = 1205, AccountId = 2, Name = "Friend", MapId = 1 },
                new Character { Id = 1206, AccountId = 3, Name = "Stranger", MapId = 1 });
            db.Friendships.Add(new Friendship { CharacterId = 1204, FriendCharacterId = 1205, AddedAt = DateTime.UtcNow });
        });
        var sessionManager = provider.GetRequiredService<SessionManager>();
        var (_, client, sent) = CreatePlayer(sessionManager, 1204, "Me");
        CreatePlayer(sessionManager, 1206, "Stranger");

        var report = new Packet(GameOpcodes.GS_FRIEND_PROCESS);
        report.WriteByte(FriendReport);
        report.WriteUShort(2);
        report.WriteSByteString("Friend");
        report.WriteSByteString("Stranger");
        await provider.GetRequiredService<ISocialPacketCoordinator>().HandleFriendProcessAsync(client, report);

        var status = sent.First(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_FRIEND_PROCESS);
        status.ResetOffset();
        status.ReadByte().Should().Be((byte)FriendSubOpcode.StatusList);
        status.ReadUShort().Should().Be(1);
        status.ReadString().Should().Be("Friend");
    }

    [Fact]
    public async Task QuestEntriesWaitUntilTheTradeIsOver()
    {
        var sessionManager = new SessionManager();
        var runner = Substitute.For<IQuestDialogRunner>();
        var (session, client, _) = CreatePlayer(sessionManager, 1207, "Trader");
        session.Quest.EventNpcUniqueId = sessionManager.Regions.SpawnNpc(new NpcInstance
        {
            NpcId = 9000, ZoneId = session.ZoneId, X = session.X, Z = session.Z, MaxHp = 1, Hp = 1,
        }).UniqueId;
        session.Trade.ExchangeUser = 1208;
        var quests = new QuestProgressionService(
            sessionManager, Substitute.For<IGameDataService>(), runner, Substitute.For<IQuestDefinitionSource>(),
            Substitute.For<ICharacterStatePersister>(), Substitute.For<ILogger<QuestProgressionService>>());
        var npcs = new QuestNpcInteractionService(
            sessionManager, Substitute.For<IGameDataService>(), runner, Substitute.For<ILogger<QuestNpcInteractionService>>());

        var accept = new Packet(GameOpcodes.GS_QUEST);
        accept.WriteByte((byte)QuestSubOpcode.Accept);
        accept.WriteInt(QuestId);
        await quests.HandleQuestAsync(client, accept);

        var clientEvent = new Packet(GameOpcodes.GS_CLIENT_EVENT);
        clientEvent.WriteInt(session.Quest.EventNpcUniqueId);
        await npcs.HandleClientEventAsync(client, clientEvent);

        await runner.DidNotReceiveWithAnyArgs().TryEntryAsync(default!, default, default!, default);
        await runner.DidNotReceiveWithAnyArgs().TryGreetAsync(default!, default!);
    }

    [Fact]
    public void ScriptGoldStopsAtTheCoinCap()
    {
        var sessionManager = new SessionManager();
        var (session, _, _) = CreatePlayer(sessionManager, 1209, "Rich");
        session.Money = int.MaxValue - 5;
        var context = new QuestScriptContext(
            session, null, Substitute.For<IGameDataService>(), sessionManager, Substitute.For<ILogger>(), 1);

        context.Items.GoldGain(0, 1_000);

        session.Money.Should().Be(int.MaxValue - 5);

        session.Money = ExchangePacketConstants.CoinMax - 100;
        context.Items.GoldGain(0, 1_000);

        session.Money.Should().Be(ExchangePacketConstants.CoinMax);
    }

    [Fact]
    public async Task AClaimedAchievementPaysOutOnce()
    {
        var definition = new AchievementData { Id = AchievementId, Name = "Once", RewardItemId = AchievementReward, RewardItemCount = 1 };
        using var provider = CreateProvider(_ => { }, gameData =>
        {
            gameData.GetItem(AchievementReward).Returns(new ItemData { Num = AchievementReward, Countable = 1 });
            gameData.AchievementTable.Returns(new Dictionary<int, AchievementData> { [AchievementId] = definition });
        });
        var (session, _, _) = CreatePlayer(provider.GetRequiredService<SessionManager>(), 1210, "Hero");
        var achievements = provider.GetRequiredService<IAchievementProgressService>();

        (await achievements.CompleteAsync(session, definition)).Should().BeTrue();
        (await achievements.CompleteAsync(session, definition)).Should().BeFalse();

        session.Inventory.Where(slot => slot.ItemId == AchievementReward).Sum(slot => slot.Count).Should().Be(1);
    }

    private static bool IsAcceptedRegistration(Packet packet)
    {
        var bytes = packet.GetBytes();
        return packet.GetOpcode() == (byte)GameOpcodes.GS_MARKET_BBS
            && bytes[1] == MarketRegister
            && bytes[2] == MarketBbsPacketWriter.Succeeded;
    }

    private static Packet RegisterAd(ushort count)
    {
        var packet = new Packet(GameOpcodes.GS_MARKET_BBS);
        packet.WriteByte(MarketRegister);
        packet.WriteInt(AdvertisedItem);
        packet.WriteInt(1_000);
        packet.WriteUShort(count);
        packet.WriteByte(Selling);
        packet.WriteSByteString(string.Empty);
        return packet;
    }

    private static Packet OpenBoard()
    {
        var packet = new Packet(GameOpcodes.GS_MARKET_BBS);
        packet.WriteByte(MarketOpen);
        packet.WriteByte(AllAds);
        return packet;
    }

    private static Packet ChatTarget(byte type, string name)
    {
        var packet = new Packet(GameOpcodes.GS_CHAT_TARGET);
        packet.WriteByte(type);
        packet.WriteString(name);
        return packet;
    }

    private static Packet BlockWhispers()
    {
        var packet = new Packet(GameOpcodes.GS_CHAT_TARGET);
        packet.WriteByte(ChatTargetPacketWriter.TypeBlockToggle);
        packet.WriteByte(1);
        return packet;
    }

    private static (UserSession Session, IClient Client, List<Packet> Sent) CreatePlayer(
        SessionManager sessionManager, int characterId, string name)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        var sent = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(packet => sent.Add(ClonePacket(packet))), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = name;
        session.Nation = AccountNation.Karus;
        session.Level = 60;
        session.ZoneId = (byte)ZoneId.Moradon;
        session.X = 100;
        session.Z = 100;
        session.MaxHp = 100;
        session.Hp = 100;
        sessionManager.Regions.AddToRegion(session);
        return (session, client, sent);
    }
}
