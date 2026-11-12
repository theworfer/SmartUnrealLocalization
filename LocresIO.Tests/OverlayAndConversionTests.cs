using Locres.IO;
using Locres.IO.Hashing;
using Xunit;

namespace Locres.IO.Tests;

public class OverlayAndConversionTests
{
    private static LocresDocument SampleDoc() =>
        LocresDocument.Parse(TestFileBuilder.Tabled(3,
            table: new[] { "Hello", "World", "Menu" },
            namespaces: new[]
            {
                ("", 0u, new[] { ("KeyA", 0u, 0x0Au, 0), ("KeyB", 0u, 0x0Bu, 1) }),
                ("UI", 0u, new[] { ("KeyC", 0u, 0x0Cu, 2) }),
            }));

    private static byte[] SaveWithToBytes(
        LocresDocument doc,
        Dictionary<(string Ns, string Key), string?> overrides,
        out LocresSaveResult result,
        LocresVersion? target = null)
    {
        using var ms = new MemoryStream();
        result = doc.SaveWith(overrides, ms,
            target is null ? null : new LocresSaveOptions { TargetVersion = target });
        return ms.ToArray();
    }

    [Fact]
    public void SaveWith_AppliesOverridesAndReportsCounts()
    {
        var doc = SampleDoc();
        var overrides = new Dictionary<(string, string), string?>
        {
            [("", "KeyA")] = "Привіт",      // applied
            [("UI", "KeyC")] = "Меню",      // applied
            [("", "KeyB")] = "",            // empty → base value kept
            [("", "Ghost")] = "translated", // unknown key → ignored
        };

        byte[] bytes = SaveWithToBytes(doc, overrides, out var result);
        var reloaded = LocresDocument.Parse(bytes);

        Assert.Equal(3, result.EntriesWritten);
        Assert.Equal(2, result.OverridesApplied);
        Assert.Equal(1, result.OverridesSkippedEmpty);
        Assert.Equal(1, result.OverridesUnknownKey);

        Assert.Equal("Привіт", reloaded.Namespaces[0].Entries[0].Value);
        Assert.Equal("World", reloaded.Namespaces[0].Entries[1].Value); // survived the empty override
        Assert.Equal("Меню", reloaded.Namespaces[1].Entries[0].Value);
    }

    [Fact]
    public void EmptyOrNullOverride_NeverBlanksShippedStrings()
    {
        // THE format-safety rule: untranslated rows must not erase base values.
        var doc = SampleDoc();
        var overrides = new Dictionary<(string, string), string?>
        {
            [("", "KeyA")] = "",
            [("", "KeyB")] = null,
        };

        var reloaded = LocresDocument.Parse(SaveWithToBytes(doc, overrides, out var result));

        Assert.Equal(0, result.OverridesApplied);
        Assert.Equal(2, result.OverridesSkippedEmpty);
        Assert.Equal("Hello", reloaded.Namespaces[0].Entries[0].Value);
        Assert.Equal("World", reloaded.Namespaces[0].Entries[1].Value);
    }

    [Fact]
    public void SaveWith_DoesNotMutateTheDocument()
    {
        var doc = SampleDoc();
        var overrides = new Dictionary<(string, string), string?> { [("", "KeyA")] = "Змінено" };

        SaveWithToBytes(doc, overrides, out _);

        Assert.Equal("Hello", doc.Namespaces[0].Entries[0].Value);

        // A plain Save afterwards still carries the original values.
        using var ms = new MemoryStream();
        doc.Save(ms);
        Assert.Equal("Hello", LocresDocument.Parse(ms.ToArray()).Namespaces[0].Entries[0].Value);
    }

    [Fact]
    public void DuplicateEntries_AllReceiveTheSameOverride()
    {
        // The original tool applied an override to the FIRST duplicate only —
        // we deliberately apply to all (documented divergence).
        var doc = LocresDocument.Parse(TestFileBuilder.Legacy(
            ("NS", new[] { ("Key", 1u, "first"), ("Key", 2u, "second") })));
        var overrides = new Dictionary<(string, string), string?> { [("NS", "Key")] = "both" };

        byte[] bytes = SaveWithToBytes(doc, overrides, out var result);
        var reloaded = LocresDocument.Parse(bytes);

        Assert.Equal(2, result.OverridesApplied);
        Assert.Equal("both", reloaded.Namespaces[0].Entries[0].Value);
        Assert.Equal("both", reloaded.Namespaces[0].Entries[1].Value);
        // SourceStringHash pass-through per entry survives the overlay
        Assert.Equal(1u, reloaded.Namespaces[0].Entries[0].SourceStringHash);
        Assert.Equal(2u, reloaded.Namespaces[0].Entries[1].SourceStringHash);
    }

    public static IEnumerable<object[]> AllVersionPairs()
    {
        for (byte from = 0; from <= 3; from++)
            for (byte to = 0; to <= 3; to++)
                yield return new object[] { from, to };
    }

    [Theory]
    [MemberData(nameof(AllVersionPairs))]
    public void VersionConversion_LosslessAcrossTheFullMatrix(byte from, byte to)
    {
        LocresDocument source = from == 0
            ? LocresDocument.Parse(TestFileBuilder.Legacy(
                ("", new[] { ("KeyA", 0xA1u, "Значення"), ("KeyB", 0xB2u, "Shared") }),
                ("Game", new[] { ("KeyC", 0xC3u, "Shared") })))
            : LocresDocument.Parse(TestFileBuilder.Tabled(from,
                table: new[] { "Значення", "Shared" },
                namespaces: new[]
                {
                    ("", 0u, new[] { ("KeyA", 0u, 0xA1u, 0), ("KeyB", 0u, 0xB2u, 1) }),
                    ("Game", 0u, new[] { ("KeyC", 0u, 0xC3u, 1) }),
                }));

        using var ms = new MemoryStream();
        source.Save(ms, new LocresSaveOptions { TargetVersion = (LocresVersion)to });
        var converted = LocresDocument.Parse(ms.ToArray());

        Assert.Equal((LocresVersion)to, converted.Version);
        Assert.Equal(source.EntryCount, converted.EntryCount);
        for (int n = 0; n < source.Namespaces.Count; n++)
        {
            Assert.Equal(source.Namespaces[n].Name, converted.Namespaces[n].Name);
            for (int k = 0; k < source.Namespaces[n].Entries.Count; k++)
            {
                Assert.Equal(source.Namespaces[n].Entries[k].Key, converted.Namespaces[n].Entries[k].Key);
                Assert.Equal(source.Namespaces[n].Entries[k].SourceStringHash, converted.Namespaces[n].Entries[k].SourceStringHash);
                Assert.Equal(source.Namespaces[n].Entries[k].Value, converted.Namespaces[n].Entries[k].Value);
            }
        }
    }

    [Fact]
    public void ConversionToV2_SwitchesHashFlavorToCrc32()
    {
        // Parse a v3 file, save as v2 — byte-parity against the independent
        // builder proves the pre-hashes really switch to StrCrc32.
        var v3doc = LocresDocument.Parse(TestFileBuilder.Tabled(3,
            table: new[] { "Val" },
            namespaces: new[]
            {
                ("Game", LocresHash.CityHashUtf16("Game"), new[]
                {
                    ("KeyA", LocresHash.CityHashUtf16("KeyA"), 0x77u, 0),
                }),
            }));

        byte[] expectedV2 = TestFileBuilder.Tabled(2,
            table: new[] { "Val" },
            namespaces: new[]
            {
                ("Game", LocresHash.Crc32("Game"), new[]
                {
                    ("KeyA", LocresHash.Crc32("KeyA"), 0x77u, 0),
                }),
            },
            refCounts: new[] { 1 });

        using var ms = new MemoryStream();
        v3doc.Save(ms, new LocresSaveOptions { TargetVersion = LocresVersion.Optimized });
        Assert.Equal(expectedV2, ms.ToArray());
    }

    [Fact]
    public void ConcurrentSaveWith_OnOneDocument_IsSafe()
    {
        var doc = SampleDoc();
        var outputs = new byte[8][];

        Parallel.For(0, outputs.Length, i =>
        {
            var overrides = new Dictionary<(string, string), string?>
            {
                [("", "KeyA")] = $"Translation {i}",
            };
            using var ms = new MemoryStream();
            doc.SaveWith(overrides, ms);
            outputs[i] = ms.ToArray();
        });

        for (int i = 0; i < outputs.Length; i++)
        {
            var reloaded = LocresDocument.Parse(outputs[i]);
            Assert.Equal($"Translation {i}", reloaded.Namespaces[0].Entries[0].Value);
            Assert.Equal("World", reloaded.Namespaces[0].Entries[1].Value);
        }
    }

    [Fact]
    public void SaveWith_ToDisk_IsAtomicAndCountsMatch()
    {
        var dir = Directory.CreateTempSubdirectory("locresio-test-");
        try
        {
            string path = Path.Combine(dir.FullName, "translated.locres");
            var doc = SampleDoc();
            var overrides = new Dictionary<(string, string), string?> { [("UI", "KeyC")] = "Меню" };

            var result = doc.SaveWith(overrides, path);

            Assert.Equal(1, result.OverridesApplied);
            Assert.Equal("Меню", LocresDocument.Load(path).Namespaces[1].Entries[0].Value);
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp-*"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
