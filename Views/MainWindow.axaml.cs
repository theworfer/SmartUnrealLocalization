using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Views
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel VM => (MainWindowViewModel)DataContext!;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closing += OnClosing;
            KeyDown += OnGlobalKeyDown;
            // Tunnel: runs BEFORE the focused TextBox and the window's KeyBindings.
            AddHandler(KeyDownEvent, OnEditorKeysTunnel, RoutingStrategies.Tunnel);
        }

        /// <summary>
        /// Keys while a text field has focus.
        ///
        /// <para>In the Target editor: Enter commits and moves to the next row,
        /// Shift+Enter is a line break, Tab commits and moves on, Esc cancels,
        /// Ctrl+T puts the row's Source into the editor. It used to be the editor's
        /// bubbling KeyDown, which never ran for Enter: the TextBox (AcceptsReturn)
        /// consumed it first, so Enter inserted a line break and an edit only
        /// committed on focus loss.</para>
        ///
        /// <para>In ANY text field, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z are that
        /// field's own undo and redo. The window's KeyBindings won before: Ctrl+Z
        /// while typing left the typing alone and silently reverted a committed
        /// edit in some other row (found 2026-10-20).</para>
        /// </summary>
        private void OnEditorKeysTunnel(object? sender, KeyEventArgs e)
        {
            if (FocusManager?.GetFocusedElement() is not TextBox tb) return;
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (ctrl && !alt && (e.Key == Key.Z || e.Key == Key.Y))
            {
                bool redo = e.Key == Key.Y || shift;
                if (redo) { if (tb.CanRedo) tb.Redo(); }
                else if (tb.CanUndo) tb.Undo();
                e.Handled = true;   // never the window's Undo while a field has the caret
                return;
            }

            if (!tb.Classes.Contains("gridEditor") || tb.DataContext is not TranslationRow row || !row.IsEditing) return;

            switch (e.Key)
            {
                case Key.Enter when shift && !ctrl:
                    return;   // the TextBox inserts the line break itself
                case Key.Enter:
                    CommitTargetEdit(row, advance: !ctrl);   // Ctrl+Enter commits and stays
                    MainDataGrid.Focus();
                    e.Handled = true;
                    return;
                case Key.Tab:
                    CommitTargetEdit(row, advance: !shift);
                    MainDataGrid.Focus();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CancelTargetEdit(row);
                    MainDataGrid.Focus();
                    e.Handled = true;
                    return;
                case Key.T when ctrl && !shift:
                    // The editor holds the text: Copy Source → Target goes INTO it,
                    // as an edit the field's own Ctrl+Z can take back.
                    tb.SelectAll();
                    tb.SelectedText = row.Source ?? "";
                    e.Handled = true;
                    return;
                case Key.T when ctrl:
                    e.Handled = true;   // the multi-row Ctrl+Shift+T makes no sense mid-edit
                    return;
            }
        }

        // The app intentionally starts CLEAN — no auto-reopening of the last
        // session's tabs (the Welcome page's Recent list covers getting back).
        // VM.RestoreSessionsAsync still exists if a "reopen last session"
        // action is ever wanted.

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            VM.OpenFileRequested += async (title, filter) => await PickOpenFile(title, filter);
            VM.SaveFileRequested += async (title, filter) => await PickSaveFile(title, filter);

            // Merge asks the user two things: an explanation it must accept before
            // picking the new file, and the match report before anything is written.
            VM.ConfirmRequested += (title, message, confirmLabel) =>
                ConfirmDialog.ShowAsync(this, title, message, confirmLabel, Loc.T("common.cancel"));
            VM.MergeReportRequested += (result, oldFile, newFile) =>
                MergeReportDialog.ShowAsync(this, result, oldFile, newFile);
            VM.OpenFolderRequested += path =>
            {
                // After a successful save: a shell that refuses (policy, a folder removed
                // meanwhile) must not turn the save into a crash.
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
                catch (Exception ex) { Console.WriteLine($"[shell] open folder: {ex.Message}"); }
            };

            // MCP bridge: attach the live window, then honor the persisted setting.
            // run_ai_translation goes through the SAME panel pipeline as the button.
            SmartUnrealLocalization.Services.Mcp.McpGuiServer.Instance.Attach(VM, (from, to, scope, useTm) =>
            {
                AiTranslatePanelHost.Initialize(VM);
                VM.ShowAiPanel = true;
                return AiTranslatePanelHost.McpTryStartRun(from, to, scope, useTm);
            });
            SmartUnrealLocalization.Services.Mcp.McpGuiServer.Instance.SetEnabled(AppConfig.Instance.McpServerEnabled);

            VM.Logs.CollectionChanged += (s, ev) =>
            {
                if (LogListBox != null && VM.Logs.Count > 0)
                {
                    var last = VM.Logs.LastOrDefault();
                    if (last != null)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            ListReveal.Reveal(LogListBox, last));
                    }
                }
            };

            // Theme handling — sync icon, re-sync when toggled.
            UpdateThemeIcon();
            ThemeManager.ThemeChanged += UpdateThemeIcon;
            ThemeManager.ThemeChanged += OnThemeChanged;

            // Etap 2: open the blocking ValidationProgressDialog after a file
            // is loaded + review-sidecar hydrated. Returns true on cancel so
            // the VM can clear partial Problems.
            VM.InitialValidationRequested += OnInitialValidationRequested;
            // Every validator fix is shown before it is written (FixReviewDialog).
            VM.FixReviewRequested += (fixes, refused) => Controls.FixReviewDialog.ShowAsync(this, fixes, refused);
            MenuHelpDocs.IsEnabled = UserGuidePath() != null;

            // Etap 1.1: confirm before closing a tab that has unsaved edits.
            VM.CloseTabConfirmRequested += session =>
                Controls.ConfirmDialog.ShowAsync(this,
                    Loc.T("tabs.unsaved.title"),
                    string.Format(Loc.T("tabs.unsaved.tab"), session.DisplayName),
                    Loc.T("common.closeAnyway"),
                    Loc.T("common.cancel"));

            // Etap 6: wire the singleton ProblemPopoverService to the
            // ProblemPopover instance hosted at the bottom of the visual tree.
            // Status-icon and wavy-underline hovers ask the service to open it.
            Services.ProblemPopoverService.Instance.Attach(ProblemPopoverHost);

            // Sync status column visibility when counts change.
            VM.PropertyChanged += OnVmPropertyChanged;
            SyncStatusColumnVisibility();
            SyncActivityBarState();

            // Side panels: route their close requests back to the VM toggles.
            AiTranslatePanelHost.ClosePanelRequested += () => VM.ShowAiPanel = false;
            StatusPanelHost.ClosePanelRequested += () => VM.ShowStatusPanel = false;
            SettingsPanelHost.ClosePanelRequested += () => VM.ShowSettingsPanel = false;
            MultiTranslatePanelHost.ClosePanelRequested += () => VM.ShowMultiTranslatePanel = false;
            QcPanelHost.ClosePanelRequested += () => VM.ShowQcPanel = false;
            // Etap 9: FindBar replaces the old FindReplacePanel modal. Scroll
            // requests bubble up from the VM as `FindBringIntoViewRequested`
            // and ScrollIntoView only works on the filtered Rows collection
            // (DataGrid binds to that, not _allRows). If the target row is
            // currently hidden by an active filter we just skip the scroll
            // and leave the cell highlight in place for when the filter clears.
            VM.FindBringIntoViewRequested += row =>
            {
                if (MainDataGrid is null) return;
                // Only select + scroll rows that are actually in the (filtered)
                // grid source. Assigning SelectedItem to a filtered-out row would
                // just clear the current selection, so skip it and leave the cell
                // highlight in place for when the filter clears.
                if (VM.Rows.Contains(row))
                {
                    MainDataGrid.SelectedItem = row;
                    MainDataGrid.ScrollIntoView(row, null);
                }
            };
            // Etap 10: Problems-tab [Go] / double-click reveals the row — same
            // visible-rows constraint as find (a filtered-out row stays put).
            VM.RevealRowRequested += row =>
            {
                // The view model only raises this for a row the grid lists (it asks
                // before clearing a filter that hides one). The grid may have been
                // rebuilt a moment ago, so it is laid out before the scroll.
                if (MainDataGrid is null || !VM.Rows.Contains(row)) return;
                VM.SetFocusedRow(row);
                MainDataGrid.SelectedItem = row;
                MainDataGrid.UpdateLayout();
                MainDataGrid.ScrollIntoView(row, null);
            };
            VM.YesNoRequested += (title, message) =>
                ConfirmDialog.ShowAsync(this, title, message, Loc.T("common.yes"), Loc.T("common.no"));
            PromptHistoryPanelHost.ClosePanelRequested += () => VM.ShowPromptHistoryPanel = false;

            // Reset cache (Settings → Advanced) rewrote the settings and deleted the
            // Translation Memory: everything that mirrors either follows.
            SettingsPanelHost.ProfileResetDone += () =>
            {
                AiTranslatePanelHost.DropTranslationMemory();
                QcPanelHost.ReloadSettings();
                VM.ShowEmptyKeys = AppConfig.Instance.ShowEmptyKeys;
                VM.AutoExpandRows = AppConfig.Instance.AutoExpandRows;
                VM.RefreshRecentFiles();
                VM.RestartAutoSave();
                VM.RevalidateAllOpenFiles();
                VM.AddLog(Loc.T("log.reset.done"), LogLevel.Warning);
            };

            // Welcome view → existing VM handlers + recent-file activation.
            WelcomeViewHost.OpenFileRequested += () => OnOpenFile(this, new RoutedEventArgs());
            WelcomeViewHost.ImportTargetRequested += () => OnImportTarget(this, new RoutedEventArgs());
            WelcomeViewHost.MergeRequested += () => OnMergeToNewVersion(this, new RoutedEventArgs());
            WelcomeViewHost.RecentFileActivated += async path => await VM.OpenFileAsync(path);

            // Modifier-aware row clicks (DataGrid Behavior §3.1).
            //   Plain click → custom focus row only, no native DataGrid color state.
            //   Ctrl+Click → toggle row's IsChecked.
            //   Shift+Click → range-check from anchor to clicked, additive.
            MainDataGrid.AddHandler(InputElement.PointerPressedEvent,
                OnDataGridRowPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            // DoubleTapped is wired straight on the Target cell Panel via the
            // XAML attribute (OnTargetCellDoubleTapped) — no MainDataGrid-level
            // tunnel handler needed. That way the gesture is owned by the
            // exact element it applies to and there's no visual-tree walk to
            // find the column/row from the event source.
            MainDataGrid.PointerMoved += OnMainDataGridPointerMoved;
            MainDataGrid.PointerReleased += OnMainDataGridPointerReleased;
            MainDataGrid.PointerExited += OnMainDataGridPointerExited;
            MainDataGrid.CellPointerPressed += OnMainDataGridCellPointerPressed;
            MainDataGrid.SelectionChanged += OnMainDataGridSelectionChanged;
            // BeginningEdit / CellEditEnded are no longer wired: the DataGrid is
            // IsReadOnly=True and inline editing is model-owned via TranslationRow.IsEditing.
            // KeyDown is tunnel-routed so we pre-empt the DataGrid's own arrow-key
            // handling (it would otherwise move CurrentCell ahead of our FocusedRow).
            MainDataGrid.AddHandler(InputElement.KeyDownEvent,
                OnMainDataGridKeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);

            // Restore persisted column widths (DataGrid Behavior §8.3).
            //   Columns[0] = checkbox (42),  Columns[1] = status (42),
            //   Columns[2] = Key,            Columns[3] = Source,
            //   Columns[4] = Target (star — auto).
            try
            {
                var cfg = Models.AppConfig.Instance;
                if (MainDataGrid.Columns.Count >= 5)
                {
                    if (cfg.KeyColumnWidth >= 100) MainDataGrid.Columns[2].Width = new DataGridLength(cfg.KeyColumnWidth);
                    if (cfg.SourceColumnWidth >= 180) MainDataGrid.Columns[3].Width = new DataGridLength(cfg.SourceColumnWidth);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[WARN] Restore column widths: {ex.Message}"); }

            // Paint DWM chrome (border + caption) to match the active theme.
            ApplyDwmChrome();
            ThemeManager.ThemeChanged += ApplyDwmChrome;
        }

        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

        /// <summary>
        /// Paint the OS-rendered window chrome (caption bar background + caption text +
        /// outer border) to match the current Avalonia theme. The caption text is set
        /// to the same color as the caption background — Avalonia paints the actual
        /// chrome content on top, and we want the OS-rendered title text to be invisible.
        /// Re-runs whenever the user toggles themes so light/dark stay consistent.
        /// </summary>
        private void ApplyDwmChrome()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var handle = TryGetPlatformHandle();
                if (handle == null || handle.Handle == IntPtr.Zero) return;

                // Outer border: kept black in both themes so the window edge reads as
                // a deliberate frame on any desktop wallpaper.
                uint black = 0x00000000u;
                uint bg = ReadColorRefFromResource("WindowBackground", fallback: 0x001E1E1Eu);

                DwmSetWindowAttribute(handle.Handle, DWMWA_BORDER_COLOR, ref black, sizeof(uint));
                DwmSetWindowAttribute(handle.Handle, DWMWA_CAPTION_COLOR, ref bg, sizeof(uint));
                DwmSetWindowAttribute(handle.Handle, DWMWA_TEXT_COLOR, ref bg, sizeof(uint));
            }
            catch { }
        }

        /// <summary>
        /// Resolve a `Color` from the active Avalonia resource dictionary and convert it
        /// to a Win32 `COLORREF` (0x00BBGGRR). Used to feed DwmSetWindowAttribute.
        /// </summary>
        private static uint ReadColorRefFromResource(string resourceKey, uint fallback)
        {
            var brush = Avalonia.Application.Current?.FindResource(resourceKey)
                as Avalonia.Media.ISolidColorBrush;
            if (brush == null) return fallback;
            var c = brush.Color;
            return (uint)c.R | ((uint)c.G << 8) | ((uint)c.B << 16);
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            // DataGrid Behavior §4 — keyboard shortcuts that target the row list.
            // Skip when an editor has focus (TextBox / ComboBox / etc.) — otherwise we'd
            // hijack typing inside Find, Settings, Target editing, etc.
            bool editorFocused = FocusManager?.GetFocusedElement() is TextBox or ComboBox or DatePicker or NumericUpDown;
            if (!editorFocused)
            {
                if (e.Key == Key.Space && !ctrl && !shift)
                {
                    // Toggle IsSelected of the currently focused DataGrid row.
                    if (VM.FocusedRow is TranslationRow row)
                    {
                        row.IsSelected = !row.IsSelected;
                        VM.UpdateSelectionCount();
                        e.Handled = true;
                        return;
                    }
                }
                else if (ctrl && e.Key == Key.A)
                {
                    // Check every currently-visible row.
                    VM.SelectAll(true);
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Escape && !ctrl && !shift)
                {
                    // Clear all checks.
                    VM.SelectAll(false);
                    e.Handled = true;
                    return;
                }
            }

            if (!ctrl) return;

            switch (e.Key)
            {
                // Undo / redo of the GRID. Not window KeyBindings: those run before
                // any handler, so Ctrl+Z typed into a text field reverted a committed
                // edit in another row. Here they only arrive when no field took them
                // (OnEditorKeysTunnel gives a focused TextBox its own undo).
                case Key.Z when !shift:
                    if (VM.UndoCommand.CanExecute(null)) VM.UndoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Y:
                case Key.Z when shift:
                    if (VM.RedoCommand.CanExecute(null)) VM.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.O when !shift:
                    OnOpenFile(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.E when !shift:
                    OnExportLocres(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                // Ctrl+S (Save) and Ctrl+Shift+S (Save As) are handled by
                // Window.KeyBindings → SaveCurrentFormatCommand / SaveAsCommand.
                case Key.F when !shift:
                    OnFind(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.H when !shift:
                    OnFindReplace(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.T when shift:
                    OnCopySourceToTargetMulti(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                case Key.T when !shift:
                    OnCopySourceToTarget(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
            }
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.HasAnyStatusIcons))
                SyncStatusColumnVisibility();

            // The QC panel reviews the ACTIVE file — refresh its header, results
            // and row counts whenever the active file changes while it is open.
            if (e.PropertyName == nameof(MainWindowViewModel.OriginalFilePath))
            {
                if (VM.ShowQcPanel) QcPanelHost.Initialize(VM);
                SyncActivityBarState();          // QC is unavailable with no file
            }

            // RowCount is what decides whether QC is available, and it lands AFTER
            // OriginalFilePath during an open. Without this the button stayed
            // disabled until some other panel toggle happened to re-sync the rail —
            // "open a file, press QC, nothing happens".
            if (e.PropertyName == nameof(MainWindowViewModel.RowCount))
                SyncActivityBarState();

            if (e.PropertyName is nameof(MainWindowViewModel.ShowPromptHistoryPanel)
                or nameof(MainWindowViewModel.ShowAiPanel)
                or nameof(MainWindowViewModel.ShowStatusPanel)
                or nameof(MainWindowViewModel.IsFindBarOpen)
                or nameof(MainWindowViewModel.ShowSettingsPanel)
                or nameof(MainWindowViewModel.ShowMultiTranslatePanel)
                or nameof(MainWindowViewModel.ShowQcPanel))
                SyncActivityBarState();
        }

        private void SyncActivityBarState()
        {
            SetActivityButtonActive(PromptHistoryButton, VM.ShowPromptHistoryPanel);
            SetActivityButtonActive(AiSidebarButton, VM.ShowAiPanel);
            SetActivityButtonActive(StatusSidebarButton, VM.ShowStatusPanel);
            SetActivityButtonActive(FindSidebarButton, VM.IsFindBarOpen);
            SetActivityButtonActive(SettingsSidebarButton, VM.ShowSettingsPanel);
            SetActivityButtonActive(MultiTranslateSidebarButton, VM.ShowMultiTranslatePanel);
            SetActivityButtonActive(QcSidebarButton, VM.ShowQcPanel);

            // QC always reviews the ACTIVE file, so it means nothing with no file
            // open — the button says so instead of opening an empty panel.
            QcSidebarButton.IsEnabled = VM.RowCount > 0;
        }

        private static void SetActivityButtonActive(Button button, bool active)
        {
            if (active)
            {
                if (!button.Classes.Contains("active"))
                    button.Classes.Add("active");
            }
            else
            {
                button.Classes.Remove("active");
            }
        }

        private void OnThemeChanged()
        {
            VM.RefreshRowVisualStates();
        }

        private bool _forceClose;

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            // Etap 1.1: guard unsaved edits across ALL open tabs before quitting.
            if (!_forceClose && VM.OpenSessions.Any(s => s.HasUnsavedChanges))
            {
                e.Cancel = true;
                int n = VM.OpenSessions.Count(s => s.HasUnsavedChanges);
                bool ok = await Controls.ConfirmDialog.ShowAsync(this,
                    Loc.T("tabs.unsaved.title"),
                    string.Format(Loc.Plural("tabs.unsaved.app", n), n),
                    Loc.T("common.closeAnyway"),
                    Loc.T("common.cancel"));
                if (ok) { _forceClose = true; Close(); }
                return;
            }

            // Persist column widths so they're restored next session (spec §8.3).
            try
            {
                var cfg = Models.AppConfig.Instance;
                if (MainDataGrid?.Columns?.Count >= 5)
                {
                    cfg.KeyColumnWidth = MainDataGrid.Columns[2].ActualWidth;
                    cfg.SourceColumnWidth = MainDataGrid.Columns[3].ActualWidth;
                    cfg.Save();
                }
            }
            catch (Exception ex) { Console.WriteLine($"[WARN] Save column widths: {ex.Message}"); }

            KeyDown -= OnGlobalKeyDown;
            if (MainDataGrid != null)
            {
                MainDataGrid.RemoveHandler(InputElement.PointerPressedEvent, OnDataGridRowPointerPressed);
                MainDataGrid.PointerMoved -= OnMainDataGridPointerMoved;
                MainDataGrid.PointerReleased -= OnMainDataGridPointerReleased;
                MainDataGrid.PointerExited -= OnMainDataGridPointerExited;
                MainDataGrid.CellPointerPressed -= OnMainDataGridCellPointerPressed;
                MainDataGrid.SelectionChanged -= OnMainDataGridSelectionChanged;
                MainDataGrid.RemoveHandler(InputElement.KeyDownEvent, OnMainDataGridKeyDown);
            }
            ThemeManager.ThemeChanged -= UpdateThemeIcon;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            ThemeManager.ThemeChanged -= ApplyDwmChrome;
            VM.PropertyChanged -= OnVmPropertyChanged;
            VM.Cleanup();
        }

        // ───────────── CUSTOM CHROME ─────────────

        private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
        {
            // Ignore presses on interactive controls inside the header (menus, buttons).
            if (e.Source is Control src && src.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Any(c => c is Button or MenuItem or Menu))
            {
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        // The project pill trims as the header narrows; below this it would be a
        // box around its dot alone, which reads as a broken control, so it goes.
        // The pill's own chrome is 53 px (margins 16, border 2, padding 20, dot 7
        // and its gap 8); the other 40 hold about four characters and the ellipsis.
        private const double MinProjectPillRoom = 93;

        private void OnHeaderDragAreaSizeChanged(object? sender, SizeChangedEventArgs e)
            => ProjectPillSlot.IsVisible = e.NewSize.Width >= MinProjectPillRoom;

        // ───────────── THEME TOGGLE ─────────────

        private void OnToggleTheme(object? sender, RoutedEventArgs e) => ThemeManager.Toggle();

        // Monochrome vector sun/moon (design I.sun / I.moon, 24-unit space) —
        // text glyphs flipped unpredictably between emoji and mono rendering.
        private static readonly Avalonia.Media.Geometry SunIcon = Avalonia.Media.Geometry.Parse(
            "M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12 Z M12,2 V4 M12,20 V22 M4,12 H2 M22,12 H20 M5,5 L6.4,6.4 M17.6,17.6 L19,19 M5,19 L6.4,17.6 M17.6,6.4 L19,5");
        private static readonly Avalonia.Media.Geometry MoonIcon = Avalonia.Media.Geometry.Parse(
            "M20,14.5 A8,8 0 1 1 9.5,4 A7,7 0 0 0 20,14.5 Z");

        private void UpdateThemeIcon()
        {
            if (ThemeIcon != null)
                ThemeIcon.Data = ThemeManager.IsDark ? SunIcon : MoonIcon;
        }

        // ───────────── INTERFACE LANGUAGE ─────────────

        /// <summary>
        /// The header's globe button. The list is built on every click rather than
        /// once, because the checkmark has to follow the current language and the
        /// language can also be changed from Settings.
        ///
        /// <para>Each entry is labelled with the language's OWN name, read from that
        /// language's file — a user who cannot read the current UI language is
        /// exactly who this menu is for, so "Deutsch" and "日本語" are the only
        /// labels that help them.</para>
        /// </summary>
        private void OnLanguageMenu(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            foreach (var code in Loc.Instance.AvailableLanguages().OrderBy(c => c, StringComparer.Ordinal))
            {
                var item = new MenuItem
                {
                    Header = Loc.Instance.NativeName(code),
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = code == Loc.Instance.CurrentLanguage,
                    Tag = code,
                };
                item.Click += OnLanguagePicked;
                menu.Items.Add(item);
            }
            menu.ShowAt(button);
        }

        private void OnLanguagePicked(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string code) return;
            if (code == Loc.Instance.CurrentLanguage) return;

            var config = AppConfig.Instance;
            config.Language = code;
            config.Save();
            Loc.Instance.Load(code);
            // The language file names its own font stacks; a switch to Japanese has
            // to re-publish them or the UI keeps rendering CJK through the previous
            // language's Latin-only stack.
            ThemeManager.ApplyFonts();
        }

        // ───────────── FILE DIALOGS ─────────────

        private async Task<string?> PickOpenFile(string title, string filter)
        {
            var parts = filter.Split('|');
            var label = parts[0];
            var patterns = parts.Length > 1
                ? parts[1].Split(';').Select(p => p.Trim()).ToArray()
                : new[] { "*.*" };

            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(label) { Patterns = patterns } }
            });
            if (result.Count == 0) return null;
            var file = result[0];
            // Guard each link in the chain — historically `result[0].Path.LocalPath` could
            // throw NRE on certain providers (e.g. cancelled cloud pickers returning a stub item).
            return file?.Path?.LocalPath;
        }

        private async Task<string?> PickSaveFile(string title, string filter)
        {
            var segments = filter.Split('|');
            var types = new List<FilePickerFileType>();
            for (int i = 0; i + 1 < segments.Length; i += 2)
            {
                types.Add(new FilePickerFileType(segments[i])
                {
                    Patterns = new[] { segments[i + 1].Trim() }
                });
            }

            var result = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                FileTypeChoices = types
            });
            return result?.Path?.LocalPath;
        }

        // ───────────── MENU HANDLERS — File ─────────────

        /// <summary>
        /// Wraps a VM async operation with uniform error reporting so every File menu
        /// handler is a one-liner. Exceptions surface in the status bar; the open-file
        /// path additionally logs to the console because it's the most common failure
        /// site and we want a stack trace during debugging.
        /// </summary>
        private async Task RunVMOperation(Func<Task> op, string opName, bool consoleLog = false)
        {
            try { await op(); }
            catch (Exception ex)
            {
                if (consoleLog) Console.WriteLine($"[FATAL] {opName}: {ex}");
                VM.StatusText = string.Format(Loc.T("status.msg.error"), ex.Message);
            }
        }

        /// <summary>
        /// Handler for <see cref="MainWindowViewModel.InitialValidationRequested"/>.
        /// Opens the modal <see cref="ValidationProgressDialog"/> while the 6
        /// light validators chew through every row of the freshly-opened file.
        /// Returns true iff the user cancelled.
        ///
        /// <para>
        /// Race-free shutdown: the work task and the dialog's ShowDialog Task
        /// both await independently. On normal completion we call
        /// dialog.RequestClose; on cancel the dialog already started closing
        /// itself (Cancel button or X) and RequestClose is a no-op idempotent.
        /// </para>
        /// </summary>
        private async Task<bool> OnInitialValidationRequested(
            System.Collections.Generic.IReadOnlyList<Models.TranslationRow> rows)
        {
            var dialog = new Controls.ValidationProgressDialog();
            var work = Services.ValidationService.Instance.RunInitialAsync(
                rows, dialog.ProgressSink, dialog.CancellationToken);
            var dialogTask = dialog.ShowDialog(this);

            try
            {
                await work;
            }
            catch (OperationCanceledException)
            {
                // Cancel button / X — dialog already closing.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ValidationDialog] work failed: {ex}");
            }
            finally
            {
                dialog.RequestClose();
            }

            try { await dialogTask; }
            catch (Exception ex) { Console.WriteLine($"[ValidationDialog] showDialog: {ex}"); }

            return dialog.WasCancelled;
        }

        private async void OnOpenFile(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.OpenFileAsync, nameof(OnOpenFile), consoleLog: true);

        /// <summary>Open-file flow for external triggers (the tab bar's "+" button).</summary>
        public void RequestOpenFile() => OnOpenFile(null, new RoutedEventArgs());

        private async void OnImportTarget(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.ImportTargetLocresAsync, nameof(OnImportTarget));

        private async void OnSaveCurrentFormat(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.SaveCurrentFormatAsync, nameof(OnSaveCurrentFormat));

        private async void OnExportLocres(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.ExportLocresAsync, nameof(OnExportLocres));

        private async void OnSaveAs(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.SaveAsAsync, nameof(OnSaveAs));

        private async void OnSaveAsFlattened(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.SaveAsFlattenedAsync, nameof(OnSaveAsFlattened));

        private async void OnMergeToNewVersion(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.MergeToNewVersionAsync, nameof(OnMergeToNewVersion));

        // ───────────── MENU HANDLERS — Edit ─────────────

        private void OnCopySourceToTarget(object? s, RoutedEventArgs e)
        {
            var highlighted = MainDataGrid.SelectedItems.Cast<TranslationRow>().ToList();
            if (highlighted.Count > 0) VM.CopySourceToTarget(highlighted);
        }

        private void OnCopySourceToTargetMulti(object? s, RoutedEventArgs e)
        {
            var checkedRows = VM.SelectedRows;
            if (checkedRows.Count > 0) VM.CopySourceToTarget(checkedRows);
        }

        private void OnFind(object? s, RoutedEventArgs e)
        {
            // Etap 9: Ctrl+F opens the FindBar with Replace row collapsed.
            // Focus + select happens inside FindBar.HandleOpenedChanged once
            // the visual tree has settled.
            VM.OpenFindBar(replaceMode: false);
        }

        private void OnFindReplace(object? s, RoutedEventArgs e)
        {
            // Ctrl+H opens the same FindBar with Replace row pre-expanded
            // and focus on the Replace input.
            VM.OpenFindBar(replaceMode: true);
        }

        private void OnFindReplaceToggle(object? s, RoutedEventArgs e)
        {
            // Activity-bar Find icon: toggle the bar without changing mode.
            if (VM.IsFindBarOpen) VM.CloseFindBar();
            else VM.OpenFindBar(replaceMode: VM.FindBarReplaceMode);
        }

        private void OnPromptHistoryToggle(object? s, RoutedEventArgs e)
        {
            VM.ShowPromptHistoryPanel = !VM.ShowPromptHistoryPanel;
        }

        private void OnStatusToggle(object? s, RoutedEventArgs e)
        {
            VM.ShowStatusPanel = !VM.ShowStatusPanel;
        }

        private void OnRunValidation(object? s, RoutedEventArgs e)
        {
            // Play / F8 — checks tag and token integrity across all translated rows.
            if (VM.CheckTagsCommand.CanExecute(null))
                VM.CheckTagsCommand.Execute(null);
        }

        private void OnExitApp(object? s, RoutedEventArgs e) => Close();

        private async void OnOpenSpreadsheet(object? s, RoutedEventArgs e) =>
            await RunVMOperation(VM.OpenSpreadsheetAsync, nameof(OnOpenSpreadsheet));

        private void OnAITranslateSelected(object? s, RoutedEventArgs e)
        {
            // Filter bar chip — open AI Translate panel pre-seeded to "selected rows" scope.
            // Reuses the existing OnAITranslation flow.
            OnAITranslation(s, e);
        }

        // Etap 13: "Close file" closes the ACTIVE tab (and falls back to Welcome
        // when it was the last one), rather than closing every open file.
        private void OnCloseCurrentFile(object? s, RoutedEventArgs e) => VM.CloseActiveTab();

        // ───────────── PROBLEMS TAB ─────────────

        private void OnProblemFixClick(object? s, RoutedEventArgs e)
        {
            if ((s as Control)?.DataContext is not ProblemListItem item) return;

            // A QC suggestion must go through the QC flow — it records the verdict
            // in the sidecar, re-raises findings the fix does not resolve, and
            // respects staleness. The validator's Fix path knows none of that.
            if (item.Qc is { } proposal) VM.ApplyQcProposal(item.Row, proposal);
            else if (item.Problem is { } problem) _ = VM.ReviewProblemFix(item.Row, problem);

            // The same template serves both tabs; refresh whichever is on screen.
            if (item.IsQc) VM.RebuildQcGroups();
            else VM.RebuildProblemItems();
        }

        /// <summary>Category header click — expand/collapse. Buttons inside it are
        /// excluded so pressing [Fix] does not also toggle the group.</summary>
        private void OnQcGroupHeaderPressed(object? s, PointerPressedEventArgs e)
        {
            if (e.Source is Control src && src.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
            if ((s as Control)?.DataContext is QcGroupItem group) VM.ToggleQcGroup(group);
        }

        private async void OnQcGroupFixClick(object? s, RoutedEventArgs e)
        {
            try
            {
                if ((s as Control)?.DataContext is not QcGroupItem group) return;
                await VM.FixQcGroup(group);
                VM.RebuildQcGroups();
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnQcGroupFixClick)); }
        }

        private void OnProblemGoClick(object? s, RoutedEventArgs e)
        {
            if ((s as Control)?.DataContext is ProblemListItem item)
                VM.RevealRow(item.Row);
        }

        private void OnProblemItemDoubleTapped(object? s, TappedEventArgs e)
        {
            if ((s as Control)?.DataContext is ProblemListItem item)
                VM.RevealRow(item.Row);
        }

        // ───────────── MENU HANDLERS — Settings / Help ─────────────

        private void OnPreferences(object? s, RoutedEventArgs e)
        {
            // Toggle: clicking the Settings sidebar icon a second time collapses the panel.
            VM.ShowSettingsPanel = !VM.ShowSettingsPanel;
        }

        /// <summary>
        /// Tools -> Advanced settings. Opens the Settings panel (never closes it,
        /// unlike the toggle above — the user asked to GO somewhere) and scrolls to
        /// the Advanced section at the bottom.
        /// </summary>
        private void OnAdvancedSettings(object? s, RoutedEventArgs e)
        {
            VM.ShowSettingsPanel = true;
            SettingsPanelHost.ScrollToAdvanced();
        }

        private void OnAITranslation(object? s, RoutedEventArgs e)
        {
            if (!VM.HasData)
            {
                VM.StatusText = Loc.T("status.msg.openFileBeforeAi");
                return;
            }
            // Toggle: second click collapses the panel.
            if (VM.ShowAiPanel)
            {
                VM.ShowAiPanel = false;
                return;
            }
            // Re-initialise the panel with current VM state each time it opens. The
            // selection is NOT handed over: the panel reads it live from the VM, so
            // rows checked after the panel opened still count.
            AiTranslatePanelHost.Initialize(VM);
            VM.ShowAiPanel = true;
        }

        private void OnQcToggle(object? sender, RoutedEventArgs e)
        {
            if (VM.ShowQcPanel) { VM.ShowQcPanel = false; return; }
            QcPanelHost.Initialize(VM);
            VM.ShowQcPanel = true;
        }

        private void OnMultiTranslateToggle(object? sender, RoutedEventArgs e)
        {
            // Toggle: second click collapses the panel.
            if (VM.ShowMultiTranslatePanel)
            {
                VM.ShowMultiTranslatePanel = false;
                return;
            }
            MultiTranslatePanelHost.Initialize(VM); // idempotent (builds the language list once)
            VM.ShowMultiTranslatePanel = true;
        }

        private void OnAbout(object? s, RoutedEventArgs e)
        {
            var dlg = new AboutWindow();
            dlg.ShowDialog(this);
        }

        /// <summary>
        /// Help → Documentation: the user guide that ships in the release zip
        /// (docs/ next to the exe), in Ukrainian when the interface is Ukrainian.
        /// The csproj copies docs/ into every build output too; only an exe copied
        /// out on its own has no guide, and then the item greys out.
        /// </summary>
        private static string? UserGuidePath()
        {
            string dir = System.IO.Path.Combine(AppContext.BaseDirectory, "docs");
            string uk = System.IO.Path.Combine(dir, "USER-GUIDE.uk.md");
            string en = System.IO.Path.Combine(dir, "USER-GUIDE.md");
            if (Localization.Loc.Instance.CurrentLanguage == "uk" && System.IO.File.Exists(uk)) return uk;
            return System.IO.File.Exists(en) ? en : null;
        }

        private void OnOpenDocs(object? s, RoutedEventArgs e)
        {
            string? guide = UserGuidePath();
            if (guide == null) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guide) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No program is associated with .md (the Windows default): Notepad
                // reads it — and formats Markdown on current Windows 11. Explorer
                // pointing at the file is the last resort.
                if (!TryStart("notepad.exe", $"\"{guide}\""))
                    TryStart("explorer.exe", $"/select,\"{guide}\"");
            }
        }

        private static bool TryStart(string exe, string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = true,
                });
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[shell] {exe}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// File → Recent files: rebuilt from the recent list every time the File menu
        /// opens, so it always matches the welcome screen. The routed event also
        /// bubbles up from the Recent submenu itself — rebuilding then would replace
        /// the items under the pointer, so only the File menu's own opening counts.
        /// </summary>
        private void OnFileMenuOpened(object? sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, sender)) return;
            VM.RefreshRecentFiles();
            MenuRecent.Items.Clear();
            foreach (var entry in VM.RecentFiles)
            {
                string path = entry.Path;
                var item = new MenuItem { Header = entry.Name };
                ToolTip.SetTip(item, path);
                item.Click += async (_, _) => await VM.OpenFileAsync(path);
                MenuRecent.Items.Add(item);
            }
            MenuRecent.IsEnabled = MenuRecent.Items.Count > 0;
        }

        /// <summary>Tools → Open Translation Memory folder: the profile folder that
        /// holds one translation_memory_{from}_{to}.json per language pair.</summary>
        private void OnOpenTmFolder(object? s, RoutedEventArgs e)
            => TryStart("explorer.exe", $"\"{SmartUnrealLocalization.Models.AppConfig.ConfigDir()}\"");

        /// <summary>
        /// Help → Check for updates. Looks at the repository's latest release; a
        /// newer one is offered as a page to open — nothing is downloaded here.
        /// </summary>
        private async void OnCheckUpdates(object? s, RoutedEventArgs e)
        {
            MenuCheckUpdates.IsEnabled = false;              // one check at a time
            try
            {
                VM.StatusText = Loc.T("update.checking");
                var current = UpdateChecker.CurrentVersion();
                var result = await UpdateChecker.CheckAsync(current);
                switch (result.Outcome)
                {
                    case UpdateChecker.Outcome.NewVersion:
                        VM.AddLog(string.Format(Loc.T("update.available.log"), result.Latest, current.ToString(3)));
                        bool open = await ConfirmDialog.ShowAsync(this, Loc.T("update.title"),
                            string.Format(Loc.T("update.available"), result.Latest, current.ToString(3)),
                            Loc.T("common.yes"), Loc.T("common.no"));
                        if (open) TryStart(result.Url, "");
                        break;
                    case UpdateChecker.Outcome.UpToDate:
                        Notify(string.Format(Loc.T("update.latest"), current.ToString(3)), LogLevel.Info);
                        break;
                    case UpdateChecker.Outcome.NoRelease:
                        Notify(Loc.T("update.none"), LogLevel.Warning);
                        break;
                    default:
                        Notify(string.Format(Loc.T("update.failed"), result.Error), LogLevel.Error);
                        break;
                }
            }
            catch (Exception ex) { VM.AddLog(string.Format(Loc.T("update.failed"), ex.Message), LogLevel.Error); }
            finally { MenuCheckUpdates.IsEnabled = true; }

            void Notify(string message, LogLevel level)
            {
                VM.AddLog(message, level);
                if (level == LogLevel.Error) ToastService.Instance.ShowError(message);
                else if (level == LogLevel.Warning) ToastService.Instance.ShowWarning(message);
                else ToastService.Instance.ShowInfo(message);
            }
        }

        private void OnLicenses(object? s, RoutedEventArgs e)
        {
            var dlg = new LicenseWindow();
            dlg.ShowDialog(this);
        }

        // ───────────── DATAGRID EDIT TRACKING ─────────────

        // (DataGrid BeginningEdit/CellEditEnding handlers were removed — undo capture now
        //  happens in MainWindowViewModel.OnRowPropertyChanged for every Target change,
        //  including the first row's first edit, which the DataGrid events miss intermittently.)

        // ───────────── COLUMN CONTEXT MENU ─────────────

        // The work lives in the view model (MainWindowViewModel.Clipboard.cs), where
        // it is tested; these only read which column's header was right-clicked.
        private static string? ColumnOf(object? s) => (s as MenuItem)?.CommandParameter as string;

        private async void OnCopyColumn(object? s, RoutedEventArgs e)
        {
            try
            {
                if (ColumnOf(s) is { } column) await VM.CopyColumnAsync(column);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCopyColumn)); }
        }

        private async void OnPasteColumn(object? s, RoutedEventArgs e)
        {
            try
            {
                if (ColumnOf(s) == "Target") await VM.PasteTargetColumnAsync();
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnPasteColumn)); }
        }

        private void OnClearColumn(object? s, RoutedEventArgs e)
        {
            if (ColumnOf(s) == "Target") VM.ClearTargetColumn();
        }

        private async void OnCutColumn(object? s, RoutedEventArgs e)
        {
            try
            {
                if (ColumnOf(s) == "Target") await VM.CutTargetColumnAsync();
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCutColumn)); }
        }
        // ───────────── ROW SELECTION ─────────────

        /// <summary>
        /// Catches DataGrid row clicks BEFORE the native selection logic runs.
        /// Only triggers our handler when a modifier (Ctrl/Shift) is pressed — plain
        /// clicks fall through so the DataGrid's own focus model still works.
        /// Spec: DataGrid Behavior §3.1.
        /// </summary>
        // ───────────── INLINE EDITING (model-owned, prototype parity) ─────────────
        //
        // The DataGrid is IsReadOnly=True. The Target cell flips between
        // TaggedTextBlock and TextBox via TranslationRow.IsEditing — the native
        // BeginEdit / BeginningEdit / CellEditEnded path is gone. Triggers:
        //   * Double-tap on Target cell (Avalonia gesture, real OS double-click time)
        //   * F2 on the focused row
        //   * Enter on the focused row (matches prototype's keyboard flow)
        // Commit / cancel rules (OnEditorKeysTunnel — tunnel-routed, or the
        // TextBox would take Enter first):
        //   * Enter           → commit + advance focused row down
        //   * Shift+Enter     → line break inside the text
        //   * Ctrl+Enter      → commit, stay
        //   * Tab             → commit + advance (Shift+Tab: commit, stay)
        //   * Esc             → cancel (revert)
        //   * Ctrl+Z / Ctrl+Y → the editor's own undo / redo
        //   * Ctrl+T          → put the row's Source into the editor
        //   * LostFocus       → commit, no advance

        // DoubleTapped is wired in XAML on the Target cell's wrapper Panel.
        // sender is that Panel; DataContext is the row's TranslationRow because
        // the Panel sits inside the cell's DataTemplate. No visual-tree walk,
        // no column-identification heuristics — the gesture is owned by the
        // exact element it applies to.
        private void OnTargetCellDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not Control c) return;
            if (c.DataContext is not TranslationRow row) return;
            // A double-click INSIDE the open editor is the TextBox's own gesture —
            // select a word. It bubbles up here too, and treating it as "start
            // editing" re-seeded the editor from Target: everything typed since
            // the edit began vanished (reported 2026-10-20).
            if (e.Source is Visual v && v.GetSelfAndVisualAncestors().Any(a => a is TextBox)) return;
            StartEditingTarget(row);
            e.Handled = true;
        }

        // Etap 6: status-icon column hover → ProblemPopover. Shows the first
        // Problem on the row (severity-icon column collapses multiple problems
        // to one icon, so the first one is the most sensible default). Wavy
        // hover paths inside TaggedTextBlock pick the segment-specific Problem
        // instead — that wiring lives in TaggedTextBlock.WrapWithOverlays.
        private void OnStatusIconHoverIn(object? sender, PointerEventArgs e)
        {
            if (sender is not Control c) return;
            if (c.DataContext is not TranslationRow row) return;
            var first = row.Problems.FirstOrDefault();
            if (first is null) return; // review-only icons (Approved/Rejected) — no popover.
            Services.ProblemPopoverService.Instance.RequestShow(c, first, row);
        }

        private void OnStatusIconHoverOut(object? sender, PointerEventArgs e)
            => Services.ProblemPopoverService.Instance.RequestHide();

        // Prototype keyboard model on the row list (when no TextBox is focused):
        //   ArrowDown / ArrowUp       → move focus 1 row (Shift extends checks)
        //   Home / End                → focus first / last
        //   PageDown / PageUp         → move focus ±10 rows
        //   F2  / Enter (no Ctrl)     → start editing focused row
        //   Other keys                → fall through (DataGrid's own handler,
        //                               OnGlobalKeyDown for Space/Ctrl+A/Esc, etc.)
        private void OnMainDataGridKeyDown(object? sender, KeyEventArgs e)
        {
            // Defer entirely to the editor when it owns focus.
            if (FocusManager?.GetFocusedElement() is TextBox) return;

            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            switch (e.Key)
            {
                case Key.Down:
                    VM.MoveFocus(+1, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.Up:
                    VM.MoveFocus(-1, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    VM.MoveFocus(+10, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.PageUp:
                    VM.MoveFocus(-10, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.Home when !ctrl:
                    VM.MoveFocusToIndex(0, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.End when !ctrl:
                    VM.MoveFocusToIndex(VM.Rows.Count - 1, shift);
                    ScrollFocusedIntoView();
                    e.Handled = true;
                    return;
                case Key.F2 when VM.FocusedRow != null:
                    StartEditingTarget(VM.FocusedRow);
                    e.Handled = true;
                    return;
                case Key.Enter when !ctrl && !shift && VM.FocusedRow != null:
                    StartEditingTarget(VM.FocusedRow);
                    e.Handled = true;
                    return;
            }
        }

        private void ScrollFocusedIntoView()
        {
            if (VM.FocusedRow != null)
                MainDataGrid.ScrollIntoView(VM.FocusedRow, null);
        }

        private void StartEditingTarget(TranslationRow row)
        {
            // Already editing this row: the editor holds the user's unsaved text.
            // Seeding it again from Target would throw that text away.
            if (row.IsEditing) return;

            // If another row is mid-edit, commit it first (cascading focus model — matches
            // what happens naturally when the new editor steals focus from the old one).
            var prior = VM.AllRows.FirstOrDefault(r => r.IsEditing);
            if (prior != null && !ReferenceEquals(prior, row))
                CommitTargetEdit(prior, advance: false);

            row.EditValue = row.Target ?? "";
            row.IsEditing = true;
            VM.SetFocusedRow(row);
            MainDataGrid.ScrollIntoView(row, MainDataGrid.Columns[^1]);
            // FocusBehavior.FocusOnVisible fires on the next dispatcher cycle once the
            // TextBox's IsVisible flips true — focus + SelectAll happen there.
        }

        private void CommitTargetEdit(TranslationRow row, bool advance)
        {
            if (!row.IsEditing) return;
            var newVal = row.EditValue ?? "";
            var oldVal = row.Target ?? "";
            // Setting Target fires OnRowPropertyChanged in the VM → records a single
            // CellEditAction in the UndoManager. One edit session = one undo step.
            if (oldVal != newVal) row.Target = newVal;
            row.IsEditing = false;

            if (advance) AdvanceFocusToNext(row);
        }

        private void CancelTargetEdit(TranslationRow row)
        {
            if (!row.IsEditing) return;
            row.IsEditing = false;
            // EditValue is discarded; re-seeded from Target the next time StartEditingTarget runs.
        }

        private void AdvanceFocusToNext(TranslationRow row)
        {
            var idx = VM.Rows.IndexOf(row);
            if (idx < 0 || idx + 1 >= VM.Rows.Count) return;
            var next = VM.Rows[idx + 1];
            VM.SetFocusedRow(next);
            MainDataGrid.ScrollIntoView(next, MainDataGrid.Columns[^1]);
        }

        private static bool IsInsidePopup(Visual v)
        {
            // Walk up the visual ancestry; if any ancestor is a PopupRoot, the
            // focused element lives inside a popup (Avalonia hosts ContextFlyout
            // / MenuFlyout / Popup content under a PopupRoot, not the main window).
            foreach (var ancestor in v.GetSelfAndVisualAncestors())
                if (ancestor is Avalonia.Controls.Primitives.PopupRoot) return true;
            return false;
        }

        private void OnTargetEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (tb.DataContext is not TranslationRow row) return;
            if (!row.IsEditing) return;

            // DEFERRED COMMIT — survives transient focus loss to the TextBox's
            // own ContextFlyout (right-click Cut/Copy/Paste). Without the
            // defer the sequence was:
            //   1. user right-clicks TextBox
            //   2. Avalonia opens the default ContextFlyout, focus jumps to it
            //   3. LostFocus fires → CommitTargetEdit → IsEditing=false
            //   4. TextBox is now IsVisible=false; the popup loses its anchor
            //      and dismisses before the user can click "Paste".
            // Posting at Background priority lets the next dispatcher cycle
            // settle the focus transition. If by then the focused element is:
            //   * still the editor TextBox → no real loss, do nothing.
            //   * inside a PopupRoot (right-click flyout, autocomplete, etc.)
            //     → still interacting with editor-owned UI, do nothing.
            //   * anywhere else → real focus loss, commit the edit.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!row.IsEditing) return;
                var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
                if (ReferenceEquals(focused, tb)) return;
                // Walk the visual tree up to find the root. If it's a PopupRoot
                // (right-click ContextFlyout, MenuFlyout, autocomplete dropdown, …),
                // the user is still interacting with editor-owned UI — keep the editor.
                if (focused is Visual v && IsInsidePopup(v)) return;
                CommitTargetEdit(row, advance: false);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        private void OnMainDataGridPointerMoved(object? sender, PointerEventArgs e)
        {
            UpdateHoveredRow(e.Source as Control);
        }

        private void OnMainDataGridPointerExited(object? sender, PointerEventArgs e)
        {
            if (e.GetCurrentPoint(MainDataGrid).Properties.IsLeftButtonPressed)
                return;

            VM.SetHoveredRow(null);
        }

        private void OnMainDataGridPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            UpdateHoveredRow(e.Source as Control);
        }

        private void UpdateHoveredRow(Control? source)
        {
            var row = source?.GetSelfAndVisualAncestors()
                .OfType<DataGridRow>()
                .FirstOrDefault()?.DataContext as TranslationRow;
            VM.SetHoveredRow(row);
        }

        private bool IsTargetColumn(DataGridColumn? column)
        {
            return column != null &&
                   MainDataGrid.Columns.Count > 0 &&
                   ReferenceEquals(column, MainDataGrid.Columns[^1]);
        }

        private TranslationRow? GetRowFromSource(Control? source)
        {
            return source?.GetSelfAndVisualAncestors()
                .OfType<DataGridRow>()
                .FirstOrDefault()?.DataContext as TranslationRow;
        }

        private DataGridColumn? GetColumnFromSource(Control? source)
        {
            var cell = source?.GetSelfAndVisualAncestors()
                .OfType<DataGridCell>()
                .FirstOrDefault();
            var row = cell?.GetSelfAndVisualAncestors()
                .OfType<DataGridRow>()
                .FirstOrDefault();
            if (cell == null || row == null)
                return null;

            var cells = row.GetVisualDescendants().OfType<DataGridCell>().ToList();
            int index = cells.IndexOf(cell);
            if (index < 0)
                return null;

            var visibleColumns = MainDataGrid.Columns.Where(c => c.IsVisible).ToList();
            return index < visibleColumns.Count ? visibleColumns[index] : null;
        }



        // Tunnel-routed PointerPressed handler — owns ALL row-click logic.
        // Drives the prototype's click model 1:1:
        //   * Plain left-click  → set FocusedRow + anchor.
        //   * Ctrl/Cmd+click    → toggle row's IsSelected; anchor moves.
        //   * Shift+click       → range-check from anchor to clicked, additive.
        //   * Right-click       → set FocusedRow (so the context menu opens on
        //                          the row the user pointed at), then let
        //                          the ContextMenu mechanism fire.
        //   * Click on checkbox → ignored here (the CheckBox's own Click fires).
        //   * Click on header / scrollbar → ignored.
        //   * Click on the inline editor TextBox → ignored (TextBox handles
        //                          the click for caret positioning / drag-select).
        //   * Click while a *different* row is mid-edit → first commit that row
        //                          (matches the LostFocus-then-process flow most
        //                          desktop apps use), then process the new click.
        private void OnDataGridRowPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(MainDataGrid);
            bool isLeft = point.Properties.IsLeftButtonPressed;
            bool isRight = point.Properties.IsRightButtonPressed;
            if (!isLeft && !isRight) return;

            if (e.Source is not Control source) return;
            var ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToList();

            // Hands-off zones.
            if (ancestors.Any(c => c is CheckBox or DataGridColumnHeader or ScrollBar))
                return;
            if (ancestors.OfType<TextBox>().Any(t => t.Classes.Contains("gridEditor")))
                return;

            var dgRow = ancestors.OfType<DataGridRow>().FirstOrDefault();
            if (dgRow?.DataContext is not TranslationRow tr) return;

            // Commit any active edit on a different row BEFORE processing this click.
            // (Same row click while editing is harmless — the click hits the TextBox
            // and is short-circuited above by the gridEditor guard.)
            var editing = VM.AllRows.FirstOrDefault(r => r.IsEditing);
            if (editing != null && !ReferenceEquals(editing, tr))
                CommitTargetEdit(editing, advance: false);

            UpdateHoveredRow(source);

            if (isRight)
            {
                // Focus the right-clicked row; let the attached ContextMenu open itself.
                VM.SetFocusedRow(tr);
                return;
            }

            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            VM.SetFocusedRow(tr);
            VM.HandleRowClick(tr, ctrl, shift);
            // We deliberately do NOT mark e.Handled: the DataGrid still updates
            // its internal SelectedItem (used by some default scroll-into-view
            // and accessibility paths), but our visuals are model-owned so the
            // native :selected pseudo-class has no effect.
        }

        // Lightweight hover-only fallback for cells that the tunnel handler
        // skipped (e.g. checkbox). Hover state needs to follow the pointer
        // regardless of where it lands.
        private void OnMainDataGridCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
        {
            UpdateHoveredRow(e.PointerPressedEventArgs.Source as Control);
        }

        private void OnMainDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (MainDataGrid.SelectedItem is TranslationRow row)
                VM.SetFocusedRow(row);
        }

        /// <summary>
        /// Click on a column header cycles the sort: same column none → asc → desc → none,
        /// different column starts fresh asc. The header XAML carries the column name in Tag.
        /// </summary>
        private void OnSortHeaderPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control c && c.Tag is string column && !string.IsNullOrEmpty(column))
            {
                // Only left-button — right-click opens the context menu attached to Target.
                if (e.GetCurrentPoint(c).Properties.IsLeftButtonPressed)
                {
                    VM.CycleSort(column);
                    e.Handled = true;
                }
            }
        }

        private void OnRowCheckboxClicked(object? s, RoutedEventArgs e) => VM.UpdateSelectionCount();

        /// <summary>Empty-state "Reset filter" click — clears every active filter at once.</summary>
        private void OnResetFiltersClick(object? s, RoutedEventArgs e) => VM.ResetAllFilters();

        // ───────────── ROW CONTEXT MENU (spec §5) ─────────────
        // Each MenuItem.DataContext is the clicked TranslationRow (inherited from
        // DataGridRow.DataContext through ContextMenu attachment). The scope helper
        // returns all checked rows if the clicked one is checked + multi-checked,
        // else just the clicked row.

        private TranslationRow? CtxRow(object? sender) =>
            sender is MenuItem mi && mi.DataContext is TranslationRow r ? r : null;

        private async void OnCtxCopySource(object? s, RoutedEventArgs e)
        {
            try
            {
                if (CtxRow(s) is not TranslationRow row) return;
                var scope = VM.GetContextScope(row);
                await SetClipboardJoined(scope.Select(r => r.Source));
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCtxCopySource)); }
        }

        private async void OnCtxCopyTarget(object? s, RoutedEventArgs e)
        {
            try
            {
                if (CtxRow(s) is not TranslationRow row) return;
                var scope = VM.GetContextScope(row);
                await SetClipboardJoined(scope.Select(r => r.Target ?? ""));
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCtxCopyTarget)); }
        }

        private async void OnCtxCopyKey(object? s, RoutedEventArgs e)
        {
            try
            {
                if (CtxRow(s) is not TranslationRow row) return;
                var scope = VM.GetContextScope(row);
                await SetClipboardJoined(scope.Select(r => r.Key));
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCtxCopyKey)); }
        }

        private void OnCtxCopySrcToTgt(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is not TranslationRow row) return;
            var scope = VM.GetContextScope(row);
            VM.CopySourceToTarget(scope);
        }

        private void OnCtxClearTarget(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is not TranslationRow row) return;
            var scope = VM.GetContextScope(row);
            VM.SuppressUndoCapture = true;
            var actions = new List<CellEditAction>();
            try
            {
                foreach (var r in scope)
                {
                    if (!string.IsNullOrEmpty(r.Target))
                    {
                        actions.Add(new CellEditAction(r, r.Target, ""));
                        r.Target = "";
                        VM.MarkTargetSnapshot(r, "");
                    }
                }
            }
            finally { VM.SuppressUndoCapture = false; }
            if (actions.Count > 0)
            {
                VM.UndoManager.AddAction(new BatchEditAction(actions));
                VM.MarkDirty();
            }
        }

        // ───────────── REVIEW (Etap 11) ─────────────

        private void OnCtxMarkApproved(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is TranslationRow row)
                VM.ApplyReview(VM.GetContextScope(row), ReviewStatus.Approved);
        }

        private void OnCtxMarkRejected(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is TranslationRow row)
                VM.ApplyReview(VM.GetContextScope(row), ReviewStatus.Rejected);
        }

        private void OnCtxClearReview(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is TranslationRow row)
                VM.ApplyReview(VM.GetContextScope(row), ReviewStatus.None);
        }

        private void OnCtxAiRow(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is not TranslationRow row) return;
            // Make the scope REAL rather than handing the panel a hidden snapshot:
            // check exactly the context rows, so the grid, the status bar and the
            // panel's "Selected (N)" all agree on what Start will run.
            VM.SetSelection(VM.GetContextScope(row));
            AiTranslatePanelHost.Initialize(VM);
            VM.ShowAiPanel = true;
        }

        private void OnCtxSendToTm(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is not TranslationRow row) return;
            // The TM file is keyed by language pair — reuse the AI panel's last-used
            // pair (persisted in config). Before any AI run there is no pair to key
            // by, so tell the user instead of guessing a file.
            var cfg = AppConfig.Instance;
            if (string.IsNullOrEmpty(cfg.LastAiFromLanguage) || string.IsNullOrEmpty(cfg.LastAiToLanguage))
            {
                VM.StatusText = Loc.T("status.msg.tmNeedsLanguagePair");
                return;
            }

            var scope = VM.GetContextScope(row)
                .Where(r => !string.IsNullOrEmpty(r.Source) && !string.IsNullOrEmpty(r.Target))
                .ToList();
            if (scope.Count == 0)
            {
                VM.StatusText = Loc.T("status.msg.tmNothingToSend");
                return;
            }

            var tm = new TranslationMemoryService();
            tm.SetLanguagePair(cfg.LastAiFromLanguage, cfg.LastAiToLanguage);
            foreach (var r in scope) tm.Add(r.Source, r.Target);
            tm.Save();
            VM.AddLog(string.Format(Loc.Plural("log.tm.sent", scope.Count), scope.Count, cfg.LastAiFromLanguage, cfg.LastAiToLanguage), LogLevel.Info);
        }

        private void OnCtxGoToFirstDuplicate(object? s, RoutedEventArgs e)
        {
            if (CtxRow(s) is not TranslationRow row) return;
            // Find the first OTHER row with same Source; scroll to it.
            var dup = VM.AllRows.FirstOrDefault(r => r != row &&
                                                    string.Equals(r.Source, row.Source, StringComparison.Ordinal));
            if (dup != null && VM.Rows.Contains(dup))
            {
                MainDataGrid.SelectedItem = dup;
                MainDataGrid.ScrollIntoView(dup, null);
            }
            else if (dup == null)
            {
                VM.StatusText = Loc.T("status.msg.noDuplicateFound");
            }
        }

        private void OnCtxShowInExplorer(object? s, RoutedEventArgs e)
        {
            // Reveal the current file in the OS file explorer.
            var path = VM.OriginalFilePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { VM.StatusText = string.Format(Loc.T("status.msg.error"), ex.Message); }
        }

        private async Task SetClipboardJoined(IEnumerable<string> values)
        {
            // CSV-quote each cell so a Source/Target that contains embedded newlines
            // stays ONE clipboard row. A raw string.Join("\n", …) turned a multi-line
            // cell into several rows, so copying N rows could yield far more than N
            // lines (and a paste-column round-trip then rejected the mismatch).
            var text = string.Join(Environment.NewLine, values.Select(TextUtils.EscapeForClipboard));
            await VM.WriteClipboardAsync(text);
        }

        /// <summary>
        /// Tri-state Select-All click — used by both the filter bar checkbox and the
        /// DataGrid column header. Intent: any click while NOT fully checked
        /// (false or indeterminate) → check all visible; click while fully checked
        /// → uncheck all. See DataGrid Behavior §10.
        /// </summary>
        private void OnSelectAllClicked(object? s, RoutedEventArgs e) => VM.ToggleSelectAll();

        private void OnHeaderSelectAllClicked(object? s, RoutedEventArgs e) => VM.ToggleSelectAll();

        // ───────────── FILTER DROPDOWN ─────────────

        // Etap 12: the Filter button now hosts a XAML Flyout (4 sections bound
        // directly to the VM filter properties), so the old code-behind
        // ContextMenu builder was removed.

        // ───────────── STATUS COLUMN VISIBILITY ─────────────

        private void SyncStatusColumnVisibility()
        {
            if (MainDataGrid?.Columns?.Count > 1)
                MainDataGrid.Columns[1].IsVisible = VM.HasAnyStatusIcons;
        }

        // ───────────── LOG LIST — COPY / SELECT ALL ─────────────

        private void OnLogListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (e.Key == Key.A)
                {
                    LogListBox.SelectAll();
                    e.Handled = true;
                }
                else if (e.Key == Key.C)
                {
                    CopySelectedLogs();
                    e.Handled = true;
                }
            }
        }

        private void OnCopyLogs(object? s, RoutedEventArgs e) => CopySelectedLogs();

        private void OnSelectAllLogs(object? s, RoutedEventArgs e) => LogListBox.SelectAll();

        private async void CopySelectedLogs()
        {
            try
            {
                var items = LogListBox.SelectedItems;
                if (items == null || items.Count == 0) return;
                var lines = items.Cast<LogMessage>()
                    .Select(m => $"{m.DisplayTimestamp}  {m.Level,-7}  {m.Message}");
                var text = string.Join(Environment.NewLine, lines);
                await VM.WriteClipboardAsync(text);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(CopySelectedLogs)); }
        }

        // ───────────── QC LIST — COPY / SELECT ALL ─────────────

        private void OnQcListKeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.Key == Key.A) { QcListBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.C) { CopySelectedQcRows(); e.Handled = true; }
        }

        private void OnCopyQcRows(object? s, RoutedEventArgs e) => CopySelectedQcRows();

        private void OnSelectAllQcRows(object? s, RoutedEventArgs e) => QcListBox.SelectAll();

        /// <summary>
        /// Copy the selection as tab-separated text, so it pastes into a spreadsheet
        /// or a bug report as-is. A category header copies as its own line — the
        /// selection is what the user sees, headers included.
        /// </summary>
        private async void CopySelectedQcRows()
        {
            try
            {
                var items = QcListBox.SelectedItems;
                if (items == null || items.Count == 0) return;

                var lines = new List<string>();
                foreach (var item in items)
                {
                    switch (item)
                    {
                        case QcGroupItem g:
                            lines.Add($"[{g.Label}]\t{g.Count}");
                            break;
                        case ProblemListItem p:
                            lines.Add(string.Join("\t", p.Row.Key, p.SeverityName, p.KindLabel, p.Title,
                                                  p.Qc is { } q && q.Before != q.After ? q.After : ""));
                            break;
                    }
                }
                await VM.WriteClipboardAsync(string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(CopySelectedQcRows)); }
        }

        // ───────────── PROBLEM LIST — COPY / SELECT ALL ─────────────

        private void OnProblemListKeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.Key == Key.A) { ProblemListBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.C) { CopySelectedProblems(); e.Handled = true; }
        }

        private void OnCopyProblems(object? s, RoutedEventArgs e) => CopySelectedProblems();

        private void OnSelectAllProblems(object? s, RoutedEventArgs e) => ProblemListBox.SelectAll();

        private async void CopySelectedProblems()
        {
            try
            {
                var items = ProblemListBox.SelectedItems;
                if (items == null || items.Count == 0) return;
                var lines = items.Cast<ProblemListItem>()
                    .Select(p => string.Join("\t", p.Row.Key, p.SeverityName, p.KindLabel, p.Title));
                await VM.WriteClipboardAsync(string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(CopySelectedProblems)); }
        }

        // ───────────── GRID ↔ PANEL SELECTION SYNC ─────────────

        /// <summary>
        /// Selecting a row in the grid highlights its entry in whichever bottom
        /// panel is open, so the two views never disagree about "the current
        /// problem". One-way on purpose: the reverse direction is the [Go] button
        /// and double-click, which also SCROLL the grid — doing that on every
        /// click here would yank the view around while the user is reading.
        /// </summary>
        private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!VM.ShowLogs) return;
            if (MainDataGrid.SelectedItem is not TranslationRow row) return;

            // Deferred and coalesced. Doing this synchronously — inside the
            // DataGrid's own selection change, and right after RevealQcEntry
            // inserted a whole category into the list — is what crashed the app
            // on 2026-09-01 (see Utils/ListReveal). Holding Find Next or arrowing
            // through rows now reveals once, for the row that ends up selected.
            bool scheduled = _pendingPanelReveal is not null;
            _pendingPanelReveal = row;
            if (!scheduled)
                Avalonia.Threading.Dispatcher.UIThread.Post(RevealSelectedRowInPanel,
                    Avalonia.Threading.DispatcherPriority.Background);
        }

        private TranslationRow? _pendingPanelReveal;

        private void RevealSelectedRowInPanel()
        {
            var row = _pendingPanelReveal;
            _pendingPanelReveal = null;
            // Still the same selection, and the panel still open on a list tab?
            if (row is null || !VM.ShowLogs || !ReferenceEquals(MainDataGrid.SelectedItem, row)) return;

            if (VM.IsQcTabActive)
            {
                var entry = VM.RevealQcEntry(row);
                if (entry is not null) ListReveal.SelectAndReveal(QcListBox, entry);
            }
            else if (VM.IsProblemsTabActive)
            {
                var entry = VM.ProblemItems.FirstOrDefault(i => ReferenceEquals(i.Row, row));
                if (entry is not null) ListReveal.SelectAndReveal(ProblemListBox, entry);
            }
        }
    }
}
