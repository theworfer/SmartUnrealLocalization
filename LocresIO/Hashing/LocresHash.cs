using System.Runtime.InteropServices;
using System.Text;

namespace Locres.IO.Hashing;

/// <summary>
/// The two hash functions Unreal Engine uses for .locres namespace/key
/// pre-hashing, exposed for consumers that need to compute them directly.
/// </summary>
public static class LocresHash
{
    /// <summary>
    /// v2 (Optimized) namespace/key hash: UE's StrCrc32 — reflected CRC-32
    /// where every UTF-16 code unit is processed as four bytes. "" → 0.
    /// </summary>
    public static uint Crc32(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UeStrCrc32.Hash(text.AsSpan());
    }

    /// <summary>
    /// v3 (OptimizedCityHash64) namespace/key hash: CityHash64 over the
    /// UTF-16LE bytes of the string (no terminator), reduced to 32 bits via
    /// <c>(uint)h + (uint)(h >> 32) * 23</c>. "" → 0 (special case — CityHash64
    /// of an empty buffer is NOT zero).
    /// </summary>
    public static uint CityHashUtf16(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return 0;

        ulong h = BitConverter.IsLittleEndian
            ? CityHash64Ue.Hash(MemoryMarshal.AsBytes(text.AsSpan()))
            : CityHash64Ue.Hash(Encoding.Unicode.GetBytes(text));
        return Reduce(h);
    }

    internal static uint Reduce(ulong hash)
        => unchecked((uint)hash + (uint)(hash >> 32) * 23);
}
