using System.Text;

namespace Locres.IO.Tests;

/// <summary>
/// Independent binary builder for test fixtures — deliberately NOT using the
/// library's writer, so reader tests validate against the spec, not against
/// our own serialization code.
/// </summary>
internal static class TestFileBuilder
{
    public static readonly byte[] Magic =
    {
        0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC,
        0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B,
    };

    public static void FStr(BinaryWriter w, string s, bool forceWide = false)
    {
        bool ascii = !forceWide && s.All(c => c <= 0x7F);
        if (ascii)
        {
            w.Write(s.Length + 1);
            foreach (char c in s) w.Write((byte)c);
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(s.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(s));
            w.Write((ushort)0);
        }
    }

    public static byte[] Legacy(
        params (string Name, (string Key, uint Hash, string Value)[] Entries)[] namespaces)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(namespaces.Length);
        foreach (var ns in namespaces)
        {
            FStr(w, ns.Name, forceWide: true); // legacy namespace names are always wide
            w.Write(ns.Entries.Length);
            foreach (var e in ns.Entries)
            {
                FStr(w, e.Key);
                w.Write(e.Hash);
                FStr(w, e.Value);
            }
        }
        return ms.ToArray();
    }

    public static byte[] Tabled(
        byte version,
        string[] table,
        (string Name, uint NsHash, (string Key, uint KeyHash, uint SrcHash, int Index)[] Entries)[] namespaces,
        int? declaredEntriesOverride = null,
        long? tableOffsetOverride = null,
        int[]? refCounts = null)
    {
        bool optimized = version >= 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(Magic);
        w.Write(version);
        long patchPos = ms.Position; // 17
        w.Write(0L);                 // table offset placeholder
        if (optimized) w.Write(0);   // entries count placeholder

        w.Write(namespaces.Length);
        int totalEntries = 0;
        foreach (var ns in namespaces)
        {
            if (optimized) w.Write(ns.NsHash);
            FStr(w, ns.Name);
            w.Write(ns.Entries.Length);
            foreach (var e in ns.Entries)
            {
                if (optimized) w.Write(e.KeyHash);
                FStr(w, e.Key);
                w.Write(e.SrcHash);
                w.Write(e.Index);
                totalEntries++;
            }
        }

        long tableOffset = ms.Position;
        w.Write(table.Length);
        for (int i = 0; i < table.Length; i++)
        {
            FStr(w, table[i]);
            if (optimized) w.Write(refCounts is null ? 1 : refCounts[i]);
        }

        ms.Position = patchPos;
        w.Write(tableOffsetOverride ?? tableOffset);
        if (optimized) w.Write(declaredEntriesOverride ?? totalEntries);

        return ms.ToArray();
    }
}
