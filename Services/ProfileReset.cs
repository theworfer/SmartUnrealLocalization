using System;
using System.IO;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Settings → Advanced → Reset cache: back to a clean profile. Deletes every
    /// Translation Memory, the prompt history and the AI review (QC) cache, and
    /// returns the settings to their defaults.
    ///
    /// <para>It does NOT touch the user's own work: open files, reviewer marks and
    /// comments, QC suggestions, crash reports, auto-saves and exports.</para>
    /// </summary>
    public static class ProfileReset
    {
        /// <summary>Run the reset. Returns how many files were deleted.</summary>
        public static int Run()
        {
            int deleted = 0;
            string dir = AppConfig.ConfigDir();
            foreach (var pattern in new[] { "translation_memory*.json", "qc_cache_*.json" })
                foreach (var file in Directory.GetFiles(dir, pattern))
                {
                    try { File.Delete(file); deleted++; }
                    catch (Exception ex) { Console.WriteLine($"[reset] {Path.GetFileName(file)}: {ex.Message}"); }
                }

            try
            {
                deleted += PromptHistory.LoadAll().Count;
                PromptHistory.Clear();
            }
            catch (Exception ex) { Console.WriteLine($"[reset] prompts: {ex.Message}"); }

            AppConfig.Instance.ResetToDefaults();
            return deleted;
        }
    }
}
