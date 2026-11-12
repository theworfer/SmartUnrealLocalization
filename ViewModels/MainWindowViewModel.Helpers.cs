using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.ViewModels
{
    // Legacy temp-dir cleanup. (The
    // UnrealLocres.exe bootstrap and the per-PID .temp-SUL working-copy
    // machinery were removed when the locres pipeline moved in-process to
    // LocresIO — the app no longer spawns or downloads any external tool.)
    public partial class MainWindowViewModel
    {
        /// <summary>
        /// Removes leftover .temp-SUL-{PID} directories from previous app sessions whose PIDs
        /// are no longer running. The app itself no longer creates these (the locres pipeline
        /// is in-process now) — this sweep is kept for one release so users upgrading from an
        /// exe-based version don't accumulate stale folders after a crash.
        /// </summary>
        private static void CleanupStaleTempDirs()
        {
            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                foreach (var dir in Directory.EnumerateDirectories(AppContext.BaseDirectory, ".temp-SUL-*"))
                {
                    var name = Path.GetFileName(dir);
                    const string prefix = ".temp-SUL-";
                    if (!name.StartsWith(prefix)) continue;
                    if (!int.TryParse(name.AsSpan(prefix.Length), out int pid)) continue;
                    if (pid == currentPid) continue;

                    bool processAlive = false;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        processAlive = !p.HasExited;
                    }
                    catch (ArgumentException) { /* PID not running */ }
                    catch (InvalidOperationException) { /* zombie */ }

                    if (!processAlive)
                    {
                        try { Directory.Delete(dir, true); }
                        catch (Exception ex) { Console.WriteLine($"[WARN] Could not delete stale temp dir '{dir}': {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Stale temp dir scan failed: {ex.Message}");
            }
        }
    }
}
