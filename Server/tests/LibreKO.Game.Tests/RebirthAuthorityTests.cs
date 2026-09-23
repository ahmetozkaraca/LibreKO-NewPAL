using FluentAssertions;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class RebirthAuthorityTests : GameTestBase
{
    private const int QualificationScroll = 900_579_000;
    private const long LevelExperience = 1_000_000;
    private const byte RedistributionPanel = 46;
    private const byte OtherNpcType = 21;

    [Fact]
    public async Task RebirthChargesItsFullPriceWhenApplied()
    {
        using var provider = CreateWorkshop();
        var (session, client) = CreateReadyPlayer(provider, 1100);
        TalkTo(provider, session, NpcData.RedistributionMerchant, RedistributionPanel);

        await Develop(provider).HandleClassChangeAsync(client, RebirthRequest(1, 1, 0, 0, 0));

        session.RebirthLevel.Should().Be(1);
        session.RebStr.Should().Be(1);
        session.RebSta.Should().Be(1);
        session.Money.Should().Be(0);
        session.Loyalty.Should().Be(0);
        session.Experience.Should().Be(0);
        session.Inventory.Should().NotContain(slot => slot.ItemId == QualificationScroll);
    }

    [Fact]
    public async Task RebirthIsRefusedBelowTheLevelOrWithoutTheFunds()
    {
        using var provider = CreateWorkshop();
        var (young, youngClient) = CreateReadyPlayer(provider, 1101);
        young.Level = ProgressionTable.MaxLevel - 1;
        var (poor, poorClient) = CreateReadyPlayer(provider, 1102);
        poor.Money = RebirthPacketCoordinator.RebirthGoldCost - 1;
        TalkTo(provider, young, NpcData.RedistributionMerchant, RedistributionPanel);
        TalkTo(provider, poor, NpcData.RedistributionMerchant, RedistributionPanel);

        await Develop(provider).HandleClassChangeAsync(youngClient, RebirthRequest(2, 0, 0, 0, 0));
        await Develop(provider).HandleClassChangeAsync(poorClient, RebirthRequest(2, 0, 0, 0, 0));

        young.RebirthLevel.Should().Be(0);
        poor.RebirthLevel.Should().Be(0);
        poor.Money.Should().Be(RebirthPacketCoordinator.RebirthGoldCost - 1);
        young.Inventory.Should().Contain(slot => slot.ItemId == QualificationScroll);
    }

    [Fact]
    public async Task RebirthNeedsTheRedistributionNpc()
    {
        using var provider = CreateWorkshop();
        var (session, client) = CreateReadyPlayer(provider, 1103);
        TalkTo(provider, session, 15002, OtherNpcType);

        await Develop(provider).HandleClassChangeAsync(client, RebirthRequest(1, 1, 0, 0, 0));

        session.RebirthLevel.Should().Be(0);
        session.Money.Should().Be(RebirthPacketCoordinator.RebirthGoldCost);
    }

    [Fact]
    public async Task StatsAreRedistributedOnlyBesideTheRedistributionNpc()
    {
        using var provider = CreateWorkshop();
        var (session, client) = CreateReadyPlayer(provider, 1104);
        session.Class = 101;
        session.Strength = 80;
        var reset = new Packet(GameOpcodes.GS_CLASS_CHANGE);
        reset.WriteByte((byte)ClassChangeSubOpcode.StatReset);

        await Develop(provider).HandleClassChangeAsync(client, reset);

        session.Strength.Should().Be(80);
    }

    private static ICharacterDevelopmentPacketCoordinator Develop(ServiceProvider provider)
        => provider.GetRequiredService<ICharacterDevelopmentPacketCoordinator>();

    private static ServiceProvider CreateWorkshop()
        => CreateProvider(_ => { }, gameData =>
        {
            gameData.GetMaxExpForLevel(Arg.Any<byte>()).Returns(LevelExperience);
            gameData.GetCoefficient(Arg.Any<short>()).Returns(CreateBasicCoefficient(101));
        });

    private static void TalkTo(ServiceProvider provider, UserSession session, int npcId, byte npcType)
    {
        session.Quest.EventNpcUniqueId = provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = npcId,
            NpcType = npcType,
            ZoneId = session.ZoneId,
            X = session.X,
            Z = session.Z,
            MaxHp = 1,
            Hp = 1,
        }).UniqueId;
    }

    private static Packet RebirthRequest(byte strength, byte stamina, byte dexterity, byte intelligence, byte magic)
    {
        var packet = new Packet(GameOpcodes.GS_CLASS_CHANGE);
        packet.WriteByte((byte)ClassChangeSubOpcode.RebirthStatChange);
        packet.WriteByte(strength);
        packet.WriteByte(stamina);
        packet.WriteByte(dexterity);
        packet.WriteByte(intelligence);
        packet.WriteByte(magic);
        return packet;
    }

    private static (UserSession Session, IClient Client) CreateReadyPlayer(ServiceProvider provider, int characterId)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = $"Veteran{characterId}";
        session.Nation = AccountNation.Karus;
        session.ZoneId = (byte)ZoneId.Moradon;
        session.X = 100;
        session.Z = 100;
        session.MaxHp = 100;
        session.Hp = 100;
        session.Level = ProgressionTable.MaxLevel;
        session.Experience = LevelExperience;
        session.Money = RebirthPacketCoordinator.RebirthGoldCost;
        session.Loyalty = RebirthPacketCoordinator.RebirthLoyaltyCost;
        var scroll = session.Inventory[InventoryConstants.InventoryStart];
        scroll.ItemId = QualificationScroll;
        scroll.Count = 1;
        scroll.Durability = 1;
        sessionManager.Regions.AddToRegion(session);
        return (session, client);
    }
}
