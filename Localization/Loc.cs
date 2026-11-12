using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace SmartUnrealLocalization.Localization
{
    /// <summary>
    /// Static UI localization service.
    ///
    /// Usage from code:
    ///     string label = Loc.T("menu.file");
    ///
    /// Usage from XAML (compiled bindings, indexer):
    ///     xmlns:loc="using:SmartUnrealLocalization.Localization"
    ///     Text="{Binding [menu.file], Source={x:Static loc:Loc.Instance}}"
    ///
    /// When the language is switched at runtime, Instance raises PropertyChanged(null)
    /// — "every property changed" — so every binding that reads the indexer re-reads.
    /// Do NOT narrow that to an indexer name: WPF calls it "Item[]" and Avalonia calls
    /// it "Item", and getting it wrong is silent (see Load).
    ///
    /// Adding a new language: drop "strings.&lt;code&gt;.json" into Localization/ and mark it
    /// as EmbeddedResource in the .csproj.  Keys missing from the chosen language fall back
    /// to English; keys missing from English fall back to the raw key (visible to the dev).
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        public static Loc Instance { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? LanguageChanged;

        private Dictionary<string, string> _current = new();
        private Dictionary<string, string> _fallback = new();
        public string CurrentLanguage { get; private set; } = "en";

        public string this[string key]
        {
            get
            {
                if (_current.TryGetValue(key, out var v)) return v;
                if (_fallback.TryGetValue(key, out var f)) return f;
                return key;
            }
        }

        public static string T(string key) => Instance[key];

        /// <summary>
        /// The counted form of <paramref name="key"/> for <paramref name="count"/>
        /// in the CURRENT language: looks up <c>key.one</c> / <c>key.few</c> /
        /// <c>key.many</c> / <c>key.other</c> per CLDR.
        ///
        /// <para>Callers still pass the number to <c>string.Format</c> themselves —
        /// this only picks which sentence to format, because a counted string often
        /// carries other placeholders too.</para>
        ///
        /// <para>Fallback chain: the exact category → <c>other</c> → <c>one</c> →
        /// the bare key. So a language that is missing a form degrades to another
        /// form of the same language before falling back to English, and never
        /// renders the raw key while any form exists.</para>
        /// </summary>
        public static string Plural(string key, int count)
        {
            string category = Localization.Plural.Category(Instance.CurrentLanguage, count);
            var loc = Instance;
            if (loc._current.TryGetValue($"{key}.{category}", out var exact)) return exact;
            if (loc._current.TryGetValue($"{key}.other", out var other)) return other;
            if (loc._current.TryGetValue($"{key}.one", out var one)) return one;

            // Nothing in the chosen language — repeat the walk over English.
            string fallbackCategory = Localization.Plural.Category("en", count);
            if (loc._fallback.TryGetValue($"{key}.{fallbackCategory}", out var f)) return f;
            if (loc._fallback.TryGetValue($"{key}.other", out var fo)) return fo;
            return Instance[key];
        }

        public void Load(string langCode)
        {
            _fallback = LoadEmbedded("en") ?? new();
            _current = LoadEmbedded(langCode) ?? _fallback;
            CurrentLanguage = langCode;

            // null = "every property changed", which is what happened: the indexer
            // now answers differently for all ~700 keys, and FontFamily/
            // FontFamilyMono/CurrentLanguage changed with it.
            //
            // This used to raise "Item[]" — WPF's indexer name. Avalonia's is
            // "Item", so the notification matched NOTHING and not one binding
            // re-read: switching the language changed the config and the fonts
            // while every visible string stayed as it was until the next start.
            // Raising null sidesteps the convention entirely and cannot rot if it
            // changes again. Pinned by LanguageSwitchTests; the framework
            // behaviour itself was measured in a headless render harness.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            LanguageChanged?.Invoke();
        }

        // ── Per-language presentation ───────────────────────────────────────
        // A language file carries its own metadata under "_meta.*": its native
        // name for the picker, and the font stacks to render it with. Keeping
        // this IN the language file is what makes adding a language a
        // drop-in-a-json operation with no C# change — the property that would
        // otherwise erode the first time someone shipped Chinese.

        /// <summary>
        /// UI font stack for the current language. Latin-first everywhere: Inter
        /// (bundled) supplies Latin, Cyrillic and Vietnamese, and the CJK files
        /// name an explicit CJK face after it. Without that name Windows falls
        /// back to SimSun/MS Mincho for Chinese and Japanese — a print serif in a
        /// dense IDE-style UI.
        /// </summary>
        public string FontFamily => Meta("_meta.fontFamily", "Inter, Segoe UI, Roboto, sans-serif");

        /// <summary>Monospace stack (logs, keys, diffs) — same reasoning.</summary>
        public string FontFamilyMono
            => Meta("_meta.fontMono", "JetBrains Mono, Cascadia Code, Consolas, Menlo, monospace");

        private string Meta(string key, string fallback)
            => _current.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        /// <summary>
        /// The language's own name for itself ("Deutsch", "日本語"), read from that
        /// language's file rather than from a table in the settings panel — a
        /// table is exactly the thing that gets forgotten when a language is added.
        /// Falls back to the uppercased code so a half-finished file is visible.
        /// </summary>
        public string NativeName(string langCode)
        {
            if (_nativeNames.TryGetValue(langCode, out var cached)) return cached;
            // Read WITHOUT caching the dictionary: the language menu asks all
            // fifteen files for one metadata key, and retaining fifteen full
            // string tables to answer that would be a poor trade. The name
            // itself is cached below, so each file is read at most once.
            var dict = ReadEmbedded(langCode);
            string name = dict != null && dict.TryGetValue("_meta.name", out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : langCode.ToUpperInvariant();
            _nativeNames[langCode] = name;
            return name;
        }

        private readonly Dictionary<string, string> _nativeNames = new();

        public IEnumerable<string> AvailableLanguages()
        {
            var asm = typeof(Loc).Assembly;
            return asm.GetManifestResourceNames()
                .Where(n => n.Contains(".strings.") && n.EndsWith(".json"))
                .Select(n =>
                {
                    var parts = n.Split('.');
                    return parts.Length >= 3 ? parts[^2] : "";
                })
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct();
        }

        /// <summary>
        /// Parsed language files, kept for the life of the process.
        ///
        /// <para>Switching the language used to re-parse two JSON files every
        /// time — English plus the target — which was fine when the only way to
        /// switch was a settings dialog. It is now a header button, so the same
        /// file can be parsed dozens of times in a session. Fifteen dictionaries
        /// of ~730 short strings is well under two megabytes, and only languages
        /// actually visited are ever built.</para>
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>?> _parsed = new();

        private static Dictionary<string, string>? LoadEmbedded(string langCode)
        {
            lock (_parsed)
            {
                if (_parsed.TryGetValue(langCode, out var cached)) return cached;
                var loaded = ReadEmbedded(langCode);
                _parsed[langCode] = loaded;
                return loaded;
            }
        }

        private static Dictionary<string, string>? ReadEmbedded(string langCode)
        {
            try
            {
                var asm = typeof(Loc).Assembly;
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith($"strings.{langCode}.json", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new();
            }
            catch
            {
                return null;
            }
        }
    }
}
