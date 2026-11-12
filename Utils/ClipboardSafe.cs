using System;
using System.Threading.Tasks;
using Avalonia.Input.Platform;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// The only way the app reads or writes the clipboard.
    ///
    /// <para>On Windows the clipboard is one global lock: while another program
    /// holds it (a clipboard manager, a remote-desktop session, Excel mid-copy)
    /// Avalonia retries for about a second and then throws. Every copy and paste
    /// handler is an <c>async void</c> event handler, and an exception there ends
    /// the process — so a busy clipboard used to close the app. These methods
    /// report failure instead; the caller tells the user and, for Cut, keeps the
    /// text it could not copy. <c>ClipboardSafetyTests</c> fails on any direct
    /// clipboard call elsewhere.</para>
    /// </summary>
    public static class ClipboardSafe
    {
        /// <summary>False when there is no clipboard or it refused the text.</summary>
        public static Task<bool> TrySetTextAsync(IClipboard? clipboard, string text)
            => clipboard == null ? Task.FromResult(false) : TryAsync(() => clipboard.SetTextAsync(text));

        /// <summary>
        /// <c>Ok</c> is false only when the clipboard could not be read; an empty
        /// clipboard is <c>Ok</c> with a null or empty <c>Text</c>.
        /// </summary>
        public static async Task<(bool Ok, string? Text)> TryGetTextAsync(IClipboard? clipboard)
        {
            if (clipboard == null) return (false, null);
            try { return (true, await clipboard.TryGetTextAsync()); }
            catch (Exception ex) { Report(ex); return (false, null); }
        }

        internal static async Task<bool> TryAsync(Func<Task> operation)
        {
            try { await operation(); return true; }
            catch (Exception ex) { Report(ex); return false; }
        }

        private static void Report(Exception ex) => Console.WriteLine($"[Clipboard] {ex.GetType().Name}: {ex.Message}");
    }
}
