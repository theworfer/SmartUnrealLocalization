using System.Collections.Generic;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// One pluggable validation rule. Implementations append zero or more
    /// <see cref="Problem"/>s to <paramref name="output"/> when the row violates
    /// the rule. Stateless — instances may be shared across rows and threads.
    ///
    /// <para>
    /// Convention: <see cref="Kind"/> matches the Problem.Kind written by this
    /// validator, so <c>ValidationService.RemoveProblems(row, kind)</c> can
    /// strip exactly the issues this validator produced without touching others.
    /// </para>
    /// </summary>
    public interface IValidator
    {
        /// <summary>Machine identifier, also used as the <c>Problem.Kind</c> tag.</summary>
        string Kind { get; }

        /// <summary>Inspect <paramref name="row"/> and append findings to <paramref name="output"/>.</summary>
        void Validate(TranslationRow row, List<Problem> output);
    }
}
