using FluentAssertions;
using LibreKO.Common.Enums;
using LibreKO.Common.Infrastructure.Network;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LibreKO.Game.Tests;

public class PartyAuthorityTests : GameTestBase
{
    private const byte Moradon = 21;
    private const byte PartyChat = (byte)ChatType.Party;

    [Fact]
    public async Task AnInviteeIsNotAMemberUntilTheyAccept()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 700, AccountNation.Karus);
        var invitee = CreateMember(sessionManager, 701, AccountNation.Karus);

        await parties.HandleAsync(leader.Client, InviteRequest(PartyRequest.Create, invitee.Name));

        invitee.IsInParty.Should().BeFalse();
        var party = sessionManager.Parties.GetParty(leader.PartyIndex)!;
        party.FindMember((short)invitee.CharacterId).Should().BeLessThan(0);
    }

    [Fact]
    public async Task APendingInviteeCannotDisbandTheParty()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 702, AccountNation.Karus);
        var member = CreateMember(sessionManager, 703, AccountNation.Karus);
        var invitee = CreateMember(sessionManager, 704, AccountNation.Karus);
        var party = await FormPartyAsync(parties, sessionManager, leader, member);

        await parties.HandleAsync(leader.Client, InviteRequest(PartyRequest.Insert, invitee.Name));
        await parties.HandleAsync(invitee.Client, Disband());

        sessionManager.Parties.GetParty(party.Index).Should().BeSameAs(party);
        party.MemberCount.Should().Be(2);
    }

    [Fact]
    public async Task OnlyTheLeaderDisbandsTheParty()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 705, AccountNation.Karus);
        var member = CreateMember(sessionManager, 706, AccountNation.Karus);
        var other = CreateMember(sessionManager, 707, AccountNation.Karus);
        var party = await FormPartyAsync(parties, sessionManager, leader, member, other);

        await parties.HandleAsync(member.Client, Disband());

        sessionManager.Parties.GetParty(party.Index).Should().BeSameAs(party);
        party.MemberCount.Should().Be(3);

        await parties.HandleAsync(leader.Client, Disband());

        sessionManager.Parties.GetParty(party.Index).Should().BeNull();
        member.IsInParty.Should().BeFalse();
        other.IsInParty.Should().BeFalse();
    }

    [Fact]
    public async Task OnlyTheLeaderInvitesNewMembers()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 708, AccountNation.Karus);
        var member = CreateMember(sessionManager, 709, AccountNation.Karus);
        var outsider = CreateMember(sessionManager, 710, AccountNation.Karus);
        var party = await FormPartyAsync(parties, sessionManager, leader, member);

        await parties.HandleAsync(member.Client, InviteRequest(PartyRequest.Insert, outsider.Name));
        await parties.HandleAsync(outsider.Client, Answer(accept: true));

        party.FindMember((short)outsider.CharacterId).Should().BeLessThan(0);
        outsider.IsInParty.Should().BeFalse();
    }

    [Fact]
    public async Task OnlyTheLeaderKicksOtherMembers()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 711, AccountNation.Karus);
        var member = CreateMember(sessionManager, 712, AccountNation.Karus);
        var other = CreateMember(sessionManager, 713, AccountNation.Karus);
        var party = await FormPartyAsync(parties, sessionManager, leader, member, other);

        await parties.HandleAsync(member.Client, Kick(other));

        party.FindMember((short)other.CharacterId).Should().BeGreaterThan(0);
        other.PartyIndex.Should().Be(party.Index);
    }

    [Fact]
    public async Task APartyNeverSpansTwoNations()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 714, AccountNation.Karus);
        var enemy = CreateMember(sessionManager, 715, AccountNation.ElMorad);

        await parties.HandleAsync(leader.Client, InviteRequest(PartyRequest.Create, enemy.Name));
        await parties.HandleAsync(enemy.Client, Answer(accept: true));

        enemy.IsInParty.Should().BeFalse();
        leader.IsInParty.Should().BeFalse();
    }

    [Fact]
    public async Task AnExpiredInviteCannotBeAccepted()
    {
        using var provider = CreateHarness(out var clock);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 716, AccountNation.Karus);
        var member = CreateMember(sessionManager, 717, AccountNation.Karus);
        var late = CreateMember(sessionManager, 718, AccountNation.Karus);
        var party = await FormPartyAsync(parties, sessionManager, leader, member);

        await parties.HandleAsync(leader.Client, InviteRequest(PartyRequest.Insert, late.Name));
        clock.Advance(PartyManager.InviteLifetime + TimeSpan.FromSeconds(1));
        await parties.HandleAsync(late.Client, Answer(accept: true));

        party.FindMember((short)late.CharacterId).Should().BeLessThan(0);
        late.IsInParty.Should().BeFalse();
    }

    [Fact]
    public async Task APendingInviteeHearsNoPartyChat()
    {
        using var provider = CreateHarness(out _);
        var (parties, sessionManager) = Services(provider);
        var leader = CreateMember(sessionManager, 719, AccountNation.Karus);
        var member = CreateMember(sessionManager, 720, AccountNation.Karus);
        var invitee = CreateMember(sessionManager, 721, AccountNation.Karus);
        await FormPartyAsync(parties, sessionManager, leader, member);
        await parties.HandleAsync(leader.Client, InviteRequest(PartyRequest.Insert, invitee.Name));
        leader.Client.ClearReceivedCalls();

        await provider.GetRequiredService<IChatPacketCoordinator>().HandleAsync(invitee, PartyChat, "spy");

        await leader.Client.DidNotReceive().SendPacket(
            Arg.Is<Packet>(packet => packet.GetOpcode() == (byte)GameOpcodes.GS_CHAT), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConcurrentJoinsNeverShareASlot()
    {
        var party = new PartyManager().CreateParty(1);

        var joined = Enumerable.Range(2, 64)
            .AsParallel()
            .Count(memberId => party.TryAddMember((short)memberId));

        joined.Should().Be(PartyGroup.MaxMembers - 1);
        party.MemberIds.Distinct().Should().HaveCount(PartyGroup.MaxMembers);
    }

    [Fact]
    public void ConcurrentPartiesGetDistinctIndices()
    {
        var parties = new PartyManager();

        var indices = Enumerable.Range(1, 2_000)
            .AsParallel()
            .Select(leaderId => parties.CreateParty((short)leaderId).Index)
            .ToList();

        indices.Should().OnlyHaveUniqueItems();
        indices.Should().OnlyContain(index => parties.GetParty(index) != null);
    }

    private static async Task<PartyGroup> FormPartyAsync(
        IPartyPacketCoordinator parties, SessionManager sessionManager, UserSession leader, params UserSession[] members)
    {
        var request = PartyRequest.Create;
        foreach (var member in members)
        {
            await parties.HandleAsync(leader.Client, InviteRequest(request, member.Name));
            await parties.HandleAsync(member.Client, Answer(accept: true));
            request = PartyRequest.Insert;
        }

        var party = sessionManager.Parties.GetParty(leader.PartyIndex)!;
        party.MemberCount.Should().Be(members.Length + 1);
        return party;
    }

    private static (IPartyPacketCoordinator Parties, SessionManager Sessions) Services(ServiceProvider provider)
        => (provider.GetRequiredService<IPartyPacketCoordinator>(), provider.GetRequiredService<SessionManager>());

    private static ServiceProvider CreateHarness(out ManualClock clock)
    {
        var manualClock = new ManualClock();
        clock = manualClock;
        return CreateProvider(_ => { }, configureServices: services => services.AddSingleton<TimeProvider>(manualClock));
    }

    private static Packet InviteRequest(PartyRequest request, string targetName)
    {
        var packet = new Packet(GameOpcodes.GS_PARTY);
        packet.WriteByte((byte)request);
        packet.WriteString(targetName);
        return packet;
    }

    private static Packet Answer(bool accept)
    {
        var packet = new Packet(GameOpcodes.GS_PARTY);
        packet.WriteByte((byte)PartyRequest.Permit);
        packet.WriteByte(accept ? (byte)1 : (byte)0);
        return packet;
    }

    private static Packet Disband()
    {
        var packet = new Packet(GameOpcodes.GS_PARTY);
        packet.WriteByte((byte)PartyRequest.Delete);
        return packet;
    }

    private static Packet Kick(UserSession member)
    {
        var packet = new Packet(GameOpcodes.GS_PARTY);
        packet.WriteByte((byte)PartyRequest.Remove);
        packet.WriteInt(member.CharacterId);
        return packet;
    }

    private static UserSession CreateMember(SessionManager sessionManager, int characterId, AccountNation nation)
    {
        var client = Substitute.For<IClient>();
        client.Id.Returns(Guid.NewGuid());
        client.CharacterId.Returns(characterId);
        client.SendPacket(Arg.Any<Packet>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var session = sessionManager.CreateSession(client, characterId, accountId: characterId + 100);
        session.Name = $"Member{characterId}";
        session.Class = 105;
        session.Level = 40;
        session.Nation = nation;
        session.ZoneId = Moradon;
        session.X = 100;
        session.Z = 100;
        session.Hp = 300;
        session.MaxHp = 300;
        sessionManager.Regions.AddToRegion(session);
        return session;
    }
}
