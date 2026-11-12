namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Visual / semantic flavour of a <see cref="ToastNotification"/>.
    /// Drives the left-accent stripe colour and the leading glyph that the
    /// Toast control renders. Mirrors <see cref="ProblemSeverity"/> for
    /// validator-driven toasts (Apply Fix etc.) but kept as its own enum so
    /// non-validator toasts (Save, Replace All, …) don't have to pretend to
    /// be Problems.
    /// </summary>
    public enum ToastKind
    {
        /// <summary>Informational message — blue stripe + ⓘ glyph.</summary>
        Info,

        /// <summary>Operation succeeded — green stripe + ✓ glyph.</summary>
        Success,

        /// <summary>Warning — yellow stripe + ⚠ glyph.</summary>
        Warning,

        /// <summary>Error — red stripe + ⓧ glyph.</summary>
        Error,
    }
}
