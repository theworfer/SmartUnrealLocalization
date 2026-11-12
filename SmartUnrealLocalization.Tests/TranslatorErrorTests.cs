using System.Net;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Provider errors the user reads (audit 2026-11): a gateway that answers with a
/// whole HTML page must not dump it into the log and the status bar, and the
/// missing-key message must name the real place to fix it.
/// </summary>
[Collection("AppState")]
public class TranslatorErrorTests
{
    [Fact]
    public async Task An_html_error_page_is_cut_to_a_readable_message()
    {
        Localization.Loc.Instance.Load("en");
        var resp = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>" + new string('x', 50_000) + "</html>"),
        };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => TranslatorService.EnsureSuccess(resp, CancellationToken.None));
        Assert.Contains("502", ex.Message);
        Assert.True(ex.Message.Length < 700, $"message is {ex.Message.Length} chars");
    }

    [Fact]
    public void No_message_points_at_the_removed_Edit_Preferences_menu()
    {
        string root = TestPaths.RepoRoot();
        foreach (var dir in new[] { "Services", "ViewModels", "Controls", "Views", "Localization" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories)
                                          .Where(f => f.EndsWith(".cs") || f.EndsWith(".json")))
                Assert.DoesNotContain("Edit → Preferences", File.ReadAllText(file));
    }
}