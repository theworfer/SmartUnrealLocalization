namespace Locres.IO;

/// <summary>
/// One localized entry: a key, the engine's hash of the ORIGINAL source
/// string, and the localized value.
/// </summary>
public sealed class LocresEntry
{
    /// <summary>Entry key within its namespace (often a GUID-like identifier).</summary>
    public string Key { get; }

    /// <summary>
    /// Hash of the original (native-culture) source text, computed by the
    /// engine when the file was cooked. It CANNOT be recomputed from this
    /// file's data — LocresIO carries it through verbatim on every save.
    /// The engine compares it against the current source text to detect
    /// stale translations.
    /// </summary>
    public uint SourceStringHash { get; }

    /// <summary>The localized string stored in the file.</summary>
    public string Value { get; internal set; }

    internal LocresEntry(string key, uint sourceStringHash, string value)
    {
        Key = key;
        SourceStringHash = sourceStringHash;
        Value = value;
    }
}
