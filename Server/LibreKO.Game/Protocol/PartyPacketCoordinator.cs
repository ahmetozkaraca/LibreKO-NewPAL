using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.World;
using Microsoft.Extensions.Logging;
using LibreKO.Game.Protocol.Writers;

namespace LibreKO.Game.Protocol;

public interface IPartyPacketCoordinator
{
    Task HandleAsync(IClient client, Packet packet);
    Task RemoveMemberAsync(UserSession session, short memberId);
}

public class PartyPacketCoordinator(
    SessionManager sessionManager,
    ICombatNotificationService combatNotificationService,
    TimeProvider timeProvider,
    ILogger<PartyPacketCoordinator> logger) : IPartyPacketCoordinator
{
    private const int MaxLevelGap = 8;
    private const int SignalIntervalMs = 850;
    private const int NoParty = -1;

    public async Task HandleAsync(IClient client, Packet packet)
    {
        var session = sessionManager.GetByClientId(client.Id);
        if (session == null)
            return;

        var subOpcode = packet.ReadByte();
        switch ((PartyRequest)subOpcode)
        {
            case PartyRequest.Create:
            case PartyRequest.Insert:
                await HandleCreateOrInsertAsync(session, packet, subOpcode);
                break;

            case PartyRequest.Permit:
            {
                if (packet.RemainingBytes < 1)
                    break;

                if (packet.ReadByte() != 0)
                    await InsertAsync(session);
                else
                    await CancelAsync(session);
                break;
            }

            case PartyRequest.Remove:
                if (packet.RemainingBytes >= 4)
                    await RemoveMemberAsync(session, (short)packet.ReadInt());
                break;

            case PartyRequest.Delete:
                await DeleteAsync(session);
                break;

            case PartyRequest.Promote:
                if (packet.RemainingBytes >= 4)
                    await PromoteAsync(session, (short)packet.ReadInt());
                break;

            case PartyRequest.CommandPromote:
                await HandleCommandPromoteAsync(session, packet);
                break;

            case PartyRequest.TargetNumber:
                await HandleTargetNumberAsync(session, packet);
                break;

            case PartyRequest.Alert:
                await HandleAlertAsync(session, packet);
                break;
        }
    }

    private async Task HandleCommandPromoteAsync(UserSession session, Packet packet)
    {
        var party = LedParty(session);
        if (party == null)
            return;

        var now = DateTime.UtcNow;
        if ((now - session.LastPartySignalTime).TotalMilliseconds < SignalIntervalMs)
            return;
        session.LastPartySignalTime = now;

        if (packet.RemainingBytes < 2) return;
        var targetId = packet.ReadShort();

        if (party.FindMember(targetId) < 0)
            return;

        party.CommandLeaderId = targetId;

        var target = sessionManager.GetByCharacterId(targetId);
        var broadcast = PartyPacketWriter.Commander(targetId, target?.Name ?? string.Empty);
        await combatNotificationService.SendToPartyAsync(party, broadcast);
    }

    private async Task HandleTargetNumberAsync(UserSession session, Packet packet)
    {
        if (session.Hp <= 0)
            return;

        var party = sessionManager.Parties.GetParty(session.PartyIndex);
        if (party == null || !party.IsCommandLeader((short)session.CharacterId))
            return;

        var now = DateTime.UtcNow;
        if ((now - session.LastPartySignalTime).TotalMilliseconds < SignalIntervalMs)
            return;
        session.LastPartySignalTime = now;

        if (packet.RemainingBytes < 7) return;
        var targetId = packet.ReadShort();
        _ = packet.ReadInt(); // effect id (visual hint, unused server-side)
        var success = (sbyte)packet.ReadByte();

        party.TargetNumberId = targetId;

        var broadcast = PartyPacketWriter.TargetNumber(targetId, success);
        await combatNotificationService.SendToPartyAsync(party, broadcast);
    }

    private async Task HandleAlertAsync(UserSession session, Packet packet)
    {
        if (session.Hp <= 0)
            return;

        var party = sessionManager.Parties.GetParty(session.PartyIndex);
        if (party == null || !party.IsCommandLeader((short)session.CharacterId))
            return;

        var now = DateTime.UtcNow;
        if ((now - session.LastPartySignalTime).TotalMilliseconds < SignalIntervalMs)
            return;
        session.LastPartySignalTime = now;

        if (packet.RemainingBytes < 5) return;
        var subOpcode = packet.ReadByte();
        _ = packet.ReadInt(); // effect id

        var broadcast = PartyPacketWriter.Alert(subOpcode);
        await combatNotificationService.SendToPartyAsync(party, broadcast);
    }

    public async Task RemoveMemberAsync(UserSession session, short memberId)
    {
        var party = MemberParty(session);
        if (party == null)
            return;

        if (memberId != session.CharacterId && !party.IsLeader(session.CharacterId))
            return;

        switch (party.Remove(memberId, out var disbandedMembers))
        {
            case PartyRemoval.NotMember:
                return;

            case PartyRemoval.Disbanded:
                await ReleaseMembersAsync(party, disbandedMembers, session);
                return;
        }

        logger.LogInformation("{Name} kicked member {MemberId} from party {PartyIndex}", session.Name, memberId, party.Index);

        var removePkt = PartyPacketWriter.MemberLeft(memberId);
        await combatNotificationService.SendToPartyAsync(party, removePkt);

        var removedUser = sessionManager.GetByCharacterId(memberId);
        if (removedUser == null)
            return;

        removedUser.PartyIndex = NoParty;
        removedUser.IsPartyLeader = false;
        await removedUser.Client.SendPacket(removePkt);
    }

    private async Task HandleCreateOrInsertAsync(UserSession session, Packet packet, byte subOpcode)
    {
        var targetName = packet.ReadString();
        if (string.IsNullOrEmpty(targetName))
            return;

        foreach (var lapsedParty in sessionManager.Parties.SweepLapsedInvites(timeProvider.GetUtcNow()))
            await AbandonInviteAsync(lapsedParty);

        var target = sessionManager.GetByName(targetName);
        if (target == null || target == session || target.IsInParty || target.Nation != session.Nation)
        {
            await SendErrorAsync(session, PartyPacketWriter.InviteFailed);
            return;
        }

        if (target.Level > session.Level + MaxLevelGap || target.Level < session.Level - MaxLevelGap)
        {
            await SendErrorAsync(session, PartyPacketWriter.LevelGapTooWide);
            return;
        }

        if (target.ZoneId != session.ZoneId)
        {
            await SendErrorAsync(session, PartyPacketWriter.DifferentZone);
            return;
        }

        PartyGroup? party;
        if ((PartyRequest)subOpcode == PartyRequest.Create)
        {
            party = MemberParty(session);
            if (party != null && (party.MemberCount > 1 || !party.IsLeader(session.CharacterId)))
            {
                await SendErrorAsync(session, PartyPacketWriter.InviteFailed);
                return;
            }

            if (party == null)
            {
                party = sessionManager.Parties.CreateParty((short)session.CharacterId);
                session.PartyIndex = party.Index;
                session.IsPartyLeader = true;
                logger.LogInformation("{Name} created party {PartyIndex}", session.Name, party.Index);
            }
        }
        else
        {
            party = LedParty(session);
            if (party == null || party.FindEmptySlot() < 0)
            {
                await SendErrorAsync(session, PartyPacketWriter.InviteFailed);
                return;
            }
        }

        var replacedParty = sessionManager.Parties.Invite(target.CharacterId, party.Index, timeProvider.GetUtcNow());
        if (replacedParty is { } replaced)
            await AbandonInviteAsync(replaced);

        var invite = PartyPacketWriter.Invite(session.CharacterId, session.Name);
        await target.Client.SendPacket(invite);
    }

    private static Packet BuildPartyMemberPacket(
        UserSession session, byte statusCode = PartyPacketWriter.MemberJoined) =>
        PartyPacketWriter.MemberInfo(MemberStateOf(session), statusCode);

    private static PartyPacketWriter.MemberState MemberStateOf(UserSession session) => new(
        session.CharacterId,
        session.Name,
        session.Level,
        session.Class,
        session.MaxHp,
        session.Hp,
        session.MaxMp,
        session.Mp);

    private async Task InsertAsync(UserSession session)
    {
        if (!sessionManager.Parties.TryTakeInvite(session.CharacterId, out var invite))
            return;

        var party = sessionManager.Parties.GetParty(invite.PartyIndex);
        var leader = party == null ? null : sessionManager.GetByCharacterId(party.LeaderId);
        if (invite.ExpiresAt < timeProvider.GetUtcNow()
            || session.IsInParty
            || party == null
            || leader == null
            || leader.Nation != session.Nation
            || !party.TryAddMember((short)session.CharacterId))
        {
            await SendErrorAsync(session, PartyPacketWriter.InviteFailed);
            await AbandonInviteAsync(invite.PartyIndex);
            return;
        }

        session.PartyIndex = party.Index;
        session.IsPartyLeader = false;

        foreach (var memberId in party.MemberIds.ToArray())
        {
            if (memberId < 0)
                continue;

            var member = sessionManager.GetByCharacterId(memberId);
            if (member != null)
                await session.Client.SendPacket(BuildPartyMemberPacket(member));
        }

        await combatNotificationService.SendToPartyAsync(party, BuildPartyMemberPacket(session));
    }

    private async Task CancelAsync(UserSession session)
    {
        if (sessionManager.Parties.TryTakeInvite(session.CharacterId, out var invite))
            await AbandonInviteAsync(invite.PartyIndex);
    }

    private async Task AbandonInviteAsync(int partyIndex)
    {
        var party = sessionManager.Parties.GetParty(partyIndex);
        var leader = party == null ? null : sessionManager.GetByCharacterId(party.LeaderId);
        if (party == null || leader == null)
            return;

        if (party.MemberCount < PartyGroup.SmallestParty
            && !sessionManager.Parties.HasLiveInvites(partyIndex, timeProvider.GetUtcNow()))
            await DisbandAsync(party, leader);

        await SendErrorAsync(leader, PartyPacketWriter.InviteFailed);
    }

    private async Task DeleteAsync(UserSession session)
    {
        var party = LedParty(session);
        if (party != null)
            await DisbandAsync(party, session);
    }

    private Task DisbandAsync(PartyGroup party, UserSession initiator)
        => ReleaseMembersAsync(party, party.Disband(), initiator);

    private async Task ReleaseMembersAsync(PartyGroup party, short[] members, UserSession initiator)
    {
        sessionManager.Parties.DeleteParty(party.Index);
        if (members.Length == 0)
            return;

        logger.LogInformation("Party {PartyIndex} deleted by {Name}", party.Index, initiator.Name);

        var online = new List<UserSession>(members.Length);
        foreach (var memberId in members)
        {
            var member = sessionManager.GetByCharacterId(memberId);
            if (member == null)
                continue;

            if (member.PartyIndex == party.Index)
            {
                member.PartyIndex = NoParty;
                member.IsPartyLeader = false;
            }

            online.Add(member);
        }

        var deletePacket = PartyPacketWriter.Disband();
        foreach (var member in online)
            await member.Client.SendPacket(deletePacket);
    }

    private async Task PromoteAsync(UserSession session, short newLeaderId)
    {
        var party = LedParty(session);
        if (party == null)
            return;

        var newLeader = sessionManager.GetByCharacterId(newLeaderId);
        if (newLeader == null
            || newLeader.PartyIndex != party.Index
            || !party.TryPromote((short)session.CharacterId, newLeaderId))
            return;

        session.IsPartyLeader = false;
        newLeader.IsPartyLeader = true;
        await combatNotificationService.SendToPartyAsync(
            party, BuildPartyMemberPacket(newLeader, PartyPacketWriter.MemberPromotedToLeader));
    }

    private PartyGroup? LedParty(UserSession session)
    {
        var party = MemberParty(session);
        return party != null && party.IsLeader(session.CharacterId) ? party : null;
    }

    private PartyGroup? MemberParty(UserSession session)
    {
        if (!session.IsInParty)
            return null;

        var party = sessionManager.Parties.GetParty(session.PartyIndex);
        if (party != null && party.FindMember((short)session.CharacterId) >= 0)
            return party;

        session.PartyIndex = NoParty;
        session.IsPartyLeader = false;
        return null;
    }

    private static async Task SendErrorAsync(UserSession session, short errorCode)
    {
        var result = PartyPacketWriter.Rejected(errorCode);
        await session.Client.SendPacket(result);
    }

}
