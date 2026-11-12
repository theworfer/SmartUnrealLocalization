using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();

            // The version comes from the assembly rather than a literal in the
            // XAML, so setting <Version> in the csproj is the only step a release
            // needs. Set once here: the window is modal, so the interface
            // language cannot change underneath it.
            var v = typeof(AboutWindow).Assembly.GetName().Version;
            string version = v is null ? "?" : $"{v.Major}.{v.Minor}.{System.Math.Max(0, v.Build)}";
            VersionText.Text = string.Format(Loc.T("about.version"), version);
        }

        private void OnClose(object? s, RoutedEventArgs e) => Close();
    }
}
