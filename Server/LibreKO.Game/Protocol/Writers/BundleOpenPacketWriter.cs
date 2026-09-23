using LibreKO.Common.Infrastructure.Network;

namespace LibreKO.Game.Protocol.Writers;

public sealed class BundleOpenPacketWriter
{
    public const int WireSlots = 12;
    public const int EntryBytes = 6;
    public const byte Empty = 0;
    public const byte Listed = 1;
    public const byte Refused = 2;

    public readonly record struct Entry(int ItemId, ushort Count);

    private readonly List<Entry> _entries = [];

    public int BundleId { get; set; }

    public BundleOpenPacketWriter Add(int itemId, ushort count)
    {
        if (_entries.Count < WireSlots)
            _entries.Add(new Entry(itemId, count));

        return this;
    }

    public static Packet Refusal(int bundleId)
    {
        var packet = new Packet(GameOpcodes.GS_BUNDLE_OPEN_REQ);
        packet.WriteInt(bundleId);
        packet.WriteByte(Refused);
        return packet;
    }

    public Packet Build()
    {
        var packet = new Packet(GameOpcodes.GS_BUNDLE_OPEN_REQ);
        packet.WriteInt(BundleId);

        if (_entries.Count == 0)
        {
            packet.WriteByte(Empty);
            return packet;
        }

        packet.WriteByte(Listed);
        for (var index = 0; index < WireSlots; index++)
        {
            var entry = index < _entries.Count ? _entries[index] : default;
            packet.WriteInt(entry.ItemId);
            packet.WriteUShort(entry.Count);
        }

        return packet;
    }
}
