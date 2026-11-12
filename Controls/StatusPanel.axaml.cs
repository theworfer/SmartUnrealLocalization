using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Status side panel — shows file progress (translated %, counts) and persistent
    /// app-usage counters. All values bind directly to MainWindowViewModel properties;
    /// no internal state.
    /// </summary>
    public partial class StatusPanel : UserControl
    {
        public event Action? ClosePanelRequested;

        public StatusPanel()
        {
            InitializeComponent();
        }

        private void OnClosePanel(object? s, RoutedEventArgs e) => ClosePanelRequested?.Invoke();
    }
}
