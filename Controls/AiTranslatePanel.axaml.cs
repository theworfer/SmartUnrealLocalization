using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;
using SmartUnrealLocalization.ViewModels;
using Newtonsoft.Json.Linq;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Embeddable side-panel form for batch AI translation. Lives inside MainWindow's
    /// docked side-panel host (not a floating dialog). Initialised after construction
    /// via <see cref="Initialize"/>; raises <see cref="ClosePanelRequested"/> when the
    /// user dismisses the panel.
    /// </summary>
    public partial class AiTranslatePanel : UserControl
    {
        private MainWindowViewModel _vm = null!;
        private CancellationTokenSource? _cts;
        private TranslationMemoryService _tm = new();

        private readonly ConcurrentDictionary<string, string> _sessionTm = new();
        private ProgressState? _progressState;

        // Captured once at OnStart (read-only for the rest of the run, so it's safe to read
        // from parallel batch tasks). When true, rows with a reviewer comment send that note
        // inline AND bypass TM (read / write / same-source propagation / session cache).
        private bool _sendComments;

        // ── Extra context (per-run; deliberately NOT persisted) ─────────────
        // _altFilePath: additional-language file picked via Browse (this panel session only).
        // _altMap / _sendKeyContext: captured at OnStart like _sendComments — read-only for
        // the run, safe from parallel batch tasks.
        private string? _altFilePath;
        private Dictionary<(string Ns, string Key), string>? _altMap;
        private bool _sendKeyContext;
        private TranslationBatchRunner.RefQuality _refQuality = TranslationBatchRunner.RefQuality.Unknown;

        // Per-run memoized reuse discriminators (reference text + reuse tag). Tags
        // never change during a run; the propagation loop touches every visible row
        // per outcome, so caching keeps that O(N) instead of O(N·regex). Concurrent —
        // parallel batch tasks and ApplyRowOutcomes may compute tags simultaneously
        // (same discipline as _sessionTm). Cleared at OnStart (fresh options) AND at
        // run end (releases row references from since-closed tabs).
        private readonly ConcurrentDictionary<TranslationRow, string?> _refTextCache = new();
        private readonly ConcurrentDictionary<TranslationRow, string?> _reuseTagCache = new();

        // GUID/hash-like keys (e.g. "EE3865524AF1E541…") carry no meaning for the AI —
        // sending them is token noise, so they're skipped even when Key-context is on.
        private static readonly Regex HashLikeKey = new(@"^[0-9A-Fa-f\-]{16,}$", RegexOptions.Compiled);

        public event Action? ClosePanelRequested;

        private static readonly string[] Languages = {
            "English","Ukrainian","Arabic","Chinese","Czech","Danish","Dutch","Finnish","French",
            "German","Greek","Hebrew","Hungarian","Indonesian","Italian","Japanese","Korean",
            "Norwegian","Polish","Portuguese","Romanian","Russian","Spanish","Swedish","Thai","Turkish","Vietnamese"
        };

        public AiTranslatePanel()
        {
            InitializeComponent();
            this.RefreshOnLanguageChange(RelocalizeChrome);
        }

        /// <summary>
        /// Re-apply the text this panel writes in code rather than binds: the
        /// "pick a language" stub at the head of both language lists, the
        /// reference-quality choices, and the "no file" placeholder. Everything
        /// else here binds through <c>Loc.Instance</c> and refreshes itself.
        ///
        /// <para>Selections are preserved by index — replacing an ItemsSource
        /// clears the selection, and silently resetting the user's language pair
        /// because they changed the interface language would be worse than the
        /// stale text this fixes.</para>
        /// </summary>
        private void RelocalizeChrome()
        {
            if (!_uiInitialized) return;

            RelocalizeLanguageList(CbFrom);
            RelocalizeLanguageList(CbTo);

            int quality = CbRefQuality.SelectedIndex;
            CbRefQuality.ItemsSource = RefQualityChoices();
            CbRefQuality.SelectedIndex = quality < 0 ? 3 : quality;

            if (string.IsNullOrEmpty(_altFilePath)) LblAltFile.Text = Loc.T("ai.ctx.noFile");
        }

        /// <summary>The language list, with the localized stub at index 0.</summary>
        private static List<string> LanguageChoices()
        {
            var list = new List<string> { Loc.T("ai.selectLanguage") };
            list.AddRange(Languages);
            return list;
        }

        /// <summary>Order mirrors <c>TranslationBatchRunner.RefQuality</c>.</summary>
        private static string[] RefQualityChoices() => new[]
        {
            Loc.T("ai.ctx.q.official"),
            Loc.T("ai.ctx.q.community"),
            Loc.T("ai.ctx.q.machine"),
            Loc.T("ai.ctx.q.unknown"),
        };

        private static void RelocalizeLanguageList(ComboBox box)
        {
            int selected = box.SelectedIndex;
            box.ItemsSource = LanguageChoices();
            box.SelectedIndex = Math.Max(0, selected);
        }

        /// <summary>
        /// Wire the panel to its VM and (optional) pre-selected rows. Must be called
        /// before the panel is shown — the parameterless ctor is XAML-only.
        /// </summary>
        private bool _uiInitialized;

        public void Initialize(MainWindowViewModel vm)
        {
            if (!ReferenceEquals(_vm, vm))
            {
                if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
                vm.PropertyChanged += OnVmPropertyChanged;
            }
            _vm = vm;
            // Keep the in-flight run's TM instance: replacing it mid-run would send
            // the run's remaining adds (and its final Save) to a FRESH service still
            // pointed at the generic translation_memory.json instead of the
            // language-pair file, and orphan everything added so far.
            if (_vm.ActiveAiTranslation is null)
                _tm = new TranslationMemoryService();

            // One-time UI setup — everything the user can tweak (languages, batch
            // size, TM checkboxes, extra context incl. the reference file, prompt)
            // is SESSION memory: it survives closing/reopening the panel and resets
            // only when the app restarts. Nothing here is written to config except
            // what already was (language pair on Start, SendComments on Start).
            if (!_uiInitialized)
            {
                _uiInitialized = true;

                var langList = LanguageChoices();
                // Restore the last-used pair (IndexOf("") == -1 → falls back to the stub).
                CbFrom.ItemsSource = langList;
                CbFrom.SelectedIndex = Math.Max(0, langList.IndexOf(AppConfig.Instance.LastAiFromLanguage));
                CbTo.ItemsSource = langList;
                CbTo.SelectedIndex = Math.Max(0, langList.IndexOf(AppConfig.Instance.LastAiToLanguage));
                CbBatch.ItemsSource = new[] { "10", "20", "30", "50", "100" };
                CbBatch.SelectedItem = AppConfig.Instance.TranslationBatchSize.ToString();

                ChkUseTm.IsChecked = AppConfig.Instance.TranslationMemoryEnabled;
                ChkSaveTm.IsChecked = AppConfig.Instance.TranslationMemoryEnabled;
                ChkSessionTm.IsChecked = false;
                ChkSendComments.IsChecked = AppConfig.Instance.SendCommentsToAi;

                // Extra context defaults (off) — session memory from here on.
                ChkKeyContext.IsChecked = false;
                ChkAltSource.IsChecked = false;
                AltSourceOptions.IsVisible = false;
                AltColumnRow.IsVisible = false;
                _altFilePath = null;
                LblAltFile.Text = Loc.T("ai.ctx.noFile");
                CbAltColumn.ItemsSource = new[] { "Source", "Target" };
                CbAltColumn.SelectedIndex = 0;
                CbRefQuality.ItemsSource = RefQualityChoices();
                CbRefQuality.SelectedIndex = 3; // Unknown
            }

            // Per-open state: opening the panel with rows checked pre-picks the
            // "Selected" scope as a convenience. It is never un-picked here — the
            // scope is resolved live at Start, and an empty selection refuses the
            // run instead of quietly falling through to the whole file.
            if (_vm.HasSelection) RbSelected.IsChecked = true;
            RefreshSelectedScopeLabel();

            // If a translation kicked off earlier (e.g. closing-and-reopening the panel
            // mid-run) is still in flight, surface Stop so the user can halt it.
            if (_vm.ActiveAiTranslation != null)
            {
                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled = true;
                LblProgress.Text = Loc.T("ai.err.alreadyRunning");
            }
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedCount))
                Dispatcher.UIThread.Post(RefreshSelectedScopeLabel);
        }

        /// <summary>
        /// Keep the "Selected rows" option showing how many rows that actually is,
        /// live while the panel is open. The scope of a paid run must be readable
        /// BEFORE Start, not inferred from the bill afterwards.
        /// </summary>
        private void RefreshSelectedScopeLabel()
        {
            int n = _vm?.SelectedCount ?? 0;
            LblSelectedCount.Text = n > 0 ? $"({n})" : "";
        }

        private void OnClosePanel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();

        // ───────────── EXTRA CONTEXT (additional language + key) ─────────────

        private void OnAltSourceToggled(object? s, RoutedEventArgs e)
            => AltSourceOptions.IsVisible = ChkAltSource.IsChecked == true;

        private async void OnAltBrowse(object? s, RoutedEventArgs e)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top == null) return;
                var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Loc.T("ai.ctx.pickTitle"),
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Localization (*.locres, *.csv, *.po)")
                            { Patterns = new[] { "*.locres", "*.csv", "*.po" } },
                    },
                });
                var path = result.Count > 0 ? result[0]?.Path?.LocalPath : null;
                if (string.IsNullOrEmpty(path)) return;

                _altFilePath = path;
                LblAltFile.Text = Path.GetFileName(path);
                // The Source/Target toggle only makes sense for csv/po — a .locres has a
                // single value column (exposed as Source when opened).
                var ext = Path.GetExtension(path).ToLowerInvariant();
                AltColumnRow.IsVisible = ext is ".csv" or ".po";
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnAltBrowse)); }
        }

        /// <summary>
        /// Load + validate the additional-language file and build the (ns, key) → text
        /// lookup. Every failure path surfaces a clear message and returns null so
        /// OnStart aborts BEFORE any run state changes. When some rows have no key
        /// match, the user chooses: continue (matched rows get two originals, the
        /// rest translate from the main source alone) or cancel.
        /// </summary>
        private async Task<Dictionary<(string Ns, string Key), string>?> BuildAltMapAsync(List<TranslationRow> rows)
        {
            if (string.IsNullOrEmpty(_altFilePath))
            {
                LblProgress.Text = Loc.T("ai.ctx.err.noFile");
                return null;
            }
            if (!File.Exists(_altFilePath))
            {
                LblProgress.Text = Loc.T("ai.ctx.err.missing");
                return null;
            }
            if (string.Equals(_altFilePath, _vm.OriginalFilePath, StringComparison.OrdinalIgnoreCase))
            {
                LblProgress.Text = Loc.T("ai.ctx.err.sameFile");
                return null;
            }

            bool takeTarget = AltColumnRow.IsVisible && CbAltColumn.SelectedIndex == 1;
            List<TranslationRow> altRows;
            try
            {
                var ext = Path.GetExtension(_altFilePath).ToLowerInvariant();
                var file = _altFilePath!;
                altRows = ext switch
                {
                    ".locres" => await Task.Run(() => LocresFileService.Open(file).Rows),
                    ".po" => await Task.Run(() => PoFileService.Import(file)),
                    ".csv" => await Task.Run(() => CsvFileService.Import(file)),
                    _ => throw new NotSupportedException($"Unsupported format: {ext}"),
                };
            }
            catch (Exception ex)
            {
                LblProgress.Text = string.Format(Loc.T("ai.ctx.err.load"), ex.Message);
                _vm.AddLog(string.Format(Loc.T("log.ai.altLoadFailed"), ex.Message), LogLevel.Error);
                return null;
            }

            var map = new Dictionary<(string Ns, string Key), string>();
            foreach (var r in altRows)
            {
                string text = takeTarget ? (r.Target ?? "") : (r.Source ?? "");
                if (string.IsNullOrEmpty(text)) continue;
                map.TryAdd((r.Namespace ?? "", r.Key ?? ""), text); // duplicate keys: first wins
            }

            int matched = rows.Count(r => LookupAlt(map, r) != null);
            if (matched < rows.Count)
            {
                bool proceed = await ShowAltMismatchDialog(altRows.Count, rows.Count, matched);
                if (!proceed)
                {
                    LblProgress.Text = Loc.T("ai.ctx.cancelled");
                    return null;
                }
            }
            _vm.AddLog(string.Format(Loc.T("log.ai.altLoaded"), Path.GetFileName(_altFilePath), matched, rows.Count), LogLevel.Info);
            return map;
        }

        /// <summary>
        /// Row → additional-language text. Exact (Namespace, Key) first, then the
        /// legacy flattened "ns/key" shapes in either direction (pre-LocresIO CSV
        /// exports carry the namespace inside the Key column).
        /// </summary>
        private static string? LookupAlt(Dictionary<(string Ns, string Key), string> map, TranslationRow r)
        {
            if (map.TryGetValue((r.Namespace ?? "", r.Key ?? ""), out var t)) return t;
            if (!string.IsNullOrEmpty(r.Namespace)
                && map.TryGetValue(("", r.Namespace + "/" + r.Key), out t)) return t;
            if (string.IsNullOrEmpty(r.Namespace) && r.Key is { } k)
            {
                int slash = k.IndexOf('/');
                if (slash >= 0 && map.TryGetValue((k[..slash], k[(slash + 1)..]), out t)) return t;
            }
            return null;
        }

        /// <summary>Key as AI context: "ns/key", or null for GUID-like keys (noise).</summary>
        private static string? ContextKeyFor(TranslationRow r)
        {
            var k = r.Key;
            if (string.IsNullOrEmpty(k) || HashLikeKey.IsMatch(k)) return null;
            return string.IsNullOrEmpty(r.Namespace) ? k : r.Namespace + "/" + k;
        }

        /// <summary>A comment is a per-row instruction that must always reach the AI —
        /// such a row never takes or feeds ANY reuse shortcut.</summary>
        private bool IsCommentBound(TranslationRow r) => _sendComments && r.HasComment;

        /// <summary>The additional-language text for a row (null when ref off / no match). Memoized.</summary>
        private string? RefTextFor(TranslationRow r)
        {
            if (_refTextCache.TryGetValue(r, out var cached)) return cached;
            var t = _altMap != null ? LookupAlt(_altMap, r) : null;
            _refTextCache[r] = t;
            return t;
        }

        /// <summary>
        /// The reuse discriminator: a meaningful sent Key wins, else the reference
        /// text, else null (plain source-only reuse). Two rows may share ONE
        /// translation only if their tags match — this is what stops a reference
        /// that disambiguates two identical sources from being blind-copied.
        /// Memoized (touched per-row inside the propagation loop).
        /// </summary>
        private string? ReuseTag(TranslationRow r)
        {
            if (_reuseTagCache.TryGetValue(r, out var cached)) return cached;
            var tag = TmReuse.Tag(_sendKeyContext ? ContextKeyFor(r) : null, RefTextFor(r));
            _reuseTagCache[r] = tag;
            return tag;
        }

        /// <summary>Session-cache / persistent-keyed identity: source scoped by reuse tag.</summary>
        private string SessionKey(TranslationRow r) => (ReuseTag(r) ?? "") + (char)0x1F + r.Source;

        /// <summary>
        /// True when a row must NOT use the GENERIC (source-only) persistent TM or
        /// its blind same-source propagation: comment rows, and any row carrying a
        /// reuse tag (meaningful key OR reference) — those reuse per-tag instead.
        /// </summary>
        private bool BypassesSourceTm(TranslationRow r) => IsCommentBound(r) || ReuseTag(r) != null;

        /// <summary>Modal "key match check" summary: counts + continue/cancel choice.</summary>
        private async Task<bool> ShowAltMismatchDialog(int altEntries, int totalRows, int matched)
        {
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow == null) return false;

            string message = string.Format(Loc.T("ai.ctx.dlg.message"), altEntries, matched, totalRows);
            bool proceed = false;

            var btnCancel = new Button { Content = Loc.T("ai.ctx.dlg.cancel"), Classes = { "subtle" } };
            var btnContinue = new Button { Content = Loc.T("ai.ctx.dlg.continue"), Classes = { "primary" } };
            var dlg = new Window
            {
                Title = Loc.T("ai.ctx.dlg.title"),
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Transparent,
                WindowDecorations = Avalonia.Controls.WindowDecorations.None,
                Content = new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#555")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Background = Avalonia.Application.Current?.FindResource("WindowBackground") as Avalonia.Media.IBrush,
                    Child = new DockPanel
                    {
                        Children =
                        {
                            new DialogTitleBar { Title = Loc.T("ai.ctx.dlg.title"), [DockPanel.DockProperty] = Dock.Top },
                            new StackPanel
                            {
                                Margin = new Avalonia.Thickness(16),
                                Spacing = 12,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = message,
                                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                        Foreground = Avalonia.Application.Current?.FindResource("TextPrimary") as Avalonia.Media.IBrush,
                                    },
                                    new StackPanel
                                    {
                                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                                        Spacing = 8,
                                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                        Children = { btnCancel, btnContinue },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            btnCancel.Click += (_, _) => dlg.Close();
            btnContinue.Click += (_, _) => { proceed = true; dlg.Close(); };
            await dlg.ShowDialog(parentWindow);
            return proceed;
        }

        private async void OnStart(object? s, RoutedEventArgs e)
        {
            try { await StartCoreAsync(); }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnStart)); }
        }

        /// <summary>
        /// Start an AI run from the MCP bridge: same pipeline as the Start button
        /// (reuse tags, tag protection, retry, circuit breaker) — MCP only presets
        /// the language/scope/TM controls and presses the button.
        /// TM POLICY: <paramref name="useTm"/> controls READING only; writing to
        /// the persistent TM is ALWAYS disabled for MCP-initiated runs — an
        /// externally-driven run must never contaminate the long-lived memory
        /// (the user promotes reviewed rows manually via "Send to Translation
        /// Memory"). Returns "started" or a human-readable refusal.
        /// </summary>
        internal string McpTryStartRun(string from, string to, string scope, bool useTm)
        {
            if (_vm.ActiveAiTranslation != null)
                return "An AI translation is already running — stop it first (stop_ai_translation).";

            var langList = CbFrom.ItemsSource as IList<string> ?? new List<string>();
            int fromIdx = langList.IndexOf(langList.FirstOrDefault(
                l => string.Equals(l, from, StringComparison.OrdinalIgnoreCase)) ?? "");
            int toIdx = langList.IndexOf(langList.FirstOrDefault(
                l => string.Equals(l, to, StringComparison.OrdinalIgnoreCase)) ?? "");
            if (fromIdx <= 0) return $"Unknown source language '{from}'. Supported: {string.Join(", ", Languages)}.";
            if (toIdx <= 0) return $"Unknown target language '{to}'. Supported: {string.Join(", ", Languages)}.";
            if (fromIdx == toIdx) return "Source and target languages must differ.";

            CbFrom.SelectedIndex = fromIdx;
            CbTo.SelectedIndex = toIdx;
            switch (scope)
            {
                case "untranslated": RbUntranslated.IsChecked = true; break;
                case "all": RbAll.IsChecked = true; break;
                default: return "scope must be 'untranslated' or 'all'.";
            }

            ChkUseTm.IsChecked = useTm;
            ChkSaveTm.IsChecked = false; // hard rule — see TM POLICY above
            ChkSessionTm.IsChecked = false;

            _ = StartCoreAsync(); // fire-and-forget; progress is visible in the app + get_status
            return "started";
        }

        private async Task StartCoreAsync()
        {
            var cfg = AppConfig.Instance;
            var srcLang = CbFrom.SelectedItem?.ToString() ?? "";
            var tgtLang = CbTo.SelectedItem?.ToString() ?? "";

            // Index, not text: the stub at index 0 is a translated string, so
            // comparing against the English "Select Language" would stop
            // recognising it the moment the interface language changed — and the
            // panel would go on to ask the API to translate into "言語を選択".
            if (CbFrom.SelectedIndex <= 0 || CbTo.SelectedIndex <= 0
                || string.IsNullOrEmpty(srcLang) || string.IsNullOrEmpty(tgtLang))
            {
                _vm.AddLog(Loc.T("ai.err.langMissing"), LogLevel.Error);
                LblProgress.Text = Loc.T("ai.err.langMissing");
                return;
            }
            if (srcLang == tgtLang)
            {
                _vm.AddLog(Loc.T("ai.err.langSame"), LogLevel.Error);
                LblProgress.Text = Loc.T("ai.err.langSame");
                return;
            }

            // Persist the pair: panel restore + the "Send to Translation Memory"
            // context action key their TM file off it.
            if (cfg.LastAiFromLanguage != srcLang || cfg.LastAiToLanguage != tgtLang)
            {
                cfg.LastAiFromLanguage = srcLang;
                cfg.LastAiToLanguage = tgtLang;
                cfg.Save();
            }

            int batchSize = int.Parse(CbBatch.SelectedItem?.ToString() ?? "30");
            var userPrompt = TbPrompt.Text ?? "";

            // Resolve the scope NOW, from the live selection. It used to come from
            // a snapshot taken when the panel opened, and an empty snapshot fell
            // through to the ELSE branch — so checking rows after opening the panel
            // and pressing Start translated the WHOLE FILE. A scope that resolves
            // to nothing must refuse, never widen.
            List<TranslationRow> rowsToProcess;
            if (RbSelected.IsChecked == true)
            {
                rowsToProcess = _vm.SelectedRows;
                if (rowsToProcess.Count == 0)
                {
                    LblProgress.Text = Loc.T("ai.scope.noSelection");
                    _vm.AddLog(Loc.T("ai.scope.noSelection"), LogLevel.Warning);
                    return;
                }
            }
            else if (RbUntranslated.IsChecked == true)
                rowsToProcess = _vm.AllRows.Where(r => r.IsUntranslated).ToList();
            else
                rowsToProcess = _vm.AllRows.ToList();

            if (rowsToProcess.Count == 0)
            {
                LblProgress.Text = Loc.T("ai.scope.empty");
                return;
            }

            if (_vm.ActiveAiTranslation != null)
            {
                _vm.AddLog(Loc.T("ai.err.alreadyRunning"), LogLevel.Warning);
                return;
            }

            // Extra context — captured (like _sendComments) BEFORE any run state
            // changes, so a validation error or a cancelled key-match dialog aborts
            // cleanly with the panel still idle.
            _sendKeyContext = ChkKeyContext.IsChecked == true;
            _altMap = null;
            _refQuality = CbRefQuality.SelectedIndex switch
            {
                0 => TranslationBatchRunner.RefQuality.OfficialHuman,
                1 => TranslationBatchRunner.RefQuality.Community,
                2 => TranslationBatchRunner.RefQuality.Machine,
                _ => TranslationBatchRunner.RefQuality.Unknown,
            };
            if (ChkAltSource.IsChecked == true)
            {
                _altMap = await BuildAltMapAsync(rowsToProcess);
                if (_altMap == null) return; // message already shown
            }

            _cts = new CancellationTokenSource();
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            Progress.Maximum = rowsToProcess.Count;
            Progress.Value = 0;

            int tmHits = 0;
            int tokenSkips = 0;

            // Etap 2: HasTokenError / TokenErrorMessage are now computed views
            // over row.Problems. To reset before the AI pass we strip any
            // TagMismatch problems via ValidationService; whatever the AI
            // produces will get re-validated automatically by the debounced
            // hook in MainWindowViewModel.TouchEditMetadata once each row's
            // Target is set below.
            foreach (var r in rowsToProcess)
                SmartUnrealLocalization.Services.ValidationService.Instance.RemoveProblems(r, "TagMismatch");

            bool useTm = ChkUseTm.IsChecked == true;
            bool saveTm = ChkSaveTm.IsChecked == true;
            bool useSessionTm = ChkSessionTm.IsChecked == true;
            _sendComments = ChkSendComments.IsChecked == true;
            if (cfg.SendCommentsToAi != _sendComments) { cfg.SendCommentsToAi = _sendComments; cfg.Save(); }

            _tm.SetLanguagePair(srcLang, tgtLang);
            _sessionTm.Clear();
            _refTextCache.Clear();
            _reuseTagCache.Clear();

            var rowsForAi = new List<TranslationRow>();

            // The pre-pass (token skips + TM hits, applied before any AI call) is its own
            // undo step. Each AI batch then adds its own step (1 batch = 1 Ctrl+Z).
            var preActions = new List<CellEditAction>();

            _vm.SuppressUndoCapture = true;
            try
            {
                foreach (var r in rowsToProcess)
                {
                    // Strip BOTH <angle> tags and {curly} tokens: a string that is entirely
                    // tags (e.g. "<Place the horn and summon Velgar>") has nothing to translate
                    // and must NOT be sent — the AI would translate inside the tag and the
                    // validator would (correctly) flag the altered tag.
                    string stripped = Regex.Replace(r.Source, @"<[^>]+>|\{[^}]+\}", "");
                    if (!Regex.IsMatch(stripped, @"\p{L}"))
                    {
                        RecordChange(preActions, r, r.Target, r.Source);
                        r.Target = r.Source;
                        _vm.MarkTargetSnapshot(r, r.Source);
                        tokenSkips++;
                        continue;
                    }

                    // Tagged rows (meaningful key OR reference): exact (tag + source)
                    // TM reuse — literally the same string in the same context of the
                    // same game, so precision is intact. No propagation: the hit is
                    // tag-specific by definition. Commented rows still bypass every
                    // read (their note must reach the AI).
                    if (useTm && !IsCommentBound(r)
                        && ReuseTag(r) is { } reuseTag
                        && _tm.TryGetKeyed(reuseTag, r.Source, out var keyedHit))
                    {
                        RecordChange(preActions, r, r.Target, keyedHit);
                        r.Target = keyedHit;
                        _vm.MarkTargetSnapshot(r, keyedHit);
                        tmHits++;
                        continue;
                    }

                    // Context-bound rows (comments / sent keys) bypass the SOURCE-ONLY
                    // TM read so their context always reaches the AI instead of a
                    // cache short-circuit.
                    if (useTm && !BypassesSourceTm(r) && _tm.TryGet(r.Source, out var cached))
                    {
                        RecordChange(preActions, r, r.Target, cached);
                        r.Target = cached;
                        _vm.MarkTargetSnapshot(r, cached);
                        tmHits++;

                        // Same-source propagation skips context-bound rows — they keep
                        // their place in the AI queue so their note/key is honoured.
                        foreach (var other in _vm.Rows.Where(x => x.Source == r.Source && x != r
                                                                  && !BypassesSourceTm(x)))
                        {
                            RecordChange(preActions, other, other.Target, cached);
                            other.Target = cached;
                            _vm.MarkTargetSnapshot(other, cached);
                        }
                        continue;
                    }

                    rowsForAi.Add(r);
                }
            }
            finally { _vm.SuppressUndoCapture = false; }

            // One undo step for the whole pre-pass (skips + TM hits).
            if (preActions.Count > 0)
                _vm.UndoManager.AddAction(new BatchEditAction(preActions));

            int preSuccess = tmHits + tokenSkips;
            LblProgress.Text = string.Format(Loc.T("ai.progress.prepass"), tmHits, tokenSkips, rowsForAi.Count);
            Progress.Value = preSuccess;
            _vm.RecordTmHits(tmHits);

            var progressState = new ProgressState
            {
                Successful = preSuccess,
                Errors = 0,
                Total = rowsToProcess.Count,
                StartedUtc = DateTime.UtcNow,
                Baseline = preSuccess,
            };
            _progressState = progressState;

            _vm.ActiveAiTranslation = new MainWindowViewModel.AiTranslationHandle(
                _cts,
                () => progressState.Halted = true)
            {
                IsTabBound = true, // works on the ACTIVE tab's rows — cancelled on tab switch/close
                ProgressProbe = () => (progressState.Successful, progressState.Errors,
                                       progressState.Total, progressState.EtaSeconds()),
            };

            if (rowsForAi.Count > 0)
            {
                var batches = rowsForAi
                    .Select((r, i) => new { r, i })
                    .GroupBy(x => x.i / batchSize)
                    .Select(g => g.Select(x => x.r).ToList())
                    .ToList();

                var systemPrompt = $"You are an expert game localizer. Translate from {srcLang} to {tgtLang}.\n" +
                                   "Rules:\n" +
                                   "1. DO NOT translate, alter, or reorder anything inside <angle brackets> or {curly braces}. " +
                                   "Keep these tags EXACTLY as in the source (same characters, same order). Translate only the readable text outside them; if a string is nothing but such a tag, return it unchanged.\n" +
                                   "2. Keep all line breaks and formatting exactly as in the source.\n" +
                                   "3. Return the answer STRICTLY as a JSON array in the exact same format as the input.\n" +
                                   "   Keep all escaped \\n characters intact where they were.\n" +
                                   "   Example: [{\"id\":\"0\",\"text\":\"translated text\"}]\n";
                if (!string.IsNullOrWhiteSpace(userPrompt))
                    systemPrompt += $"\nAdditional Instructions: {userPrompt}\n";
                PromptHistory.Record(PromptMode.SingleTranslation, _vm.CurrentFileName, systemPrompt);

                if (cfg.ParallelTranslationEnabled)
                {
                    var tasks = batches.Select(batch => ProcessBatchAsync(batch, systemPrompt, cfg, progressState, saveTm, useSessionTm));

                    try { await Task.WhenAll(tasks); }
                    catch (OperationCanceledException) { /* user cancelled — normal */ }
                    catch (Exception ex)
                    {
                        _vm.AddLog(string.Format(Loc.T("log.ai.parallelError"), ex.Message), LogLevel.Error);
                    }
                }
                else
                {
                    foreach (var batch in batches)
                    {
                        if (_cts.Token.IsCancellationRequested || progressState.Halted) break;
                        await ProcessBatchAsync(batch, systemPrompt, cfg, progressState, saveTm, useSessionTm);
                    }
                }
            }

            // (Per-batch undo entries are added inside ProcessBatchAsync as each batch
            // completes — see FlushBatchUndo — so there's no single end-of-run entry.)
            _vm.MarkDirty();
            if (saveTm) { _tm.Save(); _vm.RecordTmSave(); }
            _vm.ApplyFilter();

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            _progressState = null;
            _vm.ActiveAiTranslation = null;
            // Release the memoized row references (rows of a closed tab would
            // otherwise stay alive until the NEXT run clears these).
            _refTextCache.Clear();
            _reuseTagCache.Clear();

            int successful = progressState.Successful;
            int errors = progressState.Errors;
            int untouched = progressState.Total - successful - errors;

            if (_cts?.Token.IsCancellationRequested == true || progressState.Halted)
                _vm.AddLog(string.Format(Loc.T("log.ai.stopped"), successful, progressState.Total, errors, untouched), LogLevel.Warning);
            else if (errors > 0)
                _vm.AddLog(string.Format(Loc.T("log.ai.finishedWithErrors"), errors, successful, progressState.Total), LogLevel.Error);
            else
                _vm.AddLog(string.Format(Loc.T("log.ai.completed"), successful, progressState.Total), LogLevel.Info);

            LblProgress.Text = _vm.StatusText;
        }

        private class ProgressState
        {
            public int Successful;
            public int Errors;
            public int Total;
            public int ConsecutiveApiFails;
            public bool Halted;

            // ETA: rate is measured from the point AI batches actually start
            // (Baseline = rows already done by the instant pre-pass — TM hits and
            // tag-only skips would otherwise make the estimate absurdly optimistic).
            public DateTime StartedUtc;
            public int Baseline;

            public double? EtaSeconds()
            {
                int done = Successful + Errors;
                int processed = done - Baseline;
                double elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
                if (processed < 3 || elapsed < 1) return null; // rate unknown yet
                int remaining = Total - done;
                if (remaining <= 0) return 0;
                return remaining / (processed / elapsed);
            }

            /// <summary>" · ~2m 40s left" (empty while the rate is unknown).</summary>
            public string EtaSuffix()
            {
                var eta = EtaSeconds();
                if (eta is not { } s || s <= 0) return "";
                var t = TimeSpan.FromSeconds(s);
                string text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
                            : $"{t.Seconds}s";
                return $" · ~{text} left";
            }
        }

        private async Task ProcessBatchAsync(List<TranslationRow> batch, string systemPrompt, AppConfig cfg, ProgressState state, bool saveTm, bool useSessionTm)
        {
            if (_cts!.Token.IsCancellationRequested || state.Halted) return;

            // This batch's edits become ONE undo entry (1 batch = 1 Ctrl+Z), flushed via
            // FlushBatchUndo on every exit path so nothing applied is left uncapturable.
            var batchActions = new List<CellEditAction>();
            var sessionHits = new List<(TranslationRow row, string cached)>();
            var rowsForApi = batch;

            if (useSessionTm)
            {
                rowsForApi = new List<TranslationRow>();
                foreach (var r in batch)
                {
                    // Session cache is scoped by (source + reuse tag): a same-source
                    // row shares only with rows of the SAME tag (same key/reference).
                    // Comment rows never take a cached value (their note must be sent).
                    if (!IsCommentBound(r) && _sessionTm.TryGetValue(SessionKey(r), out var cached))
                        sessionHits.Add((r, cached));
                    else
                        rowsForApi.Add(r);
                }

                if (sessionHits.Count > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _vm.SuppressUndoCapture = true;
                        try
                        {
                            foreach (var (r, cached) in sessionHits)
                            {
                                RecordChange(batchActions, r, r.Target, cached);
                                r.Target = cached;
                                _vm.MarkTargetSnapshot(r, cached);
                            }
                        }
                        finally { _vm.SuppressUndoCapture = false; }
                    });
                    Interlocked.Add(ref state.Successful, sessionHits.Count);
                    int sessionHitCount = sessionHits.Count;
                    Dispatcher.UIThread.Post(() =>
                    {
                        Progress.Value = state.Successful + state.Errors;
                        LblProgress.Text = string.Format(Loc.T("ai.progress.session"), state.Successful + state.Errors, state.Total, sessionHitCount, state.EtaSuffix());
                        _vm.RecordTmHits(sessionHitCount);
                    });
                }

                if (rowsForApi.Count == 0) { FlushBatchUndo(batchActions); return; }
            }

            var batchData = new List<RowContext>();
            foreach (var r in rowsForApi)
            {
                var ctx = new RowContext
                {
                    Row = r,
                    OriginalSource = r.Source,
                    GlossaryTokens = new Dictionary<string, string>(),
                    OrderedTokens = new List<string>()
                };

                string preprocessed = r.Source;
                int gltIdx = 1;
                foreach (var term in cfg.ProtectedTerms)
                {
                    if (string.IsNullOrWhiteSpace(term)) continue;
                    string trimmed = term.Trim();
                    // Boundaries must exclude digits and '_' too, or "Ork" matches
                    // inside "Ork2"/"Ork_boss" and gets wrongly tokenized.
                    var pattern = $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(trimmed)}(?![\p{{L}}\p{{Nd}}_])";
                    if (Regex.IsMatch(preprocessed, pattern))
                    {
                        string placeholder = $"{{GLT_{gltIdx++}}}";
                        ctx.GlossaryTokens[placeholder] = trimmed;
                        preprocessed = Regex.Replace(preprocessed, pattern, placeholder);
                    }
                }

                foreach (Match m in Regex.Matches(preprocessed, @"{[^}]+}"))
                    ctx.OrderedTokens.Add(m.Value);

                ctx.PromptText = preprocessed;
                // Extra context (input-only; the response contract stays [{id,text}]).
                ctx.AltText = RefTextFor(r);
                ctx.KeyContext = _sendKeyContext ? ContextKeyFor(r) : null;
                ctx.ReuseTag = ReuseTag(r); // storage/reuse discriminator (may differ from KeyContext)
                batchData.Add(ctx);
            }

            // Same payload builder Multi-translate uses — per-row reviewer notes ride inline
            // (commented rows only); the instruction prefix is added only when a note is present,
            // so note-free batches stay byte-identical to before and prompt caching is unaffected.
            var batchTexts = batchData.Select(b => b.PromptText).ToList();
            var batchNotes = batchData
                .Select(b => _sendComments && b.Row.HasComment ? b.Row.Comment : null)
                .ToList();
            // Null lists when the options are off — keeps the payload byte-identical
            // to the historical shape (prompt caching, zero drift).
            var batchRefs = _altMap != null ? batchData.Select(b => b.AltText).ToList() : null;
            var batchKeys = _sendKeyContext ? batchData.Select(b => b.KeyContext).ToList() : null;
            string jsonPrompt = TranslationBatchRunner.BuildBatchUserMessage(
                batchTexts, batchNotes, batchRefs, batchKeys, _refQuality);

            int batchSuccess = 0;
            int batchErrors = 0;

            try
            {
                _vm.RecordAiRequest();
                string result;
                try
                {
                    result = await TranslatorService.TranslateAsync(jsonPrompt, systemPrompt, _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && cfg.AutoRetryEnabled)
                {
                    // Settings → "Auto-retry failed batches": one delayed second attempt
                    // before the batch counts toward the 3-strikes halt.
                    int delayS = Math.Max(1, cfg.AutoRetryDelaySeconds);
                    Dispatcher.UIThread.Post(() =>
                        _vm.AddLog(string.Format(Loc.T("log.ai.batchRetry"), ex.Message, delayS), LogLevel.Warning));
                    await Task.Delay(TimeSpan.FromSeconds(delayS), _cts.Token);
                    _vm.RecordAiRequest();
                    result = await TranslatorService.TranslateAsync(jsonPrompt, systemPrompt, _cts.Token);
                }
                _cts.Token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(result))
                {
                    batchErrors = batch.Count;
                }
                else
                {
                    string cleanJson = StripCodeFences(result);

                    JArray parsedArray;
                    try
                    {
                        parsedArray = JArray.Parse(cleanJson);
                    }
                    catch (Exception ex)
                    {
                        batchErrors = batch.Count;
                        Dispatcher.UIThread.Post(() =>
                            _vm.AddLog(string.Format(Loc.T("log.ai.badJson"), ex.Message), LogLevel.Error));
                        return;
                    }

                    var translatedBlocks = new List<string>(batchData.Count);
                    for (int i = 0; i < batchData.Count; i++)
                    {
                        var match = parsedArray.FirstOrDefault(x => x["id"]?.ToString() == i.ToString());
                        translatedBlocks.Add(match?["text"]?.ToString() ?? "");
                    }

                    var outcomes = new List<RowOutcome>(batchData.Count);
                    for (int i = 0; i < batchData.Count; i++)
                        outcomes.Add(ComputeRowOutcome(batchData[i], translatedBlocks, i, cfg, saveTm));

                    await Dispatcher.UIThread.InvokeAsync(() => ApplyRowOutcomes(batchActions, outcomes));

                    foreach (var o in outcomes)
                    {
                        if (o.Success) batchSuccess++;
                        else batchErrors++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                batchErrors = rowsForApi.Count;
                Dispatcher.UIThread.Post(() =>
                    _vm.AddLog(string.Format(Loc.T("log.ai.batchFailed"), ex.Message), LogLevel.Error));
            }
            finally
            {
                if (batchSuccess > 0)
                {
                    Interlocked.Add(ref state.Successful, batchSuccess);
                    Interlocked.Exchange(ref state.ConsecutiveApiFails, 0);
                    Dispatcher.UIThread.Post(() => _vm.RecordRowsTranslatedByAi(batchSuccess));
                }
                if (batchErrors > 0)
                {
                    Interlocked.Add(ref state.Errors, batchErrors);
                    int fails = Interlocked.Increment(ref state.ConsecutiveApiFails);
                    if (fails >= 3)
                    {
                        state.Halted = true;
                        _cts?.Cancel();
                        Dispatcher.UIThread.Post(() =>
                            _vm.AddLog(Loc.T("log.ai.halted"), LogLevel.Error));
                    }
                }

                int succSnap = state.Successful;
                int errSnap = state.Errors;
                int total = state.Total;

                Dispatcher.UIThread.Post(() =>
                {
                    Progress.Value = succSnap + errSnap;
                    LblProgress.Text = string.Format(Loc.T("ai.progress.batch"), succSnap, total, errSnap, state.EtaSuffix());
                });

                // One undo entry for this batch. The finally runs on every exit (success,
                // cancel-return from the catch, or error), so applied edits are never left
                // uncapturable. Posted to the UI thread (StateChanged updates bound commands).
                FlushBatchUndo(batchActions);
            }
        }

        /// <summary>Push this batch's edits as a single undo entry, on the UI thread.</summary>
        private void FlushBatchUndo(List<CellEditAction> actions)
        {
            if (actions.Count == 0) return;
            var entry = new BatchEditAction(actions);
            if (Dispatcher.UIThread.CheckAccess()) _vm.UndoManager.AddAction(entry);
            else Dispatcher.UIThread.Post(() => _vm.UndoManager.AddAction(entry));
        }

        private static string StripCodeFences(string raw)
        {
            string s = raw.Trim();
            if (s.StartsWith("```json")) s = s.Substring(7);
            else if (s.StartsWith("```")) s = s.Substring(3);
            if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
            return s.Trim();
        }

        private RowOutcome ComputeRowOutcome(RowContext ctx, List<string> translatedBlocks, int i, AppConfig cfg, bool saveTm)
        {
            var outcome = new RowOutcome { Row = ctx.Row, OldTarget = ctx.Row.Target };

            if (i >= translatedBlocks.Count)
            {
                outcome.Success = false;
                outcome.HasError = true;
                outcome.ErrorMessage = "AI did not return this row.";
                outcome.NewTarget = ctx.Row.Target; // keep whatever the row had — never clobber
                return outcome;
            }

            string tText = translatedBlocks[i];

            // A missing id (or empty text) in the response is a FAILED row, not an
            // empty translation: writing "" here used to silently WIPE a Target the
            // row already had, and count it as a success.
            if (string.IsNullOrEmpty(tText))
            {
                outcome.Success = false;
                outcome.HasError = true;
                outcome.ErrorMessage = "AI returned no text for this row.";
                outcome.NewTarget = ctx.Row.Target;
                return outcome;
            }

            var targetTokens = Regex.Matches(tText, @"{[^}]+}").Cast<Match>().Select(m => m.Value).ToList();
            if (ctx.OrderedTokens.Count == targetTokens.Count)
            {
                for (int j = 0; j < ctx.OrderedTokens.Count; j++)
                {
                    if (ctx.OrderedTokens[j] != targetTokens[j])
                    {
                        outcome.HasWarning = true;
                        outcome.WarningMessage = "Auto saved rows with tokens";
                        var regex = new Regex(Regex.Escape(targetTokens[j]));
                        tText = regex.Replace(tText, ctx.OrderedTokens[j], 1);
                    }
                }
            }
            else if (ctx.OrderedTokens.Count > 0)
            {
                outcome.HasError = true;
                outcome.ErrorMessage = $"Token count mismatch! Expected {ctx.OrderedTokens.Count}, found {targetTokens.Count}.";
            }

            foreach (var kvp in ctx.GlossaryTokens)
            {
                if (tText.Contains(kvp.Key))
                {
                    tText = tText.Replace(kvp.Key, kvp.Value);
                }
                else
                {
                    outcome.HasError = true;
                    outcome.ErrorMessage = $"Missing glossary token: {kvp.Key}";
                }
            }

            // <angle> tags must survive too (same contract as {curly} tokens): a
            // result that grew/lost tags is rejected so it can't reach the row or TM.
            // Compared against the ORIGINAL source — glossary placeholders never
            // contain angle brackets.
            int srcAngle = Regex.Matches(ctx.OriginalSource, @"<[^>]+>").Count;
            int tgtAngle = Regex.Matches(tText, @"<[^>]+>").Count;
            if (srcAngle != tgtAngle)
            {
                outcome.HasError = true;
                outcome.ErrorMessage = $"Angle-tag count mismatch! Expected {srcAngle}, found {tgtAngle}.";
            }

            if (outcome.HasError)
            {
                outcome.Success = false;
                // Keep the row's existing Target. The old behavior wrote Source here,
                // which both destroyed prior translations and made the row LOOK done.
                outcome.NewTarget = ctx.Row.Target;
            }
            else
            {
                outcome.Success = true;
                outcome.NewTarget = tText;
                outcome.ReuseTag = ctx.ReuseTag;
                // Comment-influenced translations are never persisted (context-specific,
                // would poison siblings). Everything else is stored + reused by
                // ApplyRowOutcomes, keyed by ReuseTag (tag-aware) or generic.
                outcome.StoreToTm = saveTm && !(_sendComments && ctx.Row.HasComment);
            }

            return outcome;
        }

        private void ApplyRowOutcomes(List<CellEditAction> sink, List<RowOutcome> outcomes)
        {
            _vm.SuppressUndoCapture = true;
            try
            {
                foreach (var o in outcomes)
                {
                    RecordChange(sink, o.Row, o.OldTarget, o.NewTarget);
                    o.Row.Target = o.NewTarget;
                    _vm.MarkTargetSnapshot(o.Row, o.NewTarget);

                    // Etap 2: setting Target above triggers the debounced
                    // ValidationService.ScheduleRowValidation hook in
                    // MainWindowViewModel.TouchEditMetadata, which runs the 6
                    // light validators ~300 ms later and writes TagMismatch /
                    // TokenMismatch / Whitespace / ... Problems automatically.
                    // The AI-side HasError / HasWarning flags from the batch
                    // result are therefore redundant — validators detect the
                    // same conditions from the final Target text. Kept here
                    // for telemetry / log noise but no longer mutate the row.
                    _ = o.HasError;
                    _ = o.HasWarning;

                    // Reuse only clean successes; comment rows never feed TM/reuse.
                    if (!o.Success || string.IsNullOrWhiteSpace(o.NewTarget) || IsCommentBound(o.Row))
                        continue;

                    // Persistent store: tag-aware (meaningful key OR reference) keyed
                    // entry, else plain generic (source-only).
                    if (o.StoreToTm)
                    {
                        if (o.ReuseTag != null) _tm.AddKeyed(o.ReuseTag, o.Row.Source, o.NewTarget);
                        else _tm.Add(o.Row.Source, o.NewTarget);
                    }

                    // Within-run reuse — session cache + same-source propagation, BOTH
                    // scoped to the SAME reuse tag. A reference that gives two identical
                    // sources different meanings yields different tags, so one is never
                    // blind-copied over the other; two rows with the same source AND the
                    // same reference (or plain generic) still auto-repeat.
                    _sessionTm[SessionKey(o.Row)] = o.NewTarget;
                    foreach (var other in _vm.Rows.Where(x => x != o.Row
                                 && TmReuse.CanShare(o.Row.Source, o.ReuseTag, false,
                                                     x.Source, ReuseTag(x), IsCommentBound(x))))
                    {
                        RecordChange(sink, other, other.Target, o.NewTarget);
                        other.Target = o.NewTarget;
                        _vm.MarkTargetSnapshot(other, o.NewTarget);
                    }
                }
            }
            finally { _vm.SuppressUndoCapture = false; }
        }

        private void OnStop(object? s, RoutedEventArgs e)
        {
            // Hard stop. Use the VM handle so this works even if the panel was reopened
            // mid-run (a new instance can halt a translation started by the previous one).
            var handle = _vm.ActiveAiTranslation;
            if (handle != null)
            {
                handle.Stop();
            }
            else
            {
                if (_progressState != null) _progressState.Halted = true;
                try { _cts?.Cancel(); } catch { }
            }
            BtnStop.IsEnabled = false;
            LblProgress.Text = Loc.T("qc.ai.stopping");
            _vm.AddLog(Loc.T("log.ai.stopRequested"), LogLevel.Warning);
        }

        private void OnTmCheckChanged(object? s, RoutedEventArgs e)
        {
            if (s == ChkUseTm && ChkUseTm.IsChecked == true)
                ChkSessionTm.IsChecked = false;
            else if (s == ChkSessionTm && ChkSessionTm.IsChecked == true)
                ChkUseTm.IsChecked = false;
        }

        /// <summary>
        /// The Translation Memory files were deleted from outside the panel (Reset
        /// cache). Drop the loaded copy too, or the next run's Save() would write
        /// the deleted memory straight back.
        /// </summary>
        internal void DropTranslationMemory()
        {
            if (_vm is null || _vm.ActiveAiTranslation is null)
                _tm = new TranslationMemoryService();
        }

        private async void OnClearTm(object? s, RoutedEventArgs e)
        {
            try
            {
                var dir = AppConfig.ConfigDir();
                var files = Directory.GetFiles(dir, "translation_memory_*.json");
                if (files.Length == 0)
                {
                    LblProgress.Text = Loc.T("ai.tm.none");
                    return;
                }

                var pairs = files.Select(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f)
                        .Replace("translation_memory_", "");
                    var parts = name.Split('_');
                    if (parts.Length >= 2)
                    {
                        string from = char.ToUpper(parts[0][0]) + parts[0].Substring(1);
                        string to = char.ToUpper(parts[1][0]) + parts[1].Substring(1);
                        return $"{from} → {to}";
                    }
                    return name;
                }).ToArray();

                // Modal sub-dialog still uses ShowDialog — find the hosting top-level window.
                var parentWindow = TopLevel.GetTopLevel(this) as Window;
                if (parentWindow == null) return;

                var dlg = new Window
                {
                    Title = Loc.T("ai.tm.clearTitle"),
                    Width = 350,
                    Height = 300,
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                    Background = Avalonia.Media.Brushes.Transparent,
                    WindowDecorations = Avalonia.Controls.WindowDecorations.None,
                    Content = new Border
                    {
                        BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#555")),
                        BorderThickness = new Avalonia.Thickness(1),
                        Background = Avalonia.Application.Current?.FindResource("WindowBackground") as Avalonia.Media.IBrush,
                        Child = new DockPanel
                        {
                            Children =
                            {
                                new DialogTitleBar { Title = Loc.T("ai.tm.clearTitle"), [DockPanel.DockProperty] = Dock.Top },
                                new StackPanel
                                {
                                    Margin = new Avalonia.Thickness(16),
                                    Spacing = 12,
                                    Children =
                                    {
                                        new TextBlock { Text = Loc.T("ai.tm.clearPick"), Foreground = Avalonia.Application.Current?.FindResource("TextPrimary") as Avalonia.Media.IBrush },
                                        new ListBox { Name = "TmList", ItemsSource = pairs, Height = 140, SelectionMode = SelectionMode.Single },
                                        new StackPanel
                                        {
                                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                                            Spacing = 8,
                                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                            Children =
                                            {
                                                new Button { Content = Loc.T("common.cancel"), Classes = { "subtle" } },
                                                new Button { Content = Loc.T("common.delete"), Classes = { "primary" } }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                var panel = (StackPanel)((DockPanel)((Border)dlg.Content).Child!).Children[1];
                var listBox = (ListBox)panel.Children[1];
                var buttons = (StackPanel)panel.Children[2];
                var btnCancel = (Button)buttons.Children[0];
                var btnDelete = (Button)buttons.Children[1];

                string? selectedFile = null;
                btnCancel.Click += (_, _) => dlg.Close();
                btnDelete.Click += (_, _) =>
                {
                    int idx = listBox.SelectedIndex;
                    if (idx >= 0 && idx < files.Length)
                        selectedFile = files[idx];
                    dlg.Close();
                };

                await dlg.ShowDialog(parentWindow);

                if (selectedFile != null && File.Exists(selectedFile))
                {
                    var pairName = pairs[Array.IndexOf(files, selectedFile)];
                    File.Delete(selectedFile);
                    // Drop the loaded copy too: SetLanguagePair short-circuits on the same
                    // pair (no reload), so a later run's Save() would resurrect the file
                    // from stale memory. A fresh instance re-loads from disk on next use.
                    if (_vm.ActiveAiTranslation is null)
                        _tm = new TranslationMemoryService();
                    _vm.AddLog(string.Format(Loc.T("log.tm.deleted"), pairName));
                    LblProgress.Text = string.Format(Loc.T("ai.tm.deleted"), pairName);
                }
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnClearTm)); }
        }

        private class RowContext
        {
            public TranslationRow Row { get; set; } = null!;
            public string OriginalSource { get; set; } = "";
            public string PromptText { get; set; } = "";
            public Dictionary<string, string> GlossaryTokens { get; set; } = new();
            public List<string> OrderedTokens { get; set; } = new();
            /// <summary>Same string in the additional language (context-only), or null.</summary>
            public string? AltText { get; set; }
            /// <summary>Row key as AI context ("ns/key"), or null (off / GUID-like).</summary>
            public string? KeyContext { get; set; }
            /// <summary>Storage/reuse discriminator (key- or reference-based), or null (generic).</summary>
            public string? ReuseTag { get; set; }
        }

        private class RowOutcome
        {
            public TranslationRow Row = null!;
            public string OldTarget = "";
            public string NewTarget = "";
            public bool Success;
            public bool HasError;
            public string ErrorMessage = "";
            public bool HasWarning;
            public string WarningMessage = "";
            /// <summary>The reuse discriminator (key/reference tag), or null for generic.</summary>
            public string? ReuseTag;
            /// <summary>Whether to persist this translation (save-TM on and not comment-bound).</summary>
            public bool StoreToTm;
        }

        private static void RecordChange(List<CellEditAction> sink, TranslationRow row, string oldVal, string newVal)
        {
            if (oldVal == newVal) return;
            sink.Add(new CellEditAction(row, oldVal, newVal));
        }
    }
}
