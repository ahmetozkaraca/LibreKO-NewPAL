using LibreKO.Common.Domain.Entities.GameData;
using LibreKO.Common.Domain.Services;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface INationSystemsPacketCoordinator
{
    Task HandleBifrostAsync(IClient client, Packet packet);
    Task HandleRankAsync(IClient client, Packet packet);
    Task HandleSiegeAsync(IClient client, Packet packet);
    Task HandleKingAsync(IClient client, Packet packet);
}

public class NationSystemsPacketCoordinator(
    IServiceProvider serviceProvider,
    SessionManager sessionManager,
    IGameDataService gameDataService,
    IKingSystemRuntimeService kingSystemRuntimeService,
    IKingElectionPacketService kingElectionPacketService,
    IKingGovernancePacketService kingGovernancePacketService,
    IBifrostEventService bifrostEventService,
    EventSchedulerService eventSchedulerService,
    IUserNotificationService userNotificationService,
    ILogger<NationSystemsPacketCoordinator> logger) : INationSystemsPacketCoordinator
{

    private const byte SiegeBaseCreate = 1;
    private const byte SiegeCastleFlag = 2;
    private const byte SiegeMoradonNpc = 3;
    private const byte SiegeDelosNpc = 4;
    private const byte SiegeRank = 5;
    private const byte DelosCollectFunds = 2;
    private const byte DelosViewTariffs = 3;
    private const byte DelosMoradonTariff = 4;
    private const byte DelosDelosTariff = 5;
    private const byte CastleLordFame = 1;
    private const ushort SiegeTariffMax = 20;
    private const byte ZoneMoradon = (byte)ZoneId.Moradon;
    private const byte ZoneDelos = (byte)ZoneId.Delos;

    private readonly Lock _siegeSync = new();

    public async Task HandleBifrostAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < 1)
            return;

        //   2 = BIFROST_EVENT    — remaining time query (the only one with logic)
        var sub = packet.ReadByte();
        if (sub != (byte)TempleSubOpcode.BifrostRemaining)
            return;

        int remaining = 0;
        byte eventType = 0;
        if (eventSchedulerService.IsTempleEventJoinOpen)
        {
            remaining = eventSchedulerService.TempleRemainingJoinSeconds;
            eventType = (byte)eventSchedulerService.CurrentTempleEvent;
        }
        else
        {
            remaining = (int)Math.Min(bifrostEventService.RemainingSecs, int.MaxValue);
        }

        var response = BifrostPacketWriter.Remaining(
            TempleSubOpcode.BifrostRemaining, remaining, eventType);
        await session.Client.SendPacket(response);
    }

    private const byte RankTypePkZone = 1;
    private const byte RankTypeBorderDefenseWar = 2;
    private const byte RankTypeChaosDungeon = 3;
    private const int PkZoneTopCount = 10;

    public async Task HandleRankAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var rankType = packet.ReadByte();

        var response = rankType switch
        {
            RankTypePkZone => await BuildPkZoneRankAsync(session, rankType),
            RankTypeBorderDefenseWar => RankPacketWriter.BorderDefenseWar(rankType),
            RankTypeChaosDungeon => RankPacketWriter.ChaosDungeon(rankType),
            _ => RankPacketWriter.Unsupported(rankType),
        };
        await session.Client.SendPacket(response);
    }

    private async Task<Packet> BuildPkZoneRankAsync(UserSession session, byte rankType)
    {
        using var scope = serviceProvider.CreateScope();
        var characterRepo = scope.ServiceProvider.GetRequiredService<ICharacterRepository>();

        var karusTop = await characterRepo.GetTopByLoyalty(AccountNation.Karus, PkZoneTopCount);
        var elmoradTop = await characterRepo.GetTopByLoyalty(AccountNation.ElMorad, PkZoneTopCount);
        var myRank = await characterRepo.GetLoyaltyRank(session.Nation, session.DailyLoyalty);

        return RankPacketWriter.PkZone(
            rankType,
            ToRankEntries(karusTop),
            ToRankEntries(elmoradTop),
            (ushort)Math.Min(myRank, ushort.MaxValue),
            session.DailyLoyalty);
    }

    private List<RankPacketWriter.RankEntry> ToRankEntries(IReadOnlyList<CharacterRankRow> entries)
    {
        var result = new List<RankPacketWriter.RankEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var clan = entry.KnightsId > 0 ? sessionManager.Knights.GetClan(entry.KnightsId) : null;
            result.Add(new RankPacketWriter.RankEntry(
                entry.Name,
                (byte)entry.Nation,
                (ushort)entry.KnightsId,
                clan?.MarkVersion < 0 ? (ushort)0 : (ushort)(clan?.MarkVersion ?? 0),
                clan?.Name ?? string.Empty,
                entry.LoyaltyDaily));
        }
        return result;
    }

    public async Task HandleSiegeAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || session.Hp <= 0 || packet.RemainingBytes < 1)
            return;

        var opcode = packet.ReadByte();
        var subType = packet.RemainingBytes >= 1 ? packet.ReadByte() : (byte)0;
        var tariff = packet.RemainingBytes >= 2 ? packet.ReadUShort() : (ushort)0;

        switch (opcode)
        {
            case SiegeBaseCreate: await HandleSiegeBaseCreateAsync(session, subType); break;
            case SiegeCastleFlag: await HandleSiegeCastleFlagAsync(session); break;
            case SiegeMoradonNpc: await HandleSiegeMoradonNpcAsync(session, subType); break;
            case SiegeDelosNpc: await HandleSiegeDelosNpcAsync(session, subType, tariff); break;
            case SiegeRank: await HandleSiegeRankAsync(session, subType); break;
        }
    }

    private static async Task HandleSiegeBaseCreateAsync(UserSession session, byte subType)
    {
        var resp = SiegePacketWriter.Result(SiegeBaseCreate, subType);
        await session.Client.SendPacket(resp);
    }

    private async Task HandleSiegeCastleFlagAsync(UserSession session)
    {
        var siege = gameDataService.SiegeWarfare;
        var masterId = siege?.MasterKnights ?? 0;
        SiegePacketWriter.ClanBanner? banner = null;
        if (masterId > 0 && sessionManager.Knights.GetClan(masterId) is { } clan)
        {
            banner = new SiegePacketWriter.ClanBanner(
                (ushort)clan.Id,
                clan.MarkVersion < 0 ? (ushort)0 : (ushort)clan.MarkVersion,
                clan.Flag,
                clan.Grade);
        }

        await session.Client.SendPacket(SiegePacketWriter.CastleFlag(SiegeCastleFlag, banner));
    }

    private async Task HandleSiegeMoradonNpcAsync(UserSession session, byte subType)
    {
        var siege = gameDataService.SiegeWarfare;
        if (siege == null) return;

        switch (subType)
        {
            case 2:
            {
                var resp = SiegePacketWriter.CastleSchedule(
                    SiegeMoradonNpc, 2, (ushort)siege.CastleIndex, siege.SiegeType,
                    new SiegePacketWriter.WarSchedule(siege.WarDay, siege.WarTime, siege.WarMinute));
                await session.Client.SendPacket(resp);
                break;
            }
            case 4:
            {
                if (siege.MasterKnights == 0) return;
                var clan = sessionManager.Knights.GetClan(siege.MasterKnights);
                if (clan == null) return;
                var resp = SiegePacketWriter.CastleApplicants(
                    SiegeMoradonNpc, 4, (ushort)siege.CastleIndex, clan.Name, clan.Nation,
                    (ushort)clan.Members,
                    new SiegePacketWriter.WarSchedule(
                        siege.WarRequestDay, siege.WarRequestTime, siege.WarRequestMinute));
                await session.Client.SendPacket(resp);
                break;
            }
            case 5:
            {
                if (siege.MasterKnights == 0) return;
                var clan = sessionManager.Knights.GetClan(siege.MasterKnights);
                if (clan == null) return;
                var resp = SiegePacketWriter.CastleOwner(
                    SiegeMoradonNpc, 5, (ushort)siege.CastleIndex, siege.SiegeType,
                    clan.Name, clan.Nation, (ushort)clan.Members);
                await session.Client.SendPacket(resp);
                break;
            }
        }
    }

    private async Task HandleSiegeDelosNpcAsync(UserSession session, byte subType, ushort tariff)
    {
        var siege = gameDataService.SiegeWarfare;
        if (siege == null) return;

        switch (subType)
        {
            case DelosCollectFunds:
                await CollectSiegeFundsAsync(session, siege);
                break;

            case DelosViewTariffs:
            {
                if (IsCallerKing(session)) return;
                var resp = SiegePacketWriter.Tariffs(
                    SiegeDelosNpc, DelosViewTariffs, (ushort)siege.CastleIndex, (ushort)siege.MoradonTariff,
                    (ushort)siege.DellosTariff, siege.DungeonCharge);
                await session.Client.SendPacket(resp);
                break;
            }

            case DelosMoradonTariff:
                await SetSiegeTariffAsync(session, siege, subType, tariff, ZoneMoradon);
                break;

            case DelosDelosTariff:
                await SetSiegeTariffAsync(session, siege, subType, tariff, ZoneDelos);
                break;
        }
    }

    private async Task CollectSiegeFundsAsync(UserSession session, SiegeWarfareData siege)
    {
        if (!NpcDialogContext.IsTalkingTo(sessionManager, session, NpcData.TypeCastleManager))
            return;

        var isKing = IsCallerKing(session);
        if (!isKing && !IsCastleLord(session, siege))
            return;

        var collected = TakeSiegeFunds(session, siege, isKing);
        if (collected <= 0)
            return;

        await userNotificationService.SendGoldGainAsync(session, collected);
        logger.LogInformation("{Name} collected {Gold} coins of siege funds", session.Name, collected);
    }

    private int TakeSiegeFunds(UserSession session, SiegeWarfareData siege, bool isKing)
    {
        using var scope = _siegeSync.EnterScope();
        var funds = isKing ? (long)siege.MoradonTax + siege.DellosTax : siege.DungeonCharge;
        if (funds <= 0 || !Coins.TryCredit(session, funds))
            return 0;

        if (isKing)
        {
            siege.MoradonTax = 0;
            siege.DellosTax = 0;
        }
        else
        {
            siege.DungeonCharge = 0;
        }

        return (int)funds;
    }

    private async Task SetSiegeTariffAsync(UserSession session, SiegeWarfareData siege, byte subType, ushort tariff, byte zone)
    {
        if (tariff > SiegeTariffMax
            || !IsCastleLord(session, siege)
            || !NpcDialogContext.IsTalkingTo(sessionManager, session, NpcData.TypeCastleManager))
            return;

        StoreTariff(siege, zone, tariff);

        await sessionManager.BroadcastToAll(SiegePacketWriter.TariffChanged(SiegeDelosNpc, subType, tariff, zone));
        logger.LogInformation("{Name} set the tariff of zone {Zone} to {Tariff}", session.Name, zone, tariff);
    }

    private void StoreTariff(SiegeWarfareData siege, byte zone, ushort tariff)
    {
        using var scope = _siegeSync.EnterScope();
        if (zone == ZoneMoradon)
            siege.MoradonTariff = (short)tariff;
        else
            siege.DellosTariff = (short)tariff;
    }

    private static bool IsCastleLord(UserSession session, SiegeWarfareData siege)
        => siege.MasterKnights > 0
            && session.KnightsId == siege.MasterKnights
            && session.KnightsFame == CastleLordFame;

    private async Task HandleSiegeRankAsync(UserSession session, byte subType)
    {
        var resp = SiegePacketWriter.RankList(SiegeRank, subType, 0);
        await session.Client.SendPacket(resp);
    }

    private bool IsCallerKing(UserSession session)
        => kingSystemRuntimeService.IsKing(session, kingSystemRuntimeService.GetKingData(session.Nation));

    public async Task HandleKingAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null || packet.RemainingBytes < 1)
            return;

        var subOpcode = packet.ReadByte();
        switch (subOpcode)
        {
            case KingPacketConstants.Election:
                await kingElectionPacketService.HandleElectionAsync(session, packet);
                break;

            case KingPacketConstants.Impeachment:
                await kingElectionPacketService.HandleImpeachmentAsync(session, packet);
                break;

            case KingPacketConstants.Tax:
                await kingGovernancePacketService.HandleTaxAsync(session, packet);
                break;

            case KingPacketConstants.Event:
                await kingGovernancePacketService.HandleKingEventAsync(session, packet);
                break;

            case KingPacketConstants.Npc:
                await kingGovernancePacketService.HandleKingNpcAsync(session);
                break;

            case KingPacketConstants.NationIntro:
                await kingGovernancePacketService.HandleNationIntroAsync(session);
                break;
        }
    }
}
