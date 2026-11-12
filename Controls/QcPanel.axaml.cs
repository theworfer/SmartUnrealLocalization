using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Qc;
using SmartUnrealLocalization.Utils;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Quality Control panel (left-bar "QC"): the AI review of the ACTIVE file.
    /// Every row in scope is sent, minus what the user's skip switches keep out
    /// (<see cref="QcAiScope"/>); the model's verdicts land in
    /// <see cref="QcService"/> as per-row proposals the user resolves with
    /// Fix / Keep. QC's own local pass was removed on 2026-10-21 — the live
    /// validator reports those problems while you edit.
    /// </summary>
    public partial class QcPanel : UserControl
    {
        private MainWindowViewModel _vm = null!;
        private bool _uiInitialized;
        private string? _initializedFor; // active file the panel state belongs to

        // Reference file (optional): a second language of the same game, matched
        // by key. Session state — deliberately not persisted.
        private string? _referenceFilePath;
        private Dictionary<(string Ns, string Key), string>? _referenceMap;

        // Original file: supplies the text the result is compared AGAINST when the
        // active file holds only the translation (every .locres, and CSV/PO with a
        // single column). Also session state.
        private string? _originalFilePath;
        private Dictionary<(string Ns, string Key), string>? _originalMap;

        /// <summary>True when the active file's rows carry a Target at all.</summary>
        private bool ActiveFileHasTarget =>
            _vm.AllRows.Any(r => !string.IsNullOrWhiteSpace(r.Target));

        /// <summary>Result sits in Source (index 0) or Target (index 1).</summary>
        private bool ResultIsInTarget => CbResultColumn.SelectedIndex == 1;

        // AI run state: the live run's cancellation source and the verdict cache
        // (per language pair).
        private CancellationTokenSource? _aiCts;
        private readonly QcCache _cache = new();

        private static readonly string[] Languages =
        {
            "English","Ukrainian","Arabic","Chinese","Czech","Danish","Dutch","Finnish","French",
            "German","Greek","Hebrew","Hungarian","Indonesian","Italian","Japanese","Korean",
            "Norwegian","Polish","Portuguese","Romanian","Russian","Spanish","Swedish","Thai","Turkish","Vietnamese"
        };

        public event Action? ClosePanelRequested;

        public QcPanel()
        {
            InitializeComponent();
            this.RefreshOnLanguageChange(RelocalizeChrome);
        }

        /// <summary>
        /// Re-apply what this panel writes in code rather than binds: the
        /// "pick a language" stub at the head of both language lists, the role
        /// hints, and the two "no file" placeholders. Selections survive — see
        /// <c>AiTranslatePanel.RelocalizeChrome</c> for why that matters.
        /// </summary>
        private void RelocalizeChrome()
        {
            if (!_uiInitialized) return;

            RelocalizeLanguageList(CbFrom);
            RelocalizeLanguageList(CbTo);

            if (string.IsNullOrEmpty(_originalFilePath)) LblOriginalFile.Text = Loc.T("ai.ctx.noFile");
            if (string.IsNullOrEmpty(_referenceFilePath)) LblReferenceFile.Text = Loc.T("ai.ctx.noFile");
            if (_vm != null)
            {
                RefreshRoleHints();
                LblResultFile.Text = string.IsNullOrEmpty(_vm.OriginalFilePath)
                    ? Loc.T("qc.file.none")
                    : Path.GetFileName(_vm.OriginalFilePath);
                RefreshRowCounts();   // the count line is written in code too
            }
        }

        /// <summary>The language list, with the localized stub at index 0.</summary>
        private static List<string> LanguageChoices()
        {
            var list = new List<string> { Loc.T("ai.selectLanguage") };
            list.AddRange(Languages);
            return list;
        }

        private static void RelocalizeLanguageList(ComboBox box)
        {
            int selected = box.SelectedIndex;
            box.ItemsSource = LanguageChoices();
            box.SelectedIndex = Math.Max(0, selected);
        }

        public void Initialize(MainWindowViewModel vm)
        {
            _vm = vm;

            if (!_uiInitialized)
            {
                _uiInitialized = true;
                // With "checked rows" as the scope, the counts must follow the
                // selection — otherwise the line promises a cost the run won't have.
                // Posted: never recount inside the grid's own selection change.
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.SelectedCount) && RbCheckSelected.IsChecked == true)
                        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshRowCounts, Avalonia.Threading.DispatcherPriority.Background);
                };
                var langs = LanguageChoices();
                CbFrom.ItemsSource = langs;
                CbFrom.SelectedIndex = Math.Max(0, langs.IndexOf(AppConfig.Instance.LastAiFromLanguage));
                CbTo.ItemsSource = langs;
                CbTo.SelectedIndex = Math.Max(0, langs.IndexOf(AppConfig.Instance.LastAiToLanguage));

                CbResultColumn.ItemsSource = new[] { "Source", "Target" };
                CbReferenceColumn.ItemsSource = new[] { "Source", "Target" };
                CbReferenceColumn.SelectedIndex = 0;
                CbOriginalFileColumn.ItemsSource = new[] { "Source", "Target" };
                CbOriginalFileColumn.SelectedIndex = 0;
            }

            LblResultFile.Text = string.IsNullOrEmpty(_vm.OriginalFilePath)
                ? Loc.T("qc.file.none")
                : Path.GetFileName(_vm.OriginalFilePath);

            // Different file than the state was built for → the extra files and
            // the progress line belong to the old one.
            if (!string.Equals(_initializedFor, _vm.OriginalFilePath, StringComparison.OrdinalIgnoreCase))
            {
                _initializedFor = _vm.OriginalFilePath;
                LblAiProgress.Text = "";
                AiProgress.Value = 0;
                // The extra files belong to the file that was being reviewed.
                OnClearOriginal(null, null!);
                OnClearReference(null, null!);
            }

            // Layout comes from CONTENT, not from the extension: .locres always
            // carries one language, but a CSV/PO may or may not have a Target.
            // With no Target there is no choice to make — say so instead of showing
            // a dropdown whose second option does not exist in this file.
            CbResultColumn.SelectedIndex = ActiveFileHasTarget ? 1 : 0;
            ResultColumnRow.IsVisible = ActiveFileHasTarget;
            RefreshRoleHints();
            LoadSkipSwitches();
            RefreshRowCounts();
            RefreshResults();
        }

        /// <summary>
        /// Explain, in the card itself, what each file is for right now — the old
        /// card stated the roles ambiguously and the user reasonably read them
        /// backwards.
        /// </summary>
        private void RefreshRoleHints()
        {
            bool hasTarget = ActiveFileHasTarget;
            LblFileLayout.Text = hasTarget ? Loc.T("qc.file.layout.both") : Loc.T("qc.file.layout.sourceOnly");

            // With the result in Source, Target is reserved for accepted fixes; the
            // original therefore has to come from a second file.
            bool needsOriginalFile = !ResultIsInTarget;
            LblOriginalRole.Text = needsOriginalFile
                ? Loc.T("qc.file.original.required")
                : Loc.T("qc.file.original.optional");
            OriginalFileRow.IsVisible = needsOriginalFile;
        }

        private void OnResultColumnChanged(object? s, SelectionChangedEventArgs e)
        {
            if (_vm == null || !_uiInitialized) return;
            RefreshRoleHints();
            RefreshRowCounts();
        }

        // ── original file ────────────────────────────────────────────────

        private async void OnPickOriginal(object? s, RoutedEventArgs e)
        {
            try
            {
                string? path = await PickLocalizationFileAsync(Loc.T("qc.file.pickOriginal"));
                if (path == null) return;

                _originalFilePath = path;
                _originalMap = null;
                LblOriginalFile.Text = Path.GetFileName(path);
                BtnClearOriginal.IsVisible = true;
                OriginalColumnRow.IsVisible = Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".po";

                await ReportKeyMatchAsync();
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnPickOriginal)); }
        }

        private void OnClearOriginal(object? s, RoutedEventArgs e)
        {
            _originalFilePath = null;
            _originalMap = null;
            LblOriginalFile.Text = Loc.T("ai.ctx.noFile");
            BtnClearOriginal.IsVisible = false;
            OriginalColumnRow.IsVisible = false;
            LblOriginalMatch.IsVisible = false;
        }

        /// <summary>
        /// Compare keys between the active file and the chosen original and show
        /// the overlap. Two files from different game versions look fine until QC
        /// silently checks nothing — this is where that becomes visible.
        /// </summary>
        private async Task ReportKeyMatchAsync()
        {
            try
            {
                _originalMap = await LoadKeyMapAsync(_originalFilePath!,
                    OriginalColumnRow.IsVisible && CbOriginalFileColumn.SelectedIndex == 1);

                int total = _vm.AllRows.Count;
                int matched = _vm.AllRows.Count(r =>
                    _originalMap.ContainsKey((r.Namespace ?? "", r.Key ?? "")));

                LblOriginalMatch.Text = string.Format(Loc.T("qc.file.match"), matched, total);
                LblOriginalMatch.IsVisible = true;
                _vm.AddLog(string.Format(Loc.T("log.qc.originalLoaded"), Path.GetFileName(_originalFilePath), matched, total),
                           matched == 0 ? LogLevel.Warning : LogLevel.Info);
                RefreshRowCounts();
            }
            catch (Exception ex)
            {
                LblOriginalMatch.Text = string.Format(Loc.T("qc.err.failed"), ex.Message);
                LblOriginalMatch.IsVisible = true;
                _originalMap = null;
            }
        }

        private void OnClosePanel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();

        // ── reference file ───────────────────────────────────────────────

        /// <summary>Shared file picker for the original / reference slots.</summary>
        private async Task<string?> PickLocalizationFileAsync(string title)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var result = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Localization (*.locres, *.csv, *.po)")
                        { Patterns = new[] { "*.locres", "*.csv", "*.po" } },
                },
            });
            var path = result.Count > 0 ? result[0]?.Path?.LocalPath : null;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private async void OnPickReference(object? s, RoutedEventArgs e)
        {
            try
            {
                string? path = await PickLocalizationFileAsync(Loc.T("qc.file.pickReference"));
                if (path == null) return;

                _referenceFilePath = path;
                _referenceMap = null; // rebuilt on the next run
                LblReferenceFile.Text = Path.GetFileName(path);
                BtnClearReference.IsVisible = true;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                ReferenceColumnRow.IsVisible = ext is ".csv" or ".po";
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnPickReference)); }
        }

        private void OnClearReference(object? s, RoutedEventArgs e)
        {
            _referenceFilePath = null;
            _referenceMap = null;
            LblReferenceFile.Text = Loc.T("ai.ctx.noFile");
            BtnClearReference.IsVisible = false;
            ReferenceColumnRow.IsVisible = false;
        }

        // ── extra files → key maps ───────────────────────────────────────

        private Task<Dictionary<(string Ns, string Key), string>> LoadReferenceMapAsync()
            => LoadKeyMapAsync(_referenceFilePath!,
                               ReferenceColumnRow.IsVisible && CbReferenceColumn.SelectedIndex == 1);

        /// <summary>Read a localization file into (namespace, key) → text.</summary>
        private async Task<Dictionary<(string Ns, string Key), string>> LoadKeyMapAsync(string file, bool takeTarget)
        {
            if (!File.Exists(file)) throw new FileNotFoundException($"File not found: {file}");
            var ext = Path.GetExtension(file).ToLowerInvariant();

            var rows = await Task.Run(() => ext switch
            {
                ".locres" => LocresFileService.Open(file).Rows,
                ".po" => PoFileService.Import(file),
                ".csv" => CsvFileService.Import(file),
                _ => throw new NotSupportedException($"Unsupported format: {ext}"),
            });

            var map = new Dictionary<(string Ns, string Key), string>();
            foreach (var r in rows)
            {
                string text = takeTarget ? r.Target ?? "" : r.Source ?? "";
                if (text.Length == 0) continue;
                map.TryAdd((r.Namespace ?? "", r.Key ?? ""), text);
            }
            return map;
        }

        /// <summary>Modal yes/no in the app's own chrome; true when the user confirms.</summary>
        private async Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
        {
            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent == null) return true;

            bool proceed = false;
            var btnCancel = new Button { Content = cancelText, Classes = { "subtle" } };
            var btnGo = new Button { Content = okText, Classes = { "primary" } };
            var dlg = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brushes.Transparent,
                WindowDecorations = WindowDecorations.None,
                Content = new Border
                {
                    BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#555")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Background = Avalonia.Application.Current?.FindResource("WindowBackground") as Avalonia.Media.IBrush,
                    Child = new DockPanel
                    {
                        Children =
                        {
                            new DialogTitleBar { Title = title, [DockPanel.DockProperty] = Dock.Top },
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
                                        Children = { btnCancel, btnGo },
                                    },
                                },
                            },
                        },
                    },
                },
            };
            btnCancel.Click += (_, _) => dlg.Close();
            btnGo.Click += (_, _) => { proceed = true; dlg.Close(); };
            await dlg.ShowDialog(parent);
            return proceed;
        }

        // ── AI layer ─────────────────────────────────────────────────────

        /// <summary>Show the saved skip switches (they are app settings, shared with MCP).</summary>
        private void LoadSkipSwitches()
        {
            var cfg = AppConfig.Instance;
            ChkSkipEmpty.IsChecked = cfg.QcSkipEmpty;
            ChkSkipAutoCarried.IsChecked = cfg.QcSkipAutoCarried;
            ChkSkipIdentical.IsChecked = cfg.QcSkipIdenticalToOriginal;
            ChkSkipApproved.IsChecked = cfg.QcSkipApproved;
            ChkOnlyFlagged.IsChecked = cfg.QcOnlyValidatorFlagged;
        }

        /// <summary>The settings were reset from outside the panel: show them again.</summary>
        internal void ReloadSettings()
        {
            if (!_uiInitialized) return;
            LoadSkipSwitches();
            RefreshRowCounts();
        }

        private void OnSkipToggled(object? s, RoutedEventArgs e)
        {
            var cfg = AppConfig.Instance;
            cfg.QcSkipEmpty = ChkSkipEmpty.IsChecked == true;
            cfg.QcSkipAutoCarried = ChkSkipAutoCarried.IsChecked == true;
            cfg.QcSkipIdenticalToOriginal = ChkSkipIdentical.IsChecked == true;
            cfg.QcSkipApproved = ChkSkipApproved.IsChecked == true;
            cfg.QcOnlyValidatorFlagged = ChkOnlyFlagged.IsChecked == true;
            cfg.Save();
            RefreshRowCounts();
        }

        private void OnScopeChanged(object? s, RoutedEventArgs e) => RefreshRowCounts();

        /// <summary>The rows in scope: the whole file, or the checked rows.</summary>
        private IReadOnlyList<TranslationRow> ScopeRows()
            => RbCheckSelected.IsChecked == true ? _vm.SelectedRows : _vm.AllRows;

        /// <summary>The review's rows under the current switches, roles and extra files.</summary>
        private QcAiScope.Result BuildScope()
            => QcAiScope.Build(ScopeRows(), QcAiScope.Options.FromConfig(AppConfig.Instance),
                               ResultIsInTarget, _originalMap, _referenceMap);

        /// <summary>
        /// Each switch shows how many rows it keeps out, and the total says what a
        /// run would send — the cost is visible before anything is sent.
        /// </summary>
        private void RefreshRowCounts()
        {
            if (_vm == null || !_uiInitialized) return;

            // Translation in Source and no original chosen yet: every row would be
            // compared with itself and counted as "identical". Say what is missing.
            if (!ResultIsInTarget && _originalMap == null)
            {
                LblSkipEmpty.Text = LblSkipAutoCarried.Text = LblSkipIdentical.Text =
                    LblSkipApproved.Text = LblOnlyFlagged.Text = "";
                LblRowsTotal.Text = Loc.T("qc.err.needOriginal");
                return;
            }

            var r = BuildScope();
            LblSkipEmpty.Text = Count(r.SkippedEmpty);
            LblSkipAutoCarried.Text = Count(r.SkippedAutoCarried);
            LblSkipIdentical.Text = Count(r.SkippedIdentical);
            LblSkipApproved.Text = Count(r.SkippedApproved);
            LblOnlyFlagged.Text = Count(r.SkippedUnflagged);

            int batchSize = Math.Max(1, AppConfig.Instance.QcBatchSize);
            int batches = (r.Inputs.Count + batchSize - 1) / batchSize;
            LblRowsTotal.Text = string.Format(Loc.T("qc.ai.selected"), r.Inputs.Count, batches);

            static string Count(int n) => n > 0 ? "−" + n : "";
        }

        private async void OnRunAi(object? s, RoutedEventArgs e)
        {
            if (_vm.RowCount == 0) { LblAiProgress.Text = Loc.T("qc.err.noFile"); return; }
            if (string.IsNullOrWhiteSpace(AppConfig.Instance.ApiKey))
            {
                LblAiProgress.Text = Loc.T("qc.ai.err.noKey");
                return;
            }

            string from = CbFrom.SelectedItem?.ToString() ?? "";
            string to = CbTo.SelectedItem?.ToString() ?? "";
            // Index, not text — the stub at index 0 is translated (see the same
            // check in AiTranslatePanel.StartCoreAsync).
            if (CbFrom.SelectedIndex <= 0 || CbTo.SelectedIndex <= 0 || from == to)
            {
                LblAiProgress.Text = Loc.T("qc.ai.err.langs");
                return;
            }
            if (ScopeRows().Count == 0) { LblAiProgress.Text = Loc.T("qc.err.noRows"); return; }

            // Everything this run depends on is pinned HERE, before the first
            // await: loading an extra file yields, and a tab switch in between
            // used to label file A's findings as file B's (audit 2026-09 §7).
            // After the await the run checks it still owns the screen.
            string runFile = QcService.Instance.CurrentFile; // results belong to THIS file
            var runSession = _vm.ActiveSession;
            bool Superseded() => !string.Equals(QcService.Instance.CurrentFile, runFile, StringComparison.OrdinalIgnoreCase)
                                 || !ReferenceEquals(_vm.ActiveSession, runSession);

            BtnRunAi.IsEnabled = false;
            try
            {
                if (_referenceFilePath != null && _referenceMap == null)
                    _referenceMap = await LoadReferenceMapAsync();
                if (_originalFilePath != null && _originalMap == null)
                    _originalMap = await LoadKeyMapAsync(_originalFilePath,
                        OriginalColumnRow.IsVisible && CbOriginalFileColumn.SelectedIndex == 1);
            }
            catch (Exception ex)
            {
                LblAiProgress.Text = string.Format(Loc.T("qc.err.failed"), ex.Message);
                BtnRunAi.IsEnabled = true;
                return;
            }
            BtnRunAi.IsEnabled = true;
            if (Superseded()) { LblAiProgress.Text = Loc.T("qc.err.fileChanged"); return; }

            // Roles, as agreed: the ACTIVE file always holds the result under
            // review. When it sits in Source, Target is reserved for accepted
            // fixes and the original must come from the second file.
            if (!ResultIsInTarget && _originalMap == null)
            {
                LblAiProgress.Text = Loc.T("qc.err.needOriginal");
                return;
            }

            var scope = BuildScope();
            RefreshRowCounts();
            if (scope.NoOriginal > 0)
                _vm.AddLog(string.Format(Loc.Plural("log.qc.skippedNoOriginal", scope.NoOriginal), scope.NoOriginal),
                           LogLevel.Warning);
            var inputs = scope.Inputs;
            if (inputs.Count == 0) { LblAiProgress.Text = Loc.T("qc.ai.err.empty"); return; }
            var cfg = AppConfig.Instance;
            var options = new QcAiRunner.Options
            {
                SourceLanguage = from,
                TargetLanguage = to,
                Guidance = TbPrompt.Text ?? "",
                BatchSize = cfg.QcBatchSize,
                Parallel = cfg.QcParallelEnabled,
                MaxConcurrent = cfg.QcMaxConcurrentRequests,
                ProtectedTerms = cfg.ProtectedTerms,
            };

            PromptHistory.Record(PromptMode.AIQC, runFile, QcAiRunner.BuildSystemPrompt(options));
            _cache.LoadForPair(from, to);
            _aiCts = new CancellationTokenSource();
            _vm.RegisterQcAiRun(_aiCts); // tab switch / file close stops the run
            BtnRunAi.IsEnabled = false;
            BtnStopAi.IsEnabled = true;
            AiProgress.Maximum = inputs.Count;
            AiProgress.Value = 0;

            var progress = new QcAiRunner.Progress { Total = inputs.Count };
            void Report(QcAiRunner.Progress p) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AiProgress.Value = p.Done;
                string eta = p.EtaSeconds() is { } secs && secs > 0
                    ? string.Format(Loc.T("qc.ai.eta"), FormatEta(secs)) : "";
                LblAiProgress.Text = string.Format(Loc.T("qc.ai.progress"),
                    p.Done, p.Total, p.Proposals, p.CacheHits, p.Rejected, p.Errors) + eta;
            });

            try
            {
                var runner = new QcAiRunner();
                var aiProposals = await runner.RunAsync(inputs, options, _cache, progress, Report,
                    (msg, lvl) => _vm.AddLog(msg, lvl), _aiCts.Token);

                // A cancelled run RETURNS its partial results (no exception), so
                // everything reviewed before Stop lands in the sidecar too.
                QcService.Instance.MergeProposals(aiProposals, runFile, progress.Done);
                _vm.RefreshQcState();
                RefreshResults();

                if (progress.Cancelled)
                    _vm.AddLog(string.Format(Loc.T("log.qc.aiStopped"), aiProposals.Count, progress.Done, progress.Total), LogLevel.Warning);
                else
                    _vm.AddLog(string.Format(Loc.T("log.qc.aiDone"), progress.Done, progress.Total, aiProposals.Count,
                                             progress.CacheHits, progress.Rejected, progress.Errors),
                               progress.Errors > 0 ? LogLevel.Warning : LogLevel.Info);
                Report(progress);
            }
            catch (Exception ex)
            {
                LblAiProgress.Text = string.Format(Loc.T("qc.err.failed"), ex.Message);
                _vm.AddLog(string.Format(Loc.T("log.qc.aiFailed"), ex.Message), LogLevel.Error);
            }
            finally
            {
                _vm.RegisterQcAiRun(null);
                _aiCts?.Dispose();
                _aiCts = null;
                BtnRunAi.IsEnabled = true;
                BtnStopAi.IsEnabled = false;
            }
        }

        private void OnStopAi(object? s, RoutedEventArgs e)
        {
            try { _aiCts?.Cancel(); } catch { }
            BtnStopAi.IsEnabled = false;
            LblAiProgress.Text = Loc.T("qc.ai.stopping");
        }

        /// <summary>
        /// QC-only API settings in their own dialog — deliberately separate from
        /// Settings → AI Speed so reviewing and translating never share knobs.
        /// </summary>
        private async void OnAiSettings(object? s, RoutedEventArgs e)
        {
            try
            {
                var parent = TopLevel.GetTopLevel(this) as Window;
                if (parent == null) return;
                var cfg = AppConfig.Instance;

                var tbBatch = new TextBox { Text = cfg.QcBatchSize.ToString(), Width = 70, Classes = { "form" } };
                var chkParallel = new CheckBox
                {
                    Content = Loc.T("qc.ai.set.parallel"),
                    IsChecked = cfg.QcParallelEnabled,
                };
                var tbConcurrent = new TextBox { Text = cfg.QcMaxConcurrentRequests.ToString(), Width = 70, Classes = { "form" } };

                // Content lists for the live validator's language-leak and terminology
                // checks (they moved there from QC's local pass on 2026-10-21).
                var tbExceptions = new TextBox
                {
                    Text = string.Join(", ", cfg.QcLeakExceptions),
                    Classes = { "form" }, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Height = 52, Width = 190, PlaceholderText = Loc.T("qc.ai.set.exceptions.hint"),
                };
                var tbFixes = new TextBox
                {
                    Text = string.Join("\n", cfg.QcTermFixes),
                    Classes = { "form" }, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Height = 72, Width = 190, PlaceholderText = Loc.T("qc.ai.set.fixes.hint"),
                };

                StackPanel LabeledRow(string key, Control input) => new()
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Loc.T(key),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            Width = 230,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Foreground = Avalonia.Application.Current?.FindResource("TextPrimary") as Avalonia.Media.IBrush,
                        },
                        input,
                    },
                };

                var btnCancel = new Button { Content = Loc.T("prefs.cancel"), Classes = { "subtle" } };
                var btnSave = new Button { Content = Loc.T("prefs.save"), Classes = { "primary" } };
                var body = new StackPanel
                {
                    Margin = new Avalonia.Thickness(16),
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Loc.T("qc.ai.set.hint"),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            FontSize = 11,
                            Foreground = Avalonia.Application.Current?.FindResource("TextMuted") as Avalonia.Media.IBrush,
                        },
                        LabeledRow("qc.ai.set.batch", tbBatch),
                        chkParallel,
                        LabeledRow("qc.ai.set.concurrent", tbConcurrent),
                        LabeledRow("qc.ai.set.exceptions", tbExceptions),
                        LabeledRow("qc.ai.set.fixes", tbFixes),
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Children = { btnCancel, btnSave },
                        },
                    },
                };

                var dlg = new Window
                {
                    Title = Loc.T("qc.ai.settings"),
                    Width = 460,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Avalonia.Media.Brushes.Transparent,
                    WindowDecorations = WindowDecorations.None,
                    Content = new Border
                    {
                        BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#555")),
                        BorderThickness = new Avalonia.Thickness(1),
                        Background = Avalonia.Application.Current?.FindResource("WindowBackground") as Avalonia.Media.IBrush,
                        Child = new DockPanel
                        {
                            Children =
                            {
                                new DialogTitleBar { Title = Loc.T("qc.ai.settings"), [DockPanel.DockProperty] = Dock.Top },
                                body,
                            },
                        },
                    },
                };

                bool saved = false;
                btnCancel.Click += (_, _) => dlg.Close();
                btnSave.Click += (_, _) =>
                {
                    if (int.TryParse(tbBatch.Text, out int b) && b is >= 1 and <= 500) cfg.QcBatchSize = b;
                    cfg.QcParallelEnabled = chkParallel.IsChecked == true;
                    if (int.TryParse(tbConcurrent.Text, out int mc) && mc is >= 1 and <= 32) cfg.QcMaxConcurrentRequests = mc;
                    cfg.QcLeakExceptions = (tbExceptions.Text ?? "")
                        .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    cfg.QcTermFixes = (tbFixes.Text ?? "")
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList();
                    cfg.Save();
                    saved = true;
                    dlg.Close();
                };
                await dlg.ShowDialog(parent);

                // The exceptions and terminology rules feed the live validator; the
                // batch size changes the request estimate.
                if (saved) _vm.RevalidateAllOpenFiles();
                RefreshRowCounts();
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnAiSettings)); }
        }

        private static string FormatEta(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                 : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
                 : $"{t.Seconds}s";
        }

        // ── results / report ─────────────────────────────────────────────

        private void RefreshResults()
        {
            var pending = QcService.Instance.Pending;
            int checkedRows = QcService.Instance.LastCheckedRows;

            int withFix = pending.Count(p => !string.Equals(p.Before, p.After, StringComparison.Ordinal));
            LblResults.Text = pending.Count == 0
                ? Loc.T("qc.results.none")
                : string.Format(Loc.T("qc.results.summary"), pending.Count, checkedRows)
                  + " " + string.Format(Loc.T("qc.results.withFix"), withFix, pending.Count - withFix);

            KindList.ItemsSource = pending
                .SelectMany(p => p.Kinds)
                .GroupBy(k => k)
                .OrderByDescending(g => g.Count())
                .Select(g => $"• {g.Key}: {g.Count()}")
                .ToList();

            LblProgress.Text = QcService.Instance.LastRunUtc is { } t
                ? string.Format(Loc.T("qc.lastRun"), t.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                : "";
        }

        private void OnExportReport(object? s, RoutedEventArgs e)
        {
            if (QcService.Instance.PendingCount == 0) { LblProgress.Text = Loc.T("qc.results.none"); return; }
            try
            {
                var path = _vm.ExportQcReport();
                LblProgress.Text = Path.GetFileName(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { LblProgress.Text = ex.Message; }
        }

        private async void OnClearResults(object? s, RoutedEventArgs e)
        {
            try
            {
                // Destructive and unrecoverable: this drops the whole review, including
                // AI suggestions the user paid for, and deletes the sidecar.
                int n = QcService.Instance.PendingCount;
                if (n > 0 && !await ConfirmAsync(
                        Loc.T("qc.clear.title"),
                        string.Format(Loc.Plural("qc.clear.message", n), n),
                        Loc.T("qc.clear.confirm"),
                        Loc.T("qc.unsaved.cancel")))
                    return;

                QcService.Instance.ClearAll(_vm.AllRows);
                RefreshRowCounts();
                _vm.RefreshQcState();
                RefreshResults();
                _vm.AddLog(string.Format(Loc.Plural("log.qc.suggestionsCleared", n), n), LogLevel.Info);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnClearResults)); }
        }
    }
}
