using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>One line of the prompt history: its title, its text, open or closed.</summary>
    public partial class PromptHistoryItem : ObservableObject
    {
        public PromptHistoryItem(PromptRecord record) => Record = record;

        public PromptRecord Record { get; }
        public string Name => Record.DisplayName;
        public string Text => Record.Text;

        [ObservableProperty] private bool _isExpanded;
    }

    /// <summary>
    /// Prompt history side panel: every distinct prompt the app sent (single
    /// translation, multi-translation, AI QC), newest first. A title opens its text
    /// in place — several can be open — and Copy puts the whole prompt on the
    /// clipboard; the text itself is selectable for copying a part.
    /// </summary>
    public partial class PromptHistoryPanel : UserControl
    {
        public event Action? ClosePanelRequested;

        private readonly ObservableCollection<PromptHistoryItem> _items = new();
        internal IReadOnlyList<PromptHistoryItem> Items => _items;

        public PromptHistoryPanel()
        {
            InitializeComponent();
            List.ItemsSource = _items;

            Reload();
        }

        // Loaded when shown and again when a run adds a prompt while it is open.
        // The history's event is static: subscribed only while the panel is in a
        // window, so a closed window's panel is not kept alive by it.
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            PromptHistory.Changed += OnHistoryChanged;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            PromptHistory.Changed -= OnHistoryChanged;
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && IsVisible) Reload();
        }

        // Record() can be called from any thread, so the reload is posted.
        private void OnHistoryChanged() => Dispatcher.UIThread.Post(Reload);

        /// <summary>Re-read the history from disk, keeping open prompts open.</summary>
        internal void Reload()
        {
            var open = _items.Where(i => i.IsExpanded).Select(i => i.Record.Number).ToHashSet();
            _items.Clear();
            foreach (var record in PromptHistory.LoadAll())
                _items.Add(new PromptHistoryItem(record) { IsExpanded = open.Contains(record.Number) });
            LblEmpty.IsVisible = _items.Count == 0;
        }

        private void OnToggle(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is PromptHistoryItem item)
                item.IsExpanded = !item.IsExpanded;
        }

        private async void OnCopy(object? sender, RoutedEventArgs e)
        {
            try
            {
                if ((sender as Control)?.DataContext is not PromptHistoryItem item) return;
                var vm = MainWindowViewModel.Current;
                if (vm == null) return;
                if (await vm.WriteClipboardAsync(item.Text))
                    vm.StatusText = string.Format(Loc.T("prompts.copied"), item.Name);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnCopy)); }
        }

        private void OnOpenFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(PromptHistory.RootDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{PromptHistory.RootDir}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Console.WriteLine($"[shell] prompts folder: {ex.Message}"); }
        }

        private void OnClosePanel(object? sender, RoutedEventArgs e) => ClosePanelRequested?.Invoke();
    }
}
