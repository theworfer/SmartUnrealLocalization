using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// What the app leaves behind when it dies or freezes.
    ///
    /// <para>Until 2026-10-19 a crash left nothing but a Windows event nobody
    /// would look for, and every unsaved edit went with the process. Now, on an
    /// unhandled exception:</para>
    /// <list type="number">
    /// <item>unsaved edits of every open tab are written to a <c>Recovered_*</c>
    /// folder next to the auto-saves, through the same save code a manual save
    /// uses (only on the UI thread, where the rows can be read consistently —
    /// which is where every crash on record happened);</item>
    /// <item>a plain-text report goes to <c>%AppData%\SmartUnrealLocalization\crash-reports</c>:
    /// versions, what was open, the last log lines and the full exception.
    /// Never the API key, never translation text;</item>
    /// <item>the process still terminates. Keeping a window alive after an
    /// unknown failure is how a corrupted state gets saved over good work.</item>
    /// </list>
    /// <para>A watchdog thread writes a "hang" report when the UI thread has not
    /// answered for <see cref="HangThreshold"/>. On the next start the app says
    /// what happened and where the report and recovered files are.</para>
    /// </summary>
    public static class CrashReporter
    {
        public static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(10);
        private const int MaxHangReportsPerSession = 3;
        private const int LogLinesInReport = 25;

        public static string ReportsDir => Path.Combine(AppConfig.ConfigDir(), "crash-reports");

        private static Func<string>? _describeState;
        private static Func<IReadOnlyList<string>>? _recoverUnsaved;
        private static Func<IReadOnlyList<string>>? _recentLog;
        private static int _fatalHandled;
        private static readonly DateTime _started = DateTime.Now;

        // Refreshed by every watchdog ping, ON the UI thread — what a hang report
        // (written while the UI thread is stuck) can still say about the state.
        private static volatile string _lastState = "";
        private static volatile IReadOnlyList<string> _lastLog = Array.Empty<string>();

        /// <summary>Hook process-wide failures. Call once, before the UI starts.</summary>
        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(e.ExceptionObject as Exception);
            // Not fatal on .NET 8 — but always a bug, and otherwise invisible.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                TryWrite(ReportsDir, "task", BuildReport("unobserved task exception", e.Exception, SafeState(), Array.Empty<string>(), null, _lastLog));
                e.SetObserved();
            };
        }

        /// <summary>Give the reporter read access to the app: state, rescue, log.
        /// Every callback is invoked on the UI thread only.</summary>
        public static void Attach(Func<string> describeState, Func<IReadOnlyList<string>> recoverUnsaved, Func<IReadOnlyList<string>> recentLog)
        {
            _describeState = describeState;
            _recoverUnsaved = recoverUnsaved;
            _recentLog = recentLog;
        }

        private static void OnFatal(Exception? ex)
        {
            if (Interlocked.Exchange(ref _fatalHandled, 1) != 0) return;

            var recovered = new List<string>();
            string? recoveryNote = null;
            IReadOnlyList<string> log = _lastLog;
            string state = _lastState;
            if (OnUiThread())
            {
                try { recovered.AddRange(_recoverUnsaved?.Invoke() ?? Array.Empty<string>()); }
                catch (Exception rx) { recoveryNote = "recovery failed: " + rx.Message; }
                state = SafeState();
                try { log = _recentLog?.Invoke() ?? log; } catch { }
            }
            else
            {
                recoveryNote = "not attempted: the failure was on a background thread, where the rows cannot be read consistently";
            }
            TryWrite(ReportsDir, "crash", BuildReport("crash", ex, state, recovered, recoveryNote, log));
        }

        // ── hang watchdog ──────────────────────────────────────────────

        public static void StartHangWatchdog()
        {
            var thread = new Thread(WatchdogLoop) { IsBackground = true, Name = "SUL hang watchdog" };
            thread.Start();
        }

        private static void WatchdogLoop()
        {
            int reports = 0;
            while (true)
            {
                using var answered = new ManualResetEventSlim();
                Dispatcher.UIThread.Post(() =>
                {
                    _lastState = SafeState();
                    try { _lastLog = _recentLog?.Invoke() ?? _lastLog; } catch { }
                    answered.Set();
                }, DispatcherPriority.Send);

                var waited = Stopwatch.StartNew();
                // Twice, two seconds apart: a machine resuming from sleep makes ONE
                // long wait look like a hang; a real hang survives the second look.
                if (!answered.Wait(HangThreshold) && !answered.Wait(TimeSpan.FromSeconds(2)))
                {
                    string? path = reports < MaxHangReportsPerSession
                        ? TryWrite(ReportsDir, "hang", BuildReport(
                            $"hang (the window has not responded for {waited.Elapsed.TotalSeconds:0} s)",
                            null, _lastState, Array.Empty<string>(), null, _lastLog))
                        : null;
                    reports++;
                    answered.Wait();   // until it recovers (or the user kills it)
                    if (path != null)
                        TryAppend(path, $"{Environment.NewLine}Recovered after {waited.Elapsed.TotalSeconds:0.0} s.{Environment.NewLine}");
                }
                Thread.Sleep(1000);
            }
        }

        // ── report text ────────────────────────────────────────────────

        internal static string BuildReport(string kind, Exception? ex, string state,
                                           IReadOnlyList<string> recovered, string? recoveryNote,
                                           IReadOnlyList<string> recentLog)
        {
            var sb = new StringBuilder();
            var now = DateTimeOffset.Now;
            sb.AppendLine("Smart Unreal Localization — problem report");
            sb.AppendLine($"Kind: {kind}");
            sb.AppendLine($"Time: {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Version: {AppVersion()}  |  .NET {Environment.Version}  |  {RuntimeInformation.OSDescription}  |  {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Running for: {DateTime.Now - _started:hh\\:mm\\:ss}");
            foreach (var r in recovered) sb.AppendLine($"Recovered: {r}");
            if (recoveryNote != null) sb.AppendLine($"Recovery: {recoveryNote}");
            sb.AppendLine();
            sb.AppendLine("== State ==");
            sb.AppendLine(string.IsNullOrWhiteSpace(state) ? "(unavailable)" : state.TrimEnd());
            sb.AppendLine();
            sb.AppendLine($"== Last {LogLinesInReport} log lines ==");
            foreach (var line in recentLog.TakeLast(LogLinesInReport)) sb.AppendLine(line);
            if (ex != null)
            {
                sb.AppendLine();
                sb.AppendLine("== Exception ==");
                sb.AppendLine(ex.ToString());
            }
            return sb.ToString();
        }

        private static string AppVersion()
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(CrashReporter).Assembly.GetName().Version;
            return v?.ToString(3) ?? "?";
        }

        // ── files ──────────────────────────────────────────────────────

        internal static string? TryWrite(string dir, string kind, string text)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{kind}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch { return null; }   // a reporter that throws would replace the real error
        }

        private static void TryAppend(string path, string text)
        {
            try { File.AppendAllText(path, text, Encoding.UTF8); } catch { }
        }

        /// <summary>A report not yet shown to the user.</summary>
        public sealed record PendingReport(string Path, string Kind, DateTime Time, IReadOnlyList<string> Recovered);

        /// <summary>
        /// Reports written since the user last heard about one — crashes and hangs
        /// of earlier sessions. Each is returned once: a marker file remembers it.
        /// </summary>
        public static IReadOnlyList<PendingReport> TakeUnseenReports() => TakeUnseenReports(ReportsDir);

        internal static IReadOnlyList<PendingReport> TakeUnseenReports(string dir)
        {
            var result = new List<PendingReport>();
            try
            {
                if (!Directory.Exists(dir)) return result;
                foreach (var path in Directory.EnumerateFiles(dir, "*.txt").OrderBy(p => p, StringComparer.Ordinal))
                {
                    string kind = Path.GetFileName(path).Split('-')[0];
                    if (kind is not ("crash" or "hang")) continue;
                    string marker = path + ".seen";
                    if (File.Exists(marker)) continue;
                    var recovered = File.ReadLines(path)
                        .TakeWhile(l => l.Length > 0)
                        .Where(l => l.StartsWith("Recovered: ", StringComparison.Ordinal))
                        .Select(l => l["Recovered: ".Length..])
                        .ToList();
                    result.Add(new PendingReport(path, kind, File.GetLastWriteTime(path), recovered));
                    File.WriteAllText(marker, "");
                }
            }
            catch { }
            return result;
        }

        private static bool OnUiThread()
        {
            try { return Dispatcher.UIThread.CheckAccess(); } catch { return false; }
        }

        private static string SafeState()
        {
            if (!OnUiThread()) return _lastState;
            try { return _describeState?.Invoke() ?? ""; }
            catch (Exception ex) { return "(state unavailable: " + ex.Message + ")"; }
        }
    }
}
