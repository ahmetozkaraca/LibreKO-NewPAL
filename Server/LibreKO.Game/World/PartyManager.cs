using System.Collections.Concurrent;

namespace LibreKO.Game.World;

public class PartyGroup
{
    public const int MaxMembers = 8;
    public const short NoMember = -1;
    public const int SmallestParty = 2;

    private readonly Lock _sync = new();

    public int Index { get; }
    public short[] MemberIds { get; } = new short[MaxMembers];

    public short TargetNumberId { get; set; } = -1;

    public short CommandLeaderId { get; set; } = -1;

    public bool IsCommandLeader(short charId)
        => charId == LeaderId || (CommandLeaderId > 0 && charId == CommandLeaderId);

    public PartyGroup(int index)
    {
        Index = index;
        for (int i = 0; i < MaxMembers; i++)
            MemberIds[i] = NoMember;
    }

    public int MemberCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxMembers; i++)
                if (MemberIds[i] >= 0) count++;
            return count;
        }
    }

    public short LeaderId => MemberIds[0];

    public bool IsLeader(int charId) => LeaderId >= 0 && LeaderId == charId;

    public int FindEmptySlot()
    {
        for (int i = 0; i < MaxMembers; i++)
            if (MemberIds[i] < 0) return i;
        return -1;
    }

    public int FindMember(short memberId)
    {
        for (int i = 0; i < MaxMembers; i++)
            if (MemberIds[i] == memberId) return i;
        return -1;
    }

    public bool TryAddMember(short memberId)
    {
        using var scope = _sync.EnterScope();
        if (LeaderId < 0 || FindMember(memberId) >= 0)
            return false;

        var slot = FindEmptySlot();
        if (slot < 0)
            return false;

        MemberIds[slot] = memberId;
        return true;
    }

    public PartyRemoval Remove(short memberId, out short[] disbandedMembers)
    {
        using var scope = _sync.EnterScope();
        disbandedMembers = [];
        var slot = FindMember(memberId);
        if (slot < 0)
            return PartyRemoval.NotMember;

        if (slot == 0 || MemberCount <= SmallestParty)
        {
            disbandedMembers = Disband();
            return PartyRemoval.Disbanded;
        }

        MemberIds[slot] = NoMember;
        return PartyRemoval.Removed;
    }

    public bool TryPromote(short leaderId, short newLeaderId)
    {
        using var scope = _sync.EnterScope();
        if (LeaderId != leaderId)
            return false;

        var slot = FindMember(newLeaderId);
        if (slot <= 0)
            return false;

        (MemberIds[0], MemberIds[slot]) = (MemberIds[slot], MemberIds[0]);
        return true;
    }

    public short[] Disband()
    {
        using var scope = _sync.EnterScope();
        var members = MemberIds.Where(memberId => memberId >= 0).ToArray();
        Array.Fill(MemberIds, NoMember);
        return members;
    }
}

public enum PartyRemoval
{
    NotMember,
    Removed,
    Disbanded,
}

public readonly record struct PartyInvite(int PartyIndex, DateTimeOffset ExpiresAt);

public class PartyManager
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<int, PartyGroup> _parties = new();
    private readonly ConcurrentDictionary<int, PartyInvite> _invites = new();
    private int _lastIndex;

    public PartyGroup? GetParty(int index)
    {
        _parties.TryGetValue(index, out var party);
        return party;
    }

    public PartyGroup CreateParty(short leaderId)
    {
        var party = new PartyGroup(Interlocked.Increment(ref _lastIndex));
        party.MemberIds[0] = leaderId;
        _parties[party.Index] = party;
        return party;
    }

    public void DeleteParty(int index)
    {
        _parties.TryRemove(index, out _);
    }

    public int? Invite(int inviteeId, int partyIndex, DateTimeOffset now)
    {
        var replaced = _invites.TryGetValue(inviteeId, out var previous) && previous.PartyIndex != partyIndex
            ? previous.PartyIndex
            : (int?)null;
        _invites[inviteeId] = new PartyInvite(partyIndex, now + InviteLifetime);
        return replaced;
    }

    public List<int> SweepLapsedInvites(DateTimeOffset now)
    {
        var lapsed = new List<int>();
        foreach (var pending in _invites)
        {
            if (pending.Value.ExpiresAt < now && _invites.TryRemove(pending))
                lapsed.Add(pending.Value.PartyIndex);
        }

        return lapsed;
    }

    public bool HasLiveInvites(int partyIndex, DateTimeOffset now)
        => _invites.Values.Any(invite => invite.PartyIndex == partyIndex && invite.ExpiresAt >= now);

    public bool TryTakeInvite(int inviteeId, out PartyInvite invite)
        => _invites.TryRemove(inviteeId, out invite);
}
