namespace Locres.IO;

/// <summary>Options for <see cref="LocresMerger.Merge(LocresDocument, LocresDocument, LocresMergeOptions?)"/>.</summary>
public sealed class LocresMergeOptions
{
    /// <summary>
    /// Carry a translation only when the entry's <c>SourceStringHash</c> is the
    /// same in both files — that is, only when the ORIGINAL text behind the key
    /// is provably unchanged. <b>On by default.</b>
    ///
    /// <para>A key match alone is not proof of anything. Game updates reword
    /// strings under the same key all the time, and carrying the old line then
    /// ships text that says the wrong thing — silently, in a file nobody
    /// re-reads. The hash is the only evidence a localized .locres carries: it
    /// fingerprints the source text, which the file itself does not contain.</para>
    ///
    /// <para>Turn it off when that evidence is unusable — an old file whose
    /// hashes were rebuilt or zeroed by another tool makes every entry look
    /// changed. Before doing so, read
    /// <see cref="LocresMergeResult.RejectedValues"/>: if it holds essentially
    /// the whole file, the hashes are the problem, not the translations.</para>
    /// </summary>
    public bool RequireMatchingSourceHash { get; init; } = true;
}

/// <summary>
/// "Carry translations to a new game version" merge. The result takes the NEW
/// file's structure, version and per-entry SourceStringHash — so the output
/// matches what the updated game actually ships (keys the update removed
/// disappear, brand-new namespaces are included) — while translated values are
/// carried over from the old file wherever (namespace, key) matches AND, by
/// default, the original text is proven unchanged. Neither input is mutated.
/// </summary>
public static class LocresMerger
{
    /// <param name="oldTranslated">The previously translated .locres.</param>
    /// <param name="newVersion">The new game version's .locres (source of structure).</param>
    /// <param name="options">Null uses the safe defaults.</param>
    public static LocresMergeResult Merge(LocresDocument oldTranslated, LocresDocument newVersion,
                                          LocresMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(oldTranslated);
        ArgumentNullException.ThrowIfNull(newVersion);
        options ??= new LocresMergeOptions();

        // First occurrence wins for duplicate (ns, key) pairs in the old file.
        var oldByKey = new Dictionary<(string Ns, string Key), LocresEntry>();
        foreach (var ns in oldTranslated.Namespaces)
            foreach (var entry in ns.Entries)
                oldByKey.TryAdd((ns.Name, entry.Key), entry);

        var newKeys = new List<(string Ns, string Key)>();
        var newKeysSeen = new HashSet<(string, string)>();
        var sourceChanged = new List<(string Ns, string Key)>();
        var sourceChangedSeen = new HashSet<(string, string)>();
        var carriedValues = new Dictionary<(string Ns, string Key), string>();
        var rejectedValues = new Dictionary<(string Ns, string Key), string>();

        var namespaces = new List<LocresNamespace>(newVersion.Namespaces.Count);
        foreach (var ns in newVersion.Namespaces)
        {
            var entries = new List<LocresEntry>(ns.Entries.Count);
            foreach (var entry in ns.Entries)
            {
                string value = entry.Value;
                if (oldByKey.TryGetValue((ns.Name, entry.Key), out var old))
                {
                    // The engine's source hash changed → the source text changed
                    // between versions, so the old translation was written for
                    // wording that no longer exists.
                    bool originalChanged = old.SourceStringHash != entry.SourceStringHash;
                    if (originalChanged && sourceChangedSeen.Add((ns.Name, entry.Key)))
                        sourceChanged.Add((ns.Name, entry.Key));

                    if (old.Value.Length > 0)
                    {
                        if (originalChanged && options.RequireMatchingSourceHash)
                        {
                            // Not carried — but handed back, so a caller can show
                            // what it would have been instead of losing it.
                            rejectedValues[(ns.Name, entry.Key)] = old.Value;
                        }
                        else
                        {
                            value = old.Value;
                            carriedValues[(ns.Name, entry.Key)] = old.Value;
                        }
                    }
                }
                else if (newKeysSeen.Add((ns.Name, entry.Key)))
                {
                    newKeys.Add((ns.Name, entry.Key));
                }

                // Fresh entry: NEW file's SourceStringHash, carried/new value.
                entries.Add(new LocresEntry(entry.Key, entry.SourceStringHash, value));
            }
            namespaces.Add(new LocresNamespace(ns.Name, entries));
        }

        var document = new LocresDocument(newVersion.Version, namespaces);
        return new LocresMergeResult(document, newKeys, sourceChanged, carriedValues, rejectedValues);
    }
}

/// <summary>Outcome of <see cref="LocresMerger.Merge(LocresDocument, LocresDocument, LocresMergeOptions?)"/>.</summary>
public sealed class LocresMergeResult
{
    /// <summary>The merged document — new version's structure with old translations carried in.</summary>
    public LocresDocument Document { get; }

    /// <summary>Keys present in the new version but absent from the old file (need translation).</summary>
    public IReadOnlyList<(string Ns, string Key)> NewKeys { get; }

    /// <summary>
    /// Keys whose SourceStringHash differs between the two files — the source
    /// text changed between versions. Reported whatever the options say; whether
    /// their translation was carried depends on
    /// <see cref="LocresMergeOptions.RequireMatchingSourceHash"/>.
    /// </summary>
    public IReadOnlyList<(string Ns, string Key)> SourceChangedKeys { get; }

    /// <summary>
    /// Translations that were carried into <see cref="Document"/>, by key.
    /// Saves the caller a second pass over the document to work out what changed.
    /// </summary>
    public IReadOnlyDictionary<(string Ns, string Key), string> CarriedValues { get; }

    /// <summary>
    /// Translations that were NOT carried because the original text changed —
    /// each with the old text, so a caller can show it, log it, or offer it for
    /// review rather than silently dropping work someone did.
    /// Always empty when <see cref="LocresMergeOptions.RequireMatchingSourceHash"/> is off.
    /// </summary>
    public IReadOnlyDictionary<(string Ns, string Key), string> RejectedValues { get; }

    internal LocresMergeResult(
        LocresDocument document,
        IReadOnlyList<(string Ns, string Key)> newKeys,
        IReadOnlyList<(string Ns, string Key)> sourceChangedKeys,
        IReadOnlyDictionary<(string Ns, string Key), string> carriedValues,
        IReadOnlyDictionary<(string Ns, string Key), string> rejectedValues)
    {
        Document = document;
        NewKeys = newKeys;
        SourceChangedKeys = sourceChangedKeys;
        CarriedValues = carriedValues;
        RejectedValues = rejectedValues;
    }
}
