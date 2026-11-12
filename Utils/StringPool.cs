using System;
using System.Collections.Generic;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Per-load string de-duplication. Localization files are heavily repetitive — the same
    /// source string under many keys, repeated targets ("Yes" / "OK" / "—"), a shared namespace
    /// on thousands of rows. Without dedup, every row holds its OWN copy of identical strings,
    /// multiplying RAM (the dump's AA0033 "duplicate strings"). <see cref="Intern"/> returns a
    /// single shared instance per distinct value.
    ///
    /// <para>
    /// Unlike <see cref="string.Intern(string)"/>, this pool is a plain dictionary scoped to one
    /// load: discard it after importing a file and the strings stay normally collectible once the
    /// file/tab closes (string.Intern would pin them for the whole process lifetime — a leak).
    /// </para>
    /// </summary>
    public sealed class StringPool
    {
        private readonly Dictionary<string, string> _pool;

        public StringPool(int capacity = 0)
            => _pool = capacity > 0
                ? new Dictionary<string, string>(capacity, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Returns the canonical shared instance for <paramref name="s"/> (or "" for null/empty).</summary>
        public string Intern(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (_pool.TryGetValue(s, out var existing)) return existing;
            _pool[s] = s;
            return s;
        }
    }
}
