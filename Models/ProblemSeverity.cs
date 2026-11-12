namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Validation problem severity. Underlying int values are ordered so that
    /// <c>Max</c> over a collection yields the strongest signal:
    /// <c>Error &gt; Warning &gt; Info</c>. <c>TranslationRow.Severity</c> relies on
    /// this ordering when collapsing a row's <c>Problems</c> down to one
    /// severity for the left-edge stripe + status-icon column.
    /// </summary>
    public enum ProblemSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }
}
