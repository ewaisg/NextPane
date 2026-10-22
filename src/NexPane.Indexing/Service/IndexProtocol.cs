using System.Buffers.Binary;
using System.Text;

namespace NexPane.Indexing.Service;

/// <summary>Frame types of the Index Service protocol (§7.5).</summary>
public enum FrameType : ushort
{
    Hello = 1,
    ListVolumes = 2,
    VolumeInfo = 3,
    Enumerate = 4,
    EntryBatch = 5,
    EnumerateDone = 6,
    Subscribe = 7,
    ChangeBatch = 8,
    Error = 9,
}

/// <summary>Frame flags.</summary>
[Flags]
public enum FrameOptions : ushort
{
    None = 0,

    /// <summary>Last <see cref="FrameType.VolumeInfo"/> of a listing.</summary>
    Final = 1,
}

public abstract record IndexMessage
{
    public abstract FrameType Type { get; }
}

public sealed record HelloMessage(uint Version) : IndexMessage
{
    public override FrameType Type => FrameType.Hello;
}

public sealed record ListVolumesMessage : IndexMessage
{
    public override FrameType Type => FrameType.ListVolumes;
}

public sealed record VolumeInfoMessage(string VolumeGuid, string Root, uint Serial, string FileSystem, ulong JournalId, long NextUsn, ulong RootFileReference) : IndexMessage
{
    public override FrameType Type => FrameType.VolumeInfo;
}

public sealed record EnumerateMessage(string VolumeGuid) : IndexMessage
{
    public override FrameType Type => FrameType.Enumerate;
}

/// <summary>One enumerated entry.</summary>
public readonly record struct WireEntry(ulong FileReference, ulong ParentReference, uint Attributes, long Size, long ModifiedUtc, long CreatedUtc, string Name);

public sealed record EntryBatchMessage(IReadOnlyList<WireEntry> Entries) : IndexMessage
{
    public override FrameType Type => FrameType.EntryBatch;
}

public sealed record EnumerateDoneMessage(long NextUsn) : IndexMessage
{
    public override FrameType Type => FrameType.EnumerateDone;
}

public sealed record SubscribeMessage(string VolumeGuid, long FromUsn) : IndexMessage
{
    public override FrameType Type => FrameType.Subscribe;
}

/// <summary>One journal change.</summary>
public readonly record struct WireChange(uint Reason, ulong FileReference, ulong ParentReference, uint Attributes, string Name, long Usn);

public sealed record ChangeBatchMessage(IReadOnlyList<WireChange> Changes) : IndexMessage
{
    public override FrameType Type => FrameType.ChangeBatch;
}

public sealed record ErrorMessage(int Code, string Message) : IndexMessage
{
    public override FrameType Type => FrameType.Error;
}

/// <summary>Thrown for malformed frames; the connection must be closed.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
    {
    }

    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Little-endian length-prefixed frames <c>[u32 length][u16 type][u16 flags][payload]</c> (§7.5, P3.13). The length
/// covers type, flags and payload and is capped at <see cref="MaxFrameLength"/>; element counts are validated against
/// the payload size before anything is allocated, so hostile frames cannot cause unbounded allocation.
/// </summary>
public static class IndexProtocol
{
    public const uint Version = 1;
    public const string PipeName = "NexPane.Index.v1";
    public const int MaxFrameLength = 8 * 1024 * 1024;
    public const int MaxStringChars = 32 * 1024;
    private const int HeaderLength = 4;

    // ------------------------------------------------------------------ encoding

    public static byte[] Encode(IndexMessage message, FrameOptions flags = FrameOptions.None)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = new List<byte>(64);
        switch (message)
        {
            case HelloMessage m:
                U32(payload, m.Version);
                break;
            case ListVolumesMessage:
                break;
            case VolumeInfoMessage m:
                Str(payload, m.VolumeGuid);
                Str(payload, m.Root);
                U32(payload, m.Serial);
                Str(payload, m.FileSystem);
                U64(payload, m.JournalId);
                U64(payload, unchecked((ulong)m.NextUsn));
                U64(payload, m.RootFileReference);
                break;
            case EnumerateMessage m:
                Str(payload, m.VolumeGuid);
                break;
            case EntryBatchMessage m:
                U32(payload, (uint)m.Entries.Count);
                foreach (var e in m.Entries)
                {
                    U64(payload, e.FileReference);
                    U64(payload, e.ParentReference);
                    U32(payload, e.Attributes);
                    U64(payload, unchecked((ulong)e.Size));
                    U64(payload, unchecked((ulong)e.ModifiedUtc));
                    U64(payload, unchecked((ulong)e.CreatedUtc));
                    Str(payload, e.Name);
                }

                break;
            case EnumerateDoneMessage m:
                U64(payload, unchecked((ulong)m.NextUsn));
                break;
            case SubscribeMessage m:
                Str(payload, m.VolumeGuid);
                U64(payload, unchecked((ulong)m.FromUsn));
                break;
            case ChangeBatchMessage m:
                U32(payload, (uint)m.Changes.Count);
                foreach (var c in m.Changes)
                {
                    U32(payload, c.Reason);
                    U64(payload, c.FileReference);
                    U64(payload, c.ParentReference);
                    U32(payload, c.Attributes);
                    Str(payload, c.Name);
                    U64(payload, unchecked((ulong)c.Usn));
                }

                break;
            case ErrorMessage m:
                U32(payload, unchecked((uint)m.Code));
                Str(payload, m.Message);
                break;
            default:
                throw new ArgumentException($"Unknown message {message.GetType().Name}.", nameof(message));
        }

        var length = 4 + payload.Count;
        if (length > MaxFrameLength)
        {
            throw new ProtocolException($"Frame of {length} bytes exceeds the {MaxFrameLength}-byte limit; split the batch.");
        }

        var frame = new byte[HeaderLength + length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)message.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)flags);
        payload.CopyTo(frame, 8);
        return frame;
    }

    public static async Task WriteAsync(Stream stream, IndexMessage message, CancellationToken ct, FrameOptions flags = FrameOptions.None)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(Encode(message, flags), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ decoding

    /// <summary>Reads one frame; null at a clean end of stream.</summary>
    public static async Task<(IndexMessage Message, FrameOptions Flags)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        var read = await stream.ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderLength)
        {
            throw new ProtocolException("Truncated frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 4 || length > MaxFrameLength)
        {
            throw new ProtocolException($"Invalid frame length {length}.");
        }

        var body = new byte[length];
        if (await stream.ReadAtLeastAsync(body, (int)length, throwOnEndOfStream: false, ct).ConfigureAwait(false) < length)
        {
            throw new ProtocolException("Truncated frame body.");
        }

        return Decode(body);
    }

    /// <summary>Decodes <c>[u16 type][u16 flags][payload]</c>.</summary>
    public static (IndexMessage Message, FrameOptions Flags) Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            throw new ProtocolException("Frame shorter than its type and flags.");
        }

        var type = (FrameType)BinaryPrimitives.ReadUInt16LittleEndian(body);
        var flags = (FrameOptions)BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        var reader = new Reader(body[4..]);
        IndexMessage message = type switch
        {
            FrameType.Hello => new HelloMessage(reader.U32()),
            FrameType.ListVolumes => new ListVolumesMessage(),
            FrameType.VolumeInfo => new VolumeInfoMessage(reader.Str(), reader.Str(), reader.U32(), reader.Str(), reader.U64(), unchecked((long)reader.U64()), reader.U64()),
            FrameType.Enumerate => new EnumerateMessage(reader.Str()),
            FrameType.EntryBatch => DecodeEntries(ref reader),
            FrameType.EnumerateDone => new EnumerateDoneMessage(unchecked((long)reader.U64())),
            FrameType.Subscribe => new SubscribeMessage(reader.Str(), unchecked((long)reader.U64())),
            FrameType.ChangeBatch => DecodeChanges(ref reader),
            FrameType.Error => new ErrorMessage(unchecked((int)reader.U32()), reader.Str()),
            _ => throw new ProtocolException($"Unknown frame type {(ushort)type}."),
        };
        if (!reader.AtEnd)
        {
            throw new ProtocolException($"{reader.Remaining} trailing bytes in a {type} frame.");
        }

        return (message, flags);
    }

    private static EntryBatchMessage DecodeEntries(ref Reader reader)
    {
        const int minimumEntry = 8 + 8 + 4 + 8 + 8 + 8 + 2;
        var count = reader.U32();
        if (count > reader.Remaining / minimumEntry)
        {
            throw new ProtocolException($"Entry count {count} exceeds what the payload can hold.");
        }

        var entries = new WireEntry[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = new WireEntry(reader.U64(), reader.U64(), reader.U32(), unchecked((long)reader.U64()), unchecked((long)reader.U64()),
                unchecked((long)reader.U64()), reader.Str());
        }

        return new EntryBatchMessage(entries);
    }

    private static ChangeBatchMessage DecodeChanges(ref Reader reader)
    {
        const int minimumChange = 4 + 8 + 8 + 4 + 2 + 8;
        var count = reader.U32();
        if (count > reader.Remaining / minimumChange)
        {
            throw new ProtocolException($"Change count {count} exceeds what the payload can hold.");
        }

        var changes = new WireChange[count];
        for (var i = 0; i < count; i++)
        {
            changes[i] = new WireChange(reader.U32(), reader.U64(), reader.U64(), reader.U32(), reader.Str(), unchecked((long)reader.U64()));
        }

        return new ChangeBatchMessage(changes);
    }

    private static void U32(List<byte> b, uint v)
    {
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
        b.AddRange(s);
    }

    private static void U64(List<byte> b, ulong v)
    {
        Span<byte> s = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(s, v);
        b.AddRange(s);
    }

    private static void Str(List<byte> b, string value)
    {
        if (value.Length > MaxStringChars)
        {
            throw new ProtocolException($"String of {value.Length} characters exceeds the protocol limit.");
        }

        Span<byte> s = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)value.Length);
        b.AddRange(s);
        b.AddRange(Encoding.Unicode.GetBytes(value));
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly int Remaining => _data.Length - _position;

        public readonly bool AtEnd => _position == _data.Length;

        public uint U32()
        {
            Require(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += 4;
            return v;
        }

        public ulong U64()
        {
            Require(8);
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            _position += 8;
            return v;
        }

        public string Str()
        {
            Require(2);
            var chars = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += 2;
            Require(chars * 2);
            var s = Encoding.Unicode.GetString(_data.Slice(_position, chars * 2));
            _position += chars * 2;
            return s;
        }

        private readonly void Require(int bytes)
        {
            if (bytes < 0 || _position + bytes > _data.Length)
            {
                throw new ProtocolException("Frame payload ends early.");
            }
        }
    }
}
