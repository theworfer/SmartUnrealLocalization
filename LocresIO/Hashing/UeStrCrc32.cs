namespace Locres.IO.Hashing;

/// <summary>
/// Unreal Engine's string CRC-32 variant used for Optimized (v2) .locres
/// namespace/key hashes: standard reflected CRC-32 (polynomial 0xEDB88320,
/// init 0xFFFFFFFF, final XOR), but each UTF-16 code unit is fed as FOUR
/// bytes — its 32-bit zero-extension, lowest byte first. The two zero bytes
/// still advance the CRC state and must not be skipped. No null terminator,
/// no case folding. The empty string hashes to 0.
/// </summary>
internal static class UeStrCrc32
{
    // Standard reflected IEEE table, generated at runtime — no vendored tables.
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Hash(ReadOnlySpan<char> text)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (char c in text)
        {
            uint ch = c;
            crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
            ch >>= 8;
            crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
            ch >>= 8;
            crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
            ch >>= 8;
            crc = (crc >> 8) ^ Table[(crc ^ ch) & 0xFF];
        }
        return ~crc;
    }
}
