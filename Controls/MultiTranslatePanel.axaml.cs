using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>One row in the target-language checklist. <see cref="ShowPrompt"/> is
    /// true only when the language is checked AND the panel is in per-language prompt
    /// mode, so each selected language reveals its own instruction box.</summary>
    public partial class LanguageChoice : ObservableObject
    {
        public string Name { get; init; } = "";
        public bool IsCustom { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPrompt))]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowPrompt))]
        private bool _separateMode;

        [ObservableProperty]
        private string _prompt = "";

        public bool ShowPrompt => IsSelected && SeparateMode;
    }

    /// <summary>A written output file, shown in the results list with an "Open" button.</summary>
    public sealed class ResultEntry
    {
        public string Path { get; init; } = "";
        public string Display { get; init; } = "";
    }

    /// <summary>
    /// Etap 3 — multi-language batch translation UI. Drives <see cref="MultiTranslateJob"/>:
    /// pick a source language, target languages (+ custom locale codes), a shared or
    /// per-language instruction prompt, and an output folder; shows a unit-count cost
    /// preview before starting; streams progress; lists result files with "Open".
    /// </summary>
    public partial class MultiTranslatePanel : UserControl
    {
        private MainWindowViewModel? _vm;
        private CancellationTokenSource? _cts;
        private readonly ObservableCollection<LanguageChoice> _languages = new();
        private readonly ObservableCollection<ResultEntry> _results = new();

        public event Action? ClosePanelRequested;

        private static readonly string[] BuiltInLanguages =
        {
            "English","Ukrainian","Arabic","Chinese","Czech","Danish","Dutch","Finnish","French",
            "German","Greek","Hebrew","Hungarian","Indonesian","Italian","Japanese","Korean",
            "Norwegian","Polish","Portuguese","Romanian","Russian","Spanish","Swedish","Thai","Turkish","Vietnamese"
        };

        public MultiTranslatePanel() => InitializeComponent();
        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Wire the panel to its VM and populate the language pickers. Idempotent —
        /// safe to call once on load (the source-text comes live from the VM at Start).</summary>
        public void Initialize(MainWindowViewModel vm)
        {
            _vm = vm;
            if (this.FindControl<ComboBox>("CbSource") is { } src)
            {
                src.ItemsSource = BuiltInLanguages;
                src.SelectedIndex = 0;
            }
            if (_languages.Count == 0)
                foreach (var l in BuiltInLanguages)
                    _languages.Add(new LanguageChoice { Name = l });

            if (this.FindControl<ItemsControl>("LangList") is { } ll) ll.ItemsSource = _languages;
            if (this.FindControl<ItemsControl>("ResultList") is { } rl) rl.ItemsSource = _results;
            if (this.FindControl<CheckBox>("ChkSendComments") is { } cc) cc.IsChecked = AppConfig.Instance.SendCommentsToAi;
        }

        private void OnClosePanel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();

        private void OnSeparatePromptsChanged(object? s, RoutedEventArgs e)
        {
            bool separate = this.FindControl<CheckBox>("ChkSeparatePrompts")?.IsChecked == true;
            if (this.FindControl<StackPanel>("SharedPromptPanel") is { } sp) sp.IsVisible = !separate;
            foreach (var l in _languages) l.SeparateMode = separate;
        }

        private void OnCustomKeyDown(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddCustom(); e.Handled = true; }
        }
        private void OnAddCustom(object? s, RoutedEventArgs e) => AddCustom();

        private void AddCustom()
        {
            var tb = this.FindControl<TextBox>("TbCustom");
            var code = tb?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(code)) return;
            // The locale becomes a literal output sub-folder name, so it must be a
            // valid path segment — reject / \ : * ? and reserved device names, else
            // the language silently fails or escapes the output folder.
            if (!IsValidLocale(code))
            {
                SetProgress(string.Format(Loc.T("mtl.badLocale"), code));
                return;
            }
            if (_languages.Any(l => string.Equals(l.Name, code, StringComparison.OrdinalIgnoreCase)))
            {
                if (tb != null) tb.Text = "";
                return; // already in the list — don't duplicate
            }
            bool separate = this.FindControl<CheckBox>("ChkSeparatePrompts")?.IsChecked == true;
            _languages.Add(new LanguageChoice { Name = code, IsCustom = true, IsSelected = true, SeparateMode = separate });
            if (tb != null) tb.Text = "";
        }

        private static readonly System.Collections.Generic.HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        private static bool IsValidLocale(string c) =>
            c.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && c.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0
            && c.Trim() == c && !c.EndsWith(".")
            && !ReservedNames.Contains(c);

        private async void OnBrowseFolder(object? s, RoutedEventArgs e)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top == null) return;
                var res = await top.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = Loc.T("mtl.outputFolder"), AllowMultiple = false });
                if (res != null && res.Count > 0 && this.FindControl<TextBox>("TbFolder") is { } tf)
                    tf.Text = res[0].Path.LocalPath;
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnBrowseFolder)); }
        }

        private async void OnStart(object? s, RoutedEventArgs e)
        {
            if (_vm == null) return;
            if (!_vm.HasData) { SetProgress(Loc.T("mtl.noFile")); return; }
            if (_vm.ActiveAiTranslation != null) { SetProgress(Loc.T("mtl.alreadyRunning")); return; }

            string srcLang = this.FindControl<ComboBox>("CbSource")?.SelectedItem?.ToString() ?? "";
            string folder = this.FindControl<TextBox>("TbFolder")?.Text ?? "";
            bool separate = this.FindControl<CheckBox>("ChkSeparatePrompts")?.IsChecked == true;
            string shared = this.FindControl<TextBox>("TbSharedPrompt")?.Text ?? "";
            bool sendComments = this.FindControl<CheckBox>("ChkSendComments")?.IsChecked == true;
            if (AppConfig.Instance.SendCommentsToAi != sendComments)
            {
                AppConfig.Instance.SendCommentsToAi = sendComments;
                AppConfig.Instance.Save();
            }

            if (string.IsNullOrWhiteSpace(srcLang)) { SetProgress(Loc.T("mtl.needSource")); return; }
            if (string.IsNullOrWhiteSpace(folder)) { SetProgress(Loc.T("mtl.needFolder")); return; }

            var selected = _languages
                .Where(l => l.IsSelected && !string.Equals(l.Name, srcLang, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (selected.Count == 0) { SetProgress(Loc.T("mtl.needTargets")); return; }

            // Re-validate every target's locale (guards any code path that bypassed AddCustom).
            var bad = selected.FirstOrDefault(l => !IsValidLocale(l.Name));
            if (bad != null) { SetProgress(string.Format(Loc.T("mtl.badLocale"), bad.Name)); return; }

            var req = new MultiTranslateRequest
            {
                SourceRows = _vm.AllRows.ToList(),
                SourceLanguage = srcLang,
                Targets = selected
                    .Select(l => new MultiTranslateTarget { Locale = l.Name, Prompt = separate ? l.Prompt : shared })
                    .ToList(),
                OutputFolder = folder,
                Format = _vm.CurrentFormat,
                OriginalFileName = string.IsNullOrEmpty(_vm.CurrentFileName) ? "translated.csv" : _vm.CurrentFileName,
                BaseDocument = _vm.CurrentLocresDocument,
                BatchSize = AppConfig.Instance.TranslationBatchSize,
                UseTranslationMemory = AppConfig.Instance.TranslationMemoryEnabled,
                SaveTranslationMemory = AppConfig.Instance.TranslationMemoryEnabled,
                SendComments = sendComments,
                SkipExisting = false,
            };

            var job = new MultiTranslateJob();

            // Claim the single-run slot SYNCHRONOUSLY (before any await) so a fast
            // second click can't start a parallel run that clobbers the same output.
            _cts = new CancellationTokenSource();
            var cts = _cts;
            _vm.ActiveAiTranslation = new MainWindowViewModel.AiTranslationHandle(cts, () => { });
            // Disable Start immediately (the estimate + confirm window is async) so
            // it doesn't look clickable; the finally re-enables on any exit path.
            SetButtons(running: true);

            MultiTranslateResult? result = null;
            try
            {
                // Off the UI thread: the estimate loads a TM file per language + a
                // lookup per unique source, which would jank the UI on big files.
                int est = await Task.Run(() => job.EstimateUnits(req));

                // Cost preview → confirm before spending API calls. Warn if any
                // target's output file already exists (re-run silently overwrites).
                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    int existing = selected.Count(l => File.Exists(Path.Combine(folder, l.Name, req.OriginalFileName)));
                    string body = string.Format(Loc.Plural("mtl.confirm.body", selected.Count), est, selected.Count);
                    if (existing > 0) body += string.Format(Loc.Plural("mtl.confirm.overwrite", existing), existing);
                    bool ok = await ConfirmDialog.ShowAsync(owner,
                        Loc.T("mtl.confirm.title"), body,
                        Loc.T("mtl.confirm.start"), Loc.T("common.cancel"));
                    if (!ok) return; // finally releases the slot + re-enables Start
                }

                _results.Clear();
                if (this.FindControl<StackPanel>("ResultsPanel") is { } rp) rp.IsVisible = false;

                job.Log += msg => Dispatcher.UIThread.Post(() => _vm.AddLog(msg));
                job.ProgressChanged += p => Dispatcher.UIThread.Post(() =>
                {
                    if (this.FindControl<ProgressBar>("Progress") is { } pb)
                    {
                        // Fall back to the language counter when there are no units
                        // (all-cached / letterless) so the bar still advances.
                        if (p.UnitsTotal > 0) { pb.Maximum = p.UnitsTotal; pb.Value = p.UnitsDone; }
                        else { pb.Maximum = Math.Max(1, p.LanguagesTotal); pb.Value = p.LanguagesDone; }
                    }
                    SetProgress($"{p.CurrentLocale} · {p.Phase} · {Loc.T("mtl.lang")} {p.LanguagesDone}/{p.LanguagesTotal} · {p.UnitsDone}/{p.UnitsTotal}");
                });

                PromptHistory.Record(PromptMode.Multitranslation, req.OriginalFileName,
                    MultiTranslateJob.DescribePrompts(req));
                result = await Task.Run(() => job.RunAsync(req, cts.Token));
            }
            catch (Exception ex)
            {
                _vm.AddLog(string.Format(Loc.T("log.mtl.failed"), ex.Message), LogLevel.Error);
                SetProgress(ex.Message);
            }
            finally
            {
                _vm.ActiveAiTranslation = null;
                _cts = null;
                SetButtons(running: false);
                // A multi-language run churns through large transient buffers (per-language
                // maps, CSV/locres temp data). Reclaim so RAM drops back after it finishes.
                MainWindowViewModel.ReclaimMemory();
            }

            if (result == null) return;

            foreach (var f in result.WrittenFiles)
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                _results.Add(new ResultEntry { Path = f, Display = $"{parent}/{Path.GetFileName(f)}" });
            }
            if (_results.Count > 0 && this.FindControl<StackPanel>("ResultsPanel") is { } rp2)
            {
                rp2.IsVisible = true;
                // Bring the freshly-written results into view (they sit at the
                // bottom of the scrollable panel).
                this.FindControl<ScrollViewer>("PageScroll")?.ScrollToEnd();
            }

            string summary = result.Cancelled
                ? Loc.T("mtl.stopped")
                : string.Format(Loc.T("mtl.done"), result.WrittenFiles.Count, result.FailedLocales.Count, result.SkippedLocales.Count);
            if (result.PartialLocales.Count > 0)
                summary += $"  {Utils.Glyphs.Warning} PARTIAL: {string.Join(", ", result.PartialLocales)} — re-run to finish";
            SetProgress(summary);
            ToastService.Instance.ShowSuccess(summary);
        }

        private void OnStop(object? s, RoutedEventArgs e) => _cts?.Cancel();

        private async void OnOpenResult(object? s, RoutedEventArgs e)
        {
            try
            {
                if (_vm != null && s is Control { Tag: string path } && !string.IsNullOrEmpty(path))
                    await _vm.OpenFileAsync(path);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnOpenResult)); }
        }

        private void SetButtons(bool running)
        {
            if (this.FindControl<Button>("BtnStart") is { } b1) b1.IsEnabled = !running;
            if (this.FindControl<Button>("BtnStop") is { } b2) b2.IsEnabled = running;
        }

        private void SetProgress(string text)
        {
            if (this.FindControl<TextBlock>("LblProgress") is { } l) l.Text = text;
        }
    }
}
