using Locres.IO;
using Xunit;

namespace Locres.IO.Tests;

public class MergerTests
{
    private static LocresDocument Doc(
        params (string Name, (string Key, uint Hash, string Value)[] Entries)[] namespaces)
        => LocresDocument.Parse(TestFileBuilder.Legacy(namespaces));

    [Fact]
    public void CarriesTranslationsByNamespaceAndKey()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад 1"), ("K2", 2u, "Переклад 2") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Source 1"), ("K2", 2u, "Source 2") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal("Переклад 1", result.Document.Namespaces[0].Entries[0].Value);
        Assert.Equal("Переклад 2", result.Document.Namespaces[0].Entries[1].Value);
        Assert.Empty(result.NewKeys);
        Assert.Empty(result.SourceChangedKeys);
    }

    [Fact]
    public void NewKeys_DetectedAndKeepNewSourceValue()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Src"), ("K2", 2u, "Fresh source") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal(new[] { ("Game", "K2") }, result.NewKeys);
        Assert.Equal("Fresh source", result.Document.Namespaces[0].Entries[1].Value);
    }

    [Fact]
    public void NewNamespace_IsAddedWithAllKeysFlaggedNew()
    {
        // The original tool silently DROPPED namespaces absent from the old
        // file — exactly the wrong behavior for game updates. We add them.
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") }));
        var newer = Doc(
            ("Game", new[] { ("K1", 1u, "Src") }),
            ("DLC", new[] { ("D1", 5u, "Dlc line 1"), ("D2", 6u, "Dlc line 2") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal(2, result.Document.Namespaces.Count);
        Assert.Equal("DLC", result.Document.Namespaces[1].Name);
        Assert.Equal(new[] { ("DLC", "D1"), ("DLC", "D2") }, result.NewKeys);
        Assert.Equal("Переклад", result.Document.Namespaces[0].Entries[0].Value);
    }

    [Fact]
    public void KeysRemovedFromTheGame_DisappearFromTheResult()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад"), ("Removed", 9u, "Старе") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Src") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal(1, result.Document.EntryCount);
        Assert.Equal("K1", result.Document.Namespaces[0].Entries[0].Key);
    }

    [Fact]
    public void SourceHashChange_RefusesToCarryTheStaleTranslation()
    {
        // The default, and the reason the option exists. A different hash means
        // the original text changed, so the old translation was written for
        // wording the game no longer has. Carrying it would ship a line that
        // says the wrong thing.
        var old = Doc(("Game", new[] { ("K1", 0xAAAAu, "\u0421\u0442\u0430\u0440\u0438\u0439 \u043f\u0435\u0440\u0435\u043a\u043b\u0430\u0434") }));
        var newer = Doc(("Game", new[] { ("K1", 0xBBBBu, "Changed source") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal(new[] { ("Game", "K1") }, result.SourceChangedKeys);
        Assert.Equal("Changed source", result.Document.Namespaces[0].Entries[0].Value);
        Assert.Empty(result.CarriedValues);
        // Not carried, but not lost either \u2014 handed back so a caller can show it.
        Assert.Equal("\u0421\u0442\u0430\u0440\u0438\u0439 \u043f\u0435\u0440\u0435\u043a\u043b\u0430\u0434", result.RejectedValues[("Game", "K1")]);
        // The hash is the NEW file\u2019s either way (matches the new game build).
        Assert.Equal(0xBBBBu, result.Document.Namespaces[0].Entries[0].SourceStringHash);
    }

    [Fact]
    public void SourceHashChange_CarriesAnyway_WhenVerificationIsTurnedOff()
    {
        var old = Doc(("Game", new[] { ("K1", 0xAAAAu, "\u0421\u0442\u0430\u0440\u0438\u0439 \u043f\u0435\u0440\u0435\u043a\u043b\u0430\u0434") }));
        var newer = Doc(("Game", new[] { ("K1", 0xBBBBu, "Changed source") }));

        var result = LocresMerger.Merge(old, newer,
            new LocresMergeOptions { RequireMatchingSourceHash = false });

        Assert.Equal("\u0421\u0442\u0430\u0440\u0438\u0439 \u043f\u0435\u0440\u0435\u043a\u043b\u0430\u0434", result.Document.Namespaces[0].Entries[0].Value);
        Assert.Equal("\u0421\u0442\u0430\u0440\u0438\u0439 \u043f\u0435\u0440\u0435\u043a\u043b\u0430\u0434", result.CarriedValues[("Game", "K1")]);
        Assert.Empty(result.RejectedValues);
        // Still REPORTED as changed \u2014 the caller decides what that means.
        Assert.Equal(new[] { ("Game", "K1") }, result.SourceChangedKeys);
    }

    [Fact]
    public void VerificationIsOnWithoutAskingForIt()
    {
        // A library whose safe behaviour needs an argument is a library that
        // ships the unsafe behaviour to everyone who did not read the docs.
        Assert.True(new LocresMergeOptions().RequireMatchingSourceHash);
    }

    [Fact]
    public void CarriedValues_ReportWhatActuallyMoved()
    {
        var old = Doc(("Game", new[] { ("Same", 1u, "\u041f\u0435\u0440\u0435\u043a\u043b\u0430\u0434"), ("Changed", 1u, "\u0421\u0442\u0430\u0440\u0438\u0439"), ("Empty", 1u, "") }));
        var newer = Doc(("Game", new[] { ("Same", 1u, "A"), ("Changed", 9u, "B"), ("Empty", 1u, "C"), ("Fresh", 1u, "D") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal(new[] { ("Game", "Same") }, result.CarriedValues.Keys.ToArray());
        Assert.Equal(new[] { ("Game", "Changed") }, result.RejectedValues.Keys.ToArray());
        Assert.Equal(new[] { ("Game", "Fresh") }, result.NewKeys);
        // "Empty" had nothing to carry: neither carried nor rejected.
        Assert.DoesNotContain(("Game", "Empty"), result.CarriedValues.Keys);
        Assert.DoesNotContain(("Game", "Empty"), result.RejectedValues.Keys);
    }

    [Fact]
    public void EmptyOldTranslation_IsNotCarried()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "New source") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal("New source", result.Document.Namespaces[0].Entries[0].Value);
        Assert.Empty(result.NewKeys); // key matched — it is not "new"
    }

    [Fact]
    public void InputsAreNotMutated_AndResultIsIndependent()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Src") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal("Переклад", old.Namespaces[0].Entries[0].Value);
        Assert.Equal("Src", newer.Namespaces[0].Entries[0].Value);
        Assert.NotSame(newer.Namespaces[0].Entries[0], result.Document.Namespaces[0].Entries[0]);
        Assert.Equal(newer.Version, result.Document.Version);
    }

    [Fact]
    public void DuplicateNewEntries_AllCarried_DiffListsDeduped()
    {
        var old = Doc(("NS", new[] { ("Dup", 1u, "Переклад") }));
        var newer = Doc(("NS", new[] { ("Dup", 1u, "S1"), ("Dup", 1u, "S2"), ("Fresh", 2u, "F"), ("Fresh", 2u, "F2") }));

        var result = LocresMerger.Merge(old, newer);

        Assert.Equal("Переклад", result.Document.Namespaces[0].Entries[0].Value);
        Assert.Equal("Переклад", result.Document.Namespaces[0].Entries[1].Value);
        Assert.Equal(new[] { ("NS", "Fresh") }, result.NewKeys); // deduped
    }

    [Fact]
    public void VersionFollowsTheNewFile()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") })); // legacy
        var newerV3 = LocresDocument.Parse(TestFileBuilder.Tabled(3,
            table: new[] { "Src" },
            namespaces: new[] { ("Game", 0u, new[] { ("K1", 0u, 1u, 0) }) }));

        var result = LocresMerger.Merge(old, newerV3);

        Assert.Equal(LocresVersion.OptimizedCityHash64, result.Document.Version);
        Assert.Equal("Переклад", result.Document.Namespaces[0].Entries[0].Value);
    }

    [Fact]
    public void MergedDocument_SavesAndReloadsCleanly()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") }));
        var newer = Doc(
            ("Game", new[] { ("K1", 1u, "Src"), ("K2", 2u, "New") }),
            ("DLC", new[] { ("D1", 3u, "Dlc") }));

        var merged = LocresMerger.Merge(old, newer).Document;

        using var ms = new MemoryStream();
        merged.Save(ms, new LocresSaveOptions { TargetVersion = LocresVersion.OptimizedCityHash64 });
        var reloaded = LocresDocument.Parse(ms.ToArray());

        Assert.Equal(3, reloaded.EntryCount);
        Assert.Equal("Переклад", reloaded.Namespaces[0].Entries[0].Value);
    }
}
