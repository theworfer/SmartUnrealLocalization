using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// GUI-side half of the MCP bridge: a named-pipe JSON-lines server that lets
    /// the stdio proxy (<c>SmartUnrealLocalization.exe --mcp</c>) drive the RUNNING
    /// window. Every operation marshals onto the Avalonia UI thread and goes
    /// through the same code paths the user's own clicks use (open, undo batches,
    /// tag validation, the AI panel's hardened run pipeline) — the model gets no
    /// side door around the app's safety rails.
    ///
    /// <para>Enabled by Settings → MCP Server (AppConfig.McpServerEnabled).
    /// Multiple chat clients may connect concurrently — each gets its own pipe
    /// instance; requests are handled per-line, state mutations serialize on the
    /// UI thread by construction.</para>
    /// </summary>
    public sealed class McpGuiServer
    {
        private static readonly Lazy<McpGuiServer> _lazy = new(() => new McpGuiServer());
        public static McpGuiServer Instance => _lazy.Value;
        private McpGuiServer() { }

        private MainWindowViewModel? _vm;
        private Func<string, string, string, bool, string>? _startAiRun; // (from, to, scope, useTm) → "started" | error text
        private CancellationTokenSource? _cts;

        private static readonly Regex CurlyToken = new(@"\{[^}]+\}", RegexOptions.Compiled);
        private static readonly Regex AngleTag = new(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex TokenStrip = new(@"<[^>]+>|\{[^}]+\}", RegexOptions.Compiled);

        /// <summary>Wire the live window in (called once from MainWindow).</summary>
        public void Attach(MainWindowViewModel vm, Func<string, string, string, bool, string> startAiRun)
        {
            _vm = vm;
            _startAiRun = startAiRun;
        }

        /// <summary>Start/stop with the Settings checkbox (idempotent).</summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled && _cts == null)
            {
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(_cts.Token);
                _vm?.AddLog(string.Format(Loc.T("log.mcp.listening"), McpPipe.PipeName), LogLevel.Info);
            }
            else if (!enabled && _cts != null)
            {
                _cts.Cancel();
                _cts = null;
                _vm?.AddLog(Loc.T("log.mcp.stopped"), LogLevel.Info);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        McpPipe.PipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    _ = HandleClientAsync(server, ct); // fire-and-forget per client
                    server = null;                     // ownership moved to the handler
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MCP] accept error: {ex.Message}");
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
                finally { server?.Dispose(); }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            _vm?.AddLog(Loc.T("log.mcp.connected"), LogLevel.Info);
            try
            {
                using var _ = pipe;
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true };

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string response;
                    try
                    {
                        var req = JObject.Parse(line);
                        string op = req.Value<string>("op") ?? "";
                        var args = req["args"] as JObject ?? new JObject();
                        var data = await DispatchAsync(op, args);
                        response = new JObject { ["ok"] = true, ["data"] = data }.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    catch (Exception ex)
                    {
                        response = new JObject { ["ok"] = false, ["error"] = ex.Message }.ToString(Newtonsoft.Json.Formatting.None);
                    }
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[MCP] client error: {ex.Message}"); }
            finally { _vm?.AddLog(Loc.T("log.mcp.disconnected"), LogLevel.Info); }
        }

        // ───────────────────────── dispatch ─────────────────────────

        private async Task<JToken> DispatchAsync(string op, JObject args)
        {
            var vm = _vm ?? throw new InvalidOperationException("The app window is not ready yet.");

            switch (op)
            {
                case "get_status":
                    return await OnUi(() => GetStatus(vm));

                case "open_file":
                {
                    string path = ReqString(args, "path");
                    if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
                    return await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await vm.OpenFileAsync(path);
                        return GetStatus(vm);
                    });
                }

                case "get_rows":
                    return await OnUi(() => GetRows(vm,
                        args.Value<string>("filter") ?? "all",
                        args.Value<string>("search"), args.Value<string>("search_in") ?? "all",
                        args.Value<bool?>("match_case") ?? false,
                        args.Value<bool?>("whole_word") ?? false,
                        args.Value<bool?>("use_regex") ?? false,
                        args.Value<int?>("offset") ?? 0, args.Value<int?>("count") ?? 50));

                case "find_replace":
                    return await OnUi(() => FindReplace(vm,
                        ReqString(args, "query"),
                        args.Value<string>("search_in") ?? "target",
                        args.Value<bool?>("match_case") ?? false,
                        args.Value<bool?>("whole_word") ?? false,
                        args.Value<bool?>("use_regex") ?? false,
                        args.Value<string>("replace_with"),
                        args.Value<bool?>("apply") ?? false));

                case "get_settings":
                    return await OnUi(() => GetSettings());

                case "set_settings":
                {
                    if (args["settings"] is not JObject settings || !settings.HasValues)
                        throw new ArgumentException("settings must be a non-empty object of {name: value}.");
                    return await OnUi(() => SetSettings(vm, settings));
                }

                case "get_app_map":
                    return McpAppMap.Build();

                case "get_logs":
                    return await OnUi(() => GetLogs(vm,
                        args.Value<string>("level") ?? "all",
                        args.Value<int?>("tail") ?? 100));

                case "get_problems":
                    return await OnUi(() => GetProblems(vm,
                        args.Value<int?>("offset") ?? 0, args.Value<int?>("count") ?? 100));

                case "run_validation":
                    return await OnUi(() => RunValidation(vm));

                case "get_qc_proposals":
                    return await OnUi(() => GetQcProposals(vm,
                        args.Value<int?>("offset") ?? 0, args.Value<int?>("count") ?? 100));

                case "apply_qc_fixes":
                {
                    var keys = args["items"] as JArray; // null = every pending proposal
                    return await OnUi(() => ApplyQcFixes(vm, keys));
                }

                case "get_qc_rows":
                    return await OnUi(() => GetQcRows(vm,
                        args.Value<bool?>("selected_only") ?? false,
                        args.Value<int?>("offset") ?? 0,
                        args.Value<int?>("count") ?? 0));

                case "submit_qc_verdicts":
                {
                    if (args["items"] is not JArray verdicts || verdicts.Count == 0)
                        throw new ArgumentException("items must be a non-empty array of {namespace?, key, verdict, fix?, reason?}.");
                    if (verdicts.Count > 500) throw new ArgumentException("Too many items — max 500 per call.");
                    return await OnUi(() => SubmitQcVerdicts(vm, verdicts));
                }

                case "keep_qc_proposals":
                {
                    var keep = args["items"] as JArray; // null = dismiss everything pending
                    return await OnUi(() => KeepQcProposals(vm, keep));
                }

                case "export_qc_report":
                    return await OnUi(() =>
                    {
                        if (Services.Qc.QcService.Instance.PendingCount == 0)
                            throw new InvalidOperationException("No QC suggestions to report — review rows first (get_qc_rows → submit_qc_verdicts, or the QC panel's AI review).");
                        return (JToken)new JObject { ["path"] = vm.ExportQcReport() };
                    });

                case "set_translations":
                {
                    if (args["items"] is not JArray items || items.Count == 0)
                        throw new ArgumentException("items must be a non-empty array of {namespace?, key, target}.");
                    if (items.Count > 500)
                        throw new ArgumentException("Too many items — send at most 500 per call and page.");
                    return await OnUi(() => SetTranslations(vm, items));
                }

                case "run_ai_translation":
                {
                    string from = ReqString(args, "from"), to = ReqString(args, "to");
                    string scope = args.Value<string>("scope") ?? "untranslated";
                    bool? useTm = args.Value<bool?>("use_translation_memory");

                    // TM decision gate: when a Translation Memory FILE exists for this
                    // pair and the caller didn't decide, don't guess — hand the choice
                    // back so the model asks the USER (use previous translations vs
                    // translate everything fresh).
                    var tmFile = Path.Combine(AppConfig.ConfigDir(),
                        $"translation_memory_{from}_{to}".ToLowerInvariant() + ".json");
                    bool tmExists = File.Exists(tmFile);
                    if (tmExists && useTm == null)
                    {
                        int entries = 0;
                        try { entries = JObject.Parse(File.ReadAllText(tmFile)).Count; } catch { }
                        return new JObject
                        {
                            ["started"] = false,
                            ["needs_decision"] = true,
                            ["question_for_user"] =
                                $"A Translation Memory for {from} → {to} already exists ({entries} saved translations). " +
                                "Should it be used to auto-fill previously translated strings (faster, cheaper, consistent), " +
                                "or should everything be translated from scratch (fresh wording)?",
                            ["how_to_proceed"] = "Ask the user, then call run_ai_translation again with use_translation_memory=true or false.",
                        };
                    }

                    var start = _startAiRun ?? throw new InvalidOperationException("AI panel is not available.");
                    bool tmRead = useTm ?? false; // no TM file (or explicit false) → nothing to reuse
                    string result = (string?)await OnUi(() => (JToken)start(from, to, scope, tmRead)) ?? "";
                    if (result != "started") throw new InvalidOperationException(result);
                    return new JObject
                    {
                        ["started"] = true,
                        ["using_translation_memory"] = tmRead,
                        ["note"] = "Poll get_status (ai_progress has done/total/ETA); ai_run_active turns false when finished. " +
                                   "This run NEVER writes to the Translation Memory (MCP safety rule).",
                    };
                }

                case "stop_ai_translation":
                    return await OnUi(() =>
                    {
                        var run = vm.ActiveAiTranslation;
                        if (run == null) return (JToken)new JObject { ["stopped"] = false, ["note"] = "No AI run is active." };
                        run.Stop();
                        return new JObject { ["stopped"] = true };
                    });

                case "save_current":
                    return await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (vm.RowCount == 0) throw new InvalidOperationException("Nothing to save — no file is open.");
                        await vm.SaveCurrentFormatAsync();
                        return (JToken)new JObject
                        {
                            ["unsaved_changes"] = vm.HasUnsavedChanges,
                            ["note"] = "Saved in the file's own format into a timestamped Export folder.",
                        };
                    });

                case "undo":
                case "redo":
                {
                    int steps = Math.Clamp(args.Value<int?>("steps") ?? 1, 1, 100);
                    return await OnUi(() =>
                    {
                        for (int i = 0; i < steps; i++)
                        {
                            if (op == "undo") vm.UndoManager.Undo();
                            else vm.UndoManager.Redo();
                        }
                        vm.ApplyFilter();
                        return (JToken)new JObject { ["done"] = op, ["steps"] = steps };
                    });
                }

                case "set_review":
                {
                    if (args["items"] is not JArray revItems || revItems.Count == 0)
                        throw new ArgumentException("items must be a non-empty array of {namespace?, key, status}.");
                    if (revItems.Count > 500) throw new ArgumentException("Too many items — max 500 per call.");
                    return await OnUi(() => SetReview(vm, revItems));
                }

                case "set_comments":
                {
                    if (args["items"] is not JArray cmtItems || cmtItems.Count == 0)
                        throw new ArgumentException("items must be a non-empty array of {namespace?, key, text}.");
                    if (cmtItems.Count > 500) throw new ArgumentException("Too many items — max 500 per call.");
                    return await OnUi(() => SetComments(vm, cmtItems));
                }

                case "get_translation_rules":
                    return await OnUi(() => (JToken)new JObject
                    {
                        ["rules"] = "Translate game UI text. NEVER translate, alter or reorder anything inside "
                                  + "<angle brackets> or {curly braces} — keep those tags exactly as in the source. "
                                  + "Keep all line breaks (\\n) where they are. Keep protected terms untranslated.",
                        ["protected_terms"] = new JArray(AppConfig.Instance.ProtectedTerms),
                    });

                default:
                    throw new ArgumentException($"Unknown op: {op}");
            }
        }

        private static string ReqString(JObject args, string name)
            => args.Value<string>(name) is { Length: > 0 } v ? v
               : throw new ArgumentException($"Missing required argument: {name}");

        private static Task<JToken> OnUi(Func<JToken> f) => Dispatcher.UIThread.InvokeAsync(f).GetTask();

        // ───────────────────────── ops (UI thread) ─────────────────────────

        private static JToken GetStatus(MainWindowViewModel vm)
        {
            var tabs = new JArray();
            foreach (var s in vm.OpenSessions)
            {
                tabs.Add(new JObject
                {
                    ["file"] = s.Path,
                    ["format"] = s.Format.ToString(),
                    ["active"] = s.IsActive,
                    ["loaded"] = s.IsLoaded,
                    ["unsaved_changes"] = s.HasUnsavedChanges,
                });
            }
            int untranslated = vm.AllRows.Count(r => r.IsUntranslated);
            var status = new JObject
            {
                ["active_file"] = vm.OriginalFilePath,
                ["format"] = vm.CurrentFormat.ToString(),
                ["rows_total"] = vm.RowCount,
                ["rows_untranslated"] = untranslated,
                ["unsaved_changes"] = vm.HasUnsavedChanges,
                ["ai_run_active"] = vm.ActiveAiTranslation != null,
                ["open_tabs"] = tabs,
            };
            if (vm.ActiveAiTranslation?.ProgressProbe is { } probe)
            {
                var (done, errors, total, eta) = probe();
                status["ai_progress"] = new JObject
                {
                    ["done"] = done,
                    ["errors"] = errors,
                    ["total"] = total,
                    ["percent"] = total > 0 ? Math.Round(100.0 * (done + errors) / total, 1) : 0,
                    ["eta_seconds"] = eta is { } s ? Math.Round(s) : (double?)null,
                };
            }
            return status;
        }

        /// <summary>
        /// FindBar-equivalent matcher: literal (escaped) unless use_regex; optional
        /// whole-word boundaries (letters/digits/_ like the protected-terms rule);
        /// case-insensitive by default. A 2s regex timeout guards against
        /// catastrophic user patterns.
        /// </summary>
        private static Regex BuildMatcher(string query, bool matchCase, bool wholeWord, bool useRegex)
        {
            string pattern = useRegex ? query : Regex.Escape(query);
            if (wholeWord) pattern = $@"(?<![\p{{L}}\p{{Nd}}_])(?:{pattern})(?![\p{{L}}\p{{Nd}}_])";
            var opts = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            try { return new Regex(pattern, opts, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException($"Invalid regex: {ex.Message}"); }
        }

        private static bool RowMatches(TranslationRow r, Regex m, string searchIn) => searchIn switch
        {
            "source" => m.IsMatch(r.Source ?? ""),
            "target" => m.IsMatch(r.Target ?? ""),
            "key" => m.IsMatch(r.Key ?? "") || m.IsMatch(r.Namespace ?? ""),
            "all" => m.IsMatch(r.Source ?? "") || m.IsMatch(r.Target ?? "")
                  || m.IsMatch(r.Key ?? "") || m.IsMatch(r.Namespace ?? ""),
            _ => throw new ArgumentException("search_in must be all | source | target | key"),
        };

        private static JToken GetRows(MainWindowViewModel vm, string filter,
            string? search, string searchIn, bool matchCase, bool wholeWord, bool useRegex,
            int offset, int count)
        {
            count = Math.Clamp(count, 1, 200);
            offset = Math.Max(0, offset);

            IEnumerable<TranslationRow> rows = vm.AllRows; // unfiltered by design (grid filters never hide data from MCP)
            rows = filter switch
            {
                "untranslated" => rows.Where(r => r.IsUntranslated),
                "translated" => rows.Where(r => !r.IsUntranslated),
                "all" => rows,
                _ => throw new ArgumentException("filter must be all | untranslated | translated"),
            };
            if (!string.IsNullOrEmpty(search))
            {
                var matcher = BuildMatcher(search, matchCase, wholeWord, useRegex);
                rows = rows.Where(r => RowMatches(r, matcher, searchIn));
            }

            var list = rows.ToList();
            var page = new JArray();
            foreach (var r in list.Skip(offset).Take(count))
                page.Add(RowJson(r));
            return new JObject
            {
                ["total"] = list.Count,
                ["offset"] = offset,
                ["returned"] = page.Count,
                ["rows"] = page,
            };
        }

        private static JObject RowJson(TranslationRow r) => new()
        {
            ["namespace"] = r.Namespace ?? "",
            ["key"] = r.Key ?? "",
            ["source"] = r.Source ?? "",
            ["target"] = r.Target ?? "",
            // Tag-only strings (no letters once tags are stripped) shouldn't be
            // "translated" — copy the source into target instead.
            ["translatable"] = Regex.IsMatch(TokenStrip.Replace(r.Source ?? "", ""), @"\p{L}"),
            ["review"] = r.Review.ToString().ToLowerInvariant(),
            ["comment"] = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment,
        };

        private static JToken FindReplace(MainWindowViewModel vm, string query, string searchIn,
            bool matchCase, bool wholeWord, bool useRegex, string? replaceWith, bool apply)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");
            var matcher = BuildMatcher(query, matchCase, wholeWord, useRegex);
            bool replacing = replaceWith != null;
            if (replacing && searchIn is not ("target" or "all"))
                throw new ArgumentException("Replacement writes only into Target — use search_in = target (or all, which still replaces only Target).");

            var matches = vm.AllRows.Where(r => RowMatches(r, matcher, searchIn)).ToList();
            int occurrences = 0;
            var preview = new JArray();
            var actions = new List<CellEditAction>();
            int skippedTagGuard = 0;

            vm.SuppressUndoCapture = true;
            try
            {
                foreach (var r in matches)
                {
                    string target = r.Target ?? "";
                    int inTarget = matcher.Matches(target).Count;
                    occurrences += searchIn == "target" ? inTarget : matcher.Matches(
                        string.Join("\n", r.Source ?? "", target, r.Key ?? "")).Count;

                    if (!replacing || inTarget == 0) continue;
                    string newTarget = matcher.Replace(target, replaceWith!);

                    // Tag guard — a replacement must not create/destroy {tokens} or <tags>
                    // relative to the SOURCE contract (same rule as set_translations).
                    if (CurlyToken.Matches(r.Source ?? "").Count != CurlyToken.Matches(newTarget).Count
                        || AngleTag.Matches(r.Source ?? "").Count != AngleTag.Matches(newTarget).Count)
                    {
                        skippedTagGuard++;
                        continue;
                    }

                    if (preview.Count < 20)
                        preview.Add(new JObject
                        {
                            ["namespace"] = r.Namespace ?? "", ["key"] = r.Key ?? "",
                            ["before"] = target, ["after"] = newTarget,
                        });
                    if (apply && newTarget != target)
                    {
                        actions.Add(new CellEditAction(r, target, newTarget));
                        r.Target = newTarget;
                        vm.MarkTargetSnapshot(r, newTarget);
                    }
                }
            }
            finally { vm.SuppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                vm.UndoManager.AddAction(new BatchEditAction(actions));
                vm.MarkDirty();
                vm.ApplyFilter();
                vm.AddLog(string.Format(Loc.T("log.mcp.findReplace"), actions.Count), LogLevel.Info);
            }

            return new JObject
            {
                ["rows_matched"] = matches.Count,
                ["occurrences"] = occurrences,
                ["applied_rows"] = actions.Count,
                ["skipped_by_tag_guard"] = skippedTagGuard,
                ["applied"] = apply && replacing,
                ["preview"] = preview,
                ["note"] = replacing && !apply
                    ? "Preview only — call again with apply=true to write the changes (one undo step)."
                    : null,
            };
        }

        private static JToken GetSettings()
        {
            var cfg = AppConfig.Instance;
            var arr = new JArray();
            foreach (var e in McpSettings.All)
            {
                arr.Add(new JObject
                {
                    ["name"] = e.Name,
                    ["value"] = e.Get(cfg),
                    ["type"] = e.Type,
                    ["allowed"] = e.Allowed,
                    ["settable"] = e.Set != null,
                    ["ui_path"] = e.UiPath,
                    ["description"] = e.Description,
                });
            }
            return new JObject { ["settings"] = arr };
        }

        private JToken SetSettings(MainWindowViewModel vm, JObject settings)
        {
            var cfg = AppConfig.Instance;
            var applied = new JArray();
            var rejected = new JArray();
            var live = McpSettings.LiveApply.None;

            foreach (var prop in settings.Properties())
            {
                var entry = McpSettings.Find(prop.Name);
                void Reject(string reason) => rejected.Add(new JObject { ["name"] = prop.Name, ["reason"] = reason });

                if (entry == null) { Reject("unknown setting — call get_settings for the full list"); continue; }
                if (entry.Set == null) { Reject($"read-only via MCP. {entry.Description}"); continue; }

                string? error = entry.Set(cfg, prop.Value);
                if (error != null) { Reject(error); continue; }
                applied.Add(prop.Name);
                live |= entry.Apply;
            }

            if (applied.Count > 0)
            {
                cfg.Save();
                if (live.HasFlag(McpSettings.LiveApply.RestartAutoSave)) vm.RestartAutoSave();
                if (live.HasFlag(McpSettings.LiveApply.Theme)) ThemeManager.Apply(cfg.IsDarkTheme);
                if (live.HasFlag(McpSettings.LiveApply.UiLanguage))
                {
                    Localization.Loc.Instance.Load(cfg.Language);
                    ThemeManager.ApplyFonts();
                }
                if (live.HasFlag(McpSettings.LiveApply.RefreshGrid)) vm.ApplyFilter(); if (live.HasFlag(McpSettings.LiveApply.Revalidate)) vm.RevalidateAllOpenFiles();
                vm.AddLog(string.Format(Loc.T("log.mcp.settings"), string.Join(", ", applied.Select(a => a.ToString()))), LogLevel.Info);
            }
            return new JObject { ["applied"] = applied, ["rejected"] = rejected };
        }

        private static JToken GetLogs(MainWindowViewModel vm, string level, int tail)
        {
            tail = Math.Clamp(tail, 1, 500);
            IEnumerable<LogMessage> logs = vm.Logs;
            logs = level switch
            {
                "all" => logs,
                "info" => logs.Where(l => l.Level == LogLevel.Info),
                "warning" => logs.Where(l => l.Level == LogLevel.Warning),
                "error" => logs.Where(l => l.Level == LogLevel.Error),
                _ => throw new ArgumentException("level must be all | info | warning | error"),
            };
            var list = logs.ToList();
            var arr = new JArray();
            foreach (var l in list.Skip(Math.Max(0, list.Count - tail)))
                arr.Add(new JObject
                {
                    ["time"] = l.DisplayTimestamp,
                    ["level"] = l.Level.ToString(),
                    ["message"] = l.Message,
                });
            return new JObject { ["total"] = list.Count, ["returned"] = arr.Count, ["logs"] = arr };
        }

        private static JToken GetProblems(MainWindowViewModel vm, int offset, int count)
        {
            count = Math.Clamp(count, 1, 200);
            offset = Math.Max(0, offset);

            var withProblems = vm.AllRows.Where(r => r.Problems.Count > 0).ToList();
            var byKind = withProblems.SelectMany(r => r.Problems)
                .GroupBy(p => p.Kind)
                .ToDictionary(g => g.Key, g => g.Count());

            var arr = new JArray();
            foreach (var r in withProblems.Skip(offset).Take(count))
            {
                var row = RowJson(r);
                row["problems"] = new JArray(r.Problems.Select(p => new JObject
                {
                    ["kind"] = p.Kind,
                    ["severity"] = p.Severity.ToString(),
                    ["title"] = p.Title,
                }));
                arr.Add(row);
            }
            return new JObject
            {
                ["rows_with_problems"] = withProblems.Count,
                ["counts_by_kind"] = JObject.FromObject(byKind),
                ["offset"] = offset,
                ["returned"] = arr.Count,
                ["rows"] = arr,
                ["note"] = "Fix a row by sending a corrected target via set_translations, then run_validation to confirm.",
            };
        }

        private static JToken SetReview(MainWindowViewModel vm, JArray items)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");
            var byKey = new Dictionary<(string, string), TranslationRow>();
            foreach (var r in vm.AllRows) byKey.TryAdd((r.Namespace ?? "", r.Key ?? ""), r);

            int applied = 0;
            var rejected = new JArray();
            foreach (var t in items)
            {
                string ns = t.Value<string>("namespace") ?? "";
                string key = t.Value<string>("key") ?? "";
                string statusText = (t.Value<string>("status") ?? "").ToLowerInvariant();
                void Reject(string reason) => rejected.Add(new JObject
                    { ["namespace"] = ns, ["key"] = key, ["reason"] = reason });

                if (key.Length == 0) { Reject("missing key"); continue; }
                if (!byKey.TryGetValue((ns, key), out var row)) { Reject("key not found in the open file"); continue; }
                ReviewStatus? status = statusText switch
                {
                    "approved" => ReviewStatus.Approved,
                    "rejected" => ReviewStatus.Rejected,
                    "none" => ReviewStatus.None,
                    _ => null,
                };
                if (status == null) { Reject("status must be approved | rejected | none"); continue; }

                // Same entry point the row context menu uses — persists to the
                // review sidecar and refreshes the status-column visibility.
                vm.ApplyReview(new[] { row }, status.Value);
                applied++;
            }
            if (applied > 0)
                vm.AddLog(string.Format(Loc.T("log.mcp.review"), applied), LogLevel.Info);
            return new JObject { ["applied"] = applied, ["rejected"] = rejected };
        }

        private static JToken SetComments(MainWindowViewModel vm, JArray items)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");
            var byKey = new Dictionary<(string, string), TranslationRow>();
            foreach (var r in vm.AllRows) byKey.TryAdd((r.Namespace ?? "", r.Key ?? ""), r);

            int applied = 0, removed = 0;
            var rejected = new JArray();
            foreach (var t in items)
            {
                string ns = t.Value<string>("namespace") ?? "";
                string key = t.Value<string>("key") ?? "";
                string? text = t.Value<string>("text");
                void Reject(string reason) => rejected.Add(new JObject
                    { ["namespace"] = ns, ["key"] = key, ["reason"] = reason });

                if (key.Length == 0) { Reject("missing key"); continue; }
                if (!byKey.TryGetValue((ns, key), out var row)) { Reject("key not found in the open file"); continue; }

                // Mirrors CommentButton: row property (drives the grid icon) + the
                // per-file sidecar service. Empty/null text removes the comment.
                if (string.IsNullOrWhiteSpace(text))
                {
                    row.Comment = null;
                    CommentService.Instance.Remove(row.Namespace, row.Key ?? "");
                    removed++;
                }
                else
                {
                    row.Comment = text;
                    CommentService.Instance.Set(row.Namespace, row.Key ?? "", text);
                    applied++;
                }
            }
            if (applied + removed > 0)
                vm.AddLog(string.Format(Loc.T("log.mcp.comments"), applied, removed), LogLevel.Info);
            return new JObject
            {
                ["set"] = applied,
                ["removed"] = removed,
                ["rejected"] = rejected,
                ["note"] = "Comments are reviewer metadata (never written into exported files). " +
                           "With 'Send with comments' on, they ride to the AI as per-row instructions.",
            };
        }

        // ── QC (Quality Control) ─────────────────────────────────────────

        private static JToken GetQcProposals(MainWindowViewModel vm, int offset, int count)
        {
            count = Math.Clamp(count, 1, 200);
            offset = Math.Max(0, offset);
            var pending = Services.Qc.QcService.Instance.Pending;
            var rowsByKey = Services.Qc.QcService.IndexRows(vm.AllRows);

            var arr = new JArray();
            foreach (var p in pending.OrderByDescending(p => p.Severity).Skip(offset).Take(count))
            {
                rowsByKey.TryGetValue((p.Namespace, p.Key), out var row);
                arr.Add(new JObject
                {
                    ["namespace"] = p.Namespace,
                    ["key"] = p.Key,
                    ["source"] = row?.Source ?? "",
                    // "current" is the LIVE target; "reviewed" is what QC looked at.
                    // They differ exactly when the row was edited after the run, and
                    // a model must write its own fix against the live text.
                    ["current"] = row?.Target ?? p.Before,
                    ["reviewed"] = p.Before,
                    ["suggested"] = p.After,
                    ["has_fix"] = !string.Equals(p.Before, p.After, StringComparison.Ordinal),
                    ["reason"] = p.Reason,
                    ["kinds"] = new JArray(p.Kinds),
                    ["severity"] = p.Severity.ToString(),
                    ["stale"] = row is null || p.IsStaleFor(row),
                });
            }
            return new JObject
            {
                ["total"] = pending.Count,
                ["offset"] = offset,
                ["returned"] = arr.Count,
                ["proposals"] = arr,
                ["note"] = "has_fix=false means the reviewer flagged the row without a replacement — " +
                           "write a corrected target yourself via set_translations.",
            };
        }

        private static JToken ApplyQcFixes(MainWindowViewModel vm, JArray? items)
        {
            var rowsByKey = Services.Qc.QcService.IndexRows(vm.AllRows);
            var pending = Services.Qc.QcService.Instance.Pending;

            IEnumerable<QcProposal> chosen = pending;
            if (items != null)
            {
                var wanted = items.Select(t => (t.Value<string>("namespace") ?? "", t.Value<string>("key") ?? "")).ToHashSet();
                chosen = pending.Where(p => wanted.Contains((p.Namespace, p.Key)));
            }

            var batch = new List<(TranslationRow, QcProposal)>();
            int noFix = 0;
            foreach (var p in chosen)
            {
                if (string.Equals(p.Before, p.After, StringComparison.Ordinal)) { noFix++; continue; }
                if (rowsByKey.TryGetValue((p.Namespace, p.Key), out var row)) batch.Add((row, p));
            }
            int applied = vm.ApplyQcProposals(batch);
            return new JObject
            {
                ["applied"] = applied,
                ["skipped_without_fix"] = noFix,
                ["note"] = "One undo step in the app. Run run_validation afterwards to confirm tags/placeholders are intact.",
            };
        }

        /// <summary>
        /// The rows the AI review would send, under the QC panel's own skip
        /// switches (qc_skip_* settings). With count = 0 it returns just the
        /// counts (cheap planning); with a count it returns the rows, so a
        /// subscription-backed chat model can BE the reviewer at no API cost.
        /// </summary>
        internal static JToken GetQcRows(MainWindowViewModel vm, bool selectedOnly, int offset, int count)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");

            // A single-language file (every .locres) holds the translation in
            // Source with nothing to compare it against — the original must come
            // from a second file, which only the QC panel can load. Counting every
            // row as "empty" would tell the model the file needs no review.
            if (!vm.AllRows.Any(r => !string.IsNullOrWhiteSpace(r.Target)))
                throw new InvalidOperationException(
                    "This file holds one language (the translation is in Source, Target is empty), so there is no " +
                    "original to compare with. Review it in the QC panel with the original file added, or read the " +
                    "rows with get_rows and judge the Source text yourself.");

            var scopeRows = selectedOnly ? vm.SelectedRows : vm.AllRows;
            if (selectedOnly && scopeRows.Count == 0)
                throw new InvalidOperationException("No rows are checked — check rows first, or omit selected_only.");
            var scope = Services.Qc.QcAiScope.Build(scopeRows,
                Services.Qc.QcAiScope.Options.FromConfig(AppConfig.Instance));

            var result = new JObject
            {
                ["in_scope"] = scopeRows.Count,
                ["to_review"] = scope.Inputs.Count,
                ["skipped"] = new JObject
                {
                    ["empty"] = scope.SkippedEmpty,
                    ["auto_carried"] = scope.SkippedAutoCarried,
                    ["identical_to_original"] = scope.SkippedIdentical,
                    ["approved"] = scope.SkippedApproved,
                    ["not_flagged_by_validator"] = scope.SkippedUnflagged,
                },
                ["note"] = "Skips follow the qc_skip_* / qc_only_validator_flagged settings (QC panel → Rows to review). " +
                           "Pass a 'count' to receive the rows; review them yourself and send verdicts with submit_qc_verdicts.",
            };

            // Row payload only when the caller asked for rows (count > 0).
            if (count > 0)
            {
                count = Math.Clamp(count, 1, 200);
                offset = Math.Max(0, offset);
                var arr = new JArray();
                foreach (var input in scope.Inputs.Skip(offset).Take(count))
                    arr.Add(RowJson(input.Row));
                result["offset"] = offset;
                result["returned"] = arr.Count;
                result["rows"] = arr;
            }
            return result;
        }
        /// <summary>
        /// Accept QC verdicts produced by the CALLING model (the MCP equivalent of
        /// the built-in AI layer). Every "fix" goes through the SAME local
        /// validator the API path uses, so a chat model cannot slip in a
        /// translation that breaks placeholders, tags, line breaks or an ellipsis.
        /// Accepted fixes become normal proposals — the user still decides.
        /// </summary>
        private static JToken SubmitQcVerdicts(MainWindowViewModel vm, JArray items)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");
            var byKey = new Dictionary<(string, string), TranslationRow>();
            foreach (var r in vm.AllRows) byKey.TryAdd((r.Namespace ?? "", r.Key ?? ""), r);

            var accepted = new List<QcProposal>();
            var rejected = new JArray();
            int ok = 0;

            foreach (var t in items)
            {
                string ns = t.Value<string>("namespace") ?? "";
                string key = t.Value<string>("key") ?? "";
                string verdict = (t.Value<string>("verdict") ?? "").Trim().ToLowerInvariant();
                string? fix = t.Value<string>("fix");
                string reason = t.Value<string>("reason") ?? "AI QC (MCP)";
                void Reject(string why) => rejected.Add(new JObject
                    { ["namespace"] = ns, ["key"] = key, ["reason"] = why });

                if (key.Length == 0) { Reject("missing key"); continue; }
                if (!byKey.TryGetValue((ns, key), out var row)) { Reject("key not found in the open file"); continue; }
                if (verdict == "ok") { ok++; continue; }
                if (verdict != "fix") { Reject("verdict must be 'ok' or 'fix'"); continue; }
                if (string.IsNullOrWhiteSpace(fix)) { Reject("verdict 'fix' requires a non-empty 'fix'"); continue; }

                string? why = Services.Qc.QcAiRunner.RejectFix(row.Source ?? "", row.Target ?? "", fix!);
                if (why != null) { Reject($"unsafe fix: {why}"); continue; }

                accepted.Add(new QcProposal
                {
                    Namespace = row.Namespace ?? "",
                    Key = row.Key ?? "",
                    Before = row.Target ?? "",
                    After = fix!,
                    Reason = reason,
                    Kinds = new List<string> { "AiQc" },
                    Severity = QcSeverity.High,
                    Source = "mcp",
                });
            }

            if (accepted.Count > 0)
            {
                Services.Qc.QcService.Instance.MergeProposals(accepted);
                vm.RefreshQcState();
                vm.AddLog(string.Format(Loc.T("log.mcp.qcSubmitted"), accepted.Count, ok, rejected.Count), LogLevel.Info);
            }
            return new JObject
            {
                ["accepted"] = accepted.Count,
                ["confirmed_ok"] = ok,
                ["rejected"] = rejected,
                ["note"] = "Accepted suggestions are NOT applied yet — the user reviews them (Fix / Keep), " +
                           "or call apply_qc_fixes.",
            };
        }

        /// <summary>Dismiss suggestions (the MCP equivalent of pressing Keep).</summary>
        private static JToken KeepQcProposals(MainWindowViewModel vm, JArray? items)
        {
            var pending = Services.Qc.QcService.Instance.Pending;
            var rowsByKey = Services.Qc.QcService.IndexRows(vm.AllRows);

            // OMITTED items = every pending proposal; an EMPTY array = none. The
            // two must not be conflated: "[]" reaching the dismiss-everything path
            // would silently wipe a whole review.
            IEnumerable<QcProposal> chosen = pending;
            if (items != null)
            {
                var wanted = items.Select(t => (t.Value<string>("namespace") ?? "", t.Value<string>("key") ?? "")).ToHashSet();
                chosen = pending.Where(p => wanted.Contains((p.Namespace, p.Key)));
            }

            int kept = 0;
            foreach (var p in chosen.ToList())
            {
                rowsByKey.TryGetValue((p.Namespace, p.Key), out var row);
                if (row != null) vm.KeepQcProposal(row, p);
                else Services.Qc.QcService.Instance.SetVerdict(p, QcVerdict.Kept);
                kept++;
            }
            if (kept > 0) vm.AddLog(string.Format(Loc.T("log.mcp.qcKept"), kept), LogLevel.Info);
            return new JObject { ["kept"] = kept };
        }

        private static JToken RunValidation(MainWindowViewModel vm)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");
            foreach (var r in vm.AllRows)
                ValidationService.Instance.RunForRow(r);
            ValidationService.Instance.RunHeavy(vm.AllRows);

            int rowsWithProblems = vm.AllRows.Count(r => r.Problems.Count > 0);
            var byKind = vm.AllRows.SelectMany(r => r.Problems)
                .GroupBy(p => p.Kind)
                .ToDictionary(g => g.Key, g => g.Count());
            return new JObject
            {
                ["rows_validated"] = vm.RowCount,
                ["rows_with_problems"] = rowsWithProblems,
                ["counts_by_kind"] = JObject.FromObject(byKind),
            };
        }

        private static JToken SetTranslations(MainWindowViewModel vm, JArray items)
        {
            if (vm.RowCount == 0) throw new InvalidOperationException("No file is open.");

            // Exact (namespace, key) identity — the same shape get_rows returns.
            var byKey = new Dictionary<(string, string), TranslationRow>();
            foreach (var r in vm.AllRows) byKey.TryAdd((r.Namespace ?? "", r.Key ?? ""), r);

            var actions = new List<CellEditAction>();
            var rejected = new JArray();
            vm.SuppressUndoCapture = true;
            try
            {
                foreach (var t in items)
                {
                    string ns = t.Value<string>("namespace") ?? "";
                    string key = t.Value<string>("key") ?? "";
                    string target = t.Value<string>("target") ?? "";
                    void Reject(string reason) => rejected.Add(new JObject
                        { ["namespace"] = ns, ["key"] = key, ["reason"] = reason });

                    if (key.Length == 0) { Reject("missing key"); continue; }
                    if (target.Length == 0) { Reject("empty target (clearing via MCP is not allowed)"); continue; }
                    if (!byKey.TryGetValue((ns, key), out var row)) { Reject("key not found in the open file"); continue; }

                    // Same integrity contract as the AI pipeline: tags must survive.
                    if (CurlyToken.Matches(row.Source ?? "").Count != CurlyToken.Matches(target).Count)
                        { Reject("curly {token} count mismatch vs source"); continue; }
                    if (AngleTag.Matches(row.Source ?? "").Count != AngleTag.Matches(target).Count)
                        { Reject("angle <tag> count mismatch vs source"); continue; }

                    if (row.Target != target)
                    {
                        actions.Add(new CellEditAction(row, row.Target ?? "", target));
                        row.Target = target;
                        vm.MarkTargetSnapshot(row, target);
                    }
                }
            }
            finally { vm.SuppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                vm.UndoManager.AddAction(new BatchEditAction(actions));
                vm.MarkDirty();
                vm.ApplyFilter();
                vm.AddLog(string.Format(Loc.T("log.mcp.applied"), actions.Count), LogLevel.Info);
            }
            return new JObject
            {
                ["applied"] = actions.Count,
                ["rejected"] = rejected,
                ["note"] = "Applied changes are one Ctrl+Z step. Rows are NOT saved to disk until save_current.",
            };
        }
    }
}
