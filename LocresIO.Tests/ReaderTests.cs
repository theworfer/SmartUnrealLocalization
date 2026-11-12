using Locres.IO;
using Xunit;

namespace Locres.IO.Tests;

public class ReaderTests
{
    [Fact]
    public void Legacy_ParsesNamespacesEntriesAndSourceHashes()
    {
        byte[] data = TestFileBuilder.Legacy(
            ("", new[] { ("KeyA", 0x11223344u, "Привіт"), ("KeyB", 0xDEADBEEFu, "World") }),
            ("Game", new[] { ("KeyC", 0u, "") }));

        var doc = LocresDocument.Parse(data);

        Assert.Equal(LocresVersion.Legacy, doc.Version);
        Assert.Equal(2, doc.Namespaces.Count);
        Assert.Equal(3, doc.EntryCount);

        Assert.Equal("", doc.Namespaces[0].Name);
        Assert.Equal("KeyA", doc.Namespaces[0].Entries[0].Key);
        Assert.Equal(0x11223344u, doc.Namespaces[0].Entries[0].SourceStringHash);
        Assert.Equal("Привіт", doc.Namespaces[0].Entries[0].Value);
        Assert.Equal(0xDEADBEEFu, doc.Namespaces[0].Entries[1].SourceStringHash);

        Assert.Equal("Game", doc.Namespaces[1].Name);
        Assert.Equal("", doc.Namespaces[1].Entries[0].Value);
    }

    [Fact]
    public void GarbageWithoutMagic_RejectedWithClearError()
    {
        byte[] garbage = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05 };
        var ex = Assert.Throws<LocresFormatException>(() => LocresDocument.Parse(garbage));
        Assert.Contains("not a .locres file", ex.Message);
    }

    [Fact]
    public void EmptyInput_Throws()
    {
        Assert.Throws<LocresFormatException>(() => LocresDocument.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Compact_ResolvesSharedTableIndexes()
    {
        byte[] data = TestFileBuilder.Tabled(1,
            table: new[] { "Hello", "Спільний" },
            namespaces: new[]
            {
                ("", 0u, new[]
                {
                    ("K1", 0u, 0xAAAAAAAAu, 1),
                    ("K2", 0u, 0xBBBBBBBBu, 1),
                    ("K3", 0u, 0xCCCCCCCCu, 0),
                }),
            });

        var doc = LocresDocument.Parse(data);

        Assert.Equal(LocresVersion.Compact, doc.Version);
        Assert.Equal(3, doc.EntryCount);
        var entries = doc.Namespaces[0].Entries;
        Assert.Equal("Спільний", entries[0].Value);
        Assert.Same(entries[0].Value, entries[1].Value); // one table slot → one string object
        Assert.Equal("Hello", entries[2].Value);
    }

    [Fact]
    public void Optimized_IgnoresStoredHashesAndRefCounts()
    {
        // Bogus pre-hashes in the file must not affect parsing — they are
        // recomputed on save, never trusted on read.
        byte[] data = TestFileBuilder.Tabled(2,
            table: new[] { "Value" },
            namespaces: new[]
            {
                ("NS", 0xBAADF00Du, new[] { ("Key", 0xCAFEBABEu, 0x12345678u, 0) }),
            });

        var doc = LocresDocument.Parse(data);

        Assert.Equal(LocresVersion.Optimized, doc.Version);
        Assert.Equal("NS", doc.Namespaces[0].Name);
        Assert.Equal("Key", doc.Namespaces[0].Entries[0].Key);
        Assert.Equal(0x12345678u, doc.Namespaces[0].Entries[0].SourceStringHash);
        Assert.Equal("Value", doc.Namespaces[0].Entries[0].Value);
    }

    [Fact]
    public void V3_SurfacesVersion()
    {
        byte[] data = TestFileBuilder.Tabled(3,
            table: new[] { "V" },
            namespaces: new[] { ("", 0u, new[] { ("K", 0u, 0u, 0) }) });

        Assert.Equal(LocresVersion.OptimizedCityHash64, LocresDocument.Parse(data).Version);
    }

    [Fact]
    public void UnknownVersionByte_RefusedNotGuessed()
    {
        byte[] data = TestFileBuilder.Tabled(3, new[] { "V" },
            new[] { ("", 0u, new[] { ("K", 0u, 0u, 0) }) });
        data[16] = 4; // future version

        var ex = Assert.Throws<LocresUnsupportedVersionException>(() => LocresDocument.Parse(data));
        Assert.Equal(4, ex.VersionByte);
    }

    [Fact]
    public void TableIndexOutOfRange_ThrowsWithContext()
    {
        byte[] data = TestFileBuilder.Tabled(1,
            table: new[] { "Only" },
            namespaces: new[] { ("", 0u, new[] { ("K", 0u, 0u, 5) }) });

        var ex = Assert.Throws<LocresFormatException>(() => LocresDocument.Parse(data));
        Assert.Contains("index 5", ex.Message);
        Assert.Contains("entry 0", ex.Message);
    }

    [Fact]
    public void NegativeTableIndex_Throws()
    {
        byte[] data = TestFileBuilder.Tabled(1,
            table: new[] { "Only" },
            namespaces: new[] { ("", 0u, new[] { ("K", 0u, 0u, -1) }) });

        Assert.Throws<LocresFormatException>(() => LocresDocument.Parse(data));
    }

    [Fact]
    public void TableOffsetOutsideFile_Throws()
    {
        byte[] data = TestFileBuilder.Tabled(1,
            table: new[] { "V" },
            namespaces: new[] { ("", 0u, new[] { ("K", 0u, 0u, 0) }) },
            tableOffsetOverride: 1_000_000);

        var ex = Assert.Throws<LocresFormatException>(() => LocresDocument.Parse(data));
        Assert.Contains("table offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclaredEntryCountMismatch_ToleratedAndDerived()
    {
        // UE derives it too; ecosystem tools write it loosely. Actual structure wins.
        byte[] data = TestFileBuilder.Tabled(3,
            table: new[] { "V" },
            namespaces: new[] { ("", 0u, new[] { ("K", 0u, 0u, 0), ("K2", 0u, 0u, 0) }) },
            declaredEntriesOverride: 999);

        Assert.Equal(2, LocresDocument.Parse(data).EntryCount);
    }

    [Fact]
    public void DuplicateNamespacesAndKeys_PreservedInFileOrder()
    {
        byte[] data = TestFileBuilder.Legacy(
            ("NS", new[] { ("Key", 1u, "first"), ("Key", 2u, "second") }),
            ("NS", new[] { ("Key", 3u, "third") }));

        var doc = LocresDocument.Parse(data);

        Assert.Equal(2, doc.Namespaces.Count);
        Assert.Equal("NS", doc.Namespaces[0].Name);
        Assert.Equal("NS", doc.Namespaces[1].Name);
        Assert.Equal(new[] { "first", "second" }, doc.Namespaces[0].Entries.Select(e => e.Value));
        Assert.Equal("third", doc.Namespaces[1].Entries[0].Value);
    }

    [Fact]
    public void DefaultInterner_DedupsIdenticalStringsWithinOneParse()
    {
        byte[] data = TestFileBuilder.Legacy(
            ("A", new[] { ("Key1", 0u, "Same value") }),
            ("B", new[] { ("Key2", 0u, "Same value") }));

        var doc = LocresDocument.Parse(data);
        Assert.Same(doc.Namespaces[0].Entries[0].Value, doc.Namespaces[1].Entries[0].Value);
    }

    [Fact]
    public void ExternalInterner_IsUsedWhenProvided()
    {
        byte[] data = TestFileBuilder.Legacy(("A", new[] { ("Key1", 0u, "val") }));
        int calls = 0;
        var options = new LocresLoadOptions { Interner = s => { calls++; return s; } };

        LocresDocument.Parse(data, options);
        Assert.True(calls >= 3); // ns name + key + value at minimum
    }

    [Fact]
    public void MagicWithVersionZero_ToleratedAsLegacyBodyAfterHeader()
    {
        byte[] legacyBody = TestFileBuilder.Legacy(("", new[] { ("K", 7u, "v") }));
        byte[] data = new byte[17 + legacyBody.Length];
        TestFileBuilder.Magic.CopyTo(data, 0);
        data[16] = 0;
        legacyBody.CopyTo(data, 17);

        var doc = LocresDocument.Parse(data);
        Assert.Equal(LocresVersion.Legacy, doc.Version);
        Assert.Equal(1, doc.EntryCount);
        Assert.Equal(7u, doc.Namespaces[0].Entries[0].SourceStringHash);
    }

    [Fact]
    public void TruncationFuzz_EveryPrefixFailsWithControlledError()
    {
        byte[] full = TestFileBuilder.Tabled(3,
            table: new[] { "Hello", "Світ" },
            namespaces: new[]
            {
                ("Game", 0u, new[] { ("KeyA", 0u, 1u, 0), ("KeyB", 0u, 2u, 1) }),
                ("", 0u, new[] { ("KeyC", 0u, 3u, 1) }),
            });

        // A valid parse of the full bytes first — so the fuzz below is meaningful.
        Assert.Equal(3, LocresDocument.Parse(full).EntryCount);

        for (int len = 0; len < full.Length; len++)
        {
            byte[] prefix = full.AsSpan(0, len).ToArray();
            try
            {
                LocresDocument.Parse(prefix);
                Assert.Fail($"prefix of length {len} parsed successfully — must throw");
            }
            catch (LocresException)
            {
                // controlled failure — expected
            }
        }
    }

    [Fact]
    public void TruncationFuzz_LegacyPrefixesAlsoFailControlled()
    {
        byte[] full = TestFileBuilder.Legacy(
            ("NS", new[] { ("Key", 5u, "Значення"), ("Key2", 6u, "x") }));

        Assert.Equal(2, LocresDocument.Parse(full).EntryCount);

        // Legacy has no magic, so a prefix could theoretically parse as a
        // smaller valid file only if counts allowed it — they don't here
        // (nsCount=1 needs the namespace body). Every prefix must fail.
        for (int len = 0; len < full.Length; len++)
        {
            byte[] prefix = full.AsSpan(0, len).ToArray();
            try
            {
                LocresDocument.Parse(prefix);
                Assert.Fail($"legacy prefix of length {len} parsed successfully — must throw");
            }
            catch (LocresException)
            {
            }
        }
    }

    [Fact]
    public void LoadFromStream_Works()
    {
        byte[] data = TestFileBuilder.Legacy(("", new[] { ("K", 1u, "v") }));
        using var ms = new MemoryStream(data);
        var doc = LocresDocument.Load(ms);
        Assert.Equal(1, doc.EntryCount);
    }

    [Fact]
    public void LoadFromStream_ReadsFromCurrentPosition_FastPath()
    {
        // Expandable MemoryStream exposes its buffer → the zero-copy fast
        // path. Parsing must start at Position, not at the buffer start, and
        // must consume the stream like a real read.
        byte[] data = TestFileBuilder.Legacy(("", new[] { ("K", 1u, "v") }));
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE });
        ms.Write(data);
        ms.Position = 7;

        var doc = LocresDocument.Load(ms);

        Assert.Equal(1, doc.EntryCount);
        Assert.Equal(ms.Length, ms.Position);
    }

    [Fact]
    public void LoadFromStream_ReadsFromCurrentPosition_FallbackPath()
    {
        // MemoryStream over a caller-owned array hides its buffer
        // (TryGetBuffer == false) → the CopyTo fallback. Same contract.
        byte[] data = TestFileBuilder.Legacy(("", new[] { ("K", 1u, "v") }));
        var prefixed = new byte[7 + data.Length];
        prefixed.AsSpan(0, 7).Fill(0xEE);
        data.CopyTo(prefixed, 7);
        using var ms = new MemoryStream(prefixed);
        ms.Position = 7;

        var doc = LocresDocument.Load(ms);

        Assert.Equal(1, doc.EntryCount);
        Assert.Equal(ms.Length, ms.Position);
    }
}
