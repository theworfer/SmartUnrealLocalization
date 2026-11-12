# Contributing

Thanks for looking at the code. This file is the workflow; the design, invariants
and platform pitfalls are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Build, test, run

Requirements: Windows 10/11, .NET 8 SDK.

```powershell
dotnet build       # whole solution — must be 0 warnings, 0 errors
dotnet test        # LocresIO.Tests + SmartUnrealLocalization.Tests — must stay green
dotnet run         # launches the app
```

The app tests include real-window tests (`SmartUnrealLocalization.Tests/Ui`, built
on `Avalonia.Headless`). They run in a sandboxed profile with auto-save and the MCP
server off, so they never touch your settings, your Translation Memory or a running
copy of the app.

## Release

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-release.ps1
```

Runs every test, publishes a self-contained single-file `win-x64` exe
(`Properties/PublishProfiles/win-x64.pubxml`), checks that it starts
(`--version`), and writes `artifacts/SmartUnrealLocalization-<version>-win-x64.zip`
with its SHA-256. The zip holds the exe, `LICENSE`, `THIRD-PARTY-NOTICES.txt`,
`licenses/`, `README.md`, `CHANGELOG.md` and the user docs (Help → Documentation
opens the guide from that `docs/` folder).

The version lives in one place: `<Version>` in `SmartUnrealLocalization.csproj`.
About, crash reports, `--version` and the MCP bundle read it. Add the release to
`CHANGELOG.md` and tag it `v<version>`.

The build is not trimmed (Avalonia compiled XAML, Newtonsoft.Json and the MCP SDK
use reflection) and not code-signed, so Windows SmartScreen warns on first start.

## Rules for changes

- **Zero warnings.** A warning is a build failure.
- **One logical change per commit**, imperative English subject under ~72
  characters ("Add filter dropdown", "Fix title bar overlap"). History stays linear:
  no amend, no force-push, no rebase of published commits.
- **Tests with the change.** A defect fix comes with the check that fails without it.
- **Docs with the change.** When behaviour or usage changes, update
  `docs/USER-GUIDE.md`, `docs/USER-GUIDE.uk.md`, `docs/FAQ.md`,
  `docs/TROUBLESHOOTING.md` and `README.md` as needed. Docs describe behaviour, not
  history — bug fixes and refactors are not documented there.
- **MCP stays in sync.** A user-facing feature or setting updates `McpSettings`,
  `McpAppMap` and the tool definitions in the same change (see ARCHITECTURE).
- **All 15 interface languages.** A new user-visible string gets a key in every
  `Localization/strings.*.json`; `LocalizationTests` checks parity, placeholders,
  plurals and terminology.
- **New dependencies need a reason.** LocresIO stays at zero dependencies — it ships
  on its own. A new NuGet package also goes into `THIRD-PARTY-NOTICES.txt` (and its
  license text into `licenses/` when it is not MIT).

## Things not to do

- Hardcode colours in views or styles — use `{DynamicResource <token>}`.
- Add `CornerRadius`, emoji, or a mark outside `Utils/Glyphs`.
- Put `PointerPressed` drag handlers on a `Grid` without a background.
- Scroll a ListBox directly — use `Utils/ListReveal`.
- Re-derive or snapshot the selection — use `MainWindowViewModel.SelectedRows`.
- Hand-roll a debounce with `Tick +=` — use `Services/Debouncer`.
- Edit `.axaml` with PowerShell `Set-Content` (it mangles Unicode).
- Copy code from Unreal Engine or the UnrealLocres tool into LocresIO, or add any
  external executable to the `.locres` pipeline.
- Touch the clipboard directly — use `Utils/ClipboardSafe`.
- Leave an `async void` handler without a `try` — end it with
  `catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(Handler)); }`; an
  exception that escapes one ends the process.
- Paint with a literal accent colour — read `Services/ThemeManager.Accent`.
- Clear the grid's filters without asking — a jump to a hidden row goes through
  `RevealRowAsync`, which asks first.
- Write a user's file with `File.WriteAllText` / `new StreamWriter(path)` — use `Utils/AtomicFile`.
- Put an English sentence in code — every text the user reads has a key in all 15 languages.
- Use `Assembly.Location` for paths — it is empty in the single-file release; use
  `AppContext.BaseDirectory`.

## Test suites

| Suite | What it guards |
|---|---|
| `LocresIO.Tests` | `.locres` v0–v3 read, write, merge and diff; UE hashes; FString |
| `ValidatorTests`, `BorrowedValidatorTests`, `DuplicateSourceValidatorTests` | Every live check, including hostile input and fix safety |
| `RuleAgreementTests` | The validator flags exactly the broken rows; the AI-fix guard and the validator agree; shared placeholder definitions |
| `FixReviewTests` (real window) | No fix is written unseen; only kept fixes are written; one undo reverts them |
| `QcAiScopeTests`, `QcPanelUiTests`, `QcAiLayerTests`, `QcServiceTests`, `QcCheckLibraryTests`, `QcDiffTests`, `QcGroupingTests`, `QcPropertyTests`, `QcRunPinningTests` | QC: which rows are sent, the panel, prompts and verdict cache, the suggestion store, the check library, diffs, grouping, file pinning |
| `MainWindowUiTests`, `EditorKeysUiTests` (real window) | The 2026-09 list crash, the editor's double-click, keys, layout at 900 px, saves during edits, tab close |
| `RevealAndDuplicatesUiTests`, `AccentAndUpdateTests`, `AsyncVoidGuardTests` | A jump to a hidden row asks first; walking and unifying the translations of one text; accent shades readable on both themes; the update check's answers; every `async void` handler has a catch |
| `MainMenuUiTests`, `HelpMenuUiTests`, `ColumnMenuUiTests`, `PromptHistoryTests` (real window) | No placeholder menu items, recent files; docs and licenses ship with every build; a copied column pastes back into the same rows; prompt history storage and panel; Reset cache asks twice and deletes only what it lists |
| `SaveCompletionTests`, `SidecarRaceTests`, `TabCloseTests`, `FileIdentityResetTests` | Saves, sidecars and tabs never cross files or lose edits |
| `CrashReporterTests` | Report content, one announcement per report, no API key |
| `LocalizationTests`, `LanguageSwitchTests`, `EmojiPolicyTests`, `XamlLiteralTests`, `XamlRunSpacingTests`, `ThemeContrastTests` | All 15 languages, live switching, monochrome marks, no literal UI text, label spacing, contrast |
| `SelectionScopeTests`, `ListRevealGuardTests`, `TimerSubscriptionTests`, `DebouncerTests`, `StatusArbiterTests` | The conventions above, as source checks and behaviour |
| `FindReplaceTests`, `FindPerformanceTests`, `FoundRowsTests` | Search and replace correctness and limits |
| `MergeMatcherTests`, `MergeRoundTripTests`, `MergeScaleTests`, `ImportTargetTests` | Merge to new version and import |
| `CsvFileServiceTests`, `PoFileServiceTests`, `LocresFileServiceTests` | File formats |
| `TmReuseTests`, `TranslationMemoryServiceTests`, `TranslatorServiceTests` | Translation memory and provider routing |
| `McpSettingsTests`, `McpAppMapTests`, `McpPipeTests` | Settings registry completeness, the API-key invariant, the feature map |
| `UndoRedoManagerTests`, `ReviewServiceTests`, `CommentServiceTests` | Undo, review and comment storage |

`McpEndToEndTests` starts the built exe as `--mcp`, speaks JSON-RPC to it and
drives a live headless window through the named pipe; `OpenValidationUiTests`
checks that opening a file reports its problems at once.

Not covered automatically: most dialogs, drag and drop, badge rendering and the
window's caption buttons. Check those by hand.
