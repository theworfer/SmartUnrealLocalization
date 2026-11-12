using System.Collections.Concurrent;

namespace Locres.IO;

/// <summary>
/// A parsed .locres file: format version plus namespaces/entries in exact
/// file order. Immutable after load — saving with edited values goes through
/// <c>SaveWith</c>, which substitutes overrides on the fly without touching
/// the document, so one document can back concurrent saves safely.
/// </summary>
public sealed class LocresDocument
{
    /// <summary>The on-disk format version this document was parsed from.</summary>
    public LocresVersion Version { get; }

    /// <summary>Namespaces in file order (duplicate names preserved as separate groups).</summary>
    public IReadOnlyList<LocresNamespace> Namespaces => _namespaces;

    /// <summary>Total entry count across all namespaces.</summary>
    public int EntryCount { get; }

    private readonly List<LocresNamespace> _namespaces;

    // Namespace/key pre-hash caches (per hash flavor): keys never change for
    // the document's lifetime, so repeated saves (autosave, multi-language
    // batch output) hash each unique name exactly once. Concurrent because
    // one document may back several simultaneous saves.
    private ConcurrentDictionary<string, uint>? _crcMemo;
    private ConcurrentDictionary<string, uint>? _cityMemo;

    internal ConcurrentDictionary<string, uint> CrcMemo =>
        LazyInitializer.EnsureInitialized(ref _crcMemo,
            static () => new ConcurrentDictionary<string, uint>(StringComparer.Ordinal));

    internal ConcurrentDictionary<string, uint> CityMemo =>
        LazyInitializer.EnsureInitialized(ref _cityMemo,
            static () => new ConcurrentDictionary<string, uint>(StringComparer.Ordinal));

    internal LocresDocument(LocresVersion version, List<LocresNamespace> namespaces)
    {
        Version = version;
        _namespaces = namespaces;
        int count = 0;
        foreach (var ns in namespaces) count += ns.Entries.Count;
        EntryCount = count;
    }

    /// <summary>Parse a .locres file from disk.</summary>
    /// <exception cref="LocresFormatException">Structurally invalid input.</exception>
    /// <exception cref="LocresUnsupportedVersionException">Unknown version byte.</exception>
    public static LocresDocument Load(string path, LocresLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllBytes(path), options);
    }

    /// <summary>Parse a .locres file from a stream (read fully into memory first).</summary>
    public static LocresDocument Load(Stream stream, LocresLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is MemoryStream direct && direct.TryGetBuffer(out var segment))
        {
            long position = direct.Position;
            long length = direct.Length;
            if (position > length)
                return Parse(ReadOnlySpan<byte>.Empty, options);

            int relativePosition = checked((int)position);
            int count = checked((int)(length - position));
            // Consume the stream like a real read would — the fallback path
            // (CopyTo) leaves the source at its end, so the fast path must too.
            direct.Position = length;
            return Parse(segment.Array!.AsSpan(segment.Offset + relativePosition, count), options);
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Parse(new ReadOnlySpan<byte>(ms.GetBuffer(), 0, (int)ms.Length), options);
    }

    /// <summary>Parse a .locres file from raw bytes.</summary>
    public static LocresDocument Parse(ReadOnlySpan<byte> data, LocresLoadOptions? options = null)
        => LocresReader.Parse(data, options);

    /// <summary>
    /// Write the document to disk atomically: the bytes go to a temp sibling
    /// file which is then moved over the destination, so a crash mid-write
    /// can never leave a truncated .locres behind.
    /// </summary>
    public LocresSaveResult Save(string path, LocresSaveOptions? options = null, CancellationToken ct = default)
        => SaveCore(path, overrides: null, options, ct);

    /// <summary>Write the document to a stream (must be seekable for v1–v3 — the table offset is backpatched).</summary>
    public LocresSaveResult Save(Stream stream, LocresSaveOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return LocresWriter.Write(this, null, stream, options?.TargetVersion ?? Version, ct);
    }

    /// <summary>
    /// Write the document to disk with per-entry value overrides substituted
    /// on the fly — the document itself is NOT mutated, so one loaded
    /// document can safely back many concurrent saves (e.g. multi-language
    /// batch output plus an autosave).
    ///
    /// <para>The overlay rule: an override applies only when its value is
    /// non-empty; a null/empty override keeps the entry's base value — an
    /// untranslated row can never blank out a shipped string. Override keys
    /// matching no entry are counted in
    /// <see cref="LocresSaveResult.OverridesUnknownKey"/> and otherwise
    /// ignored. Duplicate (namespace, key) entries all receive the same
    /// override.</para>
    /// </summary>
    public LocresSaveResult SaveWith(
        IReadOnlyDictionary<(string Ns, string Key), string?> overrides,
        string path, LocresSaveOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return SaveCore(path, overrides, options, ct);
    }

    /// <summary>Stream flavor of <see cref="SaveWith(IReadOnlyDictionary{ValueTuple{string, string}, string?}, string, LocresSaveOptions?, CancellationToken)"/>.</summary>
    public LocresSaveResult SaveWith(
        IReadOnlyDictionary<(string Ns, string Key), string?> overrides,
        Stream stream, LocresSaveOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(stream);
        return LocresWriter.Write(this, overrides, stream, options?.TargetVersion ?? Version, ct);
    }

    internal LocresSaveResult SaveCore(
        string path,
        IReadOnlyDictionary<(string Ns, string Key), string?>? overrides,
        LocresSaveOptions? options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            LocresSaveResult result;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 128 * 1024))
            {
                result = LocresWriter.Write(this, overrides, fs, options?.TargetVersion ?? Version, ct);
            }
            File.Move(tmp, path, overwrite: true);
            return result;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
