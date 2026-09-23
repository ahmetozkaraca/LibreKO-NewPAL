using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public abstract class EconomyTestBase : GameTestBase
{
    protected const byte Zone = 21;
    protected const byte OtherZone = 22;
    protected const float StandX = 100f;
    protected const float StandZ = 100f;
    protected const float FarAway = 500f;
    protected const int AccountOffset = 100_000;
    protected const byte CharacterLevel = 60;
    protected const short FullHp = 100;
    protected const int CoinMax = 2_100_000_000;

    protected static IClient Client(out List<Packet> sent)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        var captured = new List<Packet>();
        client.SendPacket(Arg.Do<Packet>(captured.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        sent = captured;
        return client;
    }

    protected static UserSession Player(ServiceProvider provider, int characterId, out List<Packet> sent, int money = 0)
    {
        var client = Client(out sent);
        client.CharacterId.Returns(characterId);
        client.AccountId.Returns(characterId + AccountOffset);

        var sessionManager = provider.GetRequiredService<SessionManager>();
        var session = sessionManager.CreateSession(client, characterId, characterId + AccountOffset);
        session.Name = $"Player{characterId}";
        session.Nation = AccountNation.Karus;
        session.ZoneId = Zone;
        session.X = StandX;
        session.Z = StandZ;
        session.Level = CharacterLevel;
        session.MaxHp = FullHp;
        session.Hp = FullHp;
        session.Money = money;
        sessionManager.Regions.AddToRegion(session);
        return session;
    }

    protected static NpcInstance Npc(
        ServiceProvider provider, byte npcType, int sellingGroup = 0, float x = StandX, float z = StandZ, byte zone = Zone)
        => provider.GetRequiredService<SessionManager>().Regions.SpawnNpc(new NpcInstance
        {
            NpcId = 30_000 + npcType,
            Name = $"Npc{npcType}",
            NpcType = npcType,
            SellingGroup = sellingGroup,
            ZoneId = zone,
            X = x,
            Z = z,
            SpawnX = x,
            SpawnZ = z,
            MaxHp = FullHp,
            Hp = FullHp,
        });

    protected static ItemSlot Bag(UserSession session, int position) =>
        session.Inventory[InventoryConstants.InventoryStart + position];

    protected static ItemSlot Give(
        UserSession session, int position, int itemId, ushort count = 1,
        ItemFlag flag = ItemFlag.Unsealed, long expiresAt = 0, short durability = 0)
    {
        var slot = Bag(session, position);
        slot.ItemId = itemId;
        slot.Count = count;
        slot.Flag = (byte)flag;
        slot.ExpiresAt = expiresAt;
        slot.Durability = durability;
        return slot;
    }

    protected static int TotalHeld(UserSession session, int itemId)
    {
        var total = 0;
        foreach (var slot in session.Inventory)
        {
            if (slot.ItemId == itemId)
                total += slot.Count;
        }

        return total;
    }

    protected static Packet? Last(List<Packet> sent, GameOpcodes opcode)
    {
        var packet = sent.LastOrDefault(candidate => candidate.GetOpcode() == (byte)opcode);
        packet?.ResetOffset();
        return packet;
    }
}
