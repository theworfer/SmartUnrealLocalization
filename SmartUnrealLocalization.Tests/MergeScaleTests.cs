using Locres.IO;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// One merge at a realistic size, checked for the properties that must hold no
/// matter what: every row is accounted for, the matcher and the grid agree on
/// how many translations came back, and every entry ships the NEW version's
/// hash. Unit tests pin the rules; this pins that they still add up at 20 000
/// rows with removals, rewordings, untranslated rows and new keys all at once.
/// </summary>
public class MergeScaleTests
{
    private readonly ITestOutputHelper _o;
    public MergeScaleTests(ITestOutputHelper o) => _o = o;

    private static byte[] Legacy((string Key, uint Hash, string Value)[] e)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.Unicode, leaveOpen: true);
        w.Write(1); Str(w, "Game"); w.Write(e.Length);
        foreach (var (k, h, v) in e) { Str(w, k); w.Write(h); Str(w, v); }
        return ms.ToArray();
        static void Str(BinaryWriter w, string s)
        {
            if (s.Length == 0) { w.Write(0); return; }
            w.Write(-(s.Length + 1));
            foreach (char c in s) w.Write((ushort)c);
            w.Write((ushort)0);
        }
    }

    [Fact]
    public void A_realistic_merge_adds_up_and_ships_the_right_hashes()
    {
        var oldE = new List<(string, uint, string)>();
        var newE = new List<(string, uint, string)>();
        for (int i = 0; i < 20000; i++)
        {
            // Every 37th row is untranslated in the old file.
            oldE.Add(("Key." + i, (uint)(i + 1), i % 37 == 0 ? "" : "Переклад " + i));
            if (i < 500) continue;                                   // removed by the update
            bool changed = i >= 500 && i < 1700;                     // 1200 reworded originals
            newE.Add(("Key." + i, changed ? (uint)(i + 900000) : (uint)(i + 1), "Source " + i));
        }
        for (int i = 0; i < 800; i++) newE.Add(("Fresh." + i, (uint)(500000 + i), "Fresh " + i));

        var oldDoc = LocresDocument.Parse(Legacy(oldE.ToArray()));
        var newDoc = LocresDocument.Parse(Legacy(newE.ToArray()));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = MergeMatcher.Match(newDoc, MergeMatcher.FromDocument(oldDoc), MergeVerification.Hash);
        sw.Stop();

        var rows = new List<TranslationRow>();
        foreach (var ns in newDoc.Namespaces)
            foreach (var e in ns.Entries)
                rows.Add(new TranslationRow
                {
                    Namespace = ns.Name, Key = e.Key, Source = e.Value,
                    Target = m.Carried.GetValueOrDefault((ns.Name, e.Key), ""),
                });

        using var outMs = new MemoryStream();
        newDoc.SaveWith(LocresFileService.BuildOverrides(rows), outMs);
        var back = LocresDocument.Parse(outMs.ToArray());

        int reallyTranslated = rows.Count(r => !r.IsUntranslated);
        int hashOk = 0;
        var newHashes = newDoc.Namespaces[0].Entries.ToDictionary(e => e.Key, e => e.SourceStringHash);
        foreach (var e in back.Namespaces[0].Entries) if (newHashes[e.Key] == e.SourceStringHash) hashOk++;

        _o.WriteLine($"PROBE match {sw.ElapsedMilliseconds} ms | total {m.TotalRows} | carried {m.CarriedRows} | "
                   + $"rejected {m.RejectedRows} | empty {m.EmptyRows} | new {m.NewRows} | removed {m.RemovedKeys}");
        _o.WriteLine($"PROBE sum {m.CarriedRows + m.RejectedRows + m.EmptyRows + m.NewRows} == total {m.TotalRows}");
        _o.WriteLine($"PROBE verifiedShare {m.VerifiedShare:F4} | grid rows with a target {reallyTranslated}");
        _o.WriteLine($"PROBE saved {back.EntryCount} entries | correct hashes {hashOk}/{back.EntryCount}");

        Assert.Equal(m.TotalRows, m.CarriedRows + m.RejectedRows + m.EmptyRows + m.NewRows);
        Assert.Equal(m.CarriedRows, reallyTranslated);
        Assert.Equal(back.EntryCount, hashOk);
        Assert.Equal(1168, m.RejectedRows);   // 1200 reworded minus the 32 that were untranslated anyway
        Assert.Equal(800, m.NewRows);
        Assert.Equal(500, m.RemovedKeys);
        Assert.Equal(527, m.EmptyRows);
    }
}
