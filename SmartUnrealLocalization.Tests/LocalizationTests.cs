using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Localization;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Keeps every shipped language honest with English and with the code.
///
/// <para>Localization rots quietly. A key added to English and forgotten in one
/// of the other files falls back to English, so nothing breaks and nobody
/// notices until a user sees half a sentence in the wrong language. A key
/// renamed in code but not in the files renders as the raw key. A format
/// placeholder dropped in translation throws <c>FormatException</c> — and these
/// strings are mostly error paths, so the crash lands exactly where the user is
/// already in trouble.</para>
///
/// <para>With fourteen languages none of that can be caught by reading. These
/// tests run over every <c>strings.*.json</c> found on disk, so a new language
/// is covered the moment its file exists.</para>
///
/// <para>Deliberately English, and therefore not checked here: exception text
/// from .NET, file names and paths, and the MCP bridge's protocol lines. The
/// rule Visual Studio uses — the IDE speaks your language, tool output speaks
/// its own.</para>
/// </summary>
public class LocalizationTests
{
    private static string RepoRoot() => TestPaths.RepoRoot();

    private static IEnumerable<string> Languages()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Localization"), "strings.*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f)["strings.".Length..])
                    .OrderBy(c => c);

    private static Dictionary<string, string> Load(string lang)
    {
        var o = JObject.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "Localization", $"strings.{lang}.json")));
        return o.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    private static IEnumerable<(string Path, string Text)> Sources(params string[] extensions)
    {
        string root = RepoRoot();
        foreach (var dir in new[] { "ViewModels", "Views", "Controls", "Services", "Models", "Utils" })
        {
            string full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(f))) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return (Path.GetRelativePath(root, f), File.ReadAllText(f));
            }
        }
    }

    /// <summary>Keys whose VALUE is language metadata, so parity applies but equality never does.</summary>
    private static bool IsMeta(string key) => key.StartsWith("_meta.", StringComparison.Ordinal);

    /// <summary>
    /// A plural variant. These deliberately DIFFER between languages — English
    /// ships two forms, Ukrainian three, Chinese one — so they are excluded from
    /// key-for-key parity and checked by the plural tests instead.
    /// </summary>
    private static bool IsPluralVariant(string key)
        => Regex.IsMatch(key, @"\.(one|two|few|many|other)$");

    /// <summary>
    /// English strings a translation may legitimately leave alone, with the
    /// reason. An unexplained entry here is how this guard rots.
    /// </summary>
    private static readonly Dictionary<string, string> MayEqualEnglish = new()
    {
        ["welcome.title"] = "the product name — translating it would be a different product",
    };

    /// <summary>
    /// What stays English in every language — identifiers and the terms of art
    /// the Ukrainian file keeps (see Localization/GLOSSARY.md). Used to recognise
    /// a string made ENTIRELY of them, which is legitimately the same everywhere.
    ///
    /// <para>Command names are deliberately NOT here. Until 2026-09-23 this list
    /// held Undo, Redo, Find, Replace, Settings, Import, Export, Merge, Fix and
    /// "Solution Explorer" — and because a string made only of those counted as
    /// "all identifiers", the untranslated check waved through about thirty
    /// command names that stayed English in thirteen languages.</para>
    /// </summary>
    private static readonly string[] IdentifierVocabulary =
    {
        "Smart Unreal Localization", "Translation Memory", "Build Locres from CSV",
        "Send to TM", "Primary Source", "Reference Translation", "Keep",
        "locres", "namespace", "SourceStringHash", "Source", "Target", "Key",
        "QC", "AI", "TM", "MCP", "API", "CSV", "PO", "TSV", "JSON", "SUL", "OK",
    };

    private static bool IsAllIdentifiers(string value)
    {
        string rest = Regex.Replace(value, @"\b(?:Ctrl|Alt|Shift)(?:\+(?:Ctrl|Alt|Shift))*\+[A-Za-z0-9]+\b", " ");
        rest = Regex.Replace(rest, @"\bF\d{1,2}\b", " ");
        foreach (var term in IdentifierVocabulary.OrderByDescending(t => t.Length))
            rest = rest.Replace(term, " ", StringComparison.Ordinal);
        return !Regex.IsMatch(rest, @"\p{L}");
    }

    [Fact]
    public void English_is_the_complete_set_and_every_language_matches_it()
    {
        var en = Load("en");
        var problems = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);

            // Plural variants are per-language by design; the plural tests own them.
            var enKeys = en.Keys.Where(k => !IsPluralVariant(k)).ToHashSet();
            var otherKeys = other.Keys.Where(k => !IsPluralVariant(k)).ToHashSet();

            var missing = enKeys.Except(otherKeys).OrderBy(k => k).ToList();
            var extra = otherKeys.Except(enKeys).OrderBy(k => k).ToList();

            if (missing.Count > 0)
                problems.Add($"{lang}: {missing.Count} key(s) not translated (they fall back to English silently) — "
                           + string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? ", …" : ""));
            if (extra.Count > 0)
                problems.Add($"{lang}: {extra.Count} key(s) that English does not have, so nothing reads them — "
                           + string.Join(", ", extra.Take(12)) + (extra.Count > 12 ? ", …" : ""));
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_translation_keeps_the_placeholders_of_its_english_original()
    {
        var en = Load("en");
        var slot = new Regex(@"\{(\d+)\}");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (!other.TryGetValue(key, out var translated)) continue;
                var a = slot.Matches(value).Select(m => m.Groups[1].Value).ToHashSet();
                var b = slot.Matches(translated).Select(m => m.Groups[1].Value).ToHashSet();
                if (!a.SetEquals(b))
                    bad.Add($"{lang} / {key}: en {{{string.Join(",", a.Order())}}} vs {{{string.Join(",", b.Order())}}}");
            }
        }

        Assert.True(bad.Count == 0,
            "Placeholder mismatch — string.Format throws at runtime:\n  " + string.Join("\n  ", bad));
    }

    [Fact]
    public void Every_counted_message_has_all_the_plural_forms_its_language_needs()
    {
        // English gets by with two forms; Polish, Russian and Ukrainian need
        // three; Chinese, Japanese, Korean and Vietnamese need one. A missing
        // form does not crash — it silently renders another number's wording,
        // which is exactly the "1 рядку(ах)" class of bug this replaced.
        var pluralKeys = new HashSet<string>(StringComparer.Ordinal);
        var call = new Regex(@"Loc\.Plural\(\s*""([^""]+)""", RegexOptions.Compiled);
        var switched = new Regex(@"=>\s*""((?:log|status|toast|qc|tabs|find|mtl)\.[^""]+)""", RegexOptions.Compiled);

        foreach (var (_, text) in Sources(".cs"))
        {
            foreach (Match m in call.Matches(text)) pluralKeys.Add(m.Groups[1].Value);
            // Loc.Plural(status switch { ... => "log.review.approved", ... }, n)
            if (text.Contains("Loc.Plural(status switch"))
                foreach (Match m in switched.Matches(text)) pluralKeys.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(pluralKeys);
        var missing = new List<string>();

        foreach (var lang in Languages())
        {
            var d = Load(lang);
            foreach (var key in pluralKeys.Order())
                foreach (var cat in Plural.CategoriesFor(lang))
                    if (!d.ContainsKey($"{key}.{cat}"))
                        missing.Add($"{lang}: {key}.{cat}");
        }

        Assert.True(missing.Count == 0,
            "Counted messages missing a plural form required by that language:\n  "
          + string.Join("\n  ", missing));
    }

    [Fact]
    public void No_language_file_carries_a_plural_form_its_language_never_selects()
    {
        // The mirror of the test above: "row.few" in German is dead weight that
        // will never render, and it usually means the file was copied from a
        // Slavic one without re-reading the rules.
        var dead = new List<string>();
        var suffix = new Regex(@"\.(one|two|few|many|other)$");

        foreach (var lang in Languages())
        {
            var allowed = Plural.CategoriesFor(lang).ToHashSet();
            foreach (var key in Load(lang).Keys)
            {
                var m = suffix.Match(key);
                if (m.Success && !allowed.Contains(m.Groups[1].Value))
                    dead.Add($"{lang}: {key} (that language selects only {string.Join("/", allowed)})");
            }
        }

        Assert.True(dead.Count == 0, string.Join("\n  ", dead));
    }

    [Fact]
    public void Every_language_declares_its_name_and_its_fonts()
    {
        // These drive the Settings picker and the font stack. Missing them is not
        // fatal — there are fallbacks — but the picker would show "ZH-HANS" and
        // Windows would resolve Chinese to SimSun, a print serif.
        var missing = new List<string>();
        foreach (var lang in Languages())
        {
            var d = Load(lang);
            foreach (var key in new[] { "_meta.name", "_meta.fontFamily", "_meta.fontMono" })
                if (!d.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                    missing.Add($"{lang}: {key}");
        }
        Assert.True(missing.Count == 0, string.Join("\n  ", missing));
    }

    /// <summary>
    /// Identifiers that must survive translation verbatim — see
    /// <c>Localization/GLOSSARY.md</c>. Only unambiguous ones are enforced here:
    /// "key" and "Target" are also ordinary English words and would fire on
    /// prose, so those stay a review matter rather than a test.
    /// </summary>
    private static readonly string[] ProtectedTerms =
    {
        "locres", "SourceStringHash", "namespace", "Translation Memory",
        "QC", "MCP", "CSV", "TSV", "JSON", "F8",
    };

    [Fact]
    public void Protected_terms_survive_translation()
    {
        var en = Load("en");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (IsMeta(key) || !other.TryGetValue(key, out var t)) continue;
                foreach (var term in ProtectedTerms)
                {
                    int expected = Regex.Matches(value, Regex.Escape(term)).Count;
                    if (expected == 0) continue;
                    if (Regex.Matches(t, Regex.Escape(term)).Count == 0)
                        bad.Add($"{lang} / {key}: \"{term}\" was translated away");
                }
            }
        }

        Assert.True(bad.Count == 0,
            "These are identifiers, not words — the docs, the UI and the file format all use them:\n  "
          + string.Join("\n  ", bad));
    }

    [Fact]
    public void Keyboard_shortcuts_are_never_translated()
    {
        var en = Load("en");
        var shortcut = new Regex(@"\b(?:Ctrl|Alt|Shift)(?:\+(?:Ctrl|Alt|Shift))*\+[A-Za-z0-9]+\b");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (!other.TryGetValue(key, out var t)) continue;
                foreach (Match m in shortcut.Matches(value))
                    if (!t.Contains(m.Value, StringComparison.Ordinal))
                        bad.Add($"{lang} / {key}: {m.Value} is missing — the tooltip and the docs must agree");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Fact]
    public void No_translation_was_left_as_the_english_original()
    {
        // A file built by copying English and translating downwards leaves the
        // untouched tail identical. Short shared tokens ("OK", "CSV", "QC") are
        // legitimately the same, so only sentences count.
        var en = Load("en");
        var suspicious = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            int same = 0;
            var examples = new List<string>();
            foreach (var (key, value) in en)
            {
                if (IsMeta(key) || value.Length < 25 || MayEqualEnglish.ContainsKey(key)
                 || IsAllIdentifiers(value)) continue;
                if (other.TryGetValue(key, out var t) && t == value)
                {
                    same++;
                    if (examples.Count < 5) examples.Add(key);
                }
            }
            if (same > 0)
                suspicious.Add($"{lang}: {same} sentence(s) identical to English — {string.Join(", ", examples)}");
        }

        Assert.True(suspicious.Count == 0, string.Join("\n  ", suspicious));
    }

    /// <summary>
    /// Words that are simply the same in a language — its own word, not an
    /// untranslated one. Every entry says why, so the list cannot become a place
    /// to hide an English leftover.
    /// </summary>
    private static readonly Dictionary<(string Lang, string Key), string> SameWordInLanguage = new()
    {
        [("it", "menu.file")] = "\"File\" is Italian for the File menu",
        [("fr", "menu.help.docs")] = "\"Documentation\" is French",
        [("de", "about.version")] = "\"Version\" is German",
        [("fr", "about.version")] = "\"Version\" is French",
        [("de", "action.filter")] = "\"Filter\" is German",
        [("de", "filter.button")] = "\"Filter\" is German",
        [("de", "filter.section.status")] = "\"Status\" is German",
        [("de", "filter.popover.statusTitle")] = "\"Status\" is German",
        [("de", "status.panel.title")] = "\"STATUS\" is German",
        [("pt-BR", "status.panel.title")] = "\"STATUS\" is the Brazilian Portuguese word in use",
        [("de", "welcome.startHeading")] = "\"Start\" is German",
        [("pl", "welcome.startHeading")] = "\"Start\" is Polish",
        [("de", "find.inColumnTag")] = "\"In:\" is German",
        [("it", "find.inColumnTag")] = "\"In:\" is Italian",
        [("de", "ai.ctx.q.community")] = "\"Community\" is the German word in use",
        [("fr", "status.panel.sessions")] = "\"Sessions\" is French",
        [("de", "set.section.editor")] = "\"EDITOR\" is German",
        [("es", "set.section.editor")] = "\"EDITOR\" is Spanish",
        [("it", "set.section.editor")] = "\"EDITOR\" is Italian",
        [("pt-BR", "set.section.editor")] = "\"EDITOR\" is Portuguese",
        [("fr", "set.minutes")] = "\"minutes\" is French",
        [("es", "status.msg.error")] = "\"Error\" is Spanish",
        [("es", "log.severity.error")] = "\"error\" is Spanish",
        [("es", "logs.level.error")] = "\"Error\" is Spanish",
        [("es", "ai.err.http")] = "\"Error de API\" is Spanish",
        [("es", "common.no")] = "\"No\" is Spanish",
        [("it", "common.no")] = "\"No\" is Italian",
        [("de", "set.section.mcp")] = "\"Server\" is German",
        [("it", "set.section.mcp")] = "\"server\" is the Italian word in use",
        [("it", "set.section.aiProvider")] = "\"provider\" is the Italian word in use",
        [("it", "qc.report")] = "\"report\" is the Italian word in use",
        [("fr", "qc.diff.title")] = "\"Suggestion\" is French",
        [("fr", "set.section.validation")] = "\"VALIDATION\" is French",
        [("fr", "mtl.lang")] = "\"lang.\" is the French abbreviation of langue",
    };

    /// <summary>"Info" as a severity label — the accepted abbreviation, as
    /// Ukrainian's own "Інфо" is.</summary>
    private static bool IsInfoAbbreviation(string lang, string key)
        => lang is "de" or "es" or "fr" or "it" or "pl" or "pt-BR"
        && key is "logs.filter.info" or "logs.level.info" or "problems.filter.info" or "log.severity.info";

    /// <summary>The English words of <paramref name="text"/> that a translation
    /// is expected to replace: letters only, with identifiers, shortcuts and
    /// placeholders removed.</summary>
    private static List<string> TranslatableWords(string text)
    {
        string rest = Regex.Replace(text, @"\{\d+\}", " ");
        rest = Regex.Replace(rest, @"\b(?:Ctrl|Alt|Shift)(?:\+(?:Ctrl|Alt|Shift))*\+[A-Za-z0-9]+\b", " ");
        rest = Regex.Replace(rest, @"\bF\d{1,2}\b", " ");
        // Model ids and versions ("gpt-4o", "gemini-1.5-flash") are identifiers:
        // any token carrying a digit is one.
        rest = Regex.Replace(rest, @"[A-Za-z0-9._-]*\d[A-Za-z0-9._-]*", " ");
        foreach (var term in IdentifierVocabulary.OrderByDescending(t => t.Length))
            rest = Regex.Replace(rest, $@"(?<![\p{{L}}]){Regex.Escape(term)}(?![\p{{L}}])", " ");
        return Regex.Matches(rest, @"[A-Za-z]{2,}").Select(m => m.Value).ToList();
    }

    [Fact]
    public void Where_Ukrainian_translates_a_string_so_does_every_language()
    {
        // The terminology rule, as a test: strings.uk.json is the reference. A
        // string it translates must not survive in another language with all of
        // its English words still standing — which is exactly what "Undo（Ctrl+Z）"
        // in Japanese or "SETTINGS" above "Einstellungen" in German looked like.
        var en = Load("en");
        var uk = Load("uk");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang is "en" or "uk") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (IsMeta(key) || IsPluralVariant(key)) continue;
                if (!uk.TryGetValue(key, out var ukValue) || ukValue == value) continue;   // Ukrainian keeps it
                if (!other.TryGetValue(key, out var t)) continue;
                if (SameWordInLanguage.ContainsKey((lang, key)) || IsInfoAbbreviation(lang, key)) continue;

                var words = TranslatableWords(value);
                if (words.Count == 0) continue;
                bool allStillThere = words.All(w =>
                    Regex.IsMatch(t, $@"(?<![A-Za-z]){Regex.Escape(w)}(?![A-Za-z])", RegexOptions.IgnoreCase));
                if (allStillThere)
                    bad.Add($"{lang} / {key}: \"{t}\" (Ukrainian: \"{ukValue}\")");
            }
        }

        Assert.True(bad.Count == 0,
            "Ukrainian translates these; the language below kept the English. Translate it, or — if it is " +
            "truly that language's own word — add it to SameWordInLanguage with the reason:\n  " +
            string.Join("\n  ", bad));
    }

    [Fact]
    public void Every_key_used_in_code_exists()
    {
        var en = Load("en");
        var call = new Regex(@"Loc\.T\(""([^""]+)""\)", RegexOptions.Compiled);
        var missing = new List<string>();

        foreach (var (path, text) in Sources(".cs"))
            foreach (Match m in call.Matches(text))
            {
                string key = m.Groups[1].Value;
                if (!en.ContainsKey(key))
                {
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    missing.Add($"{path}:{line}  {key}");
                }
            }

        Assert.True(missing.Count == 0,
            "Loc.T() with a key that is in no strings file — it renders as the raw key:\n  "
          + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_key_used_in_xaml_exists()
    {
        var en = Load("en");
        var binding = new Regex(@"\{Binding \[([^\]]+)\], Source=\{x:Static loc:Loc\.Instance\}\}", RegexOptions.Compiled);
        var missing = new List<string>();

        foreach (var (path, text) in Sources(".axaml"))
            foreach (Match m in binding.Matches(text))
            {
                string key = m.Groups[1].Value;
                if (!en.ContainsKey(key))
                {
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    missing.Add($"{path}:{line}  {key}");
                }
            }

        Assert.True(missing.Count == 0,
            "A XAML binding points at a key no strings file defines:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_status_bar_speaks_the_user_s_language()
    {
        var literal = new Regex(@"StatusText\s*=\s*(\$?"")", RegexOptions.Compiled);
        var hits = new List<string>();

        foreach (var (path, text) in Sources(".cs"))
            foreach (Match m in literal.Matches(text))
            {
                if (text.Substring(m.Index).StartsWith("StatusText = \"\"")) continue;
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                hits.Add($"{path}:{line}");
            }

        Assert.True(hits.Count == 0,
            "Hardcoded English in the status bar — route it through Loc.T:\n  " + string.Join("\n  ", hits));
    }

    [Fact]
    public void Edge_whitespace_is_preserved()
    {
        // Several of these strings are concatenated into a sentence by the
        // caller, so a leading or trailing space is layout, not decoration.
        var en = Load("en");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (IsMeta(key) || !other.TryGetValue(key, out var t) || value.Length == 0 || t.Length == 0) continue;
                if (char.IsWhiteSpace(value[0]) != char.IsWhiteSpace(t[0])
                 || char.IsWhiteSpace(value[^1]) != char.IsWhiteSpace(t[^1]))
                    bad.Add($"{lang} / {key}");
            }
        }

        Assert.True(bad.Count == 0,
            "Leading/trailing whitespace differs from English — these strings are glued to their neighbours:\n  "
          + string.Join("\n  ", bad));
    }

    [Fact]
    public void A_menu_item_that_opens_a_dialog_keeps_its_ellipsis()
    {
        // "..." is the platform convention for "this asks you something first".
        // Dropping it makes a menu promise an immediate action.
        var en = Load("en");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (!value.EndsWith("...", StringComparison.Ordinal)) continue;
                if (other.TryGetValue(key, out var t)
                 && !t.EndsWith("...", StringComparison.Ordinal) && !t.EndsWith("…", StringComparison.Ordinal))
                    bad.Add($"{lang} / {key}: \"{t}\"");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Fact]
    public void Line_breaks_are_layout_and_must_match()
    {
        // The multi-paragraph dialogs (merge info, key-match check) rely on
        // their blank lines. A translation that loses them becomes a wall.
        var en = Load("en");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                if (!other.TryGetValue(key, out var t)) continue;
                int a = value.Count(c => c == '\n'), b = t.Count(c => c == '\n');
                if (a != b) bad.Add($"{lang} / {key}: en has {a}, translation has {b}");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Fact]
    public void The_emoji_that_opens_a_log_line_is_identical_everywhere()
    {
        // ✅ ⚠️ ❌ 🔍 are the severity key of the Logs panel. A user scanning
        // the log reads the glyphs, not the words — they must not drift.
        var en = Load("en");
        var lead = new Regex(@"^([\p{So}\p{Cs}️]+)");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            var other = Load(lang);
            foreach (var (key, value) in en)
            {
                var m = lead.Match(value);
                if (!m.Success || !other.TryGetValue(key, out var t)) continue;
                if (!t.StartsWith(m.Groups[1].Value, StringComparison.Ordinal))
                    bad.Add($"{lang} / {key}: expected to start with {m.Groups[1].Value}");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Fact]
    public void Menu_accelerators_exist_and_do_not_collide()
    {
        // Alt+letter opens a menu. Two menus sharing a letter means one of them
        // cannot be reached that way — invisible unless you use the keyboard.
        var topLevel = new Regex(@"^menu\.[a-z]+$");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            var letters = new List<char>();
            foreach (var (key, value) in Load(lang))
            {
                if (!topLevel.IsMatch(key)) continue;
                var m = Regex.Match(value, @"_(.)");
                if (!m.Success) { bad.Add($"{lang} / {key}: no mnemonic marker"); continue; }
                letters.Add(char.ToUpperInvariant(m.Groups[1].Value[0]));
            }
            foreach (var g in letters.GroupBy(c => c).Where(g => g.Count() > 1))
                bad.Add($"{lang}: '{g.Key}' is the accelerator of {g.Count()} menus");
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Fact]
    public void No_translation_looks_truncated_or_runaway()
    {
        // Deliberately loose: real languages vary a lot, and Chinese says in
        // ten characters what English needs forty-five for. This catches the
        // shapes that are always a mistake — a sentence replaced by a word, or
        // a translation that tripled and will not fit its control.
        var en = Load("en");
        var bad = new List<string>();

        foreach (var lang in Languages())
        {
            if (lang == "en") continue;
            bool dense = lang.StartsWith("zh") || lang == "ja" || lang == "ko";
            double floor = dense ? 0.15 : 0.40;
            var other = Load(lang);

            foreach (var (key, value) in en)
            {
                if (IsMeta(key) || value.Length < 40 || !other.TryGetValue(key, out var t)) continue;
                double ratio = (double)t.Length / value.Length;
                if (ratio < floor) bad.Add($"{lang} / {key}: {ratio:F2}x — looks cut short");
                else if (ratio > 2.5) bad.Add($"{lang} / {key}: {ratio:F2}x — will not fit");
            }
        }

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
    }

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("en", 5, "other")]
    [InlineData("uk", 1, "one")]
    [InlineData("uk", 21, "one")]
    [InlineData("uk", 11, "many")]
    [InlineData("uk", 3, "few")]
    [InlineData("uk", 14, "many")]
    [InlineData("uk", 5, "many")]
    [InlineData("uk", 0, "many")]
    [InlineData("ru", 22, "few")]
    [InlineData("ru", 112, "many")]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 21, "many")]   // Polish differs from Russian here on purpose
    [InlineData("pl", 22, "few")]
    [InlineData("pl", 5, "many")]
    [InlineData("fr", 0, "one")]     // French counts zero as singular
    [InlineData("fr", 2, "other")]
    [InlineData("pt-BR", 0, "one")]
    [InlineData("de", 0, "other")]
    [InlineData("zh-Hans", 1, "other")]
    [InlineData("ja", 5, "other")]
    [InlineData("ko", 1, "other")]
    [InlineData("vi", 1, "other")]
    public void Cldr_plural_categories_are_right(string lang, int n, string expected)
        => Assert.Equal(expected, Plural.Category(lang, n));
}
