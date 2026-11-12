using System;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// One validation finding attached to a <see cref="TranslationRow"/>.
    /// Lifecycle owned by ValidationService (Etap 2+): added when a validator
    /// detects an issue, removed when the user fixes Target or applies a Fix.
    ///
    /// <para>
    /// <see cref="Range"/> is the half-open character span inside Target the
    /// wavy-underline / find-hit overlay highlights (Etap 4). Null means
    /// "applies to the whole cell" (e.g. <c>EmptyTarget</c> — no specific span).
    /// </para>
    ///
    /// <para>
    /// <see cref="Fix"/> is the optional auto-fix; presence drives the
    /// lightbulb 💡 icon in the Target cell (Etap 5).
    /// </para>
    /// </summary>
    public sealed class Problem
    {
        /// <summary>Stable identity for batching, popover anchoring, undo grouping.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// Short machine identifier of the validator that produced this problem
        /// (e.g. <c>"TagMismatch"</c>, <c>"EmptyTarget"</c>). Lower-case-with-dashes
        /// is fine too. Used for the small uppercase label in the popover header
        /// and for grouping in the Problems tab.
        /// </summary>
        public string Kind { get; init; } = "";

        public ProblemSeverity Severity { get; init; }

        /// <summary>Short single-line title shown in popover + Problems tab.</summary>
        public string Title { get; init; } = "";

        /// <summary>Multi-line detail body for the popover.</summary>
        public string Detail { get; init; } = "";

        /// <summary>Optional character span inside Target.</summary>
        public TextRange? Range { get; init; }

        /// <summary>Optional auto-fix. Non-null ⇒ row.HasFixableProblem is true.</summary>
        public Fix? Fix { get; init; }
    }

    /// <summary>
    /// Half-open character range <c>[Start, Start+Length)</c> inside a Target
    /// (or Source) string. Used by <c>TaggedTextBlock.ProblemSpans</c> and
    /// <c>TaggedTextBlock.FindHits</c> in Etap 4.
    /// </summary>
    public readonly record struct TextRange(int Start, int Length)
    {
        public int End => Start + Length;
    }
}
