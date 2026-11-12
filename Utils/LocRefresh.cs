using System;
using Avalonia.Controls;
using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Keeping code-set text in step with a language switch.
    ///
    /// <para>Almost everything on screen binds through <c>Loc.Instance</c>'s
    /// indexer and re-reads by itself. A handful of things cannot: a
    /// <c>ComboBox</c> whose items are built in code, and the placeholder
    /// labels a panel writes once ("no file selected"). Those need to be told,
    /// and this is how they are told — the <see cref="Loc.LanguageChanged"/>
    /// event, which existed with no subscribers until the switch became a
    /// button in the header rather than a setting you changed once.</para>
    ///
    /// <para>The subscription follows the control in and out of the visual tree.
    /// <c>Loc.Instance</c> lives as long as the process, so a panel that
    /// subscribed in its constructor and never unsubscribed would be held alive
    /// by it forever — a leak that would only show up in a session where panels
    /// are opened and closed many times.</para>
    /// </summary>
    public static class LocRefresh
    {
        /// <summary>
        /// Run <paramref name="relocalize"/> whenever the interface language
        /// changes, for as long as <paramref name="control"/> is attached.
        /// </summary>
        public static void RefreshOnLanguageChange(this Control control, Action relocalize)
        {
            void Handler() => relocalize();

            control.AttachedToVisualTree += (_, _) =>
            {
                Loc.Instance.LanguageChanged -= Handler;   // idempotent: a control
                Loc.Instance.LanguageChanged += Handler;   // can re-attach
            };
            control.DetachedFromVisualTree += (_, _) => Loc.Instance.LanguageChanged -= Handler;
        }
    }
}
