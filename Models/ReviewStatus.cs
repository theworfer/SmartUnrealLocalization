namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Reviewer-set status for a translation row, persisted per-file in
    /// %AppData%/SmartUnrealLocalization/reviews/{filename-no-ext}.json via
    /// <see cref="SmartUnrealLocalization.Services.ReviewService"/>.
    ///
    /// Trinary by design — Reject must be explicitly visible, not just
    /// "approved vs unmarked":
    /// <list type="bullet">
    ///   <item><c>None</c>     — untouched, no left-edge stripe contribution.</item>
    ///   <item><c>Approved</c> — green stripe on the row's left edge.</item>
    ///   <item><c>Rejected</c> — red stripe on the row's left edge (same red as a
    ///     validation Error; the status-icon column disambiguates with a ✗ glyph).</item>
    /// </list>
    /// </summary>
    public enum ReviewStatus
    {
        None = 0,
        Approved = 1,
        Rejected = 2,
    }
}
