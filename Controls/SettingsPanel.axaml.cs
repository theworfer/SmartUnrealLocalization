using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Embeddable Settings side-panel. Replaces the old floating PreferencesWindow.
    /// Save and Cancel both raise <see cref="ClosePanelRequested"/> so the host
    /// (MainWindow) collapses the side-panel area; Save also persists AppConfig and
    /// reloads <see cref="Loc"/> if the language changed.
    /// </summary>
    public partial class SettingsPanel : UserControl
    {
        public event Action? ClosePanelRequested;

        public SettingsPanel()
        {
            InitializeComponent();
            BuildAccentPicker();
            LoadConfig();

            // The language can now also be changed from the header, and this
            // panel may be open while that happens. Without this the picker
            // would still be showing the previous language, and OnSave would
            // faithfully write it back — silently undoing the switch the user
            // just made two inches above.
            this.RefreshOnLanguageChange(SyncLanguagePicker);
        }

        private void SyncLanguagePicker()
        {
            if (CbLanguage.ItemsSource is not IEnumerable<LanguageOption> items) return;
            var match = items.FirstOrDefault(i => i.Code == AppConfig.Instance.Language);
            if (match is not null) CbLanguage.SelectedItem = match;
        }

        private void LoadConfig()
        {
            var c = AppConfig.Instance;

            ChkTM.IsChecked = c.TranslationMemoryEnabled;
            TbGlossary.Text = string.Join(Environment.NewLine, c.ProtectedTerms);

            ChkCaching.IsChecked = c.PromptCachingEnabled;
            ChkParallel.IsChecked = c.ParallelTranslationEnabled;
            TbMaxConcurrent.Text = c.MaxConcurrentRequests.ToString();
            ChkAutoRetry.IsChecked = c.AutoRetryEnabled;
            ChkMinDelay.IsChecked = c.MinRequestDelayEnabled;
            TbMinDelayMs.Text = c.MinRequestDelayMs.ToString();
            ChkMcpServer.IsChecked = c.McpServerEnabled;
            ChkMergeKeyOnly.IsChecked = c.MergeMatchByKeyOnly;

            ChkAutoSave.IsChecked = c.AutoSaveEnabled;
            TbAutoSaveInterval.Text = c.AutoSaveIntervalMinutes.ToString();
            TbAutoSavePath.Text = c.AutoSavePath;
            TbExportPath.Text = c.ExportPath;
            CbSaveAsBehavior.SelectedIndex = (int)c.SaveAsBehavior;

            TbModelName.Text = c.ModelName;
            TbApiKey.Text = c.ApiKey;
            TbBaseUrl.Text = c.BaseUrl;

            ChkShowFullKeyHashes.IsChecked = c.ShowFullKeyHashes;
            ChkCheckWhitespace.IsChecked = c.CheckEdgeWhitespace;
            ChkCheckPunctuation.IsChecked = c.CheckEndingPunctuation;
            ChkCheckLength.IsChecked = c.CheckLengthRatio;
            ChkCheckDuplicates.IsChecked = c.CheckDuplicateSources;
            CbDefaultExportFormat.SelectedIndex = c.DefaultExportFormat switch
            {
                FileFormat.Locres => 0,
                FileFormat.Po => 1,
                FileFormat.Csv => 2,
                _ => 0,
            };

            var codes = Loc.Instance.AvailableLanguages()
                .OrderBy(code => code)
                .ToList();
            if (!codes.Contains("en")) codes.Insert(0, "en");
            // The name comes from each language's own file ("_meta.name"), not from
            // a table here — a table is what gets forgotten when a language is added.
            var items = codes.Select(code => new LanguageOption(code, Loc.Instance.NativeName(code))).ToList();
            CbLanguage.ItemsSource = items;
            CbLanguage.SelectedItem = items.FirstOrDefault(i => i.Code == c.Language) ?? items.FirstOrDefault();
        }

        private record LanguageOption(string Code, string Display)
        {
            public override string ToString() => Display;
        }

        private static void ScrollToSection(Control section) => section.BringIntoView();

        private void OnGoAiProvider(object? s, RoutedEventArgs e) => ScrollToSection(SectionAiProvider);
        private void OnGoEditor(object? s, RoutedEventArgs e) => ScrollToSection(SectionEditor);
        private void OnGoFiles(object? s, RoutedEventArgs e) => ScrollToSection(SectionFiles);
        private void OnGoValidation(object? s, RoutedEventArgs e) => ScrollToSection(SectionValidation);
        private void OnGoLanguage(object? s, RoutedEventArgs e) => ScrollToSection(SectionLanguage);
        private void OnGoTm(object? s, RoutedEventArgs e) => ScrollToSection(SectionTm);
        private void OnGoAiSpeed(object? s, RoutedEventArgs e) => ScrollToSection(SectionAiSpeed);
        private void OnGoAdvanced(object? s, RoutedEventArgs e) => ScrollToSection(SectionAdvanced);

        private void OnClosePanel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();

        /// <summary>
        /// Test Connection — ping the configured provider with a tiny prompt and
        /// surface success/latency or the error in the status label. Uses an in-memory
        /// AppConfig snapshot of the fields currently in the form so unsaved edits
        /// (Model Name, API Key, Base URL) are respected.
        /// </summary>
        private async void OnTestConnection(object? s, RoutedEventArgs e)
        {
            BtnTestConnection.IsEnabled = false;
            LblTestStatus.Text = Loc.T("set.testing");
            LblTestStatus.Foreground = Avalonia.Application.Current?
                .FindResource("TextMuted") as Avalonia.Media.IBrush;

            // Snapshot live config to avoid leaking unsaved edits into AppConfig.
            var cfg = AppConfig.Instance;
            string prevApi = cfg.ApiKey, prevModel = cfg.ModelName, prevBase = cfg.BaseUrl;
            cfg.ApiKey = TbApiKey.Text ?? "";
            cfg.ModelName = TbModelName.Text ?? "";
            cfg.BaseUrl = TbBaseUrl.Text ?? "";

            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await TranslatorService.TranslateAsync(
                    prompt: "[{\"id\":\"0\",\"text\":\"ping\"}]",
                    systemPrompt: "You are a connectivity test. Reply with the same JSON array verbatim.",
                    ct: cts.Token);
                sw.Stop();
                LblTestStatus.Text = string.Format(Loc.T("set.testOk"), sw.ElapsedMilliseconds);
                LblTestStatus.Foreground = Avalonia.Application.Current?
                    .FindResource("SuccessColor") as Avalonia.Media.IBrush;
            }
            catch (Exception ex)
            {
                sw.Stop();
                LblTestStatus.Text = string.Format(Loc.T("set.testFail"), ex.Message);
                LblTestStatus.Foreground = Avalonia.Application.Current?
                    .FindResource("DangerColor") as Avalonia.Media.IBrush;
            }
            finally
            {
                // Restore previous AppConfig values — the user hasn't clicked Save yet.
                cfg.ApiKey = prevApi;
                cfg.ModelName = prevModel;
                cfg.BaseUrl = prevBase;
                BtnTestConnection.IsEnabled = true;
            }
        }

        private void OnSave(object? s, RoutedEventArgs e)
        {
            var c = AppConfig.Instance;

            c.TranslationMemoryEnabled = ChkTM.IsChecked == true;
            c.ProtectedTerms = TbGlossary.Text?.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(t => t.Trim())
                                               .Where(t => !string.IsNullOrEmpty(t))
                                               .ToList() ?? new();

            c.PromptCachingEnabled = ChkCaching.IsChecked == true;
            c.ParallelTranslationEnabled = ChkParallel.IsChecked == true;
            if (int.TryParse(TbMaxConcurrent.Text, out int mc) && mc > 0) c.MaxConcurrentRequests = mc;
            c.AutoRetryEnabled = ChkAutoRetry.IsChecked == true;
            c.MinRequestDelayEnabled = ChkMinDelay.IsChecked == true;
            if (int.TryParse(TbMinDelayMs.Text, out int md) && md > 0) c.MinRequestDelayMs = md;
            c.McpServerEnabled = ChkMcpServer.IsChecked == true;
            c.MergeMatchByKeyOnly = ChkMergeKeyOnly.IsChecked == true;
            Services.Mcp.McpGuiServer.Instance.SetEnabled(c.McpServerEnabled); // live-apply

            c.AutoSaveEnabled = ChkAutoSave.IsChecked == true;
            if (int.TryParse(TbAutoSaveInterval.Text, out int interval) && interval > 0)
                c.AutoSaveIntervalMinutes = interval;
            c.AutoSavePath = TbAutoSavePath.Text ?? "";
            c.ExportPath = TbExportPath.Text ?? "";
            c.SaveAsBehavior = CbSaveAsBehavior.SelectedIndex switch
            {
                1 => SaveAsBehavior.ExportFolder,
                2 => SaveAsBehavior.AutosaveFolder,
                _ => SaveAsBehavior.AlwaysAsk,
            };

            c.ModelName = TbModelName.Text ?? "";
            c.ApiKey = TbApiKey.Text ?? "";
            c.BaseUrl = TbBaseUrl.Text ?? "";

            c.ShowFullKeyHashes = ChkShowFullKeyHashes.IsChecked == true;

            bool checksChanged =
                c.CheckEdgeWhitespace != (ChkCheckWhitespace.IsChecked == true)
                || c.CheckEndingPunctuation != (ChkCheckPunctuation.IsChecked == true)
                || c.CheckLengthRatio != (ChkCheckLength.IsChecked == true)
                || c.CheckDuplicateSources != (ChkCheckDuplicates.IsChecked == true);
            c.CheckEdgeWhitespace = ChkCheckWhitespace.IsChecked == true;
            c.CheckEndingPunctuation = ChkCheckPunctuation.IsChecked == true;
            c.CheckLengthRatio = ChkCheckLength.IsChecked == true;
            c.CheckDuplicateSources = ChkCheckDuplicates.IsChecked == true;
            c.DefaultExportFormat = CbDefaultExportFormat.SelectedIndex switch
            {
                1 => FileFormat.Po,
                2 => FileFormat.Csv,
                _ => FileFormat.Locres,
            };

            if (CbLanguage.SelectedItem is LanguageOption opt && opt.Code != c.Language)
            {
                c.Language = opt.Code;
                Loc.Instance.Load(opt.Code);
                Services.ThemeManager.ApplyFonts();
            }

            c.Save();
            // Apply timer-backed settings immediately (interval/enabled used to
            // require an app restart to take effect).
            ViewModels.MainWindowViewModel.Current?.RestartAutoSave();
            if (checksChanged) ViewModels.MainWindowViewModel.Current?.RevalidateAllOpenFiles();
            ClosePanelRequested?.Invoke();
        }

        private void OnCancel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();

        // ── Accent colour picker ─────────────────────────────────────────────
        // The colour applies while it is being picked (ThemeManager.SetAccent), so
        // the whole window is the preview. Hue and saturation are the user's; the
        // lightness is the theme's (AccentPalette fits it so text stays readable).

        private bool _syncingAccent;

        private void BuildAccentPicker()
        {
            foreach (var hex in AccentPalette.Presets)
            {
                if (!AccentPalette.TryParse(hex, out var color)) continue;
                var swatch = new Button
                {
                    Classes = { "accentSwatch" },
                    Tag = hex,
                    Content = new Avalonia.Controls.Shapes.Ellipse
                    {
                        Width = 22, Height = 22,
                        Fill = new Avalonia.Media.SolidColorBrush(color),
                    },
                };
                swatch.Click += (_, _) => { ThemeManager.SetAccent(hex, persist: false); SyncAccentPicker(); };
                AccentPresets.Children.Add(swatch);
            }

            var strip = new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 0, Avalonia.RelativeUnit.Relative),
            };
            for (int i = 0; i <= 6; i++)
                strip.GradientStops.Add(new Avalonia.Media.GradientStop(AccentPalette.FromHsl(i * 60, 1, 0.5), i / 6.0));
            AccentHueStrip.Background = strip;
        }

        /// <summary>Show the accent in effect in the sliders and the hex box.</summary>
        private void SyncAccentPicker()
        {
            _syncingAccent = true;
            try
            {
                AccentPalette.ToHsl(ThemeManager.Accent.Accent, out double h, out double s, out _);
                SlAccentHue.Value = h;
                SlAccentSaturation.Value = s * 100;
                TbAccentHex.Text = ThemeManager.CustomAccent;
            }
            finally { _syncingAccent = false; }
        }

        private void OnAccentPickerOpened(object? s, EventArgs e) => SyncAccentPicker();

        // Slider steps do not save; closing the picker does, once.
        private void OnAccentPickerClosed(object? s, EventArgs e) => AppConfig.Instance.Save();

        private void OnAccentSliderChanged(object? s, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_syncingAccent) return;
            var color = AccentPalette.FromHsl(SlAccentHue.Value, SlAccentSaturation.Value / 100.0, 0.5);
            ThemeManager.SetAccent(AccentPalette.ToHex(color), persist: false);
            _syncingAccent = true;
            try { TbAccentHex.Text = ThemeManager.CustomAccent; }
            finally { _syncingAccent = false; }
        }

        private void OnAccentHexKeyDown(object? s, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter) OnAccentHexCommitted(s, e);
        }

        private void OnAccentHexCommitted(object? s, RoutedEventArgs e)
        {
            if (_syncingAccent) return;
            // Anything that is not #RRGGBB leaves the accent as it was.
            if (AccentPalette.TryParse(TbAccentHex.Text, out _))
                ThemeManager.SetAccent(TbAccentHex.Text, persist: false);
            SyncAccentPicker();
        }

        private void OnAccentDefault(object? s, RoutedEventArgs e)
        {
            ThemeManager.SetAccent(null);
            SyncAccentPicker();
        }

        /// <summary>Raised after Reset cache ran, so the window can drop what other
        /// panels still hold in memory (a loaded Translation Memory would otherwise
        /// be written straight back by the next run).</summary>
        public event Action? ProfileResetDone;

        /// <summary>Test seam: answers the two confirmations instead of showing dialogs.</summary>
        internal Func<string, string, string, Task<bool>>? ConfirmOverride { get; set; }

        private Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            if (ConfirmOverride != null) return ConfirmOverride(title, message, confirmLabel);
            return TopLevel.GetTopLevel(this) is Window owner
                ? ConfirmDialog.ShowAsync(owner, title, message, confirmLabel, Loc.T("common.cancel"))
                : Task.FromResult(false);
        }

        /// <summary>
        /// Reset cache: Translation Memory, prompt history, AI review cache and the
        /// settings. Two confirmations — the first lists exactly what goes, with
        /// counts; the second says it cannot be undone.
        /// </summary>
        private async void OnResetCache(object? s, RoutedEventArgs e)
        {
            try { await ResetCacheAsync(); }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnResetCache)); }
        }

        internal async Task ResetCacheAsync()
        {
            if (ViewModels.MainWindowViewModel.Current?.ActiveAiTranslation is not null)
            {
                LblResetCache.Text = Loc.T("set.reset.busy");
                return;
            }

            string dir = AppConfig.ConfigDir();
            int tm = System.IO.Directory.GetFiles(dir, "translation_memory*.json").Length;
            int qc = System.IO.Directory.GetFiles(dir, "qc_cache_*.json").Length;
            int prompts = PromptHistory.LoadAll().Count;

            string title = Loc.T("set.reset.title");
            if (!await ConfirmAsync(title, string.Format(Loc.T("set.reset.confirm1"), tm, prompts, qc),
                                    Loc.T("set.reset.confirm1.ok"))) return;
            if (!await ConfirmAsync(title, Loc.T("set.reset.confirm2"), Loc.T("set.reset.confirm2.ok"))) return;

            int deleted = ProfileReset.Run();

            // The settings changed underneath everything that mirrors them.
            Services.Mcp.McpGuiServer.Instance.SetEnabled(AppConfig.Instance.McpServerEnabled);
            LoadConfig();
            ProfileResetDone?.Invoke();
            LblResetCache.Text = string.Format(Loc.T("set.reset.done"), deleted);
        }

        /// <summary>
        /// Scroll to the Advanced section. Called from Tools -> Advanced settings,
        /// which opens this panel and jumps here rather than making the user hunt
        /// for the bottom of a long list.
        /// </summary>
        public void ScrollToAdvanced()
        {
            // Deferred: the panel is usually made visible in the same click, and a
            // ScrollViewer cannot scroll to a child it has not measured yet.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ScrollToSection(SectionAdvanced),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void OnCreateMcpBundle(object? s, RoutedEventArgs e)
        {
            try
            {
                var path = Services.Mcp.McpBundle.Create();
                LblMcpBundle.Text = System.IO.Path.GetFileName(path);
                // Reveal the bundle so the user can double-click it into Claude Desktop.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                LblMcpBundle.Text = ex.Message;
            }
        }

        private async void OnBrowseAutoSavePath(object? s, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Loc.T("pick.autosaveDir"),
                    AllowMultiple = false
                });

                if (result != null && result.Count > 0)
                {
                    TbAutoSavePath.Text = result[0].Path.LocalPath;
                }
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnBrowseAutoSavePath)); }
        }

        private void OnOpenAutoSaveFolder(object? s, RoutedEventArgs e)
            => OpenFolder(TbAutoSavePath.Text);

        private async void OnBrowseExportPath(object? s, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Loc.T("pick.exportDir"),
                    AllowMultiple = false
                });

                if (result != null && result.Count > 0)
                {
                    TbExportPath.Text = result[0].Path.LocalPath;
                }
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnBrowseExportPath)); }
        }

        private void OnOpenExportFolder(object? s, RoutedEventArgs e)
            => OpenFolder(TbExportPath.Text);

        private static void OpenFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch { }
        }
    }
}
