using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SmartUnrealLocalization.Models
{
    public class AppConfig
    {
        private static AppConfig? _instance;
        private static readonly object _lock = new();
        private static readonly object _saveLock = new();

        // AI provider settings
        // AI generic settings

        /// <summary>
        /// Runtime-only plaintext API key. Never written to disk —
        /// <see cref="ShouldSerializeApiKey"/> suppresses it; the persisted form is
        /// <see cref="ApiKeyProtected"/> (Windows DPAPI, CurrentUser scope). A legacy
        /// plaintext "ApiKey" in config.json still DESERIALIZES into this property;
        /// <see cref="Load"/> migrates it to the encrypted field and scrubs the
        /// plaintext from disk.
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Newtonsoft convention: readable from legacy configs, never written back.</summary>
        public bool ShouldSerializeApiKey() => false;

        /// <summary>
        /// DPAPI-encrypted API key (base64). Decryptable only by the same Windows
        /// user on the same machine — a copied/backed-up config.json no longer
        /// leaks the key in plaintext.
        /// </summary>
        public string? ApiKeyProtected { get; set; }

        public string ModelName { get; set; } = "gemini-1.5-flash";
        public string BaseUrl { get; set; } = "";

        public int TranslationBatchSize { get; set; } = 30;

        // Editor
        public bool AutoSaveEnabled { get; set; } = true;
        public int AutoSaveIntervalMinutes { get; set; } = 5;
        public double DefaultColumnWidth { get; set; } = 300;

        /// <summary>Folder for timer auto-saves (timestamped AutoSaved_* subfolders). Default: &lt;app&gt;\AutoSaved.</summary>
        public string AutoSavePath { get; set; } = "";

        /// <summary>Folder for manual exports (timestamped Export_* subfolders). Default: &lt;app&gt;\Export.</summary>
        public string ExportPath { get; set; } = "";

        /// <summary>What Save As does — see <see cref="Models.SaveAsBehavior"/>.</summary>
        public SaveAsBehavior SaveAsBehavior { get; set; } = SaveAsBehavior.AlwaysAsk;

        // DataGrid column widths — persisted between sessions (DataGrid Behavior §8.3).
        // Star-sized Target column auto-fills the remainder; not persisted.
        public double KeyColumnWidth { get; set; } = 220;
        public double SourceColumnWidth { get; set; } = 260;

        // Phase 2: Translation Memory
        public bool TranslationMemoryEnabled { get; set; } = true;

        // Phase 3: Parallel translation (OFF by default — can cause API issues)
        public bool ParallelTranslationEnabled { get; set; } = false;
        public int MaxConcurrentRequests { get; set; } = 3;
        public bool PromptCachingEnabled { get; set; } = false;

        // Phase 4: Protected Terms (Glossary)
        public List<string> ProtectedTerms { get; set; } = new();

        /// <summary>
        /// When true, rows carrying a reviewer <see cref="TranslationRow.Comment"/> send that
        /// note to the AI as a per-row instruction (inline "note" field in the batch payload).
        /// Such rows also bypass Translation Memory so the note is actually honoured and the
        /// TM is not poisoned with a context-specific translation. Default on; toggled by the
        /// "Send with comments" checkbox in both translate panels.
        /// </summary>
        public bool SendCommentsToAi { get; set; } = true;

        // Last language pair used in the AI panel. Restores the panel's dropdowns
        // and lets "Send to Translation Memory" (row context menu) know which TM
        // file to write when no translation run is active.
        public string LastAiFromLanguage { get; set; } = "";
        public string LastAiToLanguage { get; set; } = "";

        /// <summary>
        /// Show locres entries whose value is empty in the grid. The document
        /// always keeps them (integrity); this only controls visibility.
        /// Toggled via View → Show empty keys.
        /// </summary>
        public bool ShowEmptyKeys { get; set; } = false;

        // Retry failed batches automatically (off by default — avoid hammering a dead server)
        public bool AutoRetryEnabled { get; set; } = false;
        public int AutoRetryDelaySeconds { get; set; } = 20;

        // Throttle: enforce a minimum gap between consecutive AI requests. Fast models
        // return batches so quickly that the app can exceed provider requests-per-minute
        // limits — this spaces out sends across ALL flows (panel, multi-translate).
        public bool MinRequestDelayEnabled { get; set; } = false;
        public int MinRequestDelayMs { get; set; } = 1000;

        // QC (Quality Control) — AI layer. Kept separate from the translation
        // settings on purpose: reviewing is a different workload (smaller batches
        // read better), and the two must not share knobs.
        public int QcBatchSize { get; set; } = 20;
        public bool QcParallelEnabled { get; set; } = false;
        public int QcMaxConcurrentRequests { get; set; } = 3;
        // Which rows the AI review skips (QC panel → Rows to review). Every row in
        // scope goes, minus these — see Services/Qc/QcAiScope.
        public bool QcSkipEmpty { get; set; } = true;
        public bool QcSkipAutoCarried { get; set; } = true;
        public bool QcSkipIdenticalToOriginal { get; set; } = true;
        public bool QcSkipApproved { get; set; } = true;
        public bool QcOnlyValidatorFlagged { get; set; } = false;

        /// <summary>
        /// Words the QC language-leak check must NOT flag (proper names, loan
        /// words). Free text from the user; matching is case-insensitive.
        /// </summary>
        public List<string> QcLeakExceptions { get; set; } = new();

        /// <summary>
        /// Known bad→good terminology replacements for the QC pass, stored as
        /// "wrong=correct" lines (parsed by <c>QcEngine.ParseTermFixes</c>).
        /// The field report's original case: "пролом=пробоїна".
        /// </summary>
        public List<string> QcTermFixes { get; set; } = new();

        // MCP bridge: lets AI chat clients (Claude Desktop/Code, Codex, Antigravity)
        // control the RUNNING app via "SmartUnrealLocalization.exe --mcp". Off by
        // default — enabling opens a local named pipe (current Windows user only).
        public bool McpServerEnabled { get; set; } = false;

        // ── Validation: the optional NOTES (errors and warnings always run) ──
        // Edge whitespace is OFF by default: on real files it raised 1 000+
        // notes that users learned to ignore, burying the findings that matter.
        public bool CheckEdgeWhitespace { get; set; } = false;
        public bool CheckEndingPunctuation { get; set; } = true;
        public bool CheckLengthRatio { get; set; } = true;
        public bool CheckDuplicateSources { get; set; } = true;

        // UI
        public bool IsDarkTheme { get; set; } = true;
        public string Language { get; set; } = "en";

        /// <summary>
        /// The accent colour the user picked, "#RRGGBB"; empty = each theme's own
        /// (blue on dark, cinnamon on light). One pick serves both themes — the
        /// shades are derived per theme (<c>Services/AccentPalette</c>).
        /// </summary>
        public string AccentColor { get; set; } = "";

        /// <summary>Recently opened ORIGINAL file paths (not temp copies). MRU order. Capped at 10.</summary>
        public List<string> RecentFiles { get; set; } = new();

        /// <summary>Open file tabs from the last session (Etap 13) — restored on launch, in tab order.</summary>
        public List<OpenTabEntry> OpenFileTabs { get; set; } = new();

        /// <summary>Show full key hashes in the grid (uncheck to abbreviate).</summary>
        public bool ShowFullKeyHashes { get; set; } = true;

        /// <summary>
        /// When true, Source / Target cells wrap their text and the row grows
        /// vertically to fit. Toggled from the filter-bar "Expand rows" chip.
        /// </summary>
        public bool AutoExpandRows { get; set; } = false;

        // --- Advanced ------------------------------------------------------

        /// <summary>
        /// Merge to new version: carry a translation on a KEY match alone, without
        /// checking that the original text is unchanged.
        ///
        /// <para>Off by default. With it off, a locres match is confirmed by
        /// SourceStringHash and a CSV/PO match by the source column, and anything
        /// whose original changed is left untranslated instead of shipping a line
        /// written for wording that no longer exists. Turning it on is the escape
        /// hatch for a file whose hashes or source column cannot be trusted.</para>
        /// </summary>
        public bool MergeMatchByKeyOnly { get; set; } = false;

        /// <summary>Default export format for Save As (orders the file-picker filter list).</summary>
        public FileFormat DefaultExportFormat { get; set; } = FileFormat.Locres;

        // ─── Usage counters (Status panel "App usage" tiles) ────────────────
        // All four are monotonic — never reset except by manual config edit.
        // Incremented in MainWindowViewModel.RecordTelemetry* helpers; persisted via Save().
        /// <summary>Total number of files ever opened across all sessions.</summary>
        public int TotalFilesOpened { get; set; }
        /// <summary>Total rows whose Target was set by AI translation across all sessions.</summary>
        public int TotalRowsTranslated { get; set; }
        /// <summary>Total AI API batch requests sent across all sessions.</summary>
        public int TotalAiRequests { get; set; }
        /// <summary>Total TM persistent-save operations across all sessions.</summary>
        public int TotalTmSaves { get; set; }
        /// <summary>Total app sessions (incremented on startup).</summary>
        public int TotalSessions { get; set; }
        /// <summary>Total seconds spent in the app across all sessions.</summary>
        public long TotalSecondsUsed { get; set; }

        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                    lock (_lock) { _instance ??= Load(); }
                return _instance;
            }
        }

        /// <summary>
        /// Test seam. Redirects EVERY per-user file (config, translation memories,
        /// QC caches) to a temporary folder, so a test run can never overwrite the
        /// real profile — the Translation Memory is accumulated API spend and a
        /// stray <c>Clear()</c> in a test would destroy it. Null = real location.
        /// </summary>
        internal static string? ConfigDirOverride;

        public static string ConfigDir()
        {
            var dir = ConfigDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartUnrealLocalization");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string ConfigPath() => Path.Combine(ConfigDir(), "config.json");

        /// <summary>Path to the Translation Memory JSON database.</summary>
        public static string TmDbPath() => Path.Combine(ConfigDir(), "translation_memory.json");

        public static AppConfig Load()
        {
            try
            {
                var path = ConfigPath();
                var cfg = new AppConfig();
                if (File.Exists(path))
                {
                    try
                    {
                        var loaded = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path));
                        if (loaded != null) cfg = loaded;
                    }
                    catch (Exception parseEx)
                    {
                        // Keep the unreadable config recoverable (API key, prefs) —
                        // continuing with defaults would overwrite it on next Save.
                        Services.ReviewService.PreserveCorrupt(path, parseEx);
                    }
                }

                // Defaults + one-time migration from the single-folder era, when
                // AutoSavePath was force-filled with <app>\Export and served both
                // auto-saves and manual exports. Users who never customized it get
                // the new split defaults; a customized path is preserved for both.
                var legacyDefault = Path.Combine(AppContext.BaseDirectory, "Export");
                if (string.IsNullOrWhiteSpace(cfg.ExportPath))
                {
                    cfg.ExportPath = string.IsNullOrWhiteSpace(cfg.AutoSavePath) || cfg.AutoSavePath == legacyDefault
                        ? legacyDefault
                        : cfg.AutoSavePath;
                }
                if (string.IsNullOrWhiteSpace(cfg.AutoSavePath) || cfg.AutoSavePath == legacyDefault)
                {
                    cfg.AutoSavePath = Path.Combine(AppContext.BaseDirectory, "AutoSaved");
                }

                // Semantic clamps: the Settings UI validates input, but a hand-edited
                // config.json bypasses it — e.g. AutoSaveIntervalMinutes:0 made the
                // Timer ctor throw INSIDE the VM constructor (startup crash).
                if (cfg.AutoSaveIntervalMinutes < 1) cfg.AutoSaveIntervalMinutes = 5;
                if (cfg.MaxConcurrentRequests < 1) cfg.MaxConcurrentRequests = 3;
                if (cfg.TranslationBatchSize < 1) cfg.TranslationBatchSize = 20;
                if (cfg.AutoRetryDelaySeconds < 1) cfg.AutoRetryDelaySeconds = 20;
                if (cfg.MinRequestDelayMs < 0) cfg.MinRequestDelayMs = 1000;
                if (cfg.QcBatchSize < 1) cfg.QcBatchSize = 20;
                if (cfg.QcMaxConcurrentRequests < 1) cfg.QcMaxConcurrentRequests = 3;

                // API key at rest: prefer the DPAPI blob. A non-empty plaintext
                // ApiKey means a pre-encryption config — encrypt it now and scrub
                // the plaintext from disk with an immediate save-back.
                if (!string.IsNullOrEmpty(cfg.ApiKeyProtected))
                {
                    cfg.ApiKey = UnprotectKey(cfg.ApiKeyProtected);
                }
                else if (!string.IsNullOrEmpty(cfg.ApiKey))
                {
                    cfg.Save(); // migration: writes ApiKeyProtected, omits plaintext
                }

                return cfg;
            }
            catch { }
            return new AppConfig
            {
                AutoSavePath = Path.Combine(AppContext.BaseDirectory, "AutoSaved"),
                ExportPath = Path.Combine(AppContext.BaseDirectory, "Export"),
            };
        }

        // App-specific DPAPI entropy. Not a secret (the source is public) — it just
        // stops generic DPAPI dumpers from decrypting the blob without naming this app.
        private static readonly byte[] DpapiEntropy =
            System.Text.Encoding.UTF8.GetBytes("SmartUnrealLocalization.ApiKey.v1");

        private static string? ProtectKey(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext) || !OperatingSystem.IsWindows()) return null;
            try
            {
                var blob = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(plaintext), DpapiEntropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(blob);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Config: API key encryption failed: {e.Message}");
                return null;
            }
        }

        private static string UnprotectKey(string? base64)
        {
            if (string.IsNullOrEmpty(base64) || !OperatingSystem.IsWindows()) return "";
            try
            {
                var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(base64), DpapiEntropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            catch (Exception e)
            {
                // Different Windows user/machine (copied config) or a corrupt blob —
                // degrade to "no key" so startup never crashes; the user re-enters it.
                Console.WriteLine($"Config: API key decryption failed: {e.Message}");
                return "";
            }
        }

        public void Save()
        {
            // Serialized to prevent concurrent writes from the auto-save timer thread + the UI
            // thread (e.g. user saving preferences while a periodic auto-save fires).
            try
            {
                lock (_saveLock)
                {
                    // Encrypt the runtime key for persistence (plaintext ApiKey is
                    // suppressed by ShouldSerializeApiKey). If DPAPI ever fails we
                    // keep the previous blob — a stale stored key beats a lost one.
                    if (string.IsNullOrEmpty(ApiKey))
                        ApiKeyProtected = null;
                    else if (ProtectKey(ApiKey) is { } blob)
                        ApiKeyProtected = blob;

                    // Atomic: a crash mid-write must not corrupt config.json
                    // (API key + all preferences live here).
                    var path = ConfigPath();
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception e) { Console.WriteLine($"Config save error: {e.Message}"); }
        }

        public void Reload() { _instance = Load(); }

        /// <summary>
        /// Put every setting back to its default — on THIS instance, because the
        /// view model and the panels hold the singleton — and save. The API key,
        /// the recent files and the usage counters go too. The interface language
        /// the theme and the accent colour stay: they are how the user reads the app, not something
        /// a "reset" should flip under them.
        /// </summary>
        public void ResetToDefaults()
        {
            var fresh = new AppConfig
            {
                Language = Language,
                IsDarkTheme = IsDarkTheme,
                AccentColor = AccentColor,
                AutoSavePath = Path.Combine(AppContext.BaseDirectory, "AutoSaved"),
                ExportPath = Path.Combine(AppContext.BaseDirectory, "Export"),
            };
            // Replace, not merge: the default handling would ADD the (empty) default
            // lists to the existing ones and keep every old entry.
            JsonConvert.PopulateObject(JsonConvert.SerializeObject(fresh), this,
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace });
            ApiKey = "";
            ApiKeyProtected = null;
            Save();
        }

        /// <summary>
        /// Push a user-selected file path to the front of <see cref="RecentFiles"/>.
        /// Contract: <paramref name="originalPath"/> must be the path the USER picked
        /// (file dialog, recents click) — NEVER a path inside a
        /// <c>.temp-SUL-*</c> working directory. Caller is responsible for passing the
        /// right argument; this method does not (and cannot) validate it.
        /// Dedupes case-insensitively and trims to a max of 10 entries.
        /// Persists immediately.
        /// </summary>
        public void AddRecentFile(string originalPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath)) return;
            // Reject temp paths defensively — silently drop rather than poison the list.
            if (originalPath.Contains(".temp-SUL-", StringComparison.OrdinalIgnoreCase)) return;

            RecentFiles.RemoveAll(p =>
                string.Equals(p, originalPath, StringComparison.OrdinalIgnoreCase));
            RecentFiles.Insert(0, originalPath);
            while (RecentFiles.Count > 10) RecentFiles.RemoveAt(RecentFiles.Count - 1);
            Save();
        }
    }

    /// <summary>One persisted open-file tab (Etap 13): the path + pinned flag.</summary>
    public class OpenTabEntry
    {
        public string Path { get; set; } = "";
        public bool IsPinned { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>What the Save As command does.</summary>
    public enum SaveAsBehavior
    {
        /// <summary>Open the system Save dialog; the picked path is honored for every format.</summary>
        AlwaysAsk = 0,
        /// <summary>Silently save in the current format into the Export folder (timestamped subfolder).</summary>
        ExportFolder = 1,
        /// <summary>Silently save in the current format into the Autosave folder (timestamped subfolder).</summary>
        AutosaveFolder = 2,
    }
}
