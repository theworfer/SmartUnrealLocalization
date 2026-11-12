# Glossary — what stays English, and what does not

The app is a localization tool, so its own vocabulary has to behave like the
vocabulary it teaches: a term that names a *thing in a file* is an identifier,
not a word, and translating it makes the manual, the forums and the file format
stop agreeing with each other. Everything the user reads as an instruction —
commands, menus, buttons, panel titles — is translated, the way Visual Studio
translates its own.

**The reference is `strings.uk.json`.** Where it translates a string, every
language translates it; where it keeps a term English, every language keeps it.
`LocalizationTests.Where_Ukrainian_translates_a_string_so_does_every_language`
enforces that, so the two halves of this page cannot drift apart again.

## Never translated

**File formats and the Unreal data model** — these are literally what is written
in the file, and users type them into search boxes and issue reports:

`locres` · `.locres` · `.po` · `.csv` · `PO` · `CSV` · `TSV` · `JSON` ·
`namespace` · `key` · `SourceStringHash` · `GUID` · `regex`

**The two columns** — the whole UI, the docs and every conversation about the
app name them this way:

`Source` · `Target`

**Terms of art the Ukrainian file keeps** — fixed names of this app's features,
used the same way in the docs and the MCP tools:

`QC` · `AI` · `TM` · `Translation Memory` · `Send to TM` · `MCP` · `API` ·
`Build Locres from CSV` · `Keep` · `Primary Source` · `Reference Translation`

…and the feature tag that opens a log line (`Merge:`, `Import Target:`, `QC:`),
written as Ukrainian writes it.

Until 2026-09-23 this list also held `Undo`, `Redo`, `Find`, `Replace`,
`Settings`, `Import`, `Export`, `Merge`, `Fix`, `Import Target locres` and
`Merge to new version`. That is how about thirty command names stayed English in
thirteen languages while Ukrainian translated them — a German Settings panel
headed SETTINGS directly above the title *Einstellungen*.

**Names of things outside the app:**

`Smart Unreal Localization` · `SUL` · `Unreal Engine` · `Claude Desktop` ·
`Windows` · model ids (`gpt-4o`, `gemini-1.5-flash`, …) · language codes
(`pt-BR`, `zh-Hans`, …)

**Every keyboard shortcut**, verbatim, including the modifier names:

`Ctrl+Z` · `Ctrl+Y` · `Ctrl+F` · `Ctrl+H` · `Ctrl+E` · `Ctrl+O` · `Ctrl+T` ·
`Ctrl+Shift+T` · `Ctrl+Alt+L` · `Ctrl+Enter` · `Shift+Enter` · `F8` · `Esc` ·
`Enter`

Windows itself localizes modifier names in some languages, but this app's help
text, tooltips and docs all quote them in English, and a user searching the
guide has to find what the tooltip said.

## Translated everywhere

**Commands, menu items, buttons and panel titles**, as Visual Studio translates
its own: *Rückgängig*, *Suchen und Ersetzen*, *Projektmappen-Explorer*,
*Einstellungen* — *元に戻す*, *検索と置換*, *ソリューション エクスプローラー*, *設定*.

**A sentence that points at one of them uses the name on screen**, in that
language: "…in den Einstellungen konfiguriert…", "…drücken „Übersetzung
starten“…". A pointer that says *Settings* to a user whose panel says
*Einstellungen* sends them looking for something that is not there.

And everything else: verbs and actions ("Save", "Cancel", "Apply"), nouns from
ordinary language ("row", "file", "folder", "language", "error", "warning",
"suggestion", "problem", "translation"), all explanatory prose, all tooltips,
all status and log sentences.

A word that is simply the same in a language — German *Filter*, *Status*,
*Export*; Italian *File*; French *Documentation* — is that language's word, not
an untranslated one.

Note the split this creates on purpose: the *column* is `Target` (never
translated), but the *word* "translation" in a sentence is translated normally.
"Source скопійовано в Target для 3 рядків" — identifiers stay, language flows.

## Punctuation and typography

Use the target language's own conventions, not English ones:

- quotation marks: „…“ (de) · «…» (fr, ru, uk, es-alt) · « … » with thin spaces
  (fr) · 「…」 (ja) · “…” (zh, ko, en)
- French puts a narrow no-break space before `:` `;` `!` `?`
- CJK uses full-width punctuation `，。：（）` and needs no space around Latin
  runs, though a thin space reads better and is acceptable
- keep the marks that prefix log lines (✓ ✕ ⚠ ℹ, from `Utils/Glyphs`) exactly
  as English has them — they are the visual severity key; colour emoji are not
  allowed at all (`EmojiPolicyTests`)

## Placeholders

`{0}`, `{1}`, `{2}` are `string.Format` slots. They may be **reordered** to suit
the language's word order — that is the point of numbered slots — but never
renamed, never dropped, and never added. `LocalizationTests` fails the build if
the set of slots differs from English.

Counted messages use CLDR plural keys (`key.one` / `key.few` / `key.many` /
`key.other`); `Localization/Plural.cs` says which forms each language must
provide, and the tests enforce completeness.
