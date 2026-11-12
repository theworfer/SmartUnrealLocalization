using System.Diagnostics;
using Locres.IO;
using Xunit;

namespace Locres.IO.Tests;

/// <summary>
/// A damaged file must fail with a <see cref="LocresException"/> — never an
/// IndexOutOfRange, an OutOfMemory from a corrupt count, or a hang. Users open
/// whatever is in a game folder, including half-downloaded and modded files.
/// </summary>
public class CorruptionFuzzTests
{
    private static IEnumerable<byte[]> Samples()
    {
        yield return TestFileBuilder.Tabled(3,
            table: new[] { "Hello", "Світ", "", "{0} gold" },
            namespaces: new[]
            {
                ("Game", 0u, new[] { ("KeyA", 0u, 1u, 0), ("KeyB", 0u, 2u, 1), ("KeyD", 0u, 4u, 3) }),
                ("", 0u, new[] { ("KeyC", 0u, 3u, 2) }),
            });
        yield return TestFileBuilder.Tabled(2,
            table: new[] { "One", "Два" },
            namespaces: new[] { ("UI", 0u, new[] { ("A", 0u, 1u, 0), ("B", 0u, 2u, 1) }) });
        yield return TestFileBuilder.Legacy(
            ("NS", new[] { ("Key", 5u, "Значення"), ("Key2", 6u, "x") }),
            ("", new[] { ("K", 1u, "y") }));
    }

    [Fact]
    public void Random_byte_damage_fails_with_a_controlled_error_and_quickly()
    {
        var rng = new Random(20261104);
        int parsed = 0, refused = 0;
        foreach (var original in Samples())
        {
            Assert.True(LocresDocument.Parse(original).EntryCount > 0);
            for (int trial = 0; trial < 3000; trial++)
            {
                byte[] data = (byte[])original.Clone();
                int flips = rng.Next(1, 6);
                for (int f = 0; f < flips; f++)
                {
                    int at = rng.Next(data.Length);
                    data[at] = rng.Next(4) switch
                    {
                        0 => 0xFF,                    // max out a count/length byte
                        1 => 0x00,
                        2 => (byte)(data[at] ^ (1 << rng.Next(8))),
                        _ => (byte)rng.Next(256),
                    };
                }

                var sw = Stopwatch.StartNew();
                try
                {
                    var doc = LocresDocument.Parse(data);
                    parsed++;
                    // A file that parses must also save without throwing.
                    using var ms = new MemoryStream();
                    doc.Save(ms);
                }
                catch (LocresException) { refused++; }
                catch (Exception ex)
                {
                    Assert.Fail($"trial {trial}: {ex.GetType().Name}: {ex.Message}");
                }
                Assert.True(sw.ElapsedMilliseconds < 2000, $"trial {trial} took {sw.ElapsedMilliseconds} ms");
            }
        }
        Assert.True(refused > 1000, $"only {refused} refusals — the damage is not reaching the parser");
        _ = parsed;
    }
}
