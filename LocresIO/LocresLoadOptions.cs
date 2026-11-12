namespace Locres.IO;

/// <summary>Options for <see cref="LocresDocument.Load(string, LocresLoadOptions?)"/> / Parse.</summary>
public sealed class LocresLoadOptions
{
    /// <summary>
    /// Optional string interner applied to every decoded namespace name, key
    /// and value — lets a host application share one dedup pool across files.
    /// When null, LocresIO uses its own per-parse pool (identical strings
    /// within one file become one object; the pool is dropped after parsing).
    /// </summary>
    public Func<string, string>? Interner { get; set; }
}
