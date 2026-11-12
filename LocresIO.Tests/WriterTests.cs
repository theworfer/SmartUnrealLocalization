using Locres.IO;
using Locres.IO.Hashing;
using Xunit;

namespace Locres.IO.Tests;

public class WriterTests
{
    private static byte[] SaveToBytes(LocresDocument doc, LocresVersion? targetVersion = null)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, targetVersion is null ? null : new LocresSaveOptions { TargetVersion = targetVersion });
        return ms.ToArray();
    }

    private static void AssertSemanticEqual(LocresDocument expected, LocresDocument actual)
    {
        Assert.Equal(expected.Namespaces.Count, actual.Namespaces.Count);
        Assert.Equal(expected.EntryCount, actual.EntryCount);
        for (int n = 0; n < expected.Namespaces.Count; n++)
        {
            Assert.Equal(expected.Namespaces[n].Name, actual.Namespaces[n].Name);
            var e = expected.Namespaces[n].Entries;
            var a = actual.Namespaces[n].Entries;
            Assert.Equal(e.Count, a.Count);
            for (int k = 0; k < e.Count; k++)
            {
                Assert.Equal(e[k].Key, a[k].Key);
                Assert.Equal(e[k].SourceStringHash, a[k].SourceStringHash);
                Assert.Equal(e[k].Value, a[k].Value);
            }
        }
    }

    private static LocresDocument SampleDoc(byte version)
    {
        if (version == 0)
        {
            return LocresDocument.Parse(TestFileBuilder.Legacy(
                ("", new[] { ("KeyA", 0x11u, "Привіт"), ("KeyB", 0x22u, "Shared") }),
                ("Game", new[] { ("KeyC", 0x33u, "Shared"), ("KeyD", 0x44u, "") })));
        }
        return LocresDocument.Parse(TestFileBuilder.Tabled(version,
            table: new[] { "Привіт", "Shared", "" },
            namespaces: new[]
            {
                ("", 0u, new[] { ("KeyA", 0u, 0x11u, 0), ("KeyB", 0u, 0x22u, 1) }),
                ("Game", 0u, new[] { ("KeyC", 0u, 0x33u, 1), ("KeyD", 0u, 0x44u, 2) }),
            }));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void RoundTrip_SemanticIdentity(byte version)
    {
        var doc = SampleDoc(version);
        var reloaded = LocresDocument.Parse(SaveToBytes(doc));
        Assert.Equal((LocresVersion)version, reloaded.Version);
        AssertSemanticEqual(doc, reloaded);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    public void SecondSave_IsByteIdentical(byte version)
    {
        var doc = SampleDoc(version);
        byte[] first = SaveToBytes(doc);
        byte[] second = SaveToBytes(LocresDocument.Parse(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public void WriteParity_V0_MatchesIndependentBuilder()
    {
        // Legacy: namespace names force-wide, everything inline. The
        // independent builder writes straight from the spec — our writer's
        // output must be byte-identical.
        byte[] expected = TestFileBuilder.Legacy(
            ("", new[] { ("KeyA", 0xAAu, "Value A"), ("KeyB", 0xBBu, "Кирилиця") }),
            ("Game", new[] { ("KeyC", 0xCCu, "") }));

        byte[] actual = SaveToBytes(LocresDocument.Parse(expected));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteParity_V1_MatchesIndependentBuilder()
    {
        byte[] expected = TestFileBuilder.Tabled(1,
            table: new[] { "First", "Спільне" },
            namespaces: new[]
            {
                ("", 0u, new[] { ("KeyA", 0u, 1u, 0), ("KeyB", 0u, 2u, 1) }),
                ("Sub", 0u, new[] { ("KeyC", 0u, 3u, 1) }),
            });

        byte[] actual = SaveToBytes(LocresDocument.Parse(expected));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteParity_V2_HashesRefCountsAndOrder()
    {
        // Independent builder gets the CORRECT v2 CRC hashes and ref counts;
        // byte-parity here proves hash placement, dedup order and ref counts
        // all at once.
        byte[] expected = TestFileBuilder.Tabled(2,
            table: new[] { "Shared", "Solo" },
            namespaces: new[]
            {
                ("Game", LocresHash.Crc32("Game"), new[]
                {
                    ("KeyA", LocresHash.Crc32("KeyA"), 0x01u, 0),
                    ("KeyB", LocresHash.Crc32("KeyB"), 0x02u, 0),
                    ("KeyC", LocresHash.Crc32("KeyC"), 0x03u, 1),
                }),
                ("", LocresHash.Crc32(""), new[]
                {
                    ("KeyD", LocresHash.Crc32("KeyD"), 0x04u, 0),
                }),
            },
            refCounts: new[] { 3, 1 });

        byte[] actual = SaveToBytes(LocresDocument.Parse(expected));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WriteParity_V3_CityHashPlacement()
    {
        byte[] expected = TestFileBuilder.Tabled(3,
            table: new[] { "Значення", "Other" },
            namespaces: new[]
            {
                ("Меню", LocresHash.CityHashUtf16("Меню"), new[]
                {
                    ("Ключ_А", LocresHash.CityHashUtf16("Ключ_А"), 0xAAu, 0),
                    ("KeyB", LocresHash.CityHashUtf16("KeyB"), 0xBBu, 1),
                }),
            },
            refCounts: new[] { 1, 1 });

        byte[] actual = SaveToBytes(LocresDocument.Parse(expected));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyDocument_RoundTrips()
    {
        byte[] data = TestFileBuilder.Tabled(1, Array.Empty<string>(),
            Array.Empty<(string, uint, (string, uint, uint, int)[])>());
        var doc = LocresDocument.Parse(data);
        Assert.Equal(0, doc.EntryCount);
        Assert.Equal(0, LocresDocument.Parse(SaveToBytes(doc)).EntryCount);
    }

    [Fact]
    public void TabledWrite_RequiresSeekableStream()
    {
        var doc = SampleDoc(1);
        using var ns = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() => doc.Save(ns));
    }

    [Fact]
    public void LegacyWrite_WorksOnNonSeekableStream()
    {
        var doc = SampleDoc(0);
        using var target = new NonSeekableStream();
        doc.Save(target);
        Assert.True(target.Written > 0);
    }

    [Fact]
    public void AtomicSave_WritesParsableFileAndLeavesNoTempJunk()
    {
        var dir = Directory.CreateTempSubdirectory("locresio-test-");
        try
        {
            string path = Path.Combine(dir.FullName, "out.locres");
            var doc = SampleDoc(3);

            var result = doc.Save(path);
            Assert.Equal(doc.EntryCount, result.EntriesWritten);
            AssertSemanticEqual(doc, LocresDocument.Load(path));

            // overwrite in place must also work
            doc.Save(path);
            AssertSemanticEqual(doc, LocresDocument.Load(path));

            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp-*"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void PreCancelledSave_ThrowsAndLeavesNothingBehind()
    {
        var dir = Directory.CreateTempSubdirectory("locresio-test-");
        try
        {
            string path = Path.Combine(dir.FullName, "out.locres");
            var doc = SampleDoc(2);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => doc.Save(path, null, cts.Token));
            Assert.Empty(Directory.GetFiles(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
    }
}
