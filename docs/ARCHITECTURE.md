# Architecture

How Smart Unreal Localization is put together, the invariants that keep it
correct, and the platform pitfalls that cost real debugging time. Read this
before changing code; [CONTRIBUTING.md](../CONTRIBUTING.md) covers the workflow.

## Stack

| Piece | Choice | Notes |
|---|---|---|
| UI | **Avalonia UI 12.0.2** | A preview line with API quirks — see [Pitfalls](#avalonia-1202-pitfalls) |
| Runtime | **.NET 8.0**, `WinExe`, Windows only | Release is a self-contained single-file exe |
| MVVM | CommunityToolkit.Mvvm | `[ObservableProperty]`, `[RelayCommand]` |
| Bindings | Compiled by default | `AvaloniaUseCompiledBindingsByDefault=true`; templates need `x:DataType` |
| JSON | Newtonsoft.Json | Settings, sidecars, MCP payloads |
| CSV | CsvHelper | Import only; export is written by hand |
| `.locres` | **LocresIO** (`LocresIO/`, namespace `Locres.IO`) | First-party, clean-room, zero dependencies, MIT |
| MCP | ModelContextProtocol C# SDK | Only in `--mcp` mode (stdio proxy) |

## Repository layout

```
LocresIO/                    Standalone .locres library (zero deps; own README, FORMAT.md spec, MIT)
LocresIO.Tests/              xUnit tests for LocresIO
SmartUnrealLocalization.Tests/  xUnit tests for the app, incl. real-window headless tests (Ui/)
App.axaml(.cs), Program.cs   Startup: --version, --mcp, crash reporter, theme, first window
Views/                       MainWindow (inline header, grid, panels), AboutWindow
ViewModels/                  MainWindowViewModel split into partials per area
                             (FileOps, Tabs, Filters, FindBar, Problems, Qc, QcTab,
                             Review, Clipboard, Helpers, CrashSupport), OpenFileSession,
                             SaveCompletion
Models/                      TranslationRow, AppConfig (singleton), Problem/Fix, QcProposal, …
Services/                    File formats (LocresFileService, PoFileService, CsvFileService),
                             TranslatorService, TranslationBatchRunner, TranslationMemoryService,
                             MultiTranslateJob, MergeMatcher, ReviewService, CommentService,
                             ValidationService, FixPlanner, UndoRedoManager, Debouncer,
                             StatusArbiter, ToastService, ThemeManager, CrashReporter
Services/Validators/         One class per live check (IValidator / IHeavyValidator)
Services/Qc/                 AI review: QcAiScope (which rows), QcAiRunner (requests),
                             QcCache (verdicts), QcService (per-file store), QcEngine (check
                             library the validators borrow), QcDiff, QcGrouping
Services/Mcp/                MCP bridge: McpStdioProxy (stdio, --mcp) → McpPipe (named pipe)
                             → McpGuiServer (runs on the UI thread of the open window);
                             McpSettings (settings registry), McpAppMap (feature map), McpBundle
Controls/                    Panels and reusable controls; DialogTitleBar for every child window
Utils/                       Static helpers only (TextUtils, Glyphs, ListReveal, LocRefresh, …)
Themes/                      Tokens.Dark.axaml, Tokens.Light.axaml, AppTheme.axaml
Localization/                strings.<code>.json (15 languages), Loc, Plural, GLOSSARY.md
Assets/                      app.ico (regenerate with tools/make-icon.ps1)
tools/                       publish-release.ps1, make-icon.ps1
licenses/                    Full texts of third-party licenses (shipped in the release zip)
docs/                        User guide (en, uk), FAQ, troubleshooting, this file
```

## Data and persistence

- Settings: `%AppData%\SmartUnrealLocalization\config.json` (`AppConfig`). The API
  key is stored DPAPI-encrypted (`ApiKeyProtected`); the plaintext `ApiKey` exists
  only at run time and is never serialized.
- Translation memory: `%AppData%\…\translation_memory_{from}_{to}.json`, one per pair.
- Review status: `%AppData%\…\reviews\` sidecars.
- Accent colour: `AppConfig.AccentColor` (`#RRGGBB` or empty). `Services/AccentPalette`
  turns one pick into per-theme shades (lightness fitted so white-on-accent and
  accent-on-background stay readable); `ThemeManager` overrides the accent tokens on
  the application resources and exposes `ThemeManager.Accent` for code-painted colours.
- Grid cell text: `Controls/CellTextSelection` selects part of a Key / Source / Target
  cell with the mouse (drag, Ctrl+C; double-click = word in Source, whole key in Key;
  Target keeps double-click for its editor). It does not take focus and starts only
  after a drag, so a click still focuses the row. `SelectableTextBlock` is not used:
  it takes focus and handles double-click itself.
- Update check: `Services/UpdateChecker` — on request only (Help menu), reads the
  repository's latest release, downloads nothing, opens only that repository's pages.
- Prompt history: `%AppData%\…\prompts\{single,multi,qc}\`, one JSON file per distinct
  prompt (`Services/PromptHistory`). Recorded by the panels when a run starts, never by
  the services, so a test of a service cannot write into a profile. Settings → Advanced
  → Reset cache (`Services/ProfileReset`) deletes it together with the Translation
  Memory and the QC cache and resets `AppConfig` in place (same singleton instance).
- Next to the opened file: `<file>.comments.json` (reviewer comments) and
  `<file>.qc.json` (pending QC suggestions). Sidecars are never written into exports.
- Crash and freeze reports: `%AppData%\…\crash-reports\`. Rescued edits after a
  crash: `Recovered_<time>` in the Autosave folder.
- Defaults for Export and AutoSaved folders sit next to the exe
  (`AppContext.BaseDirectory` — never `Assembly.Location`, which is empty in the
  single-file release).

## The .locres pipeline (invariants)

- **Open**: `LocresFileService.Open` → rows (with a real `Namespace` column) plus a
  retained `LocresDocument` skeleton per tab (`OpenFileSession.Document`, VM
  `CurrentLocresDocument`). Every save writes through that skeleton — never re-read
  or copy the original file for saving.
- **Save**: `doc.SaveWith(overrides, path)`. The document is immutable; the snapshot
  (document, overrides, file name, edit revision) is captured atomically on the UI
  thread (`SaveEditedData`). An empty target keeps the base value by contract, so
  an untranslated row can never blank a shipped string.
- `SourceStringHash` is pass-through — never recompute it.
- Sidecar migration: old review/comment sidecars used flattened keys (`Ns=""`,
  `Key="ns/key"`); `ReviewService`/`CommentService.Hydrate` keep lazy fallbacks.
- LocresIO is clean-room: `LocresIO/FORMAT.md` is the only reference. No code from
  Epic or from the UnrealLocres tool, and no external executable, ever.

## Validation and QC

- **One rule set: the validator.** Light validators run ~300 ms after each edit;
  heavy ones (duplicates) on open and on demand. Errors and warnings always run; the
  four Info notes are switches (Settings → Validation, `ValidationService.OptionalChecks`).
- **No fix is written unseen.** `FixPlanner` plans fixes on scratch rows and refuses a
  plan that adds an error or warning; `FixReviewDialog` shows each change
  before → after (`DiffText`, spaces as `·`); only the kept ones are applied, as one
  undo step. QC bulk fixes go through the same review (`ReviewAndApplyQcAsync`).
- **QC is an AI review only.** `QcAiScope.Build` sends every row in scope minus the
  panel's switches and counts each skip; `QcAiRunner` re-validates every model fix
  (`RejectFix`); verdicts are cached per language pair (`QcCache`); suggestions live
  in `<file>.qc.json` (`QcService`). A new rule goes into `Services/Validators/`;
  `QcEngine` is only the check library the validators borrow.

## MCP bridge

`SmartUnrealLocalization.exe --mcp` runs without Avalonia: a stdio server (official
SDK) that forwards each tool call over a named pipe to `McpGuiServer`, which runs it
on the UI thread of the open window. Enabled by `AppConfig.McpServerEnabled`; stdout
in `--mcp` mode is protocol-only.

Invariants that must not change without an explicit decision:
- MCP never writes to the Translation Memory (reading it is a per-run user choice).
- The API key is never readable or settable through MCP (location guidance only).
- The MCP enable switch is read-only through MCP.
- Row writes stay tag-validated and undoable.

When a user-facing feature or setting changes, update in the same change:
`McpSettings` (every `AppConfig` option: name, type, allowed values, UI path,
live-apply flag), `McpAppMap` (the feature map models read), the `McpGuiServer` op
and its `McpStdioProxy` tool definition (descriptions are the model's only manual),
and the tool list in the user guide.

## Interface language

- 15 languages in `Localization/strings.<code>.json`. Adding one is a drop-in: the
  csproj embeds `strings.*.json` by wildcard, `Loc` discovers it, the file carries its
  own name and font stacks under `_meta.*`.
- Counted messages use CLDR plural keys (`Localization/Plural.cs`). Identifiers that
  stay English are listed in `Localization/GLOSSARY.md`.
- Commands, menus, buttons and panel titles are translated. `strings.uk.json` is the
  reference: where it translates a string, every language must (a test enforces it).
- Switching is live: `Loc.Load` raises `PropertyChanged(null)` and every
  `{Binding [key], Source=Loc.Instance}` re-reads. Text that code sets (ComboBox
  items, labels written in code) must subscribe with `control.RefreshOnLanguageChange(...)`.
- Never compare a localized string as data — index the list instead (the
  "Select Language" stub is at index 0).

## Theme system

- `Themes/Tokens.{Dark,Light}.axaml`: `SolidColorBrush` resources keyed by semantic
  name (`WindowBackground`, `AccentColor`, `KeyColor`, …).
- `Themes/AppTheme.axaml`: every style uses `{DynamicResource <name>}`. No hex
  colors in views or styles.
- `ThemeManager.Apply(bool dark)` swaps the token dictionary and syncs
  `Application.RequestedThemeVariant` so Fluent popups match. `App.axaml.cs` applies
  the saved theme before the first window.
- Row error/warning tints are semi-transparent overlays (`#33FF4444`, `#33FFCC00`)
  so one value works on both themes.
- Flat look: zero `CornerRadius`. Monochrome marks only (`Utils/Glyphs.cs`:
  ✓ ✕ ✗ ⚠ ℹ ⓧ ⓘ), never emoji; `Glyphs.MonochromeFallback` routes them to Segoe UI Symbol.

## Window chrome

Every window uses `WindowDecorations="None"`: no native title bar, buttons, border
or resize. Each window draws its own.

- MainWindow's header: logo, integrated menu, drag area (the `*` column, shared with
  the project pill, which trims and then hides), interface-language button, theme
  toggle, min/max/close. The language button duplicates Settings on purpose — a user
  who opened the app in a script they cannot read must not have to find Settings.
- Child dialogs use `Controls/DialogTitleBar`.
- Drag handlers go on the outer `Border` (it has a Background), not on a `Grid`
  without one. Filter out `Button`/`MenuItem`/`Menu` sources via
  `e.Source.GetSelfAndVisualAncestors()`.
- Maximized windows bleed off-screen slightly — `RootBorder.Padding=8` compensates
  (`MainWindow.OnWindowPropertyChanged`).
- No maximize-on-double-click unless a maximize button is visible.

## Conventions

- Event handlers `On<Verb>`; private fields `_camelCase`; VM commands
  `[RelayCommand] private void Verb()` → `VerbCommand`.
- **Selection has one answer**: `MainWindowViewModel.SelectedRows` — checked rows in
  the whole file. A filter changes what is visible, never what is selected. Never
  re-derive it and never snapshot it into a panel. A scope that resolves to nothing
  refuses; it never widens to "all rows".
- The VM exposes filtered `Rows` (bound to the grid) and unfiltered `AllRows`;
  operations that must ignore filters use `AllRows`.
- **Coalescing work = `Services/Debouncer`.** Never `timer.Tick += Handler` inside the
  method that schedules: stopping a timer unsubscribes nothing.
- **Status bar = `StatusText` + `Services/StatusArbiter`.** Info lines logged in the
  same dispatcher turn as an explicit line stay in the log; warnings and errors always
  show; otherwise the newest line (by `AddLog` call) wins.
- **Scrolling a ListBox = `Utils/ListReveal`** (layout first); never `ScrollIntoView`
  directly, and never in the same turn as a collection change.
- **Clipboard = `Utils/ClipboardSafe`.** Windows' clipboard is a global lock; another program holding it makes Avalonia throw inside an `async void` handler, which ends the process. `ClipboardSafetyTests` fails on any direct call.
- **Whole-file writes = `Utils/AtomicFile`** (temp sibling, then replace): opening the target directly truncates it, so a failed save would leave a cut-off file. `.locres` goes through LocresIO's own atomic save.
- **No user-visible literal in code.** Log lines, status text, control text, dialog and picker titles, toasts, and every validator's title, explanation and fix label come from the language files (`CodeLiteralTests`). Problems are worded when the row is validated, so a language switch re-validates the open files.
- Tests that assert English text load English themselves (`Loc.Instance.Load("en")`) — never rely on another test having loaded it.
- New utilities go in `Utils/` as static classes.

## Avalonia 12.0.2 pitfalls

Measured on 12.0.2; where the official docs disagree, this table wins.

| Symptom | Cause | Fix |
|---|---|---|
| `IObservable<T>` has no `Subscribe` (lambda) | The extension is not in scope | `PropertyChanged += handler` with `AvaloniaPropertyChangedEventArgs` |
| `IClipboard` has no `SetTextAsync` | Missing using | `using Avalonia.Input.Platform;` |
| `Control` has no `GetSelfAndVisualAncestors` | Missing using | `using Avalonia.VisualTree;` |
| `ExtendClientAreaChromeHints` unresolved | Does not exist in 12.0.2 | Use `WindowDecorations="None"` |
| `SystemDecorations` obsolete | Renamed | `WindowDecorations` |
| `RelativeSource` binding fails with compiled bindings | `x:DataType` required | `x:Name="Root"` + `{Binding #Root.Property}` (see `DialogTitleBar`) |
| A mark glyph flips between colour emoji and monochrome | Font fallback depends on what the process loaded first; VS15 does not help | Vector `Path` icons, or `Utils/Glyphs` with its monochrome fallback; a new mark outside `Glyphs.MarkRanges` fails `EmojiPolicyTests` |
| A glyph with `Stretch="Uniform"` looks fat | Stretch scales geometry, not `StrokeThickness` | Draw in the 24-unit design space, `Width/Height="24"` + `RenderTransform="scale(0.583)"` (14 px) or `scale(0.5)` with `Margin="-6"` (12 px) |
| `StrokeLineJoin` unresolved on `Path` | WPF name | `StrokeJoin` (`StrokeLineCap` keeps its name) |
| The window's Title and icon appear over the app's own header, covering the logo and menu | `ExtendClientAreaToDecorationsHint` puts the client area under the title bar, and the platform still draws `Title`/`Icon` there | Keep `Title=""` and no `Icon` on MainWindow; the taskbar icon comes from `<ApplicationIcon>` in the csproj. `MainWindowUiTests` |
| A language switch leaves one string unchanged | `PropertyChanged("Item[]")` is WPF's name; Avalonia's is `"Item"` | Raise `PropertyChanged(null)`; code-set text needs `RefreshOnLanguageChange` |
| `Invalid Arrange rectangle` from `VirtualizingStackPanel` under `ScrollIntoView` | Items inserted above the realized range leave `StartU` NaN until the next measure, which `ScrollIntoView` skips — and the flag stays stuck | `Utils/ListReveal` (runs `UpdateLayout()` first); post reveals, never in the same turn as a collection change. `ListRevealGuardTests` |
| Extra spaces in a label built from `<Run>`s | A line break between inline Runs is whitespace | Keep one label's Runs on one line; `XamlRunSpacingTests` |
| A custom template part gets visuals you never set | Its `x:Name` matches a Fluent template part (`CheckGlyph`, …) whose state styles then apply | Give custom parts unique names (`GridMarkGlyph`) |

Other platform notes:
- Window `KeyBinding`s run before tunnel handlers; a tunnel `KeyDown` handler runs
  before a `TextBox` handles Enter. Undo/redo inside text fields is handled in the
  window's tunnel handler so Ctrl+Z never reaches another row.
- `Dispatcher.Post` defaults to `Default` priority, below Render and Loaded;
  `Background` is lower still — use it for work that must wait for layout.
- Editing `.axaml` with PowerShell `Set-Content` mangles Unicode (`→`, `☀`, `▶`);
  write files as UTF-8.

## Diagnostics

- `Services/CrashReporter`: on an unhandled exception it rescues every tab's unsaved
  edits, writes a report (whole exception chain, app state, recent log lines; never
  the API key) and lets the process end. A watchdog writes a freeze report when the UI
  thread does not respond for 10 s (at most three per session). The next start
  announces each report once.
- `--version` prints the version and exits before any window, config or pipe exists.
