using FluentAssertions;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.Protocol.Writers;
using Microsoft.Extensions.DependencyInjection;

namespace LibreKO.Game.Tests;

public class ItemCraftRefusalTests : EconomyTestBase
{
    private const byte CombineListSub = 1;
    private const byte CombineSub = 2;
    private const ushort NoRecipes = 0;
    private const int CatalogRecipe = 1;

    [Fact]
    public async Task TheCombineListOffersNoRecipe()
    {
        using var provider = CreateProvider(_ => { });
        var player = Player(provider, 9503, out var sent);
        var packet = new Packet(GameOpcodes.GS_ITEM_COMBINE);
        packet.WriteByte(CombineListSub);

        await provider.GetRequiredService<IItemCombinePacketCoordinator>().HandleAsync(player.Client, packet);

        var reply = Last(sent, GameOpcodes.GS_ITEM_COMBINE);
        reply.Should().NotBeNull();
        reply!.ReadByte().Should().Be(CombineListSub);
        reply.ReadUShort().Should().Be(NoRecipes);
        reply.RemainingBytes.Should().Be(0);
    }

    [Fact]
    public async Task TheExchangeListOffersNoRecipe()
    {
        using var provider = CreateProvider(_ => { });
        var player = Player(provider, 9504, out var sent);
        var packet = new Packet(GameOpcodes.GS_ITEM_EXCHANGE);
        packet.WriteByte((byte)ItemExchangeSubOpcode.List);

        await provider.GetRequiredService<IItemExchangePacketCoordinator>().HandleAsync(player.Client, packet);

        var reply = Last(sent, GameOpcodes.GS_ITEM_EXCHANGE);
        reply.Should().NotBeNull();
        reply!.ReadByte().Should().Be((byte)ItemExchangeSubOpcode.List);
        reply.ReadUShort().Should().Be(NoRecipes);
        reply.RemainingBytes.Should().Be(0);
    }

    [Fact]
    public async Task ACombineTheServerDoesNotCraftIsRefused()
    {
        using var provider = CreateProvider(_ => { });
        var player = Player(provider, 9501, out var sent);
        var packet = new Packet(GameOpcodes.GS_ITEM_COMBINE);
        packet.WriteByte(CombineSub);
        packet.WriteInt(CatalogRecipe);

        await provider.GetRequiredService<IItemCombinePacketCoordinator>().HandleAsync(player.Client, packet);

        var reply = Last(sent, GameOpcodes.GS_ITEM_COMBINE);
        reply.Should().NotBeNull();
        reply!.ReadByte().Should().Be(CombineSub);
        reply.ReadByte().Should().Be(ItemCombinePacketWriter.Failed);
        reply.ReadInt().Should().Be(ItemCombinePacketWriter.NoItemId);
    }

    [Fact]
    public async Task AnExchangeTheServerDoesNotPerformIsRefused()
    {
        using var provider = CreateProvider(_ => { });
        var player = Player(provider, 9502, out var sent);
        var packet = new Packet(GameOpcodes.GS_ITEM_EXCHANGE);
        packet.WriteByte((byte)ItemExchangeSubOpcode.Exchange);
        packet.WriteInt(CatalogRecipe);

        await provider.GetRequiredService<IItemExchangePacketCoordinator>().HandleAsync(player.Client, packet);

        var reply = Last(sent, GameOpcodes.GS_ITEM_EXCHANGE);
        reply.Should().NotBeNull();
        reply!.ReadByte().Should().Be((byte)ItemExchangeSubOpcode.Exchange);
        reply.ReadByte().Should().Be(ItemExchangePacketWriter.Failed);
        reply.ReadInt().Should().Be(ItemExchangePacketWriter.NoItemId);
    }
}
