using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Locres.IO;

/// <summary>
/// Unreal-style serialized string (FString): an int32 length prefix that
/// INCLUDES the null terminator, followed by the payload.
///
/// <para>Len == 0 → empty string, no payload. Len &gt; 0 → narrow payload of
/// Len bytes ending in 0x00; we decode narrow bytes as Latin-1 (lossless — the
/// engine only ever writes pure-ASCII narrow, but foreign tools may not).
/// Len &lt; 0 → UTF-16LE payload of |Len| code units (surrogate pair = 2
/// units) ending in a 0x0000 unit.</para>
///
/// <para>On write, a string is narrow only when every code unit is ≤ 0x7F
/// (matching the engine's conservative choice); anything else — including
/// Latin-1 range 0x80–0xFF — is written wide. The empty string is written as
/// Len=1 + 0x00 (never Len=0), byte-compatible with UnrealLocres output.
/// Exactly ONE trailing terminator is stripped on read; a payload that lacks
/// its terminator is tolerated and kept whole.</para>
/// </summary>
internal static class UnrealString
{
    public static string Read(ref SpanReader r, Func<string, string>? interner)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";

        string result;
        if (len > 0)
        {
            ReadOnlySpan<byte> bytes = r.ReadBytes(len);
            if (bytes.Length > 0 && bytes[^1] == 0)
                bytes = bytes[..^1];
            result = Encoding.Latin1.GetString(bytes);
        }
        else
        {
            if (len == int.MinValue)
                throw r.Error("String length prefix -2147483648 is invalid");
            int units = -len;
            long byteCount = (long)units * 2;
            if (byteCount > r.Remaining)
                throw r.Error($"UTF-16 string of {units} units needs {byteCount} bytes, {r.Remaining} left");
            ReadOnlySpan<byte> bytes = r.ReadBytes(units * 2);
            if (bytes.Length >= 2 && bytes[^1] == 0 && bytes[^2] == 0)
                bytes = bytes[..^2];
            result = Encoding.Unicode.GetString(bytes);
        }

        return interner is null ? result : interner(result);
    }

    public static void Write(Stream stream, string value, bool forceUnicode, ref ScratchBuffer scratch)
    {
        Span<byte> prefix = stackalloc byte[4];

        if (!forceUnicode && Ascii.IsValid(value))
        {
            int len = value.Length + 1; // + terminator
            BinaryPrimitives.WriteInt32LittleEndian(prefix, len);
            stream.Write(prefix);

            byte[] buf = scratch.Ensure(len);
            for (int i = 0; i < value.Length; i++)
                buf[i] = (byte)value[i];
            buf[value.Length] = 0;
            stream.Write(buf, 0, len);
        }
        else
        {
            int units = value.Length + 1; // + terminator; surrogate halves are already 2 units in Length
            BinaryPrimitives.WriteInt32LittleEndian(prefix, -units);
            stream.Write(prefix);

            int byteCount = units * 2;
            byte[] buf = scratch.Ensure(byteCount);
            // Copy raw UTF-16 code units — byte-preserving even for lone
            // surrogates that Encoding.Unicode would replace with U+FFFD.
            if (BitConverter.IsLittleEndian)
            {
                MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(buf);
            }
            else
            {
                for (int i = 0; i < value.Length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(i * 2), value[i]);
            }
            buf[byteCount - 2] = 0;
            buf[byteCount - 1] = 0;
            stream.Write(buf, 0, byteCount);
        }
    }
}

/// <summary>
/// A single reusable pooled byte buffer for string encoding during a save.
/// Rent-once, grow-on-demand, returned on Dispose — keeps the write path free
/// of per-string allocations without touching the LOH.
/// </summary>
internal struct ScratchBuffer : IDisposable
{
    private byte[]? _array;

    public byte[] Ensure(int size)
    {
        if (_array is null || _array.Length < size)
        {
            if (_array is not null)
                ArrayPool<byte>.Shared.Return(_array);
            _array = ArrayPool<byte>.Shared.Rent(Math.Max(size, 512));
        }
        return _array;
    }

    public void Dispose()
    {
        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
            _array = null;
        }
    }
}
