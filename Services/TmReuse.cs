using System;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Pure reuse-grouping rules for AI translation. Two rows may share ONE
    /// translation (persistent TM entry, session cache, or same-source
    /// propagation) only when they carry the SAME reuse tag — the per-row
    /// discriminator a meaningful sent Key or an additional-language Reference
    /// provides. This is what stops a reference that legitimately disambiguates
    /// two identical sources (Weapon.Charge "Зарядити" vs Store.Charge "Плата")
    /// from being blind-copied, while two rows with the SAME source AND the same
    /// reference (or no context at all) still auto-repeat as before.
    /// </summary>
    public static class TmReuse
    {
        // Built with a U+001F separator (0x1F) — a unit-separator control char that
        // cannot occur in a game key/namespace or real source text, so a
        // reference-based tag can never collide with a key-based tag or a plain
        // source. Key-based tags stay RAW (unprefixed) for backward compatibility
        // with key-aware TM entries written before references existed.
        public static readonly string RefTagPrefix = "ref" + (char)0x1F;

        /// <summary>
        /// The reuse discriminator for a row: a meaningful sent key wins (most
        /// specific and stable), else the reference text, else null (plain
        /// source-only reuse — the historical behavior).
        /// </summary>
        public static string? Tag(string? contextKey, string? referenceText)
        {
            if (!string.IsNullOrEmpty(contextKey)) return contextKey;
            if (!string.IsNullOrEmpty(referenceText)) return RefTagPrefix + referenceText;
            return null;
        }

        /// <summary>
        /// Two rows may share a single translation iff neither is comment-bound
        /// (a comment is a per-row instruction that must reach the AI) and they
        /// have the same source AND the same reuse tag (both null = plain generic).
        /// </summary>
        public static bool CanShare(string? sourceA, string? tagA, bool commentA,
                                    string? sourceB, string? tagB, bool commentB)
            => !commentA && !commentB
               && string.Equals(sourceA, sourceB, StringComparison.Ordinal)
               && string.Equals(tagA, tagB, StringComparison.Ordinal);
    }
}
