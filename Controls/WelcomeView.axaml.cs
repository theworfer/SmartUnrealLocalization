using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Welcome view (visible when no file is loaded). Visual shell for Commit A;
    /// recent-files data binding + Open folder wiring lands in Commit B.
    /// Click events bubble up to MainWindow via the four events.
    /// </summary>
    public partial class WelcomeView : UserControl
    {
        public event Action? OpenFileRequested;
        public event Action? ImportTargetRequested;
        public event Action? MergeRequested;
        public event Action<string>? RecentFileActivated;

        public WelcomeView()
        {
            InitializeComponent();
        }

        private void OnOpenFile(object? s, RoutedEventArgs e) => OpenFileRequested?.Invoke();
        private void OnImportTarget(object? s, RoutedEventArgs e) => ImportTargetRequested?.Invoke();
        private void OnMerge(object? s, RoutedEventArgs e) => MergeRequested?.Invoke();

        private void OnRecentClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Avalonia.Controls.Button btn && btn.Tag is string path && !string.IsNullOrEmpty(path))
            {
                RecentFileActivated?.Invoke(path);
            }
        }
    }
}
