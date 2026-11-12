namespace Locres.IO;

/// <summary>
/// Exact per-parse string dedup pool (ordinal). Deliberately NOT
/// string.Intern — the pool dies with the parse, so pooled strings can be
/// collected when the document is dropped.
/// </summary>
internal sealed class StringInterner
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);

    public string Intern(string s)
    {
        if (s.Length == 0) return "";
        if (_pool.TryGetValue(s, out var existing)) return existing;
        _pool[s] = s;
        return s;
    }
}
