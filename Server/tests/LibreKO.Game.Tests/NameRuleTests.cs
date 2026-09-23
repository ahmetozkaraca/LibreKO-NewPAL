using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class NameRuleTests : GameTestBase
{
    private const byte CharacterRename = 0;
    private const byte ClanRename = 16;
    private const int ClanNameScroll = 800_086_000;
    private const short Guild = 61;
    private const int RenamedId = 1300;

    [Theory]
    [InlineData("Bad Name", "Original")]
    [InlineData("Bad	Name", "Original")]
    [InlineData("Bad_Name", "Original")]
    [InlineData("Renamed", "Renamed")]
    public async Task AnInGameRenameFollowsTheCharacterNameRules(string newName, string expectedName)
    {
        using var provider = CreateProvider(db => db.Characters.Add(new Character { Id = RenamedId, AccountId = RenamedId + 1, Name = "Original" }));
        var player = CreatePlayer(provider.GetRequiredService<SessionManager>(), RenamedId, "Original");
        GiveItem(player, CharacterRules.RenameScrollItemId);

        await provider.GetRequiredService<IMiscPacketCoordinator>()
            .HandleNameChangeAsync(player.Client, NameChange(CharacterRename, newName));

        player.Name.Should().Be(expectedName);
        player.Inventory[InventoryConstants.SlotMax].IsEmpty.Should().Be(expectedName != "Original");
    }

    [Theory]
    [InlineData("Bad Name", "Guild")]
    [InlineData("Bad	Name", "Guild")]
    [InlineData("Ox", "Ox")]
    public async Task AClanRenameFollowsTheClanNameRules(string newName, string expectedName)
    {
        using var provider = CreateProvider(db => db.Set<KnightsEntity>().Add(Clan()));
        var sessionManager = provider.GetRequiredService<SessionManager>();
        sessionManager.Knights.AddClan(Guild, Clan());
        var chief = CreatePlayer(sessionManager, 1301, "Chief");
        chief.KnightsId = Guild;
        chief.KnightsFame = KnightsManager.ChiefFame;
        GiveItem(chief, ClanNameScroll);

        await provider.GetRequiredService<IMiscPacketCoordinator>()
            .HandleNameChangeAsync(chief.Client, NameChange(ClanRename, newName));

        sessionManager.Knights.GetClan(Guild)!.Name.Should().Be(expectedName);
    }

    private static KnightsEntity Clan() => new()
    {
        Id = Guild, Name = "Guild", Chief = "Chief", Nation = (byte)AccountNation.Karus,
    };

    private static void GiveItem(UserSession player, int itemId)
    {
        player.Inventory[InventoryConstants.SlotMax].ItemId = itemId;
        player.Inventory[InventoryConstants.SlotMax].Count = 1;
    }

    private static Packet NameChange(byte subOpcode, string name)
    {
        var packet = new Packet(GameOpcodes.GS_NAME_CHANGE);
        packet.WriteByte(subOpcode);
        packet.WriteString(name);
        return packet;
    }

    private static UserSession CreatePlayer(SessionManager sessionManager, int characterId, string name)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var session = sessionManager.CreateSession(client, characterId, characterId + 1);
        session.Name = name;
        session.Nation = AccountNation.Karus;
        session.ZoneId = (byte)ZoneId.Moradon;
        session.X = 100;
        session.Z = 100;
        sessionManager.Regions.AddToRegion(session);
        return session;
    }
}
