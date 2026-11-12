namespace Locres.IO;

/// <summary>Options for <see cref="LocresDocument.Save(string, LocresSaveOptions?, System.Threading.CancellationToken)"/>.</summary>
public sealed class LocresSaveOptions
{
    /// <summary>
    /// Write in a different format version than the document was loaded from
    /// (full v0↔v1↔v2↔v3 conversion — hashes and table layout adapt
    /// automatically). Null = keep the document's own version.
    /// </summary>
    public LocresVersion? TargetVersion { get; set; }
}

/// <summary>Outcome counters of a save.</summary>
/// <param name="EntriesWritten">Total entries written (always the document's full entry count).</param>
/// <param name="OverridesApplied">Entries whose value was substituted from the overrides map (duplicate entries each count).</param>
/// <param name="OverridesSkippedEmpty">Override keys carrying a null/empty value — the base value was kept (the format-safety rule).</param>
/// <param name="OverridesUnknownKey">Override keys that matched no entry in the document.</param>
public readonly record struct LocresSaveResult(
    int EntriesWritten,
    int OverridesApplied,
    int OverridesSkippedEmpty,
    int OverridesUnknownKey);
