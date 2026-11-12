namespace Locres.IO;

/// <summary>
/// On-disk format version of a .locres file. The byte value is stored in the
/// file header (except <see cref="Legacy"/>, which has no header at all).
/// </summary>
public enum LocresVersion : byte
{
    /// <summary>No magic/header; namespaces and values stored inline, no string table.</summary>
    Legacy = 0,

    /// <summary>Values deduplicated into a trailing string table referenced by index.</summary>
    Compact = 1,

    /// <summary>Compact + entry count, CRC32-prehashed namespace/key names, per-string ref counts.</summary>
    Optimized = 2,

    /// <summary>Optimized, but namespace/key hashes use CityHash64 over UTF-16LE reduced to 32 bits.</summary>
    OptimizedCityHash64 = 3,
}
