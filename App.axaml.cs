using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Views;

namespace SmartUnrealLocalization;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Apply saved theme + language before any window is constructed so DynamicResource
            // and Loc.Instance bindings resolve correctly.
            ThemeManager.Apply(AppConfig.Instance.IsDarkTheme);
            Loc.Instance.Load(AppConfig.Instance.Language);
            // Fonts come FROM the language file, so they can only be published
            // once the language is loaded — and before the first window is built.
            ThemeManager.ApplyFonts();

            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };

            CrashReporter.Attach(vm.DescribeForCrashReport, vm.RecoverUnsavedEdits, vm.RecentLogLinesForCrashReport);
            CrashReporter.StartHangWatchdog();
            desktop.MainWindow.Opened += (_, _) => vm.AnnouncePreviousSessionProblems();

            // Open files passed on the command line (file association /
            // drag-onto-exe), each in its own tab. Deferred to Opened so the
            // window's VM event wiring (validation dialog etc.) is in place.
            var args = desktop.Args ?? System.Array.Empty<string>();
            if (args.Length > 0)
            {
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    foreach (var arg in args)
                    {
                        if (string.IsNullOrWhiteSpace(arg) || !System.IO.File.Exists(arg)) continue;
                        try { await vm.OpenFileAsync(arg); }
                        catch (System.Exception ex) { System.Console.WriteLine($"[ArgsOpen] {ex}"); }
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
