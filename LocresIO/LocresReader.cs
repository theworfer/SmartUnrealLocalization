namespace Locres.IO;

/// <summary>
/// Strict parser for all .locres versions. No silent fallbacks except the
/// documented one: input without the magic GUID is treated as Legacy (v0) and
/// parsed from offset 0 — guarded by a plausibility check so arbitrary
/// garbage fails with a clear error instead of absurd allocations.
/// </summary>
internal static class LocresReader
{
    /// <summary>The 16-byte magic GUID exactly as stored on disk at offset 0.</summary>
    internal static ReadOnlySpan<byte> Magic =>
    [
        0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC,
        0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B,
    ];

    internal static LocresDocument Parse(ReadOnlySpan<byte> data, LocresLoadOptions? options)
    {
        Func<string, string> intern = options?.Interner ?? new StringInterner().Intern;
        var r = new SpanReader(data);

        if (data.Length >= 17 && data[..16].SequenceEqual(Magic))
        {
            r.Seek(16);
            byte versionByte = r.ReadByte();
            if (versionByte > (byte)LocresVersion.OptimizedCityHash64)
                throw new LocresUnsupportedVersionException(versionByte);

            var version = (LocresVersion)versionByte;
            // A magic header with version byte 0 is tolerated: legacy body after the header.
            return version == LocresVersion.Legacy
                ? ParseLegacy(ref r, intern)
                : ParseTabled(ref r, version, intern);
        }

        // No magic → Legacy layout starting at offset 0 (that is the format's
        // own rule, not a guess). Plausibility is checked inside.
        return ParseLegacy(ref r, intern);
    }

    private static LocresDocument ParseLegacy(ref SpanReader r, Func<string, string> intern)
    {
        r.Section = "legacy header";
        int nsCount = r.ReadInt32();
        // Minimum bytes per namespace: FString prefix (4) + key count (4).
        if (nsCount < 0 || (long)nsCount * 8 > r.Remaining)
            throw r.Error($"Namespace count {nsCount} is implausible — not a .locres file?");

        var namespaces = new List<LocresNamespace>(nsCount);
        for (int n = 0; n < nsCount; n++)
        {
            r.Section = $"namespace {n}";
            string name = UnrealString.Read(ref r, intern);

            int keyCount = r.ReadInt32();
            // Minimum bytes per legacy entry: key prefix (4) + hash (4) + value prefix (4).
            if (keyCount < 0 || (long)keyCount * 12 > r.Remaining)
                throw r.Error($"Entry count {keyCount} out of range in namespace \"{name}\"");

            var entries = new List<LocresEntry>(keyCount);
            for (int k = 0; k < keyCount; k++)
            {
                r.Section = $"namespace {n}, entry {k}";
                string key = UnrealString.Read(ref r, intern);
                uint sourceHash = r.ReadUInt32();
                string value = UnrealString.Read(ref r, intern);
                entries.Add(new LocresEntry(key, sourceHash, value));
            }
            namespaces.Add(new LocresNamespace(name, entries));
        }

        return new LocresDocument(LocresVersion.Legacy, namespaces);
    }

    private static LocresDocument ParseTabled(ref SpanReader r, LocresVersion version, Func<string, string> intern)
    {
        bool optimized = version >= LocresVersion.Optimized;

        r.Section = "header";
        long tableOffset = r.ReadInt64();
        if (optimized)
            r.ReadInt32(); // declared total entry count — derivable, ignored on read (ecosystem tools write it loosely)

        int nsCount = r.ReadInt32();
        if (nsCount < 0 || (long)nsCount * 8 > r.Remaining)
            throw r.Error($"Namespace count {nsCount} out of range");

        // Read the string table first, then come back for the entry records.
        int entriesStart = r.Position;
        if (tableOffset < entriesStart || tableOffset > r.Length)
            throw r.Error($"String table offset 0x{tableOffset:X} is outside the file (size 0x{r.Length:X})");

        r.Seek(tableOffset);
        r.Section = "string table";
        int tableCount = r.ReadInt32();
        // Minimum bytes per table string: FString prefix (4), +4 for the v2/v3 ref count.
        if (tableCount < 0 || (long)tableCount * (optimized ? 8 : 4) > r.Remaining)
            throw r.Error($"String table count {tableCount} out of range");

        var table = new string[tableCount];
        for (int i = 0; i < tableCount; i++)
        {
            r.Section = $"string table entry {i}";
            table[i] = UnrealString.Read(ref r, intern);
            if (optimized)
                r.ReadInt32(); // ref count — recomputed on save, ignored on read
        }

        r.Seek(entriesStart);
        var namespaces = new List<LocresNamespace>(nsCount);
        for (int n = 0; n < nsCount; n++)
        {
            r.Section = $"namespace {n}";
            if (optimized)
                r.ReadUInt32(); // namespace name hash — recomputed on save, ignored on read
            string name = UnrealString.Read(ref r, intern);

            int keyCount = r.ReadInt32();
            // Minimum bytes per entry: [key hash (4)] + key prefix (4) + source hash (4) + table index (4).
            if (keyCount < 0 || (long)keyCount * (optimized ? 16 : 12) > r.Remaining)
                throw r.Error($"Entry count {keyCount} out of range in namespace \"{name}\"");

            var entries = new List<LocresEntry>(keyCount);
            for (int k = 0; k < keyCount; k++)
            {
                r.Section = $"namespace {n}, entry {k}";
                if (optimized)
                    r.ReadUInt32(); // key hash — recomputed on save, ignored on read
                string key = UnrealString.Read(ref r, intern);
                uint sourceHash = r.ReadUInt32();
                int index = r.ReadInt32();
                if ((uint)index >= (uint)tableCount)
                    throw r.Error($"String table index {index} out of range (table has {tableCount} strings)");
                entries.Add(new LocresEntry(key, sourceHash, table[index]));
            }
            namespaces.Add(new LocresNamespace(name, entries));
        }

        return new LocresDocument(version, namespaces);
    }
}
