using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Minimal modal yes/no confirm (custom chrome). Returns true via
    /// ShowDialog&lt;bool&gt; when the user clicks the confirm button; false on
    /// Cancel or the title-bar ✕. Used for the unsaved-changes guard on tab /
    /// window close (Etap 1.1).
    /// </summary>
    public partial class ConfirmDialog : Window
    {
        // Parameterless ctor for the Avalonia runtime XAML loader (AVLN3001).
        public ConfirmDialog() => InitializeComponent();

        public ConfirmDialog(string title, string message, string confirmLabel, string cancelLabel)
        {
            InitializeComponent();
            Title = title;
            if (this.FindControl<DialogTitleBar>("TitleBar") is { } tb) tb.Title = title;
            if (this.FindControl<TextBlock>("MessageText") is { } m) m.Text = message;
            if (this.FindControl<Button>("BtnConfirm") is { } bc) bc.Content = confirmLabel;
            if (this.FindControl<Button>("BtnCancel") is { } bx) bx.Content = cancelLabel;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
        private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

        /// <summary>Show modally over <paramref name="owner"/>; true = confirm clicked.</summary>
        public static Task<bool> ShowAsync(Window owner, string title, string message,
                                           string confirmLabel, string cancelLabel)
            => new ConfirmDialog(title, message, confirmLabel, cancelLabel).ShowDialog<bool>(owner);
    }
}
