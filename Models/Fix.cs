using System;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Auto-fix proposal attached to a <see cref="Problem"/>. Invoking
    /// <see cref="Apply"/> mutates the row's Target — the change flows through
    /// the standard <c>TranslationRow.Target</c> setter so the existing
    /// UndoManager capture, EditedAt touch, and ValidationService re-run
    /// pipelines all do the right thing automatically.
    ///
    /// <para>
    /// Default behaviour: <c>row.Target = NewValue</c>. Fixes that need
    /// richer behaviour (e.g. surgical span replacement instead of whole-text
    /// swap) pass a <see cref="CustomApply"/> delegate.
    /// </para>
    /// </summary>
    public sealed class Fix
    {
        /// <summary>User-facing label shown on the popover button + toast.</summary>
        public string Label { get; init; } = "";

        /// <summary>Target value before the fix (kept for tests + toast diff).</summary>
        public string OldValue { get; init; } = "";

        /// <summary>Target value after the fix when using the default <see cref="Apply"/>.</summary>
        public string NewValue { get; init; } = "";

        /// <summary>
        /// Optional override of the default Apply behaviour. Set this when a
        /// whole-text swap is not the right semantic (e.g. a multi-fix that
        /// only touches a specific Range).
        /// </summary>
        public Action<TranslationRow>? CustomApply { get; init; }

        public void Apply(TranslationRow row)
        {
            if (CustomApply is not null) { CustomApply(row); return; }
            row.Target = NewValue;
        }
    }
}
