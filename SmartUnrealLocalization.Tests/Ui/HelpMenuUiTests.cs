using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Views;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Help → Documentation and Help → Licenses. Documentation used to be greyed out in
/// every development build: the guide was only put next to the exe by the release
/// script, so a build run from the IDE had no docs/ folder.
/// </summary>
[Collection("AppState")]
public class HelpMenuUiTests
{
    [Fact]
    public void The_user_guide_is_copied_next_to_every_build()
    {
        foreach (var name in new[] { "USER-GUIDE.md", "USER-GUIDE.uk.md", "FAQ.md", "TROUBLESHOOTING.md" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "docs", name)), $"docs/{name} is not in the build output");
    }

    [Fact]
    public void Every_license_file_is_embedded_in_the_exe()
    {
        string root = TestPaths.RepoRoot();
        var embedded = LegalTexts.LicenseFiles().Select(f => f.FileName).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "licenses"), "*.txt"))
        {
            string name = Path.GetFileName(file);
            Assert.Contains(name, embedded);
            var entry = LegalTexts.LicenseFiles().Single(f => f.FileName == name);
            Assert.False(string.IsNullOrWhiteSpace(LegalTexts.Read(entry.Resource)), name + " is empty");
        }
        Assert.Contains("MIT License", LegalTexts.Read(LegalTexts.AppLicense));
        Assert.Contains("Avalonia", LegalTexts.Read(LegalTexts.Notices));
    }

    [Fact]
    public Task The_licenses_window_opens_on_the_program_license()
        => WithWindowAsync(async (window, vm) =>
        {
            string original = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Load("uk");
                var dlg = new LicenseWindow();
                dlg.Show(window);
                await Settle();

                Assert.Equal("Smart Unreal Localization — ліцензія", dlg.Docs[0].Label);
                Assert.Equal("Сторонні компоненти", dlg.Docs[1].Label);
                Assert.True(dlg.Docs.Count >= 2 + Directory.GetFiles(Path.Combine(TestPaths.RepoRoot(), "licenses"), "*.txt").Length);
                Assert.Contains("MIT License", dlg.DocText.Text);
                string? shot = Environment.GetEnvironmentVariable("SUL_SHOT_DIR");
                if (shot != null)
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(dlg)?.Save(Path.Combine(shot, "licenses.png"));
                dlg.Close();
            }
            finally { Loc.Instance.Load(original); }
        });
}
