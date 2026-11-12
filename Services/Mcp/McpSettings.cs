using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// The single source of truth for which app settings MCP may read and write,
    /// with the metadata a model needs to act as live documentation: type, allowed
    /// values, the exact place in the UI, and a one-line description. The API key
    /// is deliberately READ-STATUS-ONLY — the model can tell the user exactly
    /// where to paste it, but can never see or set the value itself.
    /// </summary>
    public static class McpSettings
    {
        public sealed class Entry
        {
            public required string Name { get; init; }
            public required string Type { get; init; }         // bool | int | string | enum | string[]
            public required string UiPath { get; init; }
            public required string Description { get; init; }
            public string? Allowed { get; init; }              // human-readable constraint
            public required Func<AppConfig, JToken> Get { get; init; }
            /// <summary>Apply the value. Returns null on success, else the rejection reason.</summary>
            public Func<AppConfig, JToken, string?>? Set { get; init; } // null = read-only via MCP
            /// <summary>What must be refreshed after a successful set.</summary>
            public LiveApply Apply { get; init; } = LiveApply.None;
        }

        [Flags]
        public enum LiveApply
        {
            None = 0,
            RestartAutoSave = 1,
            Theme = 2,
            UiLanguage = 4,
            RefreshGrid = 8,
            Revalidate = 16,
        }

        /// <summary>
        /// Language codes this build actually contains, read from the embedded
        /// resources. Sorted so the Allowed string is stable between runs.
        /// </summary>
        private static IReadOnlyList<string> ShippedLanguages()
            => Localization.Loc.Instance.AvailableLanguages().OrderBy(c => c, StringComparer.Ordinal).ToList();

        public static readonly IReadOnlyList<Entry> All = Build();

        public static Entry? Find(string name)
            => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        private static List<Entry> Build() => new()
        {
            // ── AI Provider ──────────────────────────────────────────────
            new Entry
            {
                Name = "model_name", Type = "string",
                UiPath = "Settings → AI Provider → Model name",
                Description = "AI model id used for all translation (e.g. gemini-2.5-flash, gpt-4o-mini, claude-3-5-haiku). The provider is inferred from the model prefix; a custom Base URL forces the OpenAI-compatible provider.",
                Get = c => c.ModelName,
                Set = (c, v) => NonEmpty(v, out var s) ? Ok(() => c.ModelName = s) : "model_name must be a non-empty string",
            },
            new Entry
            {
                Name = "base_url", Type = "string",
                UiPath = "Settings → AI Provider → Base URL (optional)",
                Description = "Optional OpenAI-compatible endpoint (LM Studio, Ollama, proxies). Empty = use the official provider inferred from the model name.",
                Get = c => c.BaseUrl,
                Set = (c, v) => Ok(() => c.BaseUrl = v.Type == JTokenType.Null ? "" : v.ToString()),
            },
            new Entry
            {
                Name = "api_key", Type = "string",
                UiPath = "Settings → AI Provider → API Key (the dotted password field, between Model name and Base URL)",
                Description = "The provider API key. NOT readable or settable through MCP by design — ask the user to paste it manually into this field and press Save. Stored encrypted (Windows DPAPI); the app never writes it to disk in plain text.",
                Allowed = "manual entry only",
                Get = c => string.IsNullOrEmpty(c.ApiKey) ? "(not set)" : "(set)",
                Set = null,
            },

            // ── Editor ───────────────────────────────────────────────────
            new Entry
            {
                Name = "auto_save_enabled", Type = "bool",
                UiPath = "Settings → Editor → Auto-save",
                Description = "Timer auto-save of the open file (as a BACKUP copy into the auto-save folder; the original file is never overwritten).",
                Get = c => c.AutoSaveEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.AutoSaveEnabled = b) : NotBool,
                Apply = LiveApply.RestartAutoSave,
            },
            new Entry
            {
                Name = "auto_save_interval_minutes", Type = "int", Allowed = "1–1440",
                UiPath = "Settings → Editor → Auto-save interval",
                Description = "Minutes between timer auto-saves.",
                Get = c => c.AutoSaveIntervalMinutes,
                Set = (c, v) => AsInt(v, 1, 1440, out var i) ? Ok(() => c.AutoSaveIntervalMinutes = i) : "must be an integer 1–1440",
                Apply = LiveApply.RestartAutoSave,
            },
            new Entry
            {
                Name = "show_full_key_hashes", Type = "bool",
                UiPath = "Settings → Editor → Show full key hashes",
                Description = "Show full GUID-like keys in the grid instead of abbreviating them.",
                Get = c => c.ShowFullKeyHashes,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.ShowFullKeyHashes = b) : NotBool,
                Apply = LiveApply.RefreshGrid,
            },

            // ── Validation: the optional notes ───────────────────────────
            CheckEntry("check_edge_whitespace", "Edge spaces and line breaks differ from the source",
                "Off by default: on real files it fires on hundreds of correct rows.",
                c => c.CheckEdgeWhitespace, (c, b) => c.CheckEdgeWhitespace = b),
            CheckEntry("check_ending_punctuation", "Ending punctuation differs from the source", "",
                c => c.CheckEndingPunctuation, (c, b) => c.CheckEndingPunctuation = b),
            CheckEntry("check_length_ratio", "Translation more than twice as long as the source", "",
                c => c.CheckLengthRatio, (c, b) => c.CheckLengthRatio = b),
            CheckEntry("check_duplicate_sources", "Same source translated in different ways", "",
                c => c.CheckDuplicateSources, (c, b) => c.CheckDuplicateSources = b),

            // ── Advanced ─────────────────────────────────────────────────
            new Entry
            {
                Name = "merge_match_by_key_only", Type = "bool",
                UiPath = "Settings -> Advanced -> Merge: match by key only (Tools -> Advanced settings)",
                Description = "Merge to new version: carry a translation on a KEY match alone. Off by default, "
                            + "and off is the safe value: with it off a match is confirmed by SourceStringHash "
                            + "(.locres) or the source column (CSV/PO), and a row whose original text changed is "
                            + "left untranslated instead of shipping a line written for the old wording. Turn it "
                            + "on only when that proof is unusable, e.g. an old file whose hashes another tool "
                            + "rebuilt so every row looks changed.",
                Get = c => c.MergeMatchByKeyOnly,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.MergeMatchByKeyOnly = b) : NotBool,
            },

            // ── Files ────────────────────────────────────────────────────
            new Entry
            {
                Name = "default_export_format", Type = "enum", Allowed = "locres | po | csv",
                UiPath = "Settings → Files → Default export format",
                Description = "Which format is offered first in Save As dialogs.",
                Get = c => c.DefaultExportFormat.ToString().ToLowerInvariant(),
                Set = (c, v) => v.ToString().ToLowerInvariant() switch
                {
                    "locres" => Ok(() => c.DefaultExportFormat = FileFormat.Locres),
                    "po" => Ok(() => c.DefaultExportFormat = FileFormat.Po),
                    "csv" => Ok(() => c.DefaultExportFormat = FileFormat.Csv),
                    _ => "must be locres | po | csv",
                },
            },
            new Entry
            {
                Name = "save_as_behavior", Type = "enum", Allowed = "always_ask | export_folder | autosave_folder",
                UiPath = "Settings → Files → Save As behavior",
                Description = "always_ask shows a file dialog; the folder options save silently into the configured folder.",
                Get = c => c.SaveAsBehavior switch
                {
                    SaveAsBehavior.ExportFolder => "export_folder",
                    SaveAsBehavior.AutosaveFolder => "autosave_folder",
                    _ => "always_ask",
                },
                Set = (c, v) => v.ToString().ToLowerInvariant() switch
                {
                    "always_ask" => Ok(() => c.SaveAsBehavior = SaveAsBehavior.AlwaysAsk),
                    "export_folder" => Ok(() => c.SaveAsBehavior = SaveAsBehavior.ExportFolder),
                    "autosave_folder" => Ok(() => c.SaveAsBehavior = SaveAsBehavior.AutosaveFolder),
                    _ => "must be always_ask | export_folder | autosave_folder",
                },
            },
            new Entry
            {
                Name = "export_path", Type = "string",
                UiPath = "Settings → Files → Export folder",
                Description = "Folder for manual saves/exports (timestamped Export_* subfolders are created inside).",
                Get = c => c.ExportPath,
                Set = (c, v) => ValidDir(v, out var p, out var err) ? Ok(() => c.ExportPath = p) : err,
            },
            new Entry
            {
                Name = "auto_save_path", Type = "string",
                UiPath = "Settings → Files → Auto-save folder",
                Description = "Folder for timer auto-saves (timestamped AutoSaved_* subfolders are created inside).",
                Get = c => c.AutoSavePath,
                Set = (c, v) => ValidDir(v, out var p, out var err) ? Ok(() => c.AutoSavePath = p) : err,
            },

            // ── Interface language ───────────────────────────────────────
            // The allowed set is DISCOVERED from the embedded strings.*.json
            // files, never listed here: adding a language must stay a
            // drop-in-a-json operation, and a stale list would silently refuse
            // a language the app actually ships.
            new Entry
            {
                Name = "ui_language", Type = "enum", Allowed = string.Join(" | ", ShippedLanguages()),
                UiPath = "Header bar → 文A button, left of the theme toggle (or Settings → Interface Language)",
                Description = "Language of the app interface. Applies immediately. Codes are the ones the app ships "
                            + "(BCP-47 style, e.g. pt-BR, zh-Hans) — see the Allowed list.",
                Get = c => c.Language,
                Set = (c, v) =>
                {
                    var code = ShippedLanguages().FirstOrDefault(
                        l => string.Equals(l, v.ToString(), StringComparison.OrdinalIgnoreCase));
                    if (code is null) return "must be one of: " + string.Join(" | ", ShippedLanguages());
                    return Ok(() => c.Language = code);
                },
                Apply = LiveApply.UiLanguage,
            },

            // ── Translation Memory / glossary ────────────────────────────
            new Entry
            {
                Name = "translation_memory_enabled", Type = "bool",
                UiPath = "Settings → Translation Memory → Enable",
                Description = "Reuse confirmed translations per language pair (auto-fills repeated strings before calling the AI).",
                Get = c => c.TranslationMemoryEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.TranslationMemoryEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "protected_terms", Type = "string[]",
                UiPath = "Settings → Translation Memory → Protected Terms (Glossary)",
                Description = "Terms the AI must never translate (names, brands). Set the FULL list — it replaces the existing one.",
                Get = c => new JArray(c.ProtectedTerms),
                Set = (c, v) =>
                {
                    if (v is not JArray arr) return "must be a JSON array of strings";
                    var list = arr.Select(t => t.ToString().Trim()).Where(s => s.Length > 0).ToList();
                    return Ok(() => c.ProtectedTerms = list);
                },
            },
            new Entry
            {
                Name = "send_comments_to_ai", Type = "bool",
                UiPath = "AI Translate / Multi-translate panels → Send with comments",
                Description = "Rows with reviewer comments send the note as a per-row AI instruction and bypass the TM.",
                Get = c => c.SendCommentsToAi,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.SendCommentsToAi = b) : NotBool,
            },

            // ── AI Speed ─────────────────────────────────────────────────
            new Entry
            {
                Name = "translation_batch_size", Type = "int", Allowed = "1–500",
                UiPath = "AI Translate panel → Batch size (persisted default)",
                Description = "How many rows go into one AI request.",
                Get = c => c.TranslationBatchSize,
                Set = (c, v) => AsInt(v, 1, 500, out var i) ? Ok(() => c.TranslationBatchSize = i) : "must be an integer 1–500",
            },
            new Entry
            {
                Name = "prompt_caching_enabled", Type = "bool",
                UiPath = "Settings → AI Speed → Enable explicit prompt caching",
                Description = "Explicitly requests provider-side prompt caching (Anthropic/Gemini).",
                Get = c => c.PromptCachingEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.PromptCachingEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "parallel_translation_enabled", Type = "bool",
                UiPath = "Settings → AI Speed → Enable parallel requests",
                Description = "Send several batches concurrently. Watch provider rate limits — keep OFF or low for free tiers.",
                Get = c => c.ParallelTranslationEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.ParallelTranslationEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "max_concurrent_requests", Type = "int", Allowed = "1–32",
                UiPath = "Settings → AI Speed → Max concurrent requests",
                Description = "Upper bound of in-flight AI requests when parallel mode is on.",
                Get = c => c.MaxConcurrentRequests,
                Set = (c, v) => AsInt(v, 1, 32, out var i) ? Ok(() => c.MaxConcurrentRequests = i) : "must be an integer 1–32",
            },
            new Entry
            {
                Name = "auto_retry_enabled", Type = "bool",
                UiPath = "Settings → AI Speed → Auto-retry failed batches",
                Description = "One delayed second attempt per failed batch before it counts toward the 3-strikes stop.",
                Get = c => c.AutoRetryEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.AutoRetryEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "auto_retry_delay_seconds", Type = "int", Allowed = "1–600",
                UiPath = "Settings → AI Speed (used by Auto-retry)",
                Description = "Wait before the retry attempt.",
                Get = c => c.AutoRetryDelaySeconds,
                Set = (c, v) => AsInt(v, 1, 600, out var i) ? Ok(() => c.AutoRetryDelaySeconds = i) : "must be an integer 1–600",
            },
            new Entry
            {
                Name = "min_request_delay_enabled", Type = "bool",
                UiPath = "Settings → AI Speed → Limit request rate",
                Description = "Enforce a minimum gap between consecutive AI requests (for strict provider rate limits).",
                Get = c => c.MinRequestDelayEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.MinRequestDelayEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "min_request_delay_ms", Type = "int", Allowed = "0–600000",
                UiPath = "Settings → AI Speed → Min gap between requests (ms)",
                Description = "The minimum gap size in milliseconds.",
                Get = c => c.MinRequestDelayMs,
                Set = (c, v) => AsInt(v, 0, 600000, out var i) ? Ok(() => c.MinRequestDelayMs = i) : "must be an integer 0–600000",
            },

            // ── QC (Quality Control) ─────────────────────────────────────
            new Entry
            {
                Name = "qc_batch_size", Type = "int", Allowed = "1–500",
                UiPath = "QC panel → AI review → QC settings… → Rows per request",
                Description = "How many rows go into one AI review request. Smaller batches read better than translation batches.",
                Get = c => c.QcBatchSize,
                Set = (c, v) => AsInt(v, 1, 500, out var i) ? Ok(() => c.QcBatchSize = i) : "must be an integer 1–500",
            },
            new Entry
            {
                Name = "qc_parallel_enabled", Type = "bool",
                UiPath = "QC panel → AI review → QC settings… → Send requests in parallel",
                Description = "Run several QC review requests concurrently (watch provider rate limits).",
                Get = c => c.QcParallelEnabled,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcParallelEnabled = b) : NotBool,
            },
            new Entry
            {
                Name = "qc_max_concurrent_requests", Type = "int", Allowed = "1–32",
                UiPath = "QC panel → AI review → QC settings… → Max parallel requests",
                Description = "Upper bound of in-flight QC review requests when parallel mode is on.",
                Get = c => c.QcMaxConcurrentRequests,
                Set = (c, v) => AsInt(v, 1, 32, out var i) ? Ok(() => c.QcMaxConcurrentRequests = i) : "must be an integer 1–32",
            },
            new Entry
            {
                Name = "qc_skip_empty", Type = "bool",
                UiPath = "QC panel → AI review → Rows to review → Skip rows without text",
                Description = "Keep rows with no translation (or no original) out of the AI review.",
                Get = c => c.QcSkipEmpty,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcSkipEmpty = b) : NotBool,
            },
            new Entry
            {
                Name = "qc_skip_auto_carried", Type = "bool",
                UiPath = "QC panel → AI review → Rows to review → Skip rows carried over automatically",
                Description = "Keep rows that are only tags, numbers or placeholders (copied as is, nothing to translate) out of the AI review.",
                Get = c => c.QcSkipAutoCarried,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcSkipAutoCarried = b) : NotBool,
            },
            new Entry
            {
                Name = "qc_skip_identical", Type = "bool",
                UiPath = "QC panel → AI review → Rows to review → Skip rows identical to the original",
                Description = "Keep rows whose translation is the original verbatim (names, codes, untranslated copies) out of the AI review.",
                Get = c => c.QcSkipIdenticalToOriginal,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcSkipIdenticalToOriginal = b) : NotBool,
            },
            new Entry
            {
                Name = "qc_skip_approved", Type = "bool",
                UiPath = "QC panel → AI review → Rows to review → Skip approved rows",
                Description = "Keep rows a reviewer marked Approved out of the AI review.",
                Get = c => c.QcSkipApproved,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcSkipApproved = b) : NotBool,
            },
            new Entry
            {
                Name = "qc_only_validator_flagged", Type = "bool",
                UiPath = "QC panel → AI review → Rows to review → Only rows the validator flagged",
                Description = "Send only rows on which the live validator found a problem — the cheapest review.",
                Get = c => c.QcOnlyValidatorFlagged,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.QcOnlyValidatorFlagged = b) : NotBool,
            },            new Entry
            {
                Name = "qc_leak_exceptions", Type = "string[]",
                UiPath = "QC panel → AI review → QC settings… → Leak exceptions",
                Description = "Words the validator's leftover-language check must NOT flag (proper names, loan words). Case-insensitive. Set the FULL list — it replaces the existing one.",
                Apply = LiveApply.Revalidate,
                Get = c => new JArray(c.QcLeakExceptions),
                Set = (c, v) =>
                {
                    if (v is not JArray arr) return "must be a JSON array of strings";
                    var list = arr.Select(t => t.ToString().Trim()).Where(s => s.Length > 0).ToList();
                    return Ok(() => c.QcLeakExceptions = list);
                },
            },
            new Entry
            {
                Name = "qc_term_fixes", Type = "string[]",
                UiPath = "QC panel → AI review → QC settings… → Terminology fixes",
                Description = "Known bad→good terms, as \"wrong=correct\" lines (e.g. \"пролом=пробоїна\"). The validator flags the wrong term and offers the replacement as a reviewed fix. Lines without '=' are ignored. Set the FULL list — it replaces the existing one.",
                Apply = LiveApply.Revalidate,
                Get = c => new JArray(c.QcTermFixes),
                Set = (c, v) =>
                {
                    if (v is not JArray arr) return "must be a JSON array of \"wrong=correct\" strings";
                    var list = arr.Select(t => t.ToString().Trim()).Where(s => s.Length > 0).ToList();
                    return Ok(() => c.QcTermFixes = list);
                },
            },

            // ── View / appearance ────────────────────────────────────────
            new Entry
            {
                Name = "show_empty_keys", Type = "bool",
                UiPath = "Main window header → View → Show empty keys",
                Description = "Show locres entries whose value is empty (they are always preserved in the file; this is visibility only).",
                Get = c => c.ShowEmptyKeys,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.ShowEmptyKeys = b) : NotBool,
                Apply = LiveApply.RefreshGrid,
            },
            new Entry
            {
                Name = "dark_theme", Type = "bool",
                UiPath = "Main window header → sun/moon toggle (right side)",
                Description = "Dark (true) or light (false) interface theme. Applies immediately.",
                Get = c => c.IsDarkTheme,
                Set = (c, v) => AsBool(v, out var b) ? Ok(() => c.IsDarkTheme = b) : NotBool,
                Apply = LiveApply.Theme,
            },

            new Entry
            {
                Name = "accent_color", Type = "string",
                UiPath = "Settings → Interface Language → Accent colour (the round swatch)",
                Description = "Accent colour as #RRGGBB, or an empty string for each theme's own (blue on dark, cinnamon on light). One colour serves both themes: lighter and darker shades are derived per theme, and the lightness is adjusted where needed so text stays readable. Applies immediately.",
                Get = c => c.AccentColor,
                Set = (c, v) =>
                {
                    string s = v.ToString().Trim();
                    if (s.Length == 0) return Ok(() => c.AccentColor = "");
                    return AccentPalette.TryParse(s, out var color)
                        ? Ok(() => c.AccentColor = AccentPalette.ToHex(color))
                        : "accent_color must be #RRGGBB or an empty string";
                },
                Apply = LiveApply.Theme,
            },

            // ── MCP itself ───────────────────────────────────────────────
            new Entry
            {
                Name = "mcp_server_enabled", Type = "bool",
                UiPath = "Settings → MCP Server → Enable MCP server",
                Description = "This bridge. Read-only via MCP: turning it off would cut this very connection — ask the user to toggle it in Settings.",
                Allowed = "read-only via MCP",
                Get = c => c.McpServerEnabled,
                Set = null,
            },
        };

        // ── tiny parse helpers ───────────────────────────────────────────
        /// <summary>One optional validation note (Settings → Validation). Errors and
        /// warnings are not switchable, so they have no entry.</summary>
        private static Entry CheckEntry(string name, string label, string extra,
                                        Func<AppConfig, bool> get, Action<AppConfig, bool> set) => new()
        {
            Name = name, Type = "bool",
            UiPath = "Settings → Validation → " + label,
            Description = "Validation note: " + label + ". Optional — errors and warnings always run. "
                        + "Changing it re-checks every open file at once." + (extra.Length > 0 ? " " + extra : ""),
            Get = c => get(c),
            Set = (c, v) => AsBool(v, out var b) ? Ok(() => set(c, b)) : NotBool,
            Apply = LiveApply.Revalidate,
        };

        private const string NotBool = "must be true or false";
        private static string? Ok(Action apply) { apply(); return null; }
        private static bool NonEmpty(JToken v, out string s) { s = v.ToString().Trim(); return s.Length > 0; }
        private static bool AsBool(JToken v, out bool b)
        {
            if (v.Type == JTokenType.Boolean) { b = v.Value<bool>(); return true; }
            return bool.TryParse(v.ToString(), out b);
        }
        private static bool AsInt(JToken v, int min, int max, out int i)
            => int.TryParse(v.ToString(), out i) && i >= min && i <= max;
        private static bool ValidDir(JToken v, out string path, out string error)
        {
            path = v.ToString().Trim(); error = "";
            if (path.Length == 0) { error = "path must not be empty"; return false; }
            try
            {
                if (!Directory.Exists(path)) { error = $"directory does not exist: {path}"; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
