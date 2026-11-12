using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// Verdict cache for the AI QC layer: re-running QC after fixing a few rows
    /// must not pay for the untouched ones again. Keyed by everything that can
    /// change the answer — model, prompt version, the QC guidance text, and the
    /// row's own texts — so a changed prompt or an edited row misses by
    /// construction (the report calls this out explicitly).
    ///
    /// <para>Stored per language pair in %AppData%/SmartUnrealLocalization/qc_cache_{pair}.json,
    /// written atomically. It is a pure optimisation: a corrupt or missing cache
    /// only costs API calls, never correctness.</para>
    /// </summary>
    public sealed class QcCache
    {
        /// <summary>Bump when the AI prompt changes so old verdicts are not reused.</summary>
        public const string PromptVersion = "qc-1";

        public sealed class Entry
        {
            [JsonProperty("verdict")] public string Verdict { get; set; } = "ok";   // ok | fix
            [JsonProperty("fix")] public string? Fix { get; set; }
            [JsonProperty("reason")] public string? Reason { get; set; }
            [JsonProperty("model")] public string Model { get; set; } = "";
            [JsonProperty("utc")] public DateTime Utc { get; set; } = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private string _path = "";

        public int Count => _entries.Count;

        public void LoadForPair(string from, string to)
        {
            string pair = $"{from}_{to}".ToLowerInvariant();
            _path = Path.Combine(AppConfig.ConfigDir(), $"qc_cache_{pair}.json");
            _entries.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(_path));
                if (loaded == null) return;
                foreach (var kv in loaded) _entries[kv.Key] = kv.Value;
            }
            catch (Exception ex)
            {
                // A cache is disposable — never block a run over it.
                Console.WriteLine($"[QC cache] load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Upper bound on stored verdicts. Without it the file grows forever —
        /// every edited row leaves its old verdict behind, and entries written by
        /// an older key format can never be reached again. Oldest go first.
        /// </summary>
        private const int MaxEntries = 200_000;

        public void Save()
        {
            if (_path.Length == 0 || _entries.IsEmpty) return;
            try
            {
                var all = _entries.ToArray();
                var snapshot = (all.Length > MaxEntries
                        ? all.OrderByDescending(kv => kv.Value.Utc).Take(MaxEntries)
                        : (IEnumerable<KeyValuePair<string, Entry>>)all)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Console.WriteLine($"[QC cache] save failed: {ex.Message}"); }
        }

        public bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry!);

        public void Put(string key, Entry entry) => _entries[key] = entry;

        /// <summary>
        /// Cache key: model + prompt version + guidance + original + reference +
        /// current target. Any of those changing makes the old verdict invalid.
        /// </summary>
        public static string KeyFor(string model, string guidance, string original, string? reference, string target)
        {
            const char sep = (char)0x1F;
            // Length-prefixed, with a distinct marker for null: a plain join made
            // "no reference file" and "an empty reference" hash identically, and
            // left field boundaries ambiguous for any text containing the
            // separator. Both would silently reuse the wrong verdict.
            var sb = new StringBuilder();
            void Add(string? v)
            {
                sb.Append(v is null ? "-" : v.Length.ToString()).Append(sep)
                  .Append(v ?? "").Append(sep);
            }
            Add(PromptVersion); Add(model); Add(guidance); Add(original); Add(reference); Add(target);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash, 0, 16); // 128 bits — collision-free in practice
        }
    }
}
