using System.Text;
using Locres.IO.Hashing;
using Xunit;

namespace Locres.IO.Tests;

public class StrCrc32Tests
{
    /// <summary>
    /// Independent slow reference: bit-by-bit reflected CRC-32 built straight
    /// from the polynomial definition (no lookup table), feeding each UTF-16
    /// code unit as the four little-endian bytes of its 32-bit zero-extension.
    /// Validates both our runtime-generated table and the 4-step-per-char walk.
    /// </summary>
    private static uint ReferenceStrCrc32(string s)
    {
        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            return crc;
        }

        uint crc = 0xFFFFFFFFu;
        foreach (char c in s)
        {
            uint v = c;
            crc = Step(crc, (byte)(v & 0xFF));
            crc = Step(crc, (byte)((v >> 8) & 0xFF));
            crc = Step(crc, 0);
            crc = Step(crc, 0);
        }
        return ~crc;
    }

    public static IEnumerable<object[]> Samples()
    {
        yield return new object[] { "" };
        yield return new object[] { "A" };
        yield return new object[] { "Test" };
        yield return new object[] { "LOC_KEY_001" };
        yield return new object[] { "F4265D6E4A6D8DBF3A99BB9DE3D80E7F" };
        yield return new object[] { "Привіт, світ" };
        yield return new object[] { "日本語テキスト" };
        yield return new object[] { "emoji \U0001F3AE mix" };
        yield return new object[] { "á combining" };
        yield return new object[] { new string('x', 300) + "кінець" };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void MatchesBitwiseReference(string text)
    {
        Assert.Equal(ReferenceStrCrc32(text), LocresHash.Crc32(text));
    }

    [Fact]
    public void EmptyStringHashesToZero()
    {
        Assert.Equal(0u, LocresHash.Crc32(""));
    }

    [Fact]
    public void ZeroByteStepsAreNotSkippable()
    {
        // If an implementation skipped the two zero-byte table steps per char,
        // these two would collide with plain byte-wise CRC of ASCII. Guard by
        // checking against the reference, which always performs all 4 steps.
        Assert.NotEqual(LocresHash.Crc32("AB"), LocresHash.Crc32("BA"));
        Assert.Equal(ReferenceStrCrc32("AB"), LocresHash.Crc32("AB"));
    }
}

public class CityHash64UeTests
{
    private const ulong K2 = 0x9ae16a3b2f90404fUL;

    [Fact]
    public void EmptyInputReturnsK2()
    {
        // Mathematical anchor from the algorithm: len == 0 short-circuits to K2.
        Assert.Equal(K2, CityHash64Ue.Hash(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void EmptyStringReducesToZeroViaSpecialCase()
    {
        // The v3 key hash special-cases "" → 0 (CityHash64 of empty is K2 ≠ 0).
        Assert.Equal(0u, LocresHash.CityHashUtf16(""));
        Assert.NotEqual(0u, LocresHash.Reduce(K2));
    }

    [Theory]
    // Every length bucket and its boundaries: 0to16 sub-branches (1-3, 4-7, 8-16),
    // 17to32, 33to64, and the >64 chunked loop incl. non-multiple-of-64 tails.
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(192)]
    public void AllLengthBucketsAreDeterministicAndDistinct(int length)
    {
        byte[] data = MakePattern(length);
        ulong first = CityHash64Ue.Hash(data);
        ulong second = CityHash64Ue.Hash(data);
        Assert.Equal(first, second);

        // Flipping one byte must change the hash (avalanche sanity).
        data[length / 2] ^= 0x5A;
        Assert.NotEqual(first, CityHash64Ue.Hash(data));
    }

    [Fact]
    public void PrefixLengthsProduceDistinctHashes()
    {
        // 18 nested prefixes of one buffer — a collision here would be ~2^-64
        // per pair and indicates a length-bucket bug, not bad luck.
        byte[] data = MakePattern(192);
        int[] lengths = { 1, 3, 4, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 192 };
        var seen = new HashSet<ulong>();
        foreach (int len in lengths)
            Assert.True(seen.Add(CityHash64Ue.Hash(data.AsSpan(0, len))), $"collision at prefix {len}");
    }

    [Fact]
    public void Utf16PathMatchesExplicitEncoding()
    {
        // The zero-copy MemoryMarshal path must equal hashing Encoding.Unicode
        // bytes — for BMP, surrogate pairs, and combining marks alike.
        foreach (var s in new[] { "Key", "Ключ_01", "\U0001F3AE\U0001F0CF", "ȩ́x", "ns/Sub/Key" })
        {
            ulong viaEncoding = CityHash64Ue.Hash(Encoding.Unicode.GetBytes(s));
            Assert.Equal(LocresHash.Reduce(viaEncoding), LocresHash.CityHashUtf16(s));
        }
    }

    [Theory]
    [InlineData(0x0000000000000000UL)]
    [InlineData(0x00000000FFFFFFFFUL)]
    [InlineData(0xFFFFFFFF00000000UL)]
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(0x123456789ABCDEF0UL)]
    [InlineData(0x9ae16a3b2f90404fUL)]
    public void ReduceMatchesIndependentArithmetic(ulong h)
    {
        // (uint)h + (uint)(h >> 32) * 23, wrapping mod 2^32 — computed here
        // with 64-bit math and an explicit mod as an independent check.
        ulong low = h & 0xFFFFFFFFUL;
        ulong high = h >> 32;
        uint expected = (uint)((low + high * 23UL) % 4294967296UL);
        Assert.Equal(expected, LocresHash.Reduce(h));
    }

    private static byte[] MakePattern(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)((i * 131 + 7) & 0xFF);
        return data;
    }
}
