using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Common.Infrastructure.Persistence;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class GmSanctionTests : GameTestBase
{
    private const string TargetLogin = "target-user";
    private const string TargetName = "Target";
    private const string OfflineAltName = "TargetAlt";
    private const byte OperatorCutoff = 5;
    private const short SavedHp = 77;

    [Fact]
    public async Task KickDisconnectsTheTargetAfterSavingIt()
    {
        using var provider = CreateProvider(Seed);
        var (gm, target) = await CreateOnlinePairAsync(provider);

        await provider.GetRequiredService<IAdminPacketCoordinator>().HandleGmCommandAsync(gm, $"+kick {TargetName}");

        target.Client.Received(1).Disconnect();
        provider.GetRequiredService<SessionManager>().GetByName(TargetName).Should().BeNull();
        (await ReadTargetCharacterAsync(provider)).Hp.Should().Be(SavedHp);
    }

    [Fact]
    public async Task OperatorCutoffDisconnectsTheTarget()
    {
        using var provider = CreateProvider(Seed);
        var (gm, target) = await CreateOnlinePairAsync(provider);

        var packet = new Packet(GameOpcodes.GS_OPERATOR);
        packet.WriteByte(OperatorCutoff);
        packet.WriteSByteString(TargetName);
        await provider.GetRequiredService<IAdminPacketCoordinator>().HandleOperatorAsync(gm.Client, packet);

        target.Client.Received(1).Disconnect();
        (await ReadTargetCharacterAsync(provider)).Hp.Should().Be(SavedHp);
    }

    [Fact]
    public async Task BanDisconnectsAnOnlineTarget()
    {
        using var provider = CreateProvider(Seed);
        var (gm, target) = await CreateOnlinePairAsync(provider);

        await provider.GetRequiredService<IAdminPacketCoordinator>().HandleGmCommandAsync(gm, $"+ban {TargetName}");

        target.Client.Received(1).Disconnect();
        (await ReadTargetAccountAsync(provider)).Authority.Should().Be(AccountAuthority.Banned);
    }

    [Fact]
    public async Task BanDisconnectsAnAccountWaitingAtCharacterSelect()
    {
        using var provider = CreateProvider(Seed);
        var (gm, _) = await CreateOnlinePairAsync(provider, targetInGame: false);
        var waiting = CreateClient();
        var accountId = await GetAccountIdAsync(provider, TargetLogin);
        waiting.AccountId = accountId;
        (await provider.GetRequiredService<IAccountLockService>().AcquireAsync(waiting, accountId)).Granted.Should().BeTrue();

        await provider.GetRequiredService<IAdminPacketCoordinator>().HandleGmCommandAsync(gm, $"+ban {OfflineAltName}");

        waiting.Received(1).Disconnect();
        (await ReadTargetAccountAsync(provider)).Authority.Should().Be(AccountAuthority.Banned);
    }

    private static void Seed(AppDbContext db)
    {
        db.Accounts.AddRange(
            new Account { Login = "gm-user", Password = "pw", Nation = AccountNation.Karus, Authority = AccountAuthority.GameMaster },
            new Account { Login = TargetLogin, Password = "pw", Nation = AccountNation.Karus, Authority = AccountAuthority.Normal });
        db.SaveChanges();

        var gmAccountId = db.Accounts.Single(account => account.Login == "gm-user").Id;
        var targetAccountId = db.Accounts.Single(account => account.Login == TargetLogin).Id;
        db.Characters.AddRange(
            CreateCharacter(gmAccountId, 0, "GM"),
            CreateCharacter(targetAccountId, 0, TargetName),
            CreateCharacter(targetAccountId, 1, OfflineAltName));
        db.SaveChanges();
    }

    private static Character CreateCharacter(int accountId, byte slot, string name) => new()
    {
        AccountId = accountId,
        Slot = slot,
        Name = name,
        Race = 1,
        Class = 101,
        Face = 1,
        Hair = 1,
        Level = 20,
        Hp = 90,
        Mp = 70,
        MapId = 1,
        IsOnline = true,
        Items = new byte[InventoryConstants.InventoryTotal * UserSessionBinaryState.BytesPerItem],
        SkillPointData = new byte[MyInfoSkillDataSize],
    };

    private static IClient CreateClient()
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return client;
    }

    private static async Task<(UserSession Gm, UserSession Target)> CreateOnlinePairAsync(
        ServiceProvider provider, bool targetInGame = true)
    {
        var sessionManager = provider.GetRequiredService<SessionManager>();

        var gmClient = CreateClient();
        var gmCharacterId = await GetCharacterIdAsync(provider, "GM");
        gmClient.CharacterId = gmCharacterId;
        var gm = sessionManager.CreateSession(gmClient, gmCharacterId, await GetAccountIdAsync(provider, "gm-user"));
        gm.Name = "GM";
        gm.IsGM = true;
        gm.ZoneId = 1;

        var targetClient = CreateClient();
        var targetCharacterId = await GetCharacterIdAsync(provider, TargetName);
        targetClient.CharacterId = targetCharacterId;
        var target = sessionManager.CreateSession(targetClient, targetCharacterId, await GetAccountIdAsync(provider, TargetLogin));
        target.Name = TargetName;
        target.ZoneId = 1;
        target.Hp = SavedHp;

        if (!targetInGame)
            sessionManager.RemoveSession(target);

        return (gm, target);
    }

    private static async Task<Account> ReadTargetAccountAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Accounts.AsNoTracking()
            .SingleAsync(account => account.Login == TargetLogin);
    }

    private static async Task<Character> ReadTargetCharacterAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Characters.AsNoTracking()
            .SingleAsync(character => character.Name == TargetName);
    }
}
