# Smart Unreal Localization — User Guide

Everything the app does, workflow by workflow. Ukrainian version:
[USER-GUIDE.uk.md](USER-GUIDE.uk.md).

**Installing.** Extract the release zip to a folder you can write to (Documents,
Desktop — not Program Files: exports and autosaves go next to the exe by
default) and run `SmartUnrealLocalization.exe`. The .NET runtime is inside the
exe; nothing else is needed. The exe is not code-signed, so Windows SmartScreen
may warn on the first start (**More info → Run anyway**). This guide is also
under **Help → Documentation** in the app; **Help → Check for updates** asks the project's repository for a newer release (only when you click it; nothing is downloaded — a newer version is offered as a page to open); **Help → Licenses** shows the
program's MIT license and every third-party component with its license text.

## 1. Opening files

- **File → Open (Ctrl+O)**, dragging a file onto the window or the exe, or a
  recent file — on the Welcome screen or under **File → Recent files**.
- Every open file becomes a **tab**. Tabs keep their own edits, undo history
  and unsaved-changes dot; they can be pinned, reordered by drag, and are
  restored on the next launch (lazily — a restored tab loads on first click).
- For `.locres` the title pill shows the detected format version
  (e.g. `Game.locres · en · locres v3 (CityHash64)`). Namespace and Key are
  separate columns. Entries with an empty value are hidden by default —
  **View → Show empty keys** toggles them (the file always keeps them).
- `.po` files use msgctxt as the key; files exported by this app (or by
  Unreal) carry the namespace inside msgctxt as `Namespace,Key` and are
  split back automatically.
- **PO fidelity**: translator comments (`#`, `#.`, `#:`), flags and plural
  entries survive import → edit → export. The grid edits plural **form 0**;
  the other forms are preserved untouched. `fuzzy` translations are imported
  with a log warning and the flag is dropped automatically once you edit the
  row (Poedit semantics). Obsolete (`#~`) entries are dropped.

## 2. Editing

- **Copy part of a cell:** press the left button on a Key, Source or Target cell, drag over the text and press **Ctrl+C**. A double-click selects a word in Source (a tag counts as one word) and the whole key in Key. A click still selects the row; a double-click on Target still opens its editor.

- **Target** is the only editable data column. Double-click, F2 or Enter opens the
  editor. Inside it:
  - **Enter** commits and moves to the next row (Enter there opens it); **Ctrl+Enter**
    commits and stays; **Tab** commits and moves on.
  - **Shift+Enter** is a line break in the text; **Esc** cancels the edit.
  - **Ctrl+Z / Ctrl+Y** undo and redo your typing in the editor only — they never touch
    other rows. Outside a text field they undo and redo grid edits.
  - **Ctrl+T** puts the row's Source into the editor (Ctrl+Z takes it back).
  - Double-clicking a word selects it.
- **Undo/Redo (Ctrl+Z / Ctrl+Y)** covers everything, including AI batches
  (one batch = one undo step) and Import Target (one step). History is
  capped at 100 entries per tab.
- **Copy Source → Target** (Ctrl+T current row, Ctrl+Shift+T all selected),
  column copy/paste/clear via right-click on the Target header, whole-table
  copy as TSV for Excel.
- **Selected rows** (header checkbox / per-row checkboxes) drive the
  "Selected rows: N" menu — copy/paste/cut/clear just those rows. Pasting
  requires the clipboard row count to match the selection count.
- The selection belongs to the **whole file**: a filter changes what is
  *visible*, never what is *selected*. The status bar's `Selected: N` counts
  every checked row in the file, so it is exactly what an action will run on,
  and it adds `(N shown)` when part of the selection is currently filtered out.
  The header checkbox is the one filter-aware control — it checks and unchecks
  what is on screen.
- **Find (Ctrl+F) / Replace (Ctrl+H)** — matches are highlighted in Source
  and Target; the "Found rows" filter shows only matching rows.
  A very broad query (a common word across thousands of rows) matches far more
  times than anyone can step through, so navigation stops at 5 000 and the
  counter says so: `3 / 5000+ (of 128 431)`. The number after "of" is the real
  total — narrow the query to work with all of them.

## 3. Review workflow

- Right-click a row → **Approve / Reject / Clear review**. Status icons and
  review filters (Approved / Rejected / Unreviewed) live in the bottom bar.
- **Comments** — the speech-bubble button on a row opens a free-text reviewer note.
  Comments are metadata: stored in a sidecar next to the file, never written
  into exports. Rows with comments get a filter of their own.
- Review state and comments are per-file (keyed by the file's full path) and
  survive app restarts.

## 4. AI translation

Configure in **Settings**: provider preset (Gemini / OpenAI / Anthropic /
OpenAI-compatible), API key, model name, optional base URL, batch size,
prompt caching, parallel requests, auto-retry, and an optional request-rate
limit (minimum gap between AI requests, for strict provider limits).

Your API key is stored **encrypted** (Windows DPAPI, tied to your Windows
account on this machine) — `config.json` never contains it in plain text,
so backups or copies of the config can't leak it. The flip side: the key
does not travel — on another PC or Windows account just enter it again in
Settings.

- **AI Translate** panel: language pair, scope (All / Selected /
  Untranslated), extra prompt for context ("Sci-Fi game, formal tone").
- The **Selected** scope shows how many rows that is — `Selected rows (12)` —
  and it updates while the panel is open, so you can check rows first and the
  panel first, in any order. With nothing checked it refuses to start and says
  so; it never quietly falls back to translating the whole file.
- Protection built in: `<Tags>` and `{placeholders}` are instructed to stay
  intact; tag-only rows are copied, not sent; identical sources are
  translated once and propagated; the **Translation Memory** reuses past
  translations per language pair; **protected terms** (glossary) are never
  translated. Each pair's memory is one `translation_memory_{from}_{to}.json`
  file; **Tools → Open Translation Memory folder** shows where they are.
- **Send with comments** (default on): rows with reviewer comments send the
  note as a per-row instruction and bypass the TM so the note is honored.
- **Extra Context** (both off by default; the whole panel keeps its settings
  until you close the app — nothing is written to the config):
  - *Send row Key as context* — meaningful keys (`BTN_Cancel`, `Quest_Title`)
    tell the AI where the text appears; GUID-like keys are skipped
    automatically. Rows that send a key deliberately **skip the Translation
    Memory and same-source reuse**: the same English word under different
    keys may need different translations (`Weapon.Charge` vs `Store.Charge`),
    so each such row is translated individually — more precise, more tokens.
    Their results are stored in the TM as key-aware entries
    (key + source → translation) and reused only on an EXACT key + source
    match — the same string in the same place, so re-runs cost nothing
    without losing precision. Rows without a sent key (e.g. GUID keys) keep
    the normal TM behavior.
  - *Reference translation* — pick a second .locres/.csv/.po of the SAME game
    (e.g. its Polish or Russian localization). Rows are matched by key and
    the AI gets explicit roles: the ACTIVE file is the **Primary Source**
    (authoritative original), the picked file is a **Reference Translation**
    — a meaning hint that resolves ambiguity (gender, plural, part of
    speech) but never wins over the primary. Mark the reference's quality
    (Official/human, Community, Machine translated, Unknown) so the AI knows
    how much to trust it — a machine-translated reference is treated with
    caution. For csv/po you choose whether the text comes from the Source or
    Target column. Before the run the app checks key matches and shows the
    counts — unmatched rows simply translate from the primary source alone.
    The reference file is never modified. Reuse is reference-aware: two rows
    with the same source AND the same reference are translated once and reused
    (auto-repeat), but if the reference gives two identical sources *different*
    meanings they are translated separately — never blind-copied over each
    other, in either sequential or parallel mode.
- **Multi-translate** panel: pick N target languages, an output folder and
  per-language prompts — each language is translated in its own phase and
  written to `<folder>/<locale>/<filename>` in the tab's format. For
  `.locres` output the file is built from the session's document skeleton —
  byte-correct for the game, no intermediate files.
- **Prompt history** (the clock-with-arrow button in the left bar, or View →
  Prompt history): every distinct prompt the app sent — the instructions, not
  your rows — from AI Translation, Multi-translate and AI QC. Each line is
  named `number_file_date_time_mode`, e.g.
  `1_Game.locres_11/10/2026_17:26_SingleTranslation`; click it to open the
  text in place (several can be open), **Copy** takes the whole prompt, and
  the text can be selected to copy a part. The same prompt for the same file
  in the same mode is stored once. The prompts are separate files under
  `prompts/single`, `prompts/multi` and `prompts/qc` in the profile folder —
  the folder button in the panel header opens it.

## 5. Saving and exporting

- **Save (floppy)** — writes the current format into a timestamped
  `Export_*` subfolder of your **Export folder**. A `.locres` tab writes a
  real locres through the in-process engine: atomic, empty targets keep the
  original text, and the result reports how many translations were applied.
- **Save As (Ctrl+Shift+S)** — behavior is configurable
  (Settings → Files → *Save As behavior*):
  - **Always ask** — system dialog; the picked path is honored exactly.
  - **Default Export folder** — silent save into the Export folder.
  - **Same as Autosave** — silent save into the Autosave folder.
- **Export (Ctrl+E)** — straight to `.locres`. A CSV tab with no base file
  prompts for a sample `.locres` that defines the target format version.
- **Auto-save** — every N minutes (Settings) into `AutoSaved_*` subfolders
  of the **Autosave folder**. Auto-saves are backups: they never clear the
  unsaved-changes dot.
- **If the app crashes**, it first writes the unsaved edits of every open tab
  into a `Recovered_*` subfolder of the Autosave folder, in each tab's own
  format, then closes. On the next start the log says when it happened, where
  the recovered files are and where the report is (see Troubleshooting).

## 6. Game updates — Merge to new version

File → **Merge to new version** carries your existing translations into an
updated game build.

**Pick the old file first** — `.locres`, `.csv` or `.po`. A short dialog then
explains how that particular file will be matched, and you confirm before the
second picker appears. Then pick the new version's `.locres` (what the updated
game ships).

### A translation is carried only where it can be proven to still belong

A key match on its own is not proof. The same key can hold reworded English in
the new build, and carrying the old line then ships text that says the wrong
thing. So the key match is confirmed:

| Old file | How the match is confirmed |
|---|---|
| `.locres` | **SourceStringHash** — a fingerprint of the original English that a translated locres carries but cannot show you. Different fingerprint = the original changed. |
| `.csv` / `.po` **with a translation column** | The **source column** itself, compared to the new version's text. Strongest, because the old wording is on hand. |
| `.csv` / `.po` with only one column | Nothing to confirm with — keys alone. |

Rows that fail are left **untranslated**, so the ordinary *Untranslated* filter
and AI Translate scope reach them and you redo them against the new wording.

That is why a CSV/PO export of your previous translation is the better input:
it holds the original next to the translation, so the check can compare actual
text instead of a fingerprint — and it still works if the old locres came from
a tool that rebuilt its hashes.

### The report, before anything is written

You always see what matched first:

```
Merge — 18 973 of 20 300 rows carried
Matched by key + original-text fingerprint (SourceStringHash).
  From "old.locres" into the structure of "new.locres".
  18 973 of 20 300 rows will get their translation back.
  1 200 were NOT carried: the original text changed in this update.
  800 keys are new in this version.
  500 keys from the old file are gone from the new version and are dropped.

      [Cancel]  [Match by key only]  [Carry 18 973 translations]
```

**Match by key only** is the escape hatch. The strict rule can legitimately
match *nothing* — an old file whose hashes another tool rebuilt looks 100%
changed — and applying that silently would replace a finished translation with
an empty one. When fewer than half the matched keys survive, the dialog says so.

To make key-only the permanent default, use **Tools → Advanced settings**.

### After the merge

The grid shows the **new version's original in Source** and the **carried
translation in Target**, like every other file in the app — so counters,
filters and QC all mean what they normally mean. Rows are tinted:

- 🟩 **green** — a key new in this version,
- 🟪 **violet** — the original changed, so your old translation was not
  carried.

Both are empty, so **AI Translate with scope *Untranslated* covers exactly the
work the merge left**. The Filter panel also grows a *Merge results* section
with a **New keys** chip if you want them apart.

**Save before closing the app.** Until you do, the merge exists only in this
session; a restart re-reads the new version file, which has no translations.

**What about keys the update removed?** They are dropped on purpose — the
updated game does not read them. Your translations are not lost: keep the old
file as an archive (the log reports how many were dropped), and the Translation
Memory holds every translation by source text.

## 7. Import Target locres

File → **Import Target locres**: load a second `.locres` as the translation for
the file already open, matched by (namespace, key). The whole import is one undo
step.

Everything the imported file has for a matching key is written, **including
translations identical to the original** — tag-only strings, numbers, proper
nouns and short words that are the same in both languages are real translations,
and leaving them out made those rows read as untranslated.

The log reports what happened, so an unexpected result is checkable:

```
📥 Import Target: "Game.uk.locres" — 12 900 of 13 476 rows matched by key,
   12 431 translations written.
ℹ️ Import: 469 rows already held that exact translation — nothing to change.
ℹ️ Import: 1 204 translations are identical to the original.
ℹ️ Import: 576 rows here have no key in the imported file.
```

Rows the imported file has no key for are left exactly as they were — never
blanked. An entry that exists there but is empty is not imported either: there
is nothing to import, and overwriting a translation you already have with
nothing would be worse. If not a single key matches, the app says so and changes
nothing — usually it means the two files are from different game versions.

Imported rows are validated immediately, so a translation whose tags no longer
match its source shows up at once instead of waiting for F8.

## 8. Validation

Opening a file runs light validators on every row, and again 300 ms after each
edit:

| Check | Severity | What it catches |
|---|---|---|
| Tags | Error | `<b>`, `[b]`, `[/i]`, `[color=red]`, `[PLAYER_NAME]` dropped, invented, or typo'd. Square brackets count as markup only when they look like **code** — a closing tag, a `=` or `_` inside, or no capital letter. A capitalised word in brackets (`Mysterious Orb [Charged]`, `[NEW]`) is TEXT the translator is meant to translate, and so is a bracketed sentence (`[The page is torn]`). |
| Tokens | Error | `{count}` dropped, duplicated, or with a translated name. |
| Placeholders | Error | `%s`, `%d`, `%1$s`, `%.2f` dropped or invented — the game prints the wrong value or fails the format call. |
| Plurals | Error | `\|plural(one=…, other=…)` dropped, or with fewer forms than the original. More forms is correct work, never flagged. |
| Mixed scripts | Warning | A Latin letter hiding inside a Cyrillic word — identical glyph, broken string. |
| Content | Warning | A phrase translated into digits, or a symbol-only original that changed. |
| Model chatter | Error | `Translation:` or quotes around the whole line that came from the model, not the source. The fix removes them. |
| Line breaks | Warning | Every line break lost, or breaks the source does not have. A different count alone (reflowed prose) is not reported. |
| Other language | Warning | Letters or frequent words of another language left in the text — needs the file's language (from its folder, e.g. `uk`); silent when unknown. Words that are right can go into the leak exceptions. |
| Protected term | Warning | A term from your Protected Terms list is in the source but missing from the translation. |
| Terminology | Warning | One of your `wrong=right` terminology rules matches; the fix applies it. |
| Whitespace | Info | Edge spaces and line breaks that do not match the original **in either direction** — added *or* lost. A lost leading space glues two words together at runtime. |
| Punctuation | Info | The original's ending `. ! ? … : ;` missing or different. |
| Length | Info | The translation is more than 2× the original. |
| Duplicates | Info | The same original translated several ways. The note's popover walks through every translation of that text (◂ ▸) and offers **Use this translation for the same text**: the rows it would change are shown before → after and only the ones you keep are written, as one undo step. Runs on open and F8 only. |

The four **Info** rows are notes, and each can be switched off in **Settings →
Validation**. **Whitespace is off by default**: on real files it fires on hundreds
of rows that are fine. The Error and Warning checks always run. A switch applies at
once to every open file.

**Check Tags** re-runs validation on demand.

**No fix is written before you see it.** The lightbulb in a cell, **Fix** in the
Problems tab and **Fix all** open a review: every change is shown before and after,
with the changed words highlighted and a changed space drawn as `·`. Untick what you
do not want; the rest is applied as one undo step. A fix that would break something
else in its row — a new error or warning — is not offered at all, and the review says
how many were held back. A row you edited after opening the review is skipped, not
overwritten.
The validators and QC use the *same* definitions of a tag, a placeholder and
ending punctuation, so the two never disagree about whether a row is broken.

Problems show as row tints (red = error,
yellow = warning), a left-edge stripe, status-bar counters with filters, and
a Problems tab (double-click jumps to the row). The two count different things:
a status-bar counter counts **rows**, each under its most serious problem, while
the Problems tab lists **every problem** — a row with two warnings is 1 in the
counter and 2 in the tab. Click a counter to show only its rows; right-click it
to clear that status in the selected rows.

**Duplicates are a note, not an error.** When the same Source is translated
several ways, every one of those rows gets an Info entry naming the other
translations and the keys they live at, so you can go and compare them. There
is deliberately **no** one-click "make them all the same": each key is its own
context, and a file built from several source languages differs on purpose — a
bulk button there would erase deliberate work silently. QC reports the same
thing the same way, as a Low note.

## 9. Settings that matter

| Setting | Effect |
|---|---|
| Provider / API key / model / base URL | One unified set for all AI features |
| Batch size, parallel, caching, retry | API throughput/cost tuning |
| Translation memory | Per-language-pair reuse of confirmed translations |
| Protected terms | Glossary the AI must keep untranslated |
| Export folder / Autosave folder | Where manual saves and auto-saves land |
| Save As behavior | Dialog vs silent folder saves |
| Default export format | Ordering of Save As filters |
| Show empty keys (View menu) | Reveal locres entries with empty values |
| Validation notes | Whitespace (off by default), punctuation, length, duplicates — each on or off |
| Interface language | 15 languages — see below |
| Accent colour | The round swatch under Interface Language: presets, hue and saturation sliders or a `#RRGGBB` value. One colour serves both themes — lighter and darker shades are derived from it, and its brightness is adjusted where needed so text stays readable. **Default** returns each theme's own accent |
| MCP server | Let AI chat clients control the app (see §10) |
| Reset cache (Advanced) | Deletes every Translation Memory, the prompt history and the AI QC cache, and returns all settings to defaults — the API key included. It lists what goes, with counts, and asks twice. Your files, review marks, comments, auto-saves and exports are not touched; the interface language and theme stay |

### Interface language

The 文A button in the header bar, left of the sun/moon — or Settings →
Interface Language, which does the same thing. The app ships in 15 languages:
English, Українська, Deutsch, Español, Français, Italiano, Polski, Português
(Brasil), Русский, Türkçe, Tiếng Việt, 简体中文, 繁體中文, 日本語, 한국어. The change
applies immediately — the whole window redraws in the new language, with no restart and nothing lost.

The button is in the header on purpose. If the app opens in a language you cannot
read, hunting for the right row of a settings panel written in that language is
the one thing you cannot do — so the way out is a symbol, in the same place in
every language, and the menu lists each language under its own name.

Some words stay English in every language on purpose: `locres`, `namespace`,
`key`, `Source`, `Target`, `SourceStringHash`, `QC`, `Translation Memory`, `MCP`,
the file formats, and every keyboard shortcut. They name a thing in the file or a
control on screen, so translating them would make the UI, this guide and the
forums stop agreeing. `Localization/GLOSSARY.md` is the full list.

**Adding a language yourself.** Copy `Localization/strings.en.json`, translate
the values, name it `strings.<code>.json`, and drop it back in that folder — the
app picks it up on the next build with no code change. Give it a `_meta.name`
(how the language names itself, for the picker) and `_meta.fontFamily` /
`_meta.fontMono` if it needs a different face. Counted messages use plural keys
(`key.one`, `key.few`, `key.many`, `key.other`) — provide the forms your language
actually uses; `dotnet test` says which are missing, and checks placeholders,
shortcuts and protected terms while it is there.

## 10. QC — Quality Control

The **QC** button in the left bar reviews an **already-translated** file
instead of translating it again. It is an **AI review**: your configured model
reads each row and either confirms it or proposes a corrected line. QC has no
rule checks of its own — placeholders, tags, line breaks, leftover source
language, model chatter, protected terms and your terminology are checked
**live by the validator** while you edit (section 8), so they are
already on screen before you open this panel.

**How it works**

1. Open the file you want to check — the **active tab is the file under
   review**, and the panel names it. Then tell QC which files play which role:

   | Role | Where it comes from | Required |
   |---|---|---|
   | **The translation being checked** | the active file's Source *or* Target | yes |
   | **The original** it was translated from | the active file's Source, or a second file matched by key | yes |
   | **An extra language** | a third file | no — more context for the model |

   A `.locres` holds **one** language, so opening a translated `.locres` puts
   its text in **Source** and leaves Target empty. That is the normal case:
   pick *"The translation to check is in: Source"*, add the original-language
   file, and QC writes accepted fixes into the empty Target column. Saving
   then produces the corrected file, because an empty Target keeps the base
   value by contract. CSV and PO may already carry both columns — the panel
   detects that and preselects Target.

   After you choose the original file, the panel reports how many keys matched
   (`12 480 of 12 500 rows found`) so two files from different game versions
   are obvious before the run rather than after it.
2. Choose the scope (all rows or only checked rows) and the language pair.
3. **Rows to review** — every row in scope is sent, minus what these switches
   keep out. Each one shows how many rows it skips (`−312`), and the line under
   them says how many rows and requests the run will cost before you spend
   anything:

   | Switch | Default | Keeps out |
   |---|---|---|
   | Skip rows without text | on | rows with no translation, or no original to compare with |
   | Skip rows carried over automatically | on | rows that are only tags, numbers or placeholders — copied as is, nothing to translate |
   | Skip rows identical to the original | on | names, codes, untranslated copies |
   | Skip approved rows | on | rows a reviewer already marked Approved |
   | Only rows the validator flagged | off | everything the validator found nothing on — the cheapest review |

   The switches are saved with the app settings.
4. Press **Run AI review**. Progress, cache hits and rejected fixes are shown
   as it goes; **Stop** keeps everything already reviewed.
5. Rows with a suggestion get a **QC badge** next to the comment icon. Click it
   to see the full before/after with the changed words highlighted, then
   choose **Fix** (apply) or **Keep** (dismiss — it won't be proposed again).
6. Bulk: **Fix all** / **Fix selected** appear in the filter bar *while the
   QC panel is open*. They first open the same review window as the
   validator's fixes — every change before → after, untick what you do not
   want — and the rest is ONE undo step (Ctrl+Z).
7. **CSV report** exports only the rows that have suggestions.
8. The **QC** tab at the bottom — a peer of Problems and Logs, visible only
   while a review has open suggestions — lists them **grouped by category**
   and collapsed. Click a header to expand it; **Fix N** reviews and applies a
   whole category, **Fix all** in the toolbar every suggestion that has a
   replacement. Hovering a row previews the replacement (`− old` / `+ new`);
   double-click jumps to it in the grid.

   The list behaves like the Logs list: click to select, Ctrl+click / Shift+click
   for several, **Ctrl+A** to select all and **Ctrl+C** (or right-click → Copy)
   to copy them as tab-separated text — key, severity, category, description,
   proposed replacement. It also follows the grid: selecting a row that has a
   suggestion selects its entry here, and the Problems tab does the same for
   validator findings.

**What the model does** — it never re-translates; for each row it answers
*ok* or *fix* with a corrected line, in strict JSON (so an explanation can
never end up inside a translation). It receives the original, the current
translation and (if you loaded one) the same line in a third language. Your
QC guidance text is passed as a description of the *expected* result, never
as an instruction to translate. Every proposed fix is then re-validated
locally and **rejected** if it would break a placeholder, tag, line break,
the source's trailing ellipsis or edge spacing — the counters show how many
were rejected.

**Cost control** — verdicts are cached per language pair, keyed by the model,
prompt version, your guidance and the row's own texts; re-running QC after
fixing a few rows only pays for what actually changed. Stop is safe: a
cancelled run keeps every verdict it already paid for, and the cache is
flushed as the run goes, so closing the app mid-review loses nothing either.
Batch size and parallelism live in the QC panel's own **QC settings…**
dialog, separate from the translation panel's speed settings.

**Your terminology and exceptions** — the same dialog holds two lists the
**validator** uses: **Terminology fixes** (`wrong=correct` lines, e.g.
`пролом=пробоїна`) — the wrong term is flagged and the replacement offered
as a reviewed fix — and **Leak exceptions**, words the leftover-language
check keeps flagging that are actually fine (character names, loan words).
Saving the dialog re-checks the open files.

Suggestions live in a sidecar next to the file (`<file>.qc.json`), so an
unfinished review survives closing the app or rebooting. Nothing is written
into your translations until you press Fix; if a row changed after the
review, its outdated suggestion is marked and cannot be applied. If a fix
only covers part of what was found, the badge stays with what is left.
**Clear** discards the whole review for the file (it asks first) — export the
CSV report first if you still need the suggestions.

**Getting the corrected file out.** When the translation was reviewed in
Source, the accepted fixes sit in Target — two columns for one language. Saving
as `.locres` already resolves that (an empty target keeps the base value). For
CSV or PO use **File → Save As one language**, which writes a single column
using the same rule: Target where a fix was accepted, Source everywhere else.
The ordinary **Save As** still writes both columns, unchanged — pick that one
when you are handing the file to a translator.

## 11. MCP server — drive the app from an AI chat

Enable **Settings → MCP Server** and any MCP-capable AI client (Claude
Desktop, Claude Code, ChatGPT Codex, Antigravity, …) can control the RUNNING
app: open files, read rows, write translations (tag-validated, one undo step
per batch), start/stop the built-in AI translation, and save. If the client
has a subscription-backed model, it can translate rows ITSELF via
`get_rows` → `set_translations` — no API key spent.

Everything is local: the bridge is a named pipe on this PC, visible only to
your Windows account. The app window must be open; nothing ever touches the
disk until `save_current` (which writes to a timestamped Export folder, never
over your original file).

Point the client at the app executable with the `--mcp` argument:

**Claude Desktop — one click**: press **Create Claude Desktop extension
(.mcpb)** in Settings → MCP Server. It generates
`SmartUnrealLocalization.mcpb` next to the app — double-click it (or drag
into Claude Desktop → Settings → Extensions) and you're connected, no config
editing. Re-create the file if you move the app. Manual alternative —
`claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "smart-unreal-localization": {
      "command": "C:\\Path\\To\\SmartUnrealLocalization.exe",
      "args": ["--mcp"]
    }
  }
}
```

**Claude Code** — one command:

```
claude mcp add smart-unreal-localization -- "C:\Path\To\SmartUnrealLocalization.exe" --mcp
```

**Codex CLI** — `~/.codex/config.toml`:

```toml
[mcp_servers.smart_unreal_localization]
command = "C:\\Path\\To\\SmartUnrealLocalization.exe"
args = ["--mcp"]
```

**Antigravity / other MCP clients** — add a local (stdio) MCP server with the
same command + `--mcp` argument in the client's MCP settings.

Then just talk to the model: *"Open D:\Games\uk\Game.locres and translate the
untranslated rows to Ukrainian"*, *"find every row with the word 'charge'"*,
*"fix all validation problems"*, *"enable parallel requests and set batch
size to 50"*. The model has 17 tools:

- **Rows**: `get_rows` (paged read/search — case, whole-word, regex; rows
  include review status and comments), `set_translations` (tag-validated
  writes, one undo step), `find_replace` (preview → apply, Target-only, tag
  guard), `get_translation_rules`.
- **Review**: `set_review` (approved/rejected/none) and `set_comments` — the
  model can verify rows and mark or annotate them like a human reviewer.
- **App control**: `get_status` (includes live AI-run progress with ETA),
  `open_file`, `run_ai_translation` / `stop_ai_translation` (the built-in
  pipeline, your API key), `save_current`, `undo` / `redo`.
- **Settings**: `get_settings` / `set_settings` — every option with its type,
  allowed values and exact UI location. The API key is the one deliberate
  exception: the model can only tell you WHERE to paste it (Settings → AI
  Provider → API Key), never read or set it.
- **Diagnostics**: `get_logs` (the Logs panel stream), `get_problems`
  (validator findings per row), `run_validation` (fresh re-check) — so the
  model can fix flagged rows itself, often better than the built-in auto-fix.
- **Quality Control**: `get_qc_rows`, `submit_qc_verdicts`,
  `get_qc_proposals`, `apply_qc_fixes`, `keep_qc_proposals`,
  `export_qc_report`. The assistant can be the reviewer itself: fetch the rows
  the QC panel's switches would send, judge them, and submit verdicts — billed
  to your chat subscription instead of your API key. Its fixes pass the same
  local check as the built-in review, and you still approve each one with
  Fix / Keep.
- **Guidance**: `get_app_map` — a built-in map of the whole interface, so the
  assistant doubles as live documentation ("what does Expand rows do?",
  "where is Merge to new version?").

**Translation Memory policy (safety):** MCP **never writes** to the TM —
neither the model's own translations nor MCP-started AI runs. Unreviewed
machine output must not silently contaminate the memory that auto-fills
future runs; promote rows you've checked via the row context menu → *Send to
Translation Memory*. TM **reading** is your explicit choice: when a TM
already exists for the language pair, the assistant will ask whether to use
it or translate from scratch.

Everything it does is visible live in the app and undoable with Ctrl+Z.
