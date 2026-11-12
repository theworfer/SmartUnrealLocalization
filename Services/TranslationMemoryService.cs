using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    public class TranslationMemoryService
    {
        private ConcurrentDictionary<string, string> _memory = new();
        private string _dbPath;
        private string _langPair = "";

        public int Count => _memory.Count;

        public TranslationMemoryService()
        {
            _dbPath = AppConfig.TmDbPath();
        }

        public void SetLanguagePair(string from, string to)
        {
            string newPair = $"{from}_{to}".ToLowerInvariant();
            if (newPair == _langPair) return;
            _langPair = newPair;
            _dbPath = Path.Combine(AppConfig.ConfigDir(), $"translation_memory_{_langPair}.json");
            Load();
        }

        public bool TryGet(string source, out string translation)
        {
            translation = "";
            // A source carrying the composite separator could address a key-aware
            // entry and receive a translation meant for one specific key. Control
            // characters in game text are rare but perfectly legal, so this is
            // enforced rather than assumed.
            if (source is null || source.IndexOf(KeyedSep) >= 0) return false;
            return _memory.TryGetValue(source, out translation!);
        }

        public void Add(string source, string translation)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation)) return;
            if (source.IndexOf(KeyedSep) >= 0) return;   // never let a source overwrite a keyed entry
            // Latest wins: re-translating a string with a corrected result must
            // UPDATE the memory (TryAdd made the first-ever translation immortal).
            _memory[source] = translation;
        }

        /// <summary>U+001F unit separator — cannot occur in real key/source text
        /// (same convention as CommentService.ComposeKey).</summary>
        private const char KeyedSep = (char)0x1F;

        /// <summary>
        /// Store a KEY-AWARE entry ("ns/key" + source → target) for a row translated
        /// with Key Context on: its translation may be key-specific (e.g. "Charge" →
        /// "Плата" only under Store.Charge), so it must never overwrite the generic
        /// source-only entry. Stored in the same per-pair file under a composite
        /// dictionary key; generic <see cref="TryGet"/> lookups can never collide
        /// with it (real source text cannot contain U+001F).
        /// </summary>
        public void AddKeyed(string key, string source, string translation)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(source)
                || string.IsNullOrWhiteSpace(translation)) return;
            if (key.IndexOf(KeyedSep) >= 0 || source.IndexOf(KeyedSep) >= 0) return; // ambiguous composite
            _memory[key + KeyedSep + source] = translation;
        }

        /// <summary>
        /// Exact-match read of a key-aware entry: same key AND same source text.
        /// Safe to reuse without losing Key-Context precision — it is literally the
        /// same string in the same place of the same game; a source-text change
        /// (game update) or a different key misses by construction.
        /// </summary>
        public bool TryGetKeyed(string key, string source, out string translation)
        {
            translation = "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(source)) return false;
            if (key.IndexOf(KeyedSep) >= 0 || source.IndexOf(KeyedSep) >= 0) return false;
            return _memory.TryGetValue(key + KeyedSep + source, out translation!);
        }

        public void Save()
        {
            try
            {
                var snapshot = _memory.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value);
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                // Atomic: TM is accumulated API spend — a crash mid-write must not
                // corrupt it (a corrupt TM used to be silently replaced-empty).
                var tmp = _dbPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _dbPath, overwrite: true);
                Console.WriteLine($"[TM] Saved {snapshot.Count} entries ({_langPair}) to {_dbPath}");
            }
            catch (Exception ex) { Console.WriteLine($"[TM] Save error: {ex.Message}"); }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    var json = File.ReadAllText(_dbPath);
                    Dictionary<string, string> loaded;
                    try
                    {
                        loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
                    }
                    catch (Exception parseEx)
                    {
                        // Preserve the unreadable TM for recovery instead of letting
                        // the next Save overwrite it with an empty database.
                        ReviewService.PreserveCorrupt(_dbPath, parseEx);
                        _memory = new();
                        return;
                    }
                    // Dedup repeated target strings (many entries share the same translation)
                    // so the TM doesn't keep N copies of "Yes"/"OK"/… in memory.
                    var pool = new Utils.StringPool(loaded.Count);
                    var deduped = new ConcurrentDictionary<string, string>();
                    foreach (var kv in loaded) deduped[pool.Intern(kv.Key)] = pool.Intern(kv.Value);
                    _memory = deduped;
                    Console.WriteLine($"[TM] Loaded {_memory.Count} entries ({_langPair}) from {_dbPath}");
                }
                else
                {
                    _memory = new();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TM] Load error: {ex.Message}");
                _memory = new();
            }
        }

        public void Clear()
        {
            _memory.Clear();
            Save();
        }
    }
}
