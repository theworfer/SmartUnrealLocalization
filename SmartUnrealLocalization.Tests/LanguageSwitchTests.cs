using System.ComponentModel;
using SmartUnrealLocalization.Localization;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Switching the interface language has to repaint the window, not just change
/// the config.
///
/// <para>It did not. Almost every string on screen binds through
/// <c>Loc.Instance</c>'s INDEXER, and <c>Load</c> announced the change by
/// raising <c>PropertyChanged("Item[]")</c> — which is WPF's name for an
/// indexer. Avalonia's is <c>"Item"</c>, so the notification matched nothing:
/// not one binding re-read, and picking a language changed the saved setting
/// and the fonts while every visible word stayed as it was until the next
/// start. Nothing threw, nothing logged, and the setting really had been
/// applied — so the only symptom was a user saying the switch "doesn't work".</para>
///
/// <para>The fix raises <c>null</c>, which means "every property changed" and is
/// independent of what any framework calls its indexer. These tests pin the
/// contract in a form that does not need Avalonia; the framework end of it was
/// measured in a headless render harness.</para>
/// </summary>
[Collection("AppState")]
public class LanguageSwitchTests
{
    /// <summary>Names Avalonia acts on: null and "" mean everything.</summary>
    private static bool RefreshesEveryBinding(string? name)
        => string.IsNullOrEmpty(name);

    [Fact]
    public void Switching_the_language_notifies_every_binding()
    {
        string original = Loc.Instance.CurrentLanguage;
        var names = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => names.Add(e.PropertyName);

        Loc.Instance.PropertyChanged += handler;
        try
        {
            Loc.Instance.Load("uk");
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
            Loc.Instance.Load(original);
        }

        Assert.NotEmpty(names);
        Assert.True(names.Exists(RefreshesEveryBinding),
            "Load must announce the change with a name every binding acts on (null or \"\"). "
            + "It raised: " + string.Join(", ", names.ConvertAll(n => n is null ? "null" : $"\"{n}\"")) + ". "
            + "\"Item[]\" is WPF's indexer name and matches nothing in Avalonia — that was the bug.");
    }

    [Fact]
    public void The_language_event_fires_so_code_set_text_can_follow()
    {
        // Panels that build ComboBox items or write placeholder labels in code
        // cannot re-read on their own; they subscribe to this.
        string original = Loc.Instance.CurrentLanguage;
        int fired = 0;
        void OnChanged() => fired++;

        Loc.Instance.LanguageChanged += OnChanged;
        try
        {
            Loc.Instance.Load("de");
            Loc.Instance.Load("ja");
        }
        finally
        {
            Loc.Instance.LanguageChanged -= OnChanged;
            Loc.Instance.Load(original);
        }

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Repeated_switching_returns_the_same_strings_every_time()
    {
        string original = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.Load("en");
            string english = Loc.T("menu.file");
            string englishFont = Loc.Instance.FontFamily;

            foreach (var code in new[] { "uk", "ja", "de", "zh-Hant", "en", "ko", "en" })
                Loc.Instance.Load(code);

            // The dictionaries are cached and handed out by reference; a caller
            // that mutated one would corrupt every later switch.
            Assert.Equal(english, Loc.T("menu.file"));
            Assert.Equal(englishFont, Loc.Instance.FontFamily);
            Assert.Equal("en", Loc.Instance.CurrentLanguage);
        }
        finally
        {
            Loc.Instance.Load(original);
        }
    }

    [Fact]
    public void Each_language_reports_its_own_font_stack()
    {
        string original = Loc.Instance.CurrentLanguage;
        try
        {
            // Without an explicit CJK face Windows falls back to SimSun / MS
            // Mincho — print serifs in a dense IDE-style UI. The stack is
            // declared in the language file, so switching must pick it up.
            Loc.Instance.Load("ja");
            Assert.Contains("Yu Gothic", Loc.Instance.FontFamily);

            Loc.Instance.Load("ko");
            Assert.Contains("Malgun", Loc.Instance.FontFamily);

            Loc.Instance.Load("en");
            Assert.DoesNotContain("Gothic", Loc.Instance.FontFamily);
        }
        finally
        {
            Loc.Instance.Load(original);
        }
    }
}
