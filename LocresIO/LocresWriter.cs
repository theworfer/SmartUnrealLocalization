using System.Buffers.Binary;
using Locres.IO.Hashing;

namespace Locres.IO;

/// <summary>
/// Streaming .locres writer: sequential write with a Seek-backpatch of the
/// string-table offset (and entry count for v2/v3) — no in-memory image of
/// the file, peak memory ≈ the stream's own buffer. The source document is
/// never mutated; value overrides are substituted on the fly.
/// </summary>
internal static class LocresWriter
{
    internal static LocresSaveResult Write(
        LocresDocument doc,
        IReadOnlyDictionary<(string Ns, string Key), string?>? overrides,
        Stream stream,
        LocresVersion version,
        CancellationToken ct)
    {
        var scratch = new ScratchBuffer();
        try
        {
            HashSet<(string, string)>? matched = overrides is null ? null : new();
            int applied = 0;

            if (version == LocresVersion.Legacy)
                WriteLegacy(doc, overrides, stream, matched, ref applied, ref scratch, ct);
            else
                WriteTabled(doc, overrides, stream, version, matched, ref applied, ref scratch, ct);

            int skippedEmpty = 0, unknown = 0;
            if (overrides is not null)
            {
                foreach (var kv in overrides)
                {
                    if (!matched!.Contains(kv.Key)) unknown++;
                    else if (string.IsNullOrEmpty(kv.Value)) skippedEmpty++;
                }
            }
            return new LocresSaveResult(doc.EntryCount, applied, skippedEmpty, unknown);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// The overlay rule: an override replaces the entry's value ONLY when it is
    /// non-empty — an empty/null override keeps the base value, so untranslated
    /// rows can never blank out shipped strings. Duplicate (ns,key) entries all
    /// receive the same override.
    /// </summary>
    private static string ResolveValue(
        LocresNamespace ns, LocresEntry entry,
        IReadOnlyDictionary<(string, string), string?>? overrides,
        HashSet<(string, string)>? matched, ref int applied)
    {
        if (overrides is not null && overrides.TryGetValue((ns.Name, entry.Key), out var over))
        {
            matched!.Add((ns.Name, entry.Key));
            if (!string.IsNullOrEmpty(over))
            {
                applied++;
                return over;
            }
        }
        return entry.Value;
    }

    private static void WriteLegacy(
        LocresDocument doc,
        IReadOnlyDictionary<(string, string), string?>? overrides,
        Stream stream,
        HashSet<(string, string)>? matched, ref int applied,
        ref ScratchBuffer scratch, CancellationToken ct)
    {
        WriteInt32(stream, doc.Namespaces.Count);
        foreach (var ns in doc.Namespaces)
        {
            ct.ThrowIfCancellationRequested();
            // Legacy namespace names are always serialized wide — engine parity.
            UnrealString.Write(stream, ns.Name, forceUnicode: true, ref scratch);
            WriteInt32(stream, ns.Entries.Count);
            foreach (var entry in ns.Entries)
            {
                UnrealString.Write(stream, entry.Key, forceUnicode: false, ref scratch);
                WriteUInt32(stream, entry.SourceStringHash);
                string value = ResolveValue(ns, entry, overrides, matched, ref applied);
                UnrealString.Write(stream, value, forceUnicode: false, ref scratch);
            }
        }
    }

    private static void WriteTabled(
        LocresDocument doc,
        IReadOnlyDictionary<(string, string), string?>? overrides,
        Stream stream, LocresVersion version,
        HashSet<(string, string)>? matched, ref int applied,
        ref ScratchBuffer scratch, CancellationToken ct)
    {
        if (!stream.CanSeek)
            throw new ArgumentException(
                "Writing versions 1-3 requires a seekable stream (the string table offset is backpatched).",
                nameof(stream));

        bool optimized = version >= LocresVersion.Optimized;
        long basePos = stream.Position;

        stream.Write(LocresReader.Magic);
        stream.WriteByte((byte)version);

        long patchPos = stream.Position; // basePos + 17
        WriteInt64(stream, 0);           // string table offset placeholder
        if (optimized)
            WriteInt32(stream, 0);       // entries count placeholder

        WriteInt32(stream, doc.Namespaces.Count);

        // Value dedup: ordinal, case-sensitive, first-appearance order.
        var tableIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var tableStrings = new List<string>();
        var refCounts = new List<int>();

        foreach (var ns in doc.Namespaces)
        {
            ct.ThrowIfCancellationRequested();
            if (optimized)
                WriteUInt32(stream, NameHash(doc, ns.Name, version));
            UnrealString.Write(stream, ns.Name, forceUnicode: false, ref scratch);
            WriteInt32(stream, ns.Entries.Count);

            foreach (var entry in ns.Entries)
            {
                if (optimized)
                    WriteUInt32(stream, NameHash(doc, entry.Key, version));
                UnrealString.Write(stream, entry.Key, forceUnicode: false, ref scratch);
                WriteUInt32(stream, entry.SourceStringHash);

                string value = ResolveValue(ns, entry, overrides, matched, ref applied);
                if (tableIndex.TryGetValue(value, out int index))
                {
                    refCounts[index]++;
                }
                else
                {
                    index = tableStrings.Count;
                    tableIndex.Add(value, index);
                    tableStrings.Add(value);
                    refCounts.Add(1);
                }
                WriteInt32(stream, index);
            }
        }

        long tableOffset = stream.Position - basePos;
        WriteInt32(stream, tableStrings.Count);
        for (int i = 0; i < tableStrings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            UnrealString.Write(stream, tableStrings[i], forceUnicode: false, ref scratch);
            if (optimized)
                WriteInt32(stream, refCounts[i]);
        }

        long endPos = stream.Position;
        stream.Position = patchPos;
        WriteInt64(stream, tableOffset);
        if (optimized)
            WriteInt32(stream, doc.EntryCount);
        stream.Position = endPos;
    }

    /// <summary>Namespace/key pre-hash by target version, memoized per document.</summary>
    private static uint NameHash(LocresDocument doc, string name, LocresVersion version)
        => version == LocresVersion.Optimized
            ? doc.CrcMemo.GetOrAdd(name, static n => LocresHash.Crc32(n))
            : doc.CityMemo.GetOrAdd(name, static n => LocresHash.CityHashUtf16(n));

    private static void WriteInt32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteInt64(Stream s, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        s.Write(b);
    }
}
