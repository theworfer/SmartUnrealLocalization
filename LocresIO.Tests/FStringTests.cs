using Locres.IO;
using Xunit;

namespace Locres.IO.Tests;

public class FStringTests
{
    private static byte[] WriteToBytes(string value, bool forceUnicode = false)
    {
        using var ms = new MemoryStream();
        var scratch = new ScratchBuffer();
        try
        {
            UnrealString.Write(ms, value, forceUnicode, ref scratch);
        }
        finally
        {
            scratch.Dispose();
        }
        return ms.ToArray();
    }

    private static string ReadFromBytes(byte[] data)
    {
        var reader = new SpanReader(data);
        return UnrealString.Read(ref reader, null);
    }

    // ---- spec-derived byte-exact vectors ----

    [Fact]
    public void NarrowAscii_LengthIncludesTerminator()
    {
        // "AsCiI" → len 6 (5 chars + NUL), Latin-1 bytes, trailing 0x00
        byte[] expected = { 0x06, 0x00, 0x00, 0x00, (byte)'A', (byte)'s', (byte)'C', (byte)'i', (byte)'I', 0x00 };
        Assert.Equal(expected, WriteToBytes("AsCiI"));
    }

    [Fact]
    public void EmptyString_WritesAsLenOnePlusNul_NeverLenZero()
    {
        byte[] expected = { 0x01, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(expected, WriteToBytes(""));
    }

    [Fact]
    public void EmptyString_ForcedUnicode_WritesMinusOnePlusWideNul()
    {
        byte[] expected = { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00 };
        Assert.Equal(expected, WriteToBytes("", forceUnicode: true));
    }

    [Fact]
    public void Wide_PrefixIsNegativeUnitCountIncludingTerminator()
    {
        // "юа" → 2 units + terminator = 3 → prefix -3 (FD FF FF FF),
        // UTF-16LE: ю = 044E → 4E 04, а = 0430 → 30 04, then 00 00
        byte[] expected = { 0xFD, 0xFF, 0xFF, 0xFF, 0x4E, 0x04, 0x30, 0x04, 0x00, 0x00 };
        Assert.Equal(expected, WriteToBytes("юа"));
    }

    [Fact]
    public void SurrogatePair_CountsAsTwoUnits()
    {
        // 🎮 U+1F3AE = D83C DFAE → 2 units + terminator = 3 → prefix -3
        byte[] bytes = WriteToBytes("\U0001F3AE");
        Assert.Equal(-3, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(10, bytes.Length); // 4 prefix + 4 pair + 2 terminator
        Assert.Equal("\U0001F3AE", ReadFromBytes(bytes));
    }

    [Fact]
    public void ReaderAcceptsLenZeroAsEmpty()
    {
        byte[] data = { 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal("", ReadFromBytes(data));
    }

    // ---- encoding policy ----

    [Fact]
    public void Latin1HighBytes_ReadLosslessly()
    {
        // narrow payload é(E9) è(E8) ç(E7) + NUL, len 4 — original tool would
        // corrupt these to '?'; we must decode Latin-1 byte-for-byte
        byte[] data = { 0x04, 0x00, 0x00, 0x00, 0xE9, 0xE8, 0xE7, 0x00 };
        Assert.Equal("éèç", ReadFromBytes(data));
    }

    [Fact]
    public void Latin1Range_ForcesWideOnWrite()
    {
        // policy: narrow is pure-ASCII only; é must round-trip via wide
        byte[] bytes = WriteToBytes("café");
        Assert.True(BitConverter.ToInt32(bytes, 0) < 0, "0x80–0xFF must be written wide");
        Assert.Equal("café", ReadFromBytes(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain ASCII 123")]
    [InlineData("Привіт, світ!")]
    [InlineData("日本語テキスト")]
    [InlineData("\U0001F3AE\U0001F0CF emoji")]
    [InlineData("é combining acute")]
    [InlineData("line\r\nbreak\ttab \"quotes\"")]
    public void RoundTrip_PreservesValueExactly(string value)
    {
        Assert.Equal(value, ReadFromBytes(WriteToBytes(value)));
        Assert.Equal(value, ReadFromBytes(WriteToBytes(value, forceUnicode: true)));
    }

    [Fact]
    public void MissingTerminator_IsToleratedAndKeptWhole()
    {
        // len 3 narrow, payload "ABC" with no NUL — tolerated, nothing stripped
        byte[] data = { 0x03, 0x00, 0x00, 0x00, (byte)'A', (byte)'B', (byte)'C' };
        Assert.Equal("ABC", ReadFromBytes(data));
    }

    [Fact]
    public void StripsExactlyOneTerminator()
    {
        // payload "A\0\0" (len 3): one NUL is the terminator, the second is data
        byte[] data = { 0x03, 0x00, 0x00, 0x00, (byte)'A', 0x00, 0x00 };
        Assert.Equal("A\0", ReadFromBytes(data));
    }

    // ---- robustness ----

    [Fact]
    public void TruncatedNarrowPayload_Throws()
    {
        byte[] data = { 0x0A, 0x00, 0x00, 0x00, (byte)'A', (byte)'B' }; // says 10, has 2
        Assert.Throws<LocresFormatException>(() => ReadFromBytes(data));
    }

    [Fact]
    public void TruncatedWidePayload_Throws()
    {
        byte[] data = { 0xFB, 0xFF, 0xFF, 0xFF, 0x41, 0x00 }; // says 5 units, has 1
        Assert.Throws<LocresFormatException>(() => ReadFromBytes(data));
    }

    [Fact]
    public void IntMinValuePrefix_Throws()
    {
        byte[] data = { 0x00, 0x00, 0x00, 0x80 }; // int.MinValue — negation overflows
        Assert.Throws<LocresFormatException>(() => ReadFromBytes(data));
    }

    [Fact]
    public void TruncatedPrefix_Throws()
    {
        byte[] data = { 0x06, 0x00 };
        Assert.Throws<LocresFormatException>(() => ReadFromBytes(data));
    }

    [Fact]
    public void ErrorCarriesOffsetAndSection()
    {
        var data = new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x41 };
        var reader = new SpanReader(data) { Section = "namespace 2, key 7" };
        try
        {
            UnrealString.Read(ref reader, null);
            Assert.Fail("expected LocresFormatException");
        }
        catch (LocresFormatException ex)
        {
            Assert.Contains("namespace 2, key 7", ex.Message);
            Assert.Equal("namespace 2, key 7", ex.Section);
            Assert.Equal(4, ex.Offset); // failed right after the prefix
        }
    }

    [Fact]
    public void InternerIsApplied()
    {
        byte[] data = WriteToBytes("Shared");
        var pool = new Dictionary<string, string>();
        string Intern(string s) => pool.TryGetValue(s, out var e) ? e : pool[s] = s;

        var r1 = new SpanReader(data);
        var first = UnrealString.Read(ref r1, Intern);
        var r2 = new SpanReader(data);
        var second = UnrealString.Read(ref r2, Intern);
        Assert.Same(first, second);
    }
}
