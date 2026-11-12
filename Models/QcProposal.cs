using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SmartUnrealLocalization.Models
{
    /// <summary>How serious a QC finding is (drives sorting, bulk actions, reports).</summary>
    public enum QcSeverity
    {
        Low,
        Medium,
        High,
        Critical,
    }

    /// <summary>Where the row stands in the QC review flow.</summary>
    public enum QcVerdict
    {
        /// <summary>Waiting for the user's Fix / Keep decision.</summary>
        Pending,
        /// <summary>User accepted the suggestion — the target was rewritten.</summary>
        Fixed,
        /// <summary>User rejected it — the row stays as-is and is not re-proposed.</summary>
        Kept,
    }

    /// <summary>
    /// One QC suggestion for one row: what is wrong, and the concrete replacement
    /// text proposed for Target. Proposals live in a per-file sidecar
    /// (<c>&lt;file&gt;.qc.json</c>) so a review survives an app or PC restart —
    /// they are NEVER written into the exported .locres/.po/.csv, and they do not
    /// touch the row until the user presses Fix.
    /// </summary>
    public sealed class QcProposal
    {
        [JsonProperty("ns")] public string Namespace { get; set; } = "";
        [JsonProperty("key")] public string Key { get; set; } = "";

        /// <summary>Target text at the moment the proposal was produced (staleness check).</summary>
        [JsonProperty("before")] public string Before { get; set; } = "";

        /// <summary>Proposed replacement for Target.</summary>
        [JsonProperty("after")] public string After { get; set; } = "";

        /// <summary>Short human-readable explanation shown in the diff popup and the CSV report.</summary>
        [JsonProperty("reason")] public string Reason { get; set; } = "";

        /// <summary>Machine identifiers of the checks that fired (e.g. "LanguageLeak").</summary>
        [JsonProperty("kinds")] public List<string> Kinds { get; set; } = new();

        // Written as names, not ordinals: a sidecar outlives the app version that
        // produced it, and reordering either enum would silently turn a dismissed
        // row into a pending one. Newtonsoft still READS old numeric values.
        [JsonProperty("severity")]
        [JsonConverter(typeof(StringEnumConverter))]
        public QcSeverity Severity { get; set; } = QcSeverity.Medium;

        [JsonProperty("verdict")]
        [JsonConverter(typeof(StringEnumConverter))]
        public QcVerdict Verdict { get; set; } = QcVerdict.Pending;

        /// <summary>The AI model id, or "mcp" for a verdict sent through MCP. "deterministic"
        /// marks a suggestion from QC's former local pass (removed 2026-10-21) — the
        /// default, so an old sidecar entry without a source reads as one.</summary>
        [JsonProperty("source")] public string Source { get; set; } = "deterministic";

        /// <summary>
        /// Findings this proposal's replacement does NOT repair (e.g. a missing
        /// tag alongside a punctuation fix). Applying the fix therefore does not
        /// close the row: these are re-raised as a flag-only suggestion so a
        /// Critical problem can never be buried by an unrelated mechanical fix.
        /// </summary>
        [JsonProperty("unresolvedKinds")] public List<string> UnresolvedKinds { get; set; } = new();

        [JsonProperty("unresolvedReason")] public string UnresolvedReason { get; set; } = "";

        [JsonProperty("unresolvedSeverity")]
        [JsonConverter(typeof(StringEnumConverter))]
        public QcSeverity UnresolvedSeverity { get; set; } = QcSeverity.Low;

        [JsonProperty("createdUtc")] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Which column the reviewed translation came from. For a single-language
        /// file (every .locres) the translation sits in Source and Target is empty,
        /// reserved for accepted fixes — so staleness must be measured against
        /// Source there, not against the still-empty Target.
        /// </summary>
        [JsonProperty("resultInSource")] public bool ResultInSource { get; set; }

        /// <summary>The text this proposal was made about, read from the right column.</summary>
        public string CurrentTextIn(TranslationRow row)
            => (ResultInSource ? row.Source : row.Target) ?? "";

        /// <summary>
        /// True when the reviewed text no longer equals <see cref="Before"/> — the
        /// row was edited after this proposal was made, so applying it blindly
        /// would revert that edit. Stale proposals are shown but never bulk-applied.
        /// </summary>
        public bool IsStaleFor(TranslationRow row) => IsStaleFor(CurrentTextIn(row));

        public bool IsStaleFor(string? currentText)
            => !string.Equals(currentText ?? "", Before, StringComparison.Ordinal);
    }

    /// <summary>On-disk shape of <c>&lt;file&gt;.qc.json</c>.</summary>
    public sealed class QcSidecar
    {
        [JsonProperty("version")] public int Version { get; set; } = 1;

        /// <summary>When the last QC run finished (shown in the panel).</summary>
        [JsonProperty("lastRunUtc")] public DateTime? LastRunUtc { get; set; }

        /// <summary>How many rows the last run examined (for the "1700/13000" log line).</summary>
        [JsonProperty("lastCheckedRows")] public int LastCheckedRows { get; set; }

        [JsonProperty("proposals")] public List<QcProposal> Proposals { get; set; } = new();
    }
}
