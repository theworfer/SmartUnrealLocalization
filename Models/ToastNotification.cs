using System;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// One floating toast. The <see cref="Services.ToastService"/> owns the
    /// queue and the auto-dismiss timer; this class is a passive POCO that
    /// the <see cref="Controls.Toast"/> control binds to. <see cref="Glyph"/>
    /// and <see cref="StripeBrushKey"/> are derived from <see cref="Kind"/>
    /// so the XAML can stay simple (no value converters required).
    /// </summary>
    public sealed class ToastNotification
    {
        /// <summary>Stable id; used by the service to dismiss this specific toast.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Body text rendered next to the glyph.</summary>
        public string Message { get; init; } = "";

        /// <summary>Severity / flavour.</summary>
        public ToastKind Kind { get; init; } = ToastKind.Info;

        /// <summary>
        /// Optional callback wired to the "Undo" affordance. When null the
        /// Undo button is hidden. Common payload: <c>() =&gt; vm.UndoManager.Undo()</c>.
        /// </summary>
        public Action? UndoAction { get; init; }

        /// <summary>
        /// Auto-dismiss delay. Default 4 s — matches the handoff preview and
        /// the established VS Code / IntelliJ toast cadence. Pass 0 to make
        /// the toast sticky (only manual X closes it; rarely needed).
        /// </summary>
        public int DurationMs { get; init; } = 4000;

        /// <summary>Glyph used by the Toast control's left icon slot.</summary>
        public string Glyph => Kind switch
        {
            ToastKind.Success => Utils.Glyphs.Ok, // ✓
            ToastKind.Warning => Utils.Glyphs.Warning, // ⚠
            ToastKind.Error => Utils.Glyphs.ErrorRound, // ⓧ
            _ => Utils.Glyphs.InfoRound, // ⓘ
        };

        /// <summary>
        /// Theme-resource key for the 3 px left accent stripe. The Toast
        /// control resolves this via <c>DynamicResource</c> so dark/light
        /// follows the user's theme without extra plumbing.
        /// </summary>
        public string StripeBrushKey => Kind switch
        {
            ToastKind.Success => "SuccessColor",
            ToastKind.Warning => "WarningColor",
            ToastKind.Error => "DangerColor",
            _ => "InfoColor",
        };

        /// <summary>True when the Undo button should render.</summary>
        public bool HasUndo => UndoAction is not null;
    }
}
