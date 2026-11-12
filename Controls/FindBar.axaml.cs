using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// VS-style inline Find / Replace bar (Etap 9). Top-right overlay on the
    /// grid. Reads / writes <see cref="MainWindowViewModel"/>'s FindBar state
    /// via DataContext binding. Most behaviour is driven by VM commands;
    /// this code-behind handles keyboard shortcuts (Enter / Esc) and the
    /// "In column" dropdown menu that doesn't fit cleanly into a XAML-only
    /// pattern.
    /// </summary>
    public partial class FindBar : UserControl
    {
        public FindBar()
        {
            InitializeComponent();
            DataContextChanged += OnVmChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private MainWindowViewModel? _vm;

        private void OnVmChanged(object? sender, EventArgs e)
        {
            // Detach from previous VM if any (defensive — DataContext usually
            // only changes once at construction).
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;

            _vm = DataContext as MainWindowViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                // Initial sync — the bar might already be open at the time
                // DataContext arrives (e.g. after a hot-reload).
                if (_vm.IsFindBarOpen) HandleOpenedChanged();
                UpdateColumnLabel();
            }
        }

        private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.IsFindBarOpen): HandleOpenedChanged(); break;
                case nameof(MainWindowViewModel.FindBarReplaceMode): HandleReplaceModeChanged(); break;
                case nameof(MainWindowViewModel.FindColumn): UpdateColumnLabel(); break;
            }
        }

        private void HandleOpenedChanged()
        {
            if (_vm?.IsFindBarOpen != true) return;
            // Focus the find input after layout finishes — needs Loaded-style
            // dispatch so the TextBox is in the visual tree.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var input = _vm.FindBarReplaceMode ? this.FindControl<TextBox>("ReplaceInput")
                                                   : this.FindControl<TextBox>("FindInput");
                input?.Focus();
                input?.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void HandleReplaceModeChanged()
        {
            // Rotate chevron glyph to communicate the toggle state — points
            // down (▼) when replace expanded, right (▶) when collapsed.
            if (this.FindControl<TextBlock>("ChevronGlyph") is { } glyph)
                glyph.Text = _vm?.FindBarReplaceMode == true ? "▼" : "▶";

            // If the user just expanded into replace mode, jump focus to the
            // replace input — typical IDE UX (Ctrl+H → cursor already in replace).
            if (_vm?.FindBarReplaceMode == true)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    this.FindControl<TextBox>("ReplaceInput")?.Focus();
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        private void UpdateColumnLabel()
        {
            if (_vm is null) return;
            string colKey = _vm.FindColumn switch
            {
                FindBarColumn.Key => "find.col.key",
                FindBarColumn.Source => "find.col.source",
                FindBarColumn.Target => "find.col.target",
                _ => "find.col.all",
            };
            if (this.FindControl<TextBlock>("ColumnTagLabel") is { } lbl)
                lbl.Text = string.Format(Loc.T("find.inColumnTag"), Loc.T(colKey));
        }

        // ───────────── Keyboard shortcuts ─────────────

        private void OnFindInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (_vm is null) return;
            if (e.Key == Key.Escape)
            {
                _vm.CloseFindBar();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _vm.FindPrev();
                else _vm.FindNext();
                e.Handled = true;
            }
        }

        private void OnReplaceInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (_vm is null) return;
            if (e.Key == Key.Escape)
            {
                _vm.CloseFindBar();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) _vm.ReplaceAll();
                else _vm.ReplaceNext();
                e.Handled = true;
            }
        }

        // ───────────── In column dropdown ─────────────

        private void OnColumnMenu(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control anchor || _vm is null) return;

            var menu = new ContextMenu();
            void AddItem(string locKey, FindBarColumn col)
            {
                var mi = new MenuItem
                {
                    Header = Loc.T(locKey),
                    Icon = new CheckBox
                    {
                        IsChecked = _vm.FindColumn == col,
                        IsHitTestVisible = false,
                        MinWidth = 0,
                        Padding = new Avalonia.Thickness(0),
                    },
                };
                mi.Click += (_, _) => _vm.FindColumn = col;
                menu.Items.Add(mi);
            }
            AddItem("find.col.all", FindBarColumn.All);
            AddItem("find.col.key", FindBarColumn.Key);
            AddItem("find.col.source", FindBarColumn.Source);
            AddItem("find.col.target", FindBarColumn.Target);

            menu.Open(anchor);
        }
    }
}
