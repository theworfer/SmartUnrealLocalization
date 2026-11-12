using Newtonsoft.Json.Linq;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// The app explained to a model — a hand-maintained map of every user-facing
    /// area: what it does, where it lives on screen, and which MCP tool (if any)
    /// covers it. This is what lets a chat assistant answer "where do I paste the
    /// API key?" or "how do I merge a game update?" accurately.
    /// KEEP IN SYNC with docs/USER-GUIDE.md when features move.
    /// </summary>
    public static class McpAppMap
    {
        public static JObject Build() => new()
        {
            ["app"] = "Smart Unreal Localization — Windows desktop editor for Unreal Engine .locres (plus .po and .csv) with multi-provider AI batch translation.",
            ["golden_rules"] = new JArray(
                "The opened original file is NEVER overwritten in place — every save/export goes to a timestamped folder (or an explicitly chosen path).",
                "Empty targets never blank shipped strings: on save, rows with an empty translation keep the file's original value.",
                "<angle tags> and {curly tokens} must survive translation exactly; the app validates and rejects violations.",
                "Everything an MCP tool changes in rows is one Ctrl+Z step in the app.",
                "MCP NEVER writes to the Translation Memory — TM reading is the user's explicit choice per run; reviewed rows are promoted to TM manually (row context menu → Send to Translation Memory)."),
            ["areas"] = new JArray(
                Area("Header bar (top)",
                    "Logo, main menu, drag area, project pill (current file name; trims as the window narrows and hides when there is no room — the file tab still names it), interface-language button (the 文A mark — a menu of the 15 shipped languages, each under its own name), theme toggle (sun/moon), minimize/maximize/close. The window has custom chrome — no native title bar.",
                    "Menu highlights: File (Open, recent files), Edit (Copy Source → Target: Ctrl+T current row, Ctrl+Shift+T all selected; Preferences), View (Show empty keys), plus Export (Ctrl+E), Merge to new version, Import Target locres, Build locres from CSV, AI Translate, Multi-translate, Settings, Advanced settings (Tools -> Advanced settings jumps to the Advanced section at the bottom of Settings), Grid text selection is mouse-only (drag over Key/Source/Target, Ctrl+C; double-click = word in Source, whole key in Key, editor in Target) — MCP reads rows directly instead; Accent colour (Settings -> Interface Language -> round swatch; setting accent_color, #RRGGBB or empty for the theme default; shades are derived per theme); Problems tab Go / double-click jumps to a row and, when a filter hides it, asks the user Yes/No before showing all rows; the popover of a same-text-different-translations note walks the translations and offers Use this translation for the same text, behind the before -> after review; Prompt history (left-bar clock-with-arrow button or View -> Prompt history: every distinct prompt sent by AI Translation, Multi-translate and AI QC, stored as separate files under prompts/single, prompts/multi, prompts/qc in the profile folder; a title expands its text, Copy copies it; there is no MCP tool for it); Settings -> Advanced -> Reset cache (UI only, never via MCP: deletes all Translation Memory files, the prompt history and the AI QC cache and resets every setting incl. the API key, after two confirmations); Tools (AI Translation, Open Translation Memory folder = the profile folder holding translation_memory_{from}_{to}.json per pair, Run validation F8, Preferences, Advanced settings); File -> Recent files lists the last 10 opened files; Help (Check for updates asks the repository for its latest release on request only — UI only, no MCP tool; Documentation opens the user guide from docs/ next to the exe, Licenses shows the program's MIT license, the third-party component list and every full license text, About shows the version)."),
                Area("File tabs bar",
                    "One tab per open file (pin, close, drag-reorder; unsaved dot). Tabs from the last session restore lazily on launch.",
                    "MCP: open_file opens or activates a tab; get_status lists all tabs."),
                Area("Main grid",
                    "Columns: selection checkbox, status icons, KEY, SOURCE (tag-aware rendering), TARGET (double-click or F2/Enter to edit inline; in the editor Enter commits and moves to the next row, Ctrl+Enter commits and stays, Shift+Enter is a line break, Esc cancels, Ctrl+Z/Ctrl+Y undo/redo the typing only, Ctrl+T puts the Source into the editor; outside a text field Ctrl+Z/Ctrl+Y undo/redo grid edits). Right-click a row: copy key/source/target, Copy Source → Target, clear, review marks (Approved/Rejected), translate row with AI, comment. Right-click a column header: copy column (Key/Source/Target), plus cut/paste/clear for Target.",
                    "MCP: get_rows / set_translations work on the SAME rows (always unfiltered — grid filters never hide data from MCP)."),
                Area("Filter bar (above grid)",
                    "Text filter, chips: untranslated/translated, review status, commented rows, found rows (search matches), Expand rows (wrap long text).",
                    "MCP equivalents: get_rows filter/search parameters."),
                Area("Find / Replace (Ctrl+F / Ctrl+H)",
                    "Inline bar: query, In: All/Key/Source/Target, match case, whole word, regex; Replace writes only into Target.",
                    "MCP: find_replace mirrors this (same options) and can preview before applying."),
                Area("AI Translate panel",
                    "Single-file AI translation: language pair, scope (All/Selected/Untranslated; Selected shows its live row count and refuses to start when nothing is checked — it never falls back to the whole file), batch size, Translation Memory checkboxes (use persistent / save / session), Send with comments, Extra Context (Send row Key as context; Reference translation from a second file matched by key, with Source/Target column choice and reference quality), custom prompt, Start/Stop, progress.",
                    "MCP: run_ai_translation(from,to,scope) presses the same Start; stop_ai_translation stops. Extra Context options are per-session UI-only."),
                Area("Multi-translate panel",
                    "Batch translate into MANY languages at once; writes one output file per language into <output>/<locale>/<filename>. Has its own circuit breaker and PARTIAL marking on repeated failures.",
                    "MCP: not exposed (single-file flow is); use run_ai_translation per language pair instead."),
                Area("Settings (gear) — sections",
                    "AI Provider: Model name, API Key (dotted password field between Model name and Base URL — PASTE MANUALLY, MCP cannot read or set it; press Save after), Base URL, Test connection. " +
                    "Editor: auto-save enable/interval, show full key hashes. " +
                    "Validation: the OPTIONAL notes only — edge whitespace (OFF by default), ending punctuation, length ratio, duplicate sources; errors and warnings (tags, tokens, placeholders, plurals, mixed alphabets, content) always run, and a switch re-checks every open file. " +
                    "Files: default export format, Save As behavior, Export folder, Auto-save folder. " +
                    "Interface Language: 15 languages ship with the app (en, uk, de, es, fr, it, pl, pt-BR, ru, tr, vi, zh-Hans, zh-Hant, ja, ko); get_settings reports the exact allowed set for this build. " +
                    "Translation Memory: enable + Protected Terms glossary. " +
                    "AI Speed: prompt caching, parallel requests + max concurrent, auto-retry, request-rate limit. " +
                    "MCP Server: this bridge's enable checkbox.",
                    "MCP: get_settings / set_settings cover every field except the API key (location-guidance only) and the MCP toggle itself."),
                Area("Logs panel (bottom)",
                    "Tabs: Problems (validation findings grouped by kind), QC (suggestions from the AI review, grouped by category, each with a bulk Fix that opens the before/after review first — the tab only appears while a review has open suggestions), and Logs (info/warning/error stream; double-click some entries jumps to the row). Status bar under it, left to right: rows by their most serious problem (error / warning / info chips — a row counts once there, the Problems tab lists every problem; click filters, right-click clears the marks from the checked rows; hidden until a file is open), the latest message (the newest line by when it was issued — a log line never replaces a message set after it; clears after 15 s), the format badge, Selected, Showing X of Y (only while a filter hides rows), Untranslated N / total, and the Logs button. Selected counts checked rows in the WHOLE file — a filter changes what is visible, never what is selected — and an (N shown) note appears when part of the selection is hidden by the active filter.",
                    "MCP: get_logs streams the same log list; get_problems lists per-row validation problems; run_validation re-runs validators on demand."),
                Area("QC — Quality Control (left bar, \"QC\")",
                    "An AI review of an ALREADY-TRANSLATED file (it has no local checks of its own — the live validator reports those problems while you edit). The active tab is the file under review; when its translation sits in Source, the original must come from a second file; an optional reference file (same game, another language, matched by key) gives the model more context. EVERY row in scope (all rows, or the checked ones) is sent, minus what the Rows to review switches keep out, each showing how many rows it skips: rows without text (on), rows carried over automatically — only tags/numbers/placeholders (on), rows identical to the original (on), approved rows (on), and only rows the validator flagged (off). The panel shows the resulting row and request counts before anything is sent. The model receives the original, the current translation and the optional reference, and answers 'ok' or 'fix' with a corrected line (strict JSON, never prose); every fix is re-validated locally and rejected if it breaks a placeholder, tag, line break, trailing ellipsis or edge spacing. Verdicts are cached per language pair (keyed by model + prompt version + guidance + row texts), so re-runs only pay for what changed, and a cancelled run keeps everything already paid for. QC has its OWN batch size / parallelism settings, separate from the translation panel. Suggestions: a QC badge next to the comment icon opens a full before/after diff and the user picks Fix or Keep; Fix all / Fix selected (filter bar while the QC panel is open) and each category's Fix in the QC tab first open the same review window as the validator's fixes — nothing is written unseen, and every apply is ONE undo step. Suggestions persist in <file>.qc.json so a review survives a restart. A CSV report exports only rows with suggestions.",
                    "MCP: get_qc_proposals / apply_qc_fixes / keep_qc_proposals / export_qc_report cover the results; settings qc_batch_size, qc_parallel_enabled, qc_max_concurrent_requests, the row switches qc_skip_empty, qc_skip_auto_carried, qc_skip_identical, qc_skip_approved, qc_only_validator_flagged, plus the validator's content lists qc_leak_exceptions (words the leftover-language check must not flag) and qc_term_fixes (\"wrong=correct\" terminology rules). YOU CAN BE THE REVIEWER instead of the API: call get_qc_rows (counts first, then a count to fetch the rows), judge each row, and return {verdict:ok|fix, fix, reason} via submit_qc_verdicts — the same local FixValidator guards your fixes, and the user still approves them with Fix / Keep. That way the review costs the user's chat subscription, not their API key."),
                Area("Validation",
                    "Light validators run automatically ~300 ms after each edit plus a duplicate pass on open. ERRORS (tags, tokens, printf placeholders, plurals, model chatter such as a \"Translation:\" prefix) and WARNINGS (mixed alphabets, content, lost line breaks, leftover source language — needs the file language from its folder —, a missing protected term, the user's terminology rules) always run; the four Info NOTES (edge whitespace — off by default —, ending punctuation, length ratio, duplicate sources) are switches in Settings → Validation. Rows get red/yellow tints. No fix is ever written unseen: the lightbulb, the Problems tab [Fix] and Fix all open a review window showing every change before → after (changed words highlighted, spaces shown as dots); the user unticks what they do not want and the rest is one undo step. A fix that would break something else in its row is not offered at all. A model can usually fix flagged rows BETTER than the auto-fix by rewriting the target properly via set_translations.",
                    "MCP: get_problems → fix with set_translations → run_validation to confirm zero."),
                Area("Review & comments",
                    "Per-row review marks (Approved/Rejected) and free-text reviewer comments (comment icon on the row). Both are sidecar metadata: reviews in AppData, comments next to the file (<file>.comments.json); never written into exports. Comments can ride to the AI as per-row instructions.",
                    "MCP: rows returned by get_rows/get_problems include review + comment fields; set_review marks rows approved/rejected/none; set_comments sets or removes comments."),
                Area("Game updates — Merge to new version",
                    "Carries translations from an old file (.locres, .csv or .po) into a new game version's .locres. A translation is carried only where the key matches AND the original text is PROVEN unchanged - by SourceStringHash for an old .locres, by the source column for a CSV/PO. Rows whose original changed are left untranslated (violet tint) so they are retranslated rather than shipping a line written for the old wording; new keys get a green tint. Before anything is written the user sees a report (carried / not carried / new / removed) and can fall back to matching by key alone for that run; the permanent default lives in Settings -> Advanced (merge_match_by_key_only). The grid shows the new original in Source and the carried translation in Target, so Untranslated counts and AI scopes mean what they normally mean.",
                    "MCP: not exposed; guide the user through the menu."),
                Area("Build locres from CSV / Export",
                    "Pack the open CSV grid into a .locres using a base .locres from the game (the base defines the binary format and per-key hashes). Export (Ctrl+E) does format-appropriate saving.",
                    "MCP: save_current covers normal saving; the CSV→locres build needs the user to pick the base file."),
                Area("Where files live",
                    "Saves/exports: Export folder (timestamped Export_* subfolders). Auto-saves: AutoSaved_* subfolders. Config + per-pair Translation Memory + review sidecars: %AppData%/SmartUnrealLocalization/. API key inside config.json is DPAPI-encrypted.",
                    "MCP: export_path / auto_save_path settings control the folders.")
            ),
            ["api_key_help"] = "To set the API key: open Settings (gear icon) → AI Provider section → paste the key into the dotted password field labeled 'API Key' (between Model name and Base URL) → press Save. Use 'Test connection' to verify. MCP deliberately cannot read or write this value.",
        };

        private static JObject Area(string name, string what, string details) => new()
        {
            ["area"] = name,
            ["what"] = what,
            ["details"] = details,
        };
    }
}
