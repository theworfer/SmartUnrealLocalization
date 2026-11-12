using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Reuse-grouping rules for AI translation. These decide when ONE paid
/// translation may be copied onto another row — get them wrong and either the
/// user pays many times over, or a context-specific translation is blindly
/// spread onto rows it does not fit.
/// </summary>
public class TmReuseTests
{
    [Fact]
    public void A_sent_key_is_the_strongest_discriminator()
        => Assert.Equal("Store.Charge", TmReuse.Tag("Store.Charge", "reference text"));

    [Fact]
    public void A_reference_translation_is_used_when_there_is_no_key()
        => Assert.Equal(TmReuse.RefTagPrefix + "Плата", TmReuse.Tag(null, "Плата"));

    [Fact]
    public void No_context_means_plain_source_only_reuse()
        => Assert.Null(TmReuse.Tag(null, null));

    [Fact]
    public void A_reference_tag_can_never_be_confused_with_a_key_tag()
    {
        // The prefix uses U+001F, which cannot occur in a game key or in real
        // source text — otherwise a reference could impersonate a key.
        Assert.Contains((char)0x1F, TmReuse.RefTagPrefix);
        Assert.NotEqual(TmReuse.Tag("Charge", null), TmReuse.Tag(null, "Charge"));
    }

    [Fact]
    public void Rows_with_the_same_source_and_no_context_still_auto_repeat()
        => Assert.True(TmReuse.CanShare("Charge", null, false, "Charge", null, false));

    [Fact]
    public void Rows_disambiguated_by_different_keys_are_never_shared()
    {
        // Weapon.Charge "Зарядити" must not be copied onto Store.Charge "Плата".
        Assert.False(TmReuse.CanShare("Charge", "Weapon.Charge", false,
                                      "Charge", "Store.Charge", false));
    }

    [Fact]
    public void Rows_with_different_references_are_never_shared()
        => Assert.False(TmReuse.CanShare("Charge", TmReuse.Tag(null, "Зарядити"), false,
                                         "Charge", TmReuse.Tag(null, "Плата"), false));

    [Fact]
    public void Rows_with_the_same_source_and_the_same_reference_are_shared()
        => Assert.True(TmReuse.CanShare("Charge", TmReuse.Tag(null, "Плата"), false,
                                        "Charge", TmReuse.Tag(null, "Плата"), false));

    [Fact]
    public void Different_sources_are_never_shared_however_they_are_tagged()
        => Assert.False(TmReuse.CanShare("Charge", null, false, "Discharge", null, false));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_row_carrying_a_comment_is_always_translated_on_its_own(bool commentA, bool commentB)
    {
        // A comment is a per-row instruction that must reach the model, so the
        // row can neither donate nor receive a shared translation.
        Assert.False(TmReuse.CanShare("Charge", null, commentA, "Charge", null, commentB));
    }

    [Fact]
    public void Sharing_is_case_and_whitespace_sensitive()
    {
        Assert.False(TmReuse.CanShare("Charge", null, false, "charge", null, false));
        Assert.False(TmReuse.CanShare("Charge", null, false, "Charge ", null, false));
    }
}

/// <summary>
/// The persistent Translation Memory. It is accumulated API spend, so the tests
/// redirect the config directory — a stray <c>Clear()</c> against the real
/// profile would destroy the user's memory.
/// </summary>
[Collection("AppState")]
public class TranslationMemoryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-tm-tests", Guid.NewGuid().ToString("N"));

    public TranslationMemoryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        AppConfig.ConfigDirOverride = _dir;
    }

    public void Dispose()
    {
        AppConfig.ConfigDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static TranslationMemoryService Tm(string from = "english", string to = "ukrainian")
    {
        var tm = new TranslationMemoryService();
        tm.SetLanguagePair(from, to);
        return tm;
    }

    // ── generic entries ──────────────────────────────────────────────────

    [Fact]
    public void A_stored_translation_is_found_again()
    {
        var tm = Tm();
        tm.Add("Play", "Грати");

        Assert.True(tm.TryGet("Play", out string hit));
        Assert.Equal("Грати", hit);
    }

    [Fact]
    public void Re_translating_a_string_updates_it_latest_wins()
    {
        // Deliberate: a corrected translation must replace the old one, so the
        // first-ever result is never immortal.
        var tm = Tm();
        tm.Add("Play", "Грати");
        tm.Add("Play", "Відтворити");

        tm.TryGet("Play", out string hit);
        Assert.Equal("Відтворити", hit);
    }

    [Theory]
    [InlineData("", "Грати")]
    [InlineData("   ", "Грати")]
    [InlineData("Play", "")]
    [InlineData("Play", "   ")]
    public void Blank_sources_or_translations_are_not_stored(string source, string translation)
    {
        var tm = Tm();
        tm.Add(source, translation);
        Assert.Equal(0, tm.Count);
    }

    // ── key-aware entries ────────────────────────────────────────────────

    [Fact]
    public void A_key_aware_entry_never_answers_a_generic_lookup()
    {
        // "Charge" → "Плата" is only true under Store.Charge; a plain lookup for
        // "Charge" elsewhere must not receive it.
        var tm = Tm();
        tm.AddKeyed("Store.Charge", "Charge", "Плата");

        Assert.False(tm.TryGet("Charge", out _));
        Assert.True(tm.TryGetKeyed("Store.Charge", "Charge", out string hit));
        Assert.Equal("Плата", hit);
    }

    [Fact]
    public void A_key_aware_entry_requires_the_same_key_and_the_same_source()
    {
        var tm = Tm();
        tm.AddKeyed("Store.Charge", "Charge", "Плата");

        Assert.False(tm.TryGetKeyed("Weapon.Charge", "Charge", out _));   // different key
        Assert.False(tm.TryGetKeyed("Store.Charge", "Recharge", out _));  // source changed (game update)
    }

    [Fact]
    public void Generic_and_key_aware_entries_coexist_without_overwriting_each_other()
    {
        var tm = Tm();
        tm.Add("Charge", "Зарядити");
        tm.AddKeyed("Store.Charge", "Charge", "Плата");

        tm.TryGet("Charge", out string generic);
        tm.TryGetKeyed("Store.Charge", "Charge", out string keyed);

        Assert.Equal("Зарядити", generic);
        Assert.Equal("Плата", keyed);
        Assert.Equal(2, tm.Count);
    }

    [Fact]
    public void A_source_containing_the_composite_separator_cannot_reach_a_keyed_entry()
    {
        // Control characters are legal in game text. Without this guard a source
        // crafted as "keysource" READ a translation meant for one specific
        // key — and storing it OVERWROTE that paid-for keyed entry.
        var tm = Tm();
        tm.AddKeyed("Store.Charge", "Charge", "Плата");
        string forged = "Store.Charge" + (char)0x1F + "Charge";

        Assert.False(tm.TryGet(forged, out _));

        tm.Add(forged, "ІН'ЄКЦІЯ");
        tm.TryGetKeyed("Store.Charge", "Charge", out string keyed);
        Assert.Equal("Плата", keyed);
    }

    [Fact]
    public void A_key_or_source_containing_the_separator_is_not_stored_as_a_keyed_entry()
    {
        var tm = Tm();
        tm.AddKeyed("Store" + (char)0x1F + "X", "Charge", "Плата");
        tm.AddKeyed("Store.Charge", "Cha" + (char)0x1F + "rge", "Плата");
        Assert.Equal(0, tm.Count);
    }

    // ── persistence ──────────────────────────────────────────────────────

    [Fact]
    public void Entries_survive_a_save_and_reload()
    {
        var tm = Tm();
        tm.Add("Play", "Грати");
        tm.AddKeyed("Store.Charge", "Charge", "Плата");
        tm.Save();

        var reloaded = Tm();
        Assert.True(reloaded.TryGet("Play", out string generic));
        Assert.True(reloaded.TryGetKeyed("Store.Charge", "Charge", out string keyed));
        Assert.Equal("Грати", generic);
        Assert.Equal("Плата", keyed);
    }

    [Fact]
    public void Each_language_pair_keeps_its_own_memory()
    {
        var uk = Tm("english", "ukrainian");
        uk.Add("Play", "Грати");
        uk.Save();

        var pl = Tm("english", "polish");
        Assert.False(pl.TryGet("Play", out _));
        Assert.Equal(0, pl.Count);
    }

    [Fact]
    public void A_corrupt_memory_is_preserved_for_recovery_not_silently_replaced()
    {
        // Overwriting an unreadable TM with an empty one would throw away every
        // translation the user has paid for.
        var tm = Tm();
        tm.Add("Play", "Грати");
        tm.Save();

        string path = Path.Combine(_dir, "translation_memory_english_ukrainian.json");
        File.WriteAllText(path, "{ this is not json");

        var reloaded = Tm();
        Assert.Equal(0, reloaded.Count);                     // starts empty rather than throwing
        Assert.NotEmpty(Directory.GetFiles(_dir, "*corrupt*")); // and the broken file is kept
    }

    [Fact]
    public void Switching_to_the_same_pair_again_does_not_reload()
    {
        var tm = Tm();
        tm.Add("Play", "Грати");
        tm.SetLanguagePair("english", "ukrainian");   // same pair — must be a no-op

        Assert.Equal(1, tm.Count);                    // unsaved entry still in memory
    }
}
