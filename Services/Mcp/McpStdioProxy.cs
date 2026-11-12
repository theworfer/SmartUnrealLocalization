using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// The MCP stdio server chat clients launch (<c>SmartUnrealLocalization.exe --mcp</c>).
    /// Deliberately DUMB: it owns no app state — every tool call is forwarded over
    /// the named pipe to the RUNNING GUI (<see cref="McpGuiServer"/>), so the model
    /// controls exactly what the user sees. stdout carries ONLY the MCP protocol;
    /// diagnostics go to stderr.
    /// </summary>
    public static class McpStdioProxy
    {
        public static async Task<int> RunAsync(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithTools<SulTools>();
            await builder.Build().RunAsync();
            return 0;
        }
    }

    /// <summary>
    /// Tool surface exposed to the model. Each handler forwards to the GUI over
    /// the pipe and returns the GUI's JSON verbatim (or a friendly error when the
    /// app is not running / MCP is disabled in Settings).
    /// </summary>
    [McpServerToolType]
    public sealed class SulTools
    {
        [McpServerTool(Name = "get_status"), Description(
            "Current state of the Smart Unreal Localization app: active file, format, row counts " +
            "(total / untranslated), unsaved-changes flag, whether an AI translation run is active, and open tabs.")]
        public static Task<string> GetStatus() => Call("get_status", new JObject());

        [McpServerTool(Name = "open_file"), Description(
            "Open a localization file (.locres, .csv or .po) in the app, or switch to its tab if already open. " +
            "Returns the same payload as get_status.")]
        public static Task<string> OpenFile(
            [Description("Absolute path to the .locres/.csv/.po file")] string path)
            => Call("open_file", new JObject { ["path"] = path });

        [McpServerTool(Name = "get_rows"), Description(
            "Read rows from the open file (paged). Each row: namespace, key, source, target, translatable. " +
            "Rows with translatable=false are tag-only — copy source to target instead of translating. " +
            "Use filter=untranslated to fetch work; page with offset until 'returned' is 0. " +
            "Search supports case sensitivity, whole-word matching and regex — e.g. find a row by its key, " +
            "or every row containing a specific word.")]
        public static Task<string> GetRows(
            [Description("all | untranslated | translated")] string filter = "all",
            [Description("Optional text/pattern to search for")] string? search = null,
            [Description("Where to search: all | source | target | key")] string search_in = "all",
            [Description("Case-sensitive search")] bool match_case = false,
            [Description("Match whole words only")] bool whole_word = false,
            [Description("Treat search as a .NET regex")] bool use_regex = false,
            [Description("Rows to skip")] int offset = 0,
            [Description("Rows to return (max 200)")] int count = 50)
            => Call("get_rows", new JObject
            {
                ["filter"] = filter, ["search"] = search, ["search_in"] = search_in,
                ["match_case"] = match_case, ["whole_word"] = whole_word, ["use_regex"] = use_regex,
                ["offset"] = offset, ["count"] = count,
            });

        [McpServerTool(Name = "find_replace"), Description(
            "Search — and optionally replace — across the open file, like the app's Ctrl+H bar. " +
            "Replacement writes ONLY into Target (source/key are read-only by design) and respects the tag " +
            "guard: a change that would break {tokens} or <tags> vs the source is skipped per-row. " +
            "Call with apply=false first to PREVIEW (before/after for up to 20 rows), then apply=true to write " +
            "(one undo step). Regex replacement supports $1 group references.")]
        public static Task<string> FindReplace(
            [Description("Text or regex to find")] string query,
            [Description("Where to search: target (default) | source | key | all")] string search_in = "target",
            [Description("Case-sensitive")] bool match_case = false,
            [Description("Whole words only (letter/digit/_ boundaries)")] bool whole_word = false,
            [Description("Treat query as a .NET regex")] bool use_regex = false,
            [Description("Replacement text (omit for search-only)")] string? replace_with = null,
            [Description("false = preview, true = write the changes")] bool apply = false)
            => Call("find_replace", new JObject
            {
                ["query"] = query, ["search_in"] = search_in,
                ["match_case"] = match_case, ["whole_word"] = whole_word, ["use_regex"] = use_regex,
                ["replace_with"] = replace_with, ["apply"] = apply,
            });

        [McpServerTool(Name = "get_settings"), Description(
            "Every app setting MCP can see: current value, type, allowed values, whether it is settable via " +
            "set_settings, its exact location in the UI, and what it does. Also shows whether the API key is " +
            "set (the key itself is never exposed).")]
        public static Task<string> GetSettings() => Call("get_settings", new JObject());

        [McpServerTool(Name = "set_settings"), Description(
            "Change app settings by name (see get_settings for names/allowed values). Pass a JSON object like " +
            "{\"parallel_translation_enabled\": true, \"max_concurrent_requests\": 3}. Values are validated; " +
            "invalid ones are rejected per-item with a reason. Changes persist and live-apply where the app " +
            "supports it (theme, UI language, auto-save timer, grid visibility). The API key cannot be set " +
            "this way — tell the user where to paste it (get_settings shows the exact field location).")]
        public static Task<string> SetSettings(
            [Description("JSON object of {setting_name: value}")] string settings_json)
        {
            JObject settings;
            try { settings = JObject.Parse(settings_json); }
            catch (Exception ex) { return Task.FromResult(Err($"settings_json is not a valid JSON object: {ex.Message}")); }
            return Call("set_settings", new JObject { ["settings"] = settings });
        }

        [McpServerTool(Name = "get_app_map"), Description(
            "A complete map of the app's interface and features: every panel, menu and setting — what it does, " +
            "where it is on screen, and which MCP tool covers it. Use it to guide the user around the app " +
            "(e.g. exactly where to paste the API key) or to decide which tool to call.")]
        public static Task<string> GetAppMap() => Call("get_app_map", new JObject());

        [McpServerTool(Name = "get_logs"), Description(
            "The app's log stream (same as the bottom Logs panel): timestamped info/warning/error entries — " +
            "AI batch failures, save results, validation notes. Returns the newest 'tail' entries.")]
        public static Task<string> GetLogs(
            [Description("all | info | warning | error")] string level = "all",
            [Description("How many newest entries (max 500)")] int tail = 100)
            => Call("get_logs", new JObject { ["level"] = level, ["tail"] = tail });

        [McpServerTool(Name = "get_problems"), Description(
            "Rows flagged by the app's validators (tag/token mismatches, whitespace issues, …) with per-row " +
            "problem kinds and severities. You can usually fix these BETTER than the app's auto-fix: rewrite " +
            "the target correctly and send it via set_translations, then run_validation to confirm.")]
        public static Task<string> GetProblems(
            [Description("Rows to skip")] int offset = 0,
            [Description("Rows to return (max 200)")] int count = 100)
            => Call("get_problems", new JObject { ["offset"] = offset, ["count"] = count });

        [McpServerTool(Name = "get_qc_proposals"), Description(
            "Read the pending QC suggestions — from the app's AI review or from submit_qc_verdicts (paged, most severe first): source, current target, " +
            "suggested replacement, reason, kinds, severity. has_fix=false means the row is flagged without a " +
            "replacement — you can write a better translation yourself and send it via set_translations.")]
        public static Task<string> GetQcProposals(
            [Description("Rows to skip")] int offset = 0,
            [Description("Rows to return (max 200)")] int count = 100)
            => Call("get_qc_proposals", new JObject { ["offset"] = offset, ["count"] = count });

        [McpServerTool(Name = "apply_qc_fixes"), Description(
            "Apply QC suggestions to the rows — all pending ones, or only the listed keys. One undo step in the " +
            "app. Suggestions without a replacement are skipped; a row edited after the review is skipped as " +
            "outdated. Run run_validation afterwards to confirm tags and placeholders survived.")]
        public static Task<string> ApplyQcFixes(
            [Description("Optional JSON array of {\"namespace\":\"...\",\"key\":\"...\"}; omit to apply all")] string? items_json = null)
        {
            if (string.IsNullOrWhiteSpace(items_json)) return Call("apply_qc_fixes", new JObject());
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("apply_qc_fixes", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "get_qc_rows"), Description(
            "The rows the QC AI review would check in the OPEN file: every row in scope minus what the user's " +
            "skip switches keep out (empty, carried over automatically, identical to the original, approved; or " +
            "only rows the validator flagged — settings qc_skip_* / qc_only_validator_flagged). Without 'count' it " +
            "returns just the counts — cheap planning. Pass a 'count' to get the ACTUAL rows (source, current " +
            "target, problems), review them YOURSELF, and return your verdicts with submit_qc_verdicts — that way " +
            "the review costs the user's subscription, not their API key. Page with offset. A single-language file " +
            "(translation in Source, Target empty — every .locres) is refused: its original lives in a second file.")]
        public static Task<string> GetQcRows(
            [Description("Rows to return (0 = counts only, max 200)")] int count = 0,
            [Description("Rows to skip")] int offset = 0,
            [Description("Only the rows checked in the grid. The selection is the whole file: rows hidden by an active filter stay checked and are still included.")] bool selected_only = false)
            => Call("get_qc_rows", new JObject
            {
                ["count"] = count,
                ["offset"] = offset,
                ["selected_only"] = selected_only,
            });
        [McpServerTool(Name = "submit_qc_verdicts"), Description(
            "Send YOUR quality verdicts for rows you reviewed (from get_qc_rows or get_rows). Each item is " +
            "{namespace, key, verdict:\"ok\"|\"fix\", fix?, reason?}. Fix only real problems — wrong meaning, " +
            "foreign-language leftovers, grammar, broken tags/placeholders, wrong UI casing, lost ellipsis, " +
            "glossary violations — never rewrite good text for style alone. Every fix is re-validated locally and " +
            "REJECTED if it changes placeholder/tag/line-break counts, drops a trailing ellipsis or edge spaces. " +
            "Accepted ones become suggestions the user reviews (Fix / Keep) — nothing is applied automatically.")]
        public static Task<string> SubmitQcVerdicts(
            [Description("JSON array of {\"namespace\":\"...\",\"key\":\"...\",\"verdict\":\"ok|fix\",\"fix\":\"...\",\"reason\":\"...\"}")] string items_json)
        {
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("submit_qc_verdicts", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "keep_qc_proposals"), Description(
            "Dismiss QC suggestions (the equivalent of pressing Keep in the app). Pass the keys to dismiss; OMIT " +
            "items_json entirely to dismiss every pending suggestion (an empty array dismisses nothing). A " +
            "dismissed suggestion is remembered and will not be proposed again by a later review.")]
        public static Task<string> KeepQcProposals(
            [Description("Optional JSON array of {\"namespace\":\"...\",\"key\":\"...\"}; omit to dismiss all")] string? items_json = null)
        {
            if (string.IsNullOrWhiteSpace(items_json)) return Call("keep_qc_proposals", new JObject());
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("keep_qc_proposals", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "export_qc_report"), Description(
            "Write a CSV report of the current QC suggestions (only rows that have one) into the app's export " +
            "folder and return its path. Columns: namespace, key, severity, kinds, source, before, after, reason.")]
        public static Task<string> ExportQcReport() => Call("export_qc_report", new JObject());

        [McpServerTool(Name = "run_validation"), Description(
            "Re-run all validators over every row right now and return fresh problem counts by kind. " +
            "Call after a batch of set_translations to verify your fixes landed clean.")]
        public static Task<string> RunValidation() => Call("run_validation", new JObject());

        [McpServerTool(Name = "set_translations"), Description(
            "Write translations into the open file's rows (identified by namespace+key exactly as get_rows " +
            "returned them; max 500 per call). Every <angle tag> and {curly token} from the source must appear " +
            "in the target — mismatched items are rejected per-item, the rest still apply. All applied changes " +
            "form ONE undo step in the app. Nothing touches the disk until save_current. SAFETY: these writes " +
            "NEVER enter the Translation Memory — the user promotes reviewed rows manually (row context menu → " +
            "Send to Translation Memory), so unreviewed output can't contaminate future auto-fills.")]
        public static Task<string> SetTranslations(
            [Description("JSON array of {\"namespace\":\"...\",\"key\":\"...\",\"target\":\"...\"} objects")] string items_json)
        {
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("set_translations", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "run_ai_translation"), Description(
            "Start the app's OWN AI translation pipeline (uses the user's configured API key, glossary and tag " +
            "protection). SAFETY: an MCP-initiated run NEVER WRITES to the Translation Memory — reviewed rows are " +
            "promoted manually by the user. TM READING is a choice: when a TM exists for the pair and " +
            "use_translation_memory is omitted, this returns needs_decision=true with a question — ASK THE USER, " +
            "then call again with their answer. Runs in the background: poll get_status (ai_progress has " +
            "done/total/percent/eta_seconds) until ai_run_active is false. Refuses if a run is already active.")]
        public static Task<string> RunAiTranslation(
            [Description("Source language, e.g. English")] string from,
            [Description("Target language, e.g. Ukrainian")] string to,
            [Description("untranslated (default) | all")] string scope = "untranslated",
            [Description("Use the existing Translation Memory to auto-fill repeats? Omit to be asked when one exists.")] bool? use_translation_memory = null)
            => Call("run_ai_translation", new JObject
            {
                ["from"] = from, ["to"] = to, ["scope"] = scope,
                ["use_translation_memory"] = use_translation_memory,
            });

        [McpServerTool(Name = "set_review"), Description(
            "Set the reviewer status on rows (approved | rejected | none), identified by namespace+key exactly " +
            "as get_rows returns them (max 500 per call). Persists to the review sidecar — same as clicking " +
            "Approve/Reject in the row context menu. Use after checking translations to mark what you verified.")]
        public static Task<string> SetReview(
            [Description("JSON array of {\"namespace\":\"...\",\"key\":\"...\",\"status\":\"approved|rejected|none\"}")] string items_json)
        {
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("set_review", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "set_comments"), Description(
            "Set or remove reviewer comments on rows (max 500 per call). Empty/omitted text removes the comment. " +
            "Comments are metadata (never exported into files); with 'Send with comments' enabled they travel " +
            "to the AI as per-row instructions — useful to pin context like 'this Charge means a fee'.")]
        public static Task<string> SetComments(
            [Description("JSON array of {\"namespace\":\"...\",\"key\":\"...\",\"text\":\"...\"} (empty text = remove)")] string items_json)
        {
            JArray items;
            try { items = JArray.Parse(items_json); }
            catch (Exception ex) { return Task.FromResult(Err($"items_json is not a valid JSON array: {ex.Message}")); }
            return Call("set_comments", new JObject { ["items"] = items });
        }

        [McpServerTool(Name = "stop_ai_translation"), Description(
            "Stop the currently running AI translation, if any.")]
        public static Task<string> StopAiTranslation() => Call("stop_ai_translation", new JObject());

        [McpServerTool(Name = "save_current"), Description(
            "Save the open file in its own format into a timestamped Export folder (the app's normal Save). " +
            "The original opened file is never overwritten in place.")]
        public static Task<string> SaveCurrent() => Call("save_current", new JObject());

        [McpServerTool(Name = "undo"), Description("Undo the last N edit steps in the app (default 1).")]
        public static Task<string> Undo([Description("Steps, 1-100")] int steps = 1)
            => Call("undo", new JObject { ["steps"] = steps });

        [McpServerTool(Name = "redo"), Description("Redo the last N undone edit steps in the app (default 1).")]
        public static Task<string> Redo([Description("Steps, 1-100")] int steps = 1)
            => Call("redo", new JObject { ["steps"] = steps });

        [McpServerTool(Name = "get_translation_rules"), Description(
            "The app's translation contract: tag/token preservation rules and the user's protected terms " +
            "(glossary). Read this once before translating rows yourself.")]
        public static Task<string> GetTranslationRules() => Call("get_translation_rules", new JObject());

        // ───────────────────────── pipe client ─────────────────────────

        private static async Task<string> Call(string op, JObject args)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", McpPipe.PipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(2000); }
                catch (TimeoutException)
                {
                    return Err("Smart Unreal Localization is not running (or its MCP server is off). " +
                               "Start the app and enable Settings → MCP Server, then retry.");
                }

                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);

                var request = new JObject { ["op"] = op, ["args"] = args };
                await writer.WriteLineAsync(request.ToString(Newtonsoft.Json.Formatting.None));

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // open_file on a huge locres can take a while
                string? line = await reader.ReadLineAsync().WaitAsync(cts.Token);
                return line ?? Err("The app closed the connection unexpectedly.");
            }
            catch (Exception ex)
            {
                return Err($"Bridge failure: {ex.Message}");
            }
        }

        private static string Err(string message)
            => new JObject { ["ok"] = false, ["error"] = message }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
