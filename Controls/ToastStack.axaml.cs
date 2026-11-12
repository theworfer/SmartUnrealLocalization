using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Overlay host for the toast queue. Placed by MainWindow.axaml inside
    /// the outermost Grid so it floats above the DockPanel (above status bar,
    /// above the grid, but below the FindReplacePanel modal and the
    /// ProblemPopover). Binds its ItemsControl to <c>ToastService.Active</c>
    /// in the constructor so every push/pop on the singleton flows here.
    /// </summary>
    public partial class ToastStack : UserControl
    {
        public ToastStack()
        {
            InitializeComponent();
            // Direct singleton binding — no DataContext indirection so this
            // works regardless of where the host inserts the control.
            this.FindControl<ItemsControl>("ToastsHost")!.ItemsSource = ToastService.Instance.Active;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
