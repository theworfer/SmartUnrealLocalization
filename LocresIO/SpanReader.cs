using System.Buffers.Binary;

namespace Locres.IO;

/// <summary>
/// Forward-only little-endian reader over a byte span with strict bounds
/// checking. Every read past the end throws <see cref="LocresFormatException"/>
/// carrying the byte offset and the current parse <see cref="Section"/>.
/// </summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    /// <summary>Parse context used in error messages, e.g. "namespace 3, entry 41".</summary>
    public string Section;

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        Section = "header";
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;
    public int Length => _data.Length;

    public byte ReadByte()
    {
        Need(1);
        return _data[_pos++];
    }

    public int ReadInt32()
    {
        Need(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_pos));
        _pos += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos));
        _pos += 4;
        return v;
    }

    public long ReadInt64()
    {
        Need(8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_pos));
        _pos += 8;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Need(count);
        ReadOnlySpan<byte> s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }

    /// <summary>Absolute reposition (used for the string-table offset jump).</summary>
    public void Seek(long offset)
    {
        if (offset < 0 || offset > _data.Length)
            throw new LocresFormatException(_pos, Section,
                $"Offset 0x{offset:X} is outside the file (size 0x{_data.Length:X})");
        _pos = (int)offset;
    }

    /// <summary>Build a format error at the current position with section context.</summary>
    public readonly LocresFormatException Error(string message)
        => new(_pos, Section, message);

    private readonly void Need(int count)
    {
        if (count < 0 || _data.Length - _pos < count)
            throw new LocresFormatException(_pos, Section,
                $"Unexpected end of data: need {count} bytes, {_data.Length - _pos} left");
    }
}
