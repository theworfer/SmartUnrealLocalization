using System;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// The catch of an <c>async void</c> event handler. An exception that leaves
    /// such a handler has no caller to return to: it is rethrown on the UI thread
    /// and ends the process. A button that fails — a locked file, a folder that went
    /// away — must say so in the log instead of closing the app with unsaved work.
    /// Every <c>async void</c> handler ends in a catch that calls this; a source
    /// test keeps it that way.
    /// </summary>
    public static class HandlerGuard
    {
        public static void Report(Exception ex, string handler)
        {
            if (ex is OperationCanceledException) return;         // a cancelled action is not a failure
            Console.WriteLine($"[handler] {handler}: {ex}");
            try
            {
                MainWindowViewModel.Current?.AddLog(
                    string.Format(Loc.T("status.msg.error"), ex.Message), LogLevel.Error);
            }
            catch (Exception logEx) { Console.WriteLine($"[handler] log failed: {logEx.Message}"); }
        }
    }
}
