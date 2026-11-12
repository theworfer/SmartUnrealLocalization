namespace Locres.IO;

/// <summary>Base class for all LocresIO errors.</summary>
public class LocresException : Exception
{
    public LocresException(string message) : base(message) { }
    public LocresException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The input is not a structurally valid .locres file (bad magic heuristics,
/// out-of-range counts/indexes/offsets, truncated data). Carries the byte
/// offset and the section being parsed so the message is actionable.
/// </summary>
public sealed class LocresFormatException : LocresException
{
    /// <summary>Byte offset in the input at which the violation was detected.</summary>
    public long Offset { get; }

    /// <summary>Human-readable parse context, e.g. "namespace 3, entry 41".</summary>
    public string Section { get; }

    public LocresFormatException(long offset, string section, string message)
        : base($"{message} (at offset 0x{offset:X}, {section})")
    {
        Offset = offset;
        Section = section;
    }
}

/// <summary>
/// The file declares a version byte this library does not know. Refusing is
/// deliberate: guessing a layout for an unknown version would corrupt data on
/// a later save.
/// </summary>
public sealed class LocresUnsupportedVersionException : LocresException
{
    public byte VersionByte { get; }

    public LocresUnsupportedVersionException(byte versionByte)
        : base($"Unsupported .locres version byte {versionByte}. " +
               $"Supported versions: 0 (Legacy) through 3 (Optimized CityHash64).")
    {
        VersionByte = versionByte;
    }
}
