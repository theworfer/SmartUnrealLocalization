using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using SmartUnrealLocalization.Models;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The main menu holds only working commands. Before 1.0 it carried eight items that
/// were permanently greyed out (File → Recent files, Translation Memory twice, Filter
/// panel, source/target language, project settings, glossary manager) — a product
/// that looks unfinished. File → Recent files is now the real list.
/// </summary>
[Collection("AppState")]
public class MainMenuUiTests
{
    [Fact]
    public void No_menu_item_is_a_permanently_disabled_placeholder()
    {
        string xaml = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Views", "MainWindow.axaml"));
        var placeholders = Regex.Matches(xaml, @"<MenuItem\b[^>]*IsEnabled=""False""[^>]*>")
            .Select(m => Regex.Replace(m.Value, @"\s+", " ")).ToList();
        Assert.True(placeholders.Count == 0, "Placeholder menu items:\n" + string.Join("\n", placeholders));
    }

    [Fact]
    public Task File_menu_lists_the_recent_files()
        => WithWindowAsync(async (window, vm) =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "sul-recent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var cfg = AppConfig.Instance;
            var saved = cfg.RecentFiles.ToList();
            try
            {
                cfg.RecentFiles.Clear();
                string a = Path.Combine(dir, "a.locres"), b = Path.Combine(dir, "b.locres");
                File.WriteAllText(a, ""); File.WriteAllText(b, "");
                cfg.AddRecentFile(a);
                cfg.AddRecentFile(b);

                var file = window.GetLogicalDescendants().OfType<MenuItem>()
                    .First(m => m.Items.Contains(window.MenuRecent));
                file.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, file));
                await Settle();

                string? shot = Environment.GetEnvironmentVariable("SUL_SHOT_DIR");
                if (shot != null)
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(shot, "header.png"));

                var headers = window.MenuRecent.Items.OfType<MenuItem>().Select(m => m.Header).ToList();
                Assert.Equal(new object[] { "b.locres", "a.locres" }, headers);
                Assert.True(window.MenuRecent.IsEnabled);

                // Opening the Recent submenu itself must not rebuild it under the pointer.
                var first = window.MenuRecent.Items[0];
                window.MenuRecent.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, window.MenuRecent));
                Assert.Same(first, window.MenuRecent.Items[0]);

                cfg.RecentFiles.Clear();
                file.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, file));
                Assert.Empty(window.MenuRecent.Items);
                Assert.False(window.MenuRecent.IsEnabled);
            }
            finally
            {
                cfg.RecentFiles.Clear();
                cfg.RecentFiles.AddRange(saved);
                Directory.Delete(dir, true);
            }
        });
}
