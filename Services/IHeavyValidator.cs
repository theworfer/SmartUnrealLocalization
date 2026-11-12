using System.Collections.Generic;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Cross-row / expensive validator. Runs against the whole row set instead
    /// of one row at a time (the light <see cref="IValidator"/> contract).
    /// Examples: DuplicateSource (cross-row), LengthExcessive (per-row but
    /// cheap-enough that batching is fine), PluralIncomplete (per-row regex).
    ///
    /// <para>
    /// Heavy validators run only on file open (inside the
    /// <see cref="Controls.ValidationProgressDialog"/>) and on F8 / Project →
    /// Run Validation. They are NOT debounced per-edit because re-scanning
    /// every row on every keystroke would be wasteful.
    /// </para>
    /// </summary>
    public interface IHeavyValidator
    {
        /// <summary>Machine identifier, also used as <c>Problem.Kind</c>.</summary>
        string Kind { get; }

        /// <summary>
        /// Inspect all <paramref name="rows"/> and append findings directly to
        /// each row's <c>Problems</c> collection. Caller (ValidationService)
        /// is responsible for clearing prior heavy-kind problems first.
        /// </summary>
        void ValidateAll(IReadOnlyList<TranslationRow> rows);
    }
}
