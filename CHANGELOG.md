# Changelog

All notable changes to Smart Unreal Localization. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] — 2026-10-22

First public release.

### Files
- Open, edit and save Unreal Engine `.locres` files of every version (v0 Legacy
  to v3 CityHash64), byte-exact with the engine's own output, through the
  built-in LocresIO engine — no external tools, no downloads.
- Gettext `.po` and `.csv` (2, 3 and 4 columns) import and export; Save As one
  language for two-column files.
- Several files open at once as tabs; drag and drop onto the window or the exe.
- Atomic saves to a timestamped Export folder or an exact path; an empty
  target keeps the base value, so untranslated rows never blank a shipped string.
- Merge to new version: carry translations onto a new game build — new keys
  and keys whose source text changed are highlighted for review.
- Import Target from another file by key.

### Translation
- AI batch translation with Gemini, OpenAI, Anthropic or any
  OpenAI-compatible endpoint: batching, parallel requests, retry, prompt
  caching, tag and placeholder protection.
- Translation memory per language pair, protected-terms glossary, reviewer
  comments sent to the model as per-row instructions, reference translation
  from a second file.
- Multi-language pipeline: one source file into many languages in one run,
  each written to its own locale folder.

### Review and quality
- Live validator on every edit: tags, tokens, printf placeholders, plurals,
  model chatter, mixed alphabets, lost line breaks, leftover source language,
  protected terms and terminology rules. Optional notes (edge whitespace,
  ending punctuation, length ratio, duplicate sources) are switches in
  Settings → Validation.
- No fix is written unseen: every fix — one or Fix all — opens a review with
  each change before → after, changed words highlighted and spaces made visible.
- QC: an AI review of an already-translated file. Every row in scope is
  checked except what the Rows to review switches keep out, with the cost
  shown before anything is sent; suggestions are accepted or dismissed per
  row, in bulk or by category, and survive a restart.
- Approve / reject rows, free-text comments, filters, find and replace with
  highlighting, undo / redo including batch operations.

### Integration
- MCP server: AI chat clients (Claude Desktop, Claude Code, Codex and others)
  can drive the running app — open files, read and write rows (tag-validated,
  undoable), run translation and review. It never writes to the Translation
  Memory and never exposes the API key.

### Interface
- 15 interface languages, switchable live; light and dark themes.
- File → Recent files (the last ten); Tools → Open Translation Memory folder;
  F8 validation under Tools.
- Part of a Key, Source or Target cell can be selected by dragging with the mouse and copied with Ctrl+C; a double-click selects a word in Source and the whole key in Key.
- Accent colour picker: one colour for both themes, with the lighter and darker
  shades derived from it.
- Help → Check for updates: looks at the repository's latest release on request;
  nothing runs in the background and nothing is downloaded.
- A jump to a row that a filter hides asks before showing all rows.
- The "same text, different translations" note walks through the translations
  and can apply one of them to every row with that text, after a review.
- Prompt history: every distinct prompt sent by AI translation,
  multi-language translation and AI QC, kept as separate files and shown in a
  side panel — open several at once, copy one in a click or select a part.
- Settings → Advanced → Reset cache: deletes the Translation Memory, the
  prompt history and the AI QC cache and returns the settings to defaults,
  after listing what goes and asking twice.
- Help → Documentation opens the user guide; Help → Licenses shows the
  program's license, the third-party components and their license texts.
- Crash and freeze reports; on a crash, unsaved edits are rescued to the
  Autosave folder and announced on the next start.
