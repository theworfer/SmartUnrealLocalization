using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Prompt history: every distinct prompt, one file each, three modes in three
/// folders, nothing stored twice — and the panel that shows them. Plus Reset cache,
/// which deletes the history together with the Translation Memory and the settings.
/// All of it runs in the sandboxed profile the real-window tests use.
/// </summary>
[Collection("AppState")]
public class PromptHistoryTests
{
    [Fact]
    public Task Prompts_are_stored_once_per_mode_file_and_text()
        => WithWindowAsync(async (window, vm) =>
        {
            PromptHistory.Clear();
            var when = new DateTime(2026, 11, 10, 17, 26, 0);

            var first = PromptHistory.Record(PromptMode.SingleTranslation, @"C:\games\Game.locres", "Translate A", when);
            Assert.NotNull(first);
            Assert.Equal("1_Game.locres_11/10/2026_17:26_SingleTranslation", first!.DisplayName);

            // The same prompt again — same mode, same file — adds nothing.
            Assert.Null(PromptHistory.Record(PromptMode.SingleTranslation, "Game.locres", "Translate A"));
            // Another text, another mode or another file is another prompt.
            Assert.NotNull(PromptHistory.Record(PromptMode.SingleTranslation, "Game.locres", "Translate B"));
            Assert.NotNull(PromptHistory.Record(PromptMode.AIQC, "Game.locres", "Translate A"));
            Assert.NotNull(PromptHistory.Record(PromptMode.Multitranslation, "Other.locres", "Translate A"));
            Assert.Null(PromptHistory.Record(PromptMode.AIQC, "Game.locres", "   "));

            var all = PromptHistory.LoadAll();
            Assert.Equal(new[] { 4, 3, 2, 1 }, all.Select(r => r.Number));           // newest first

            // One file per prompt, each mode in its own folder.
            Assert.Equal(2, Directory.GetFiles(PromptHistory.DirFor(PromptMode.SingleTranslation)).Length);
            Assert.Single(Directory.GetFiles(PromptHistory.DirFor(PromptMode.AIQC)));
            Assert.Single(Directory.GetFiles(PromptHistory.DirFor(PromptMode.Multitranslation)));
            Assert.StartsWith(AppConfig.ConfigDir(), PromptHistory.RootDir);

            // The panel lists them, opens several at once and survives a reload.
            vm.ShowPromptHistoryPanel = true;
            await Settle();
            var panel = window.PromptHistoryPanelHost;
            Assert.Equal(4, panel.Items.Count);
            Assert.True(vm.IsSidePanelVisible);
            panel.Items[0].IsExpanded = true;
            panel.Items[2].IsExpanded = true;
            PromptHistory.Record(PromptMode.AIQC, "Game.locres", "Review C");
            await Settle();
            Assert.Equal(5, panel.Items.Count);
            Assert.Equal(new[] { 4, 2 }, panel.Items.Where(i => i.IsExpanded).Select(i => i.Record.Number));
            Assert.Equal("Translate A", panel.Items.Single(i => i.Record.Number == 4).Text);

            string? shot = Environment.GetEnvironmentVariable("SUL_SHOT_DIR");
            if (shot != null)
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(shot, "prompts.png"));

            // Opening it closes the other side panels, like every panel on the rail.
            vm.ShowSettingsPanel = true;
            Assert.False(vm.ShowPromptHistoryPanel);
            PromptHistory.Clear();
        });

    [Fact]
    public void A_multi_language_run_is_one_readable_prompt()
    {
        var req = new MultiTranslateRequest
        {
            SourceLanguage = "English",
            Targets = new[]
            {
                new MultiTranslateTarget { Locale = "de", Prompt = "Be formal" },
                new MultiTranslateTarget { Locale = "fr", Prompt = "Be formal" },
                new MultiTranslateTarget { Locale = "ja", Prompt = "Use keigo" },
            },
        };
        string text = MultiTranslateJob.DescribePrompts(req);
        Assert.Contains("[de, fr]", text);
        Assert.Contains("Translate from English to {language}", text);
        Assert.Contains("[ja]", text);
        Assert.Contains("Translate from English to ja", text);
        Assert.Equal(1, text.Split("Be formal").Length - 1);       // shared prompt shown once
    }

    [Fact]
    public Task Reset_cache_asks_twice_and_deletes_exactly_what_it_lists()
        => WithWindowAsync(async (window, vm) =>
        {
            string dir = AppConfig.ConfigDir();
            var cfg = AppConfig.Instance;
            string language = cfg.Language;
            bool dark = cfg.IsDarkTheme;
            string tm = Path.Combine(dir, "translation_memory_english_ukrainian.json");
            string qc = Path.Combine(dir, "qc_cache_english_ukrainian.json");
            string review = Path.Combine(dir, "reviews", "keep.json");
            Directory.CreateDirectory(Path.GetDirectoryName(review)!);

            void Seed()
            {
                File.WriteAllText(tm, "{}");
                File.WriteAllText(qc, "{}");
                File.WriteAllText(review, "{}");
                PromptHistory.Record(PromptMode.SingleTranslation, "Game.locres", "Translate A " + Guid.NewGuid());
                cfg.ApiKey = "test-key";
                cfg.TranslationBatchSize = 77;
                cfg.ProtectedTerms = new List<string> { "Aloy" };
                cfg.QcSkipEmpty = false;
                cfg.Save();
            }

            var panel = window.SettingsPanelHost;
            var asked = new List<string>();
            try
            {
                // Refused at the SECOND question: nothing is touched.
                Seed();
                panel.ConfirmOverride = (_, message, _) => { asked.Add(message); return Task.FromResult(asked.Count < 2); };
                await panel.ResetCacheAsync();
                Assert.Equal(2, asked.Count);
                Assert.True(File.Exists(tm));
                Assert.Equal(77, cfg.TranslationBatchSize);
                Assert.NotEmpty(PromptHistory.LoadAll());
                Assert.Contains("Translation Memory", asked[0]);

                // Confirmed twice: the listed data goes, the user's own work stays.
                asked.Clear();
                panel.ConfirmOverride = (_, message, _) => { asked.Add(message); return Task.FromResult(true); };
                await panel.ResetCacheAsync();
                await Settle();
                Assert.Equal(2, asked.Count);
                Assert.False(File.Exists(tm));
                Assert.False(File.Exists(qc));
                Assert.Empty(PromptHistory.LoadAll());
                Assert.True(File.Exists(review));

                Assert.Equal("", cfg.ApiKey);
                Assert.Equal(new AppConfig().TranslationBatchSize, cfg.TranslationBatchSize);
                Assert.Empty(cfg.ProtectedTerms);
                Assert.True(cfg.QcSkipEmpty);
                Assert.Equal(language, cfg.Language);                 // language and theme stay
                Assert.Equal(dark, cfg.IsDarkTheme);
                Assert.Same(cfg, AppConfig.Instance);                 // same instance everyone holds

                // …and it is what is on disk, not only in memory.
                string saved = File.ReadAllText(Path.Combine(dir, "config.json"));
                Assert.DoesNotContain("Aloy", saved);
                Assert.DoesNotContain("ApiKeyProtected\": \"", saved);
            }
            finally
            {
                panel.ConfirmOverride = null;
                UseSandbox();
                foreach (var f in new[] { tm, qc, review }) if (File.Exists(f)) File.Delete(f);
            }
        });
}
