# Troubleshooting

**"Cannot open X: … not a .locres file?" / format error with an offset**
The file is not a valid `.locres` (or is truncated). The message includes
the byte offset and parse context. If the file comes straight from a game
and another tool reads it fine, please report it with the file attached —
the parser is strict by design and we treat any real-file failure as a bug.

**"Unsupported .locres version byte N"**
The file declares a format version this build doesn't know (newer engine?).
The app refuses rather than guessing — update the app, or report the game
so support can be added.

**Imported CSV/PO text looks like garbage ("Ã‡ÐµÐ¹" or boxes)**
The file is not UTF-8 and the import says so. Nothing was lost: the bytes are
kept exactly as they are, so re-saving the file as UTF-8 (Notepad → Save As →
Encoding: UTF-8, or Excel → Save As → CSV UTF-8) and importing again shows the
correct text. Files with a UTF-8/UTF-16 byte-order mark are detected
automatically and need no action.

**A CSV opened in Excel shows a formula or a warning about a command**
Game text can begin with `=`, `+`, `-` or `@`, and Excel treats such a cell as a
formula. The export keeps text exactly as it is in the file — it does not add
escaping, because that would change the content on re-import. If you only need
to read the file, open it in a text editor, or import it into Excel via
Data → From Text rather than double-clicking.

**Save failed / "Your changes are NOT saved"**
The save never partially overwrites anything (writes are atomic), so your
target file is intact. Check the message: most commonly the export folder
is not writable, the disk is full, or the destination file is locked by
another program.

**The app closed by itself / the window froze**
Every crash and every freeze longer than 10 seconds leaves a plain-text report in
`%AppData%\SmartUnrealLocalization\crash-reports` (`crash-…` / `hang-…`),
and the next start announces it in the log. A crash also rescues the unsaved
edits of every open tab into `Recovered_<date>` inside the Autosave folder —
open that file to get your work back. The report holds the versions, what was
open (file names and counts), the last log lines exactly as the Logs panel
shows them, and the error itself. It never contains the API key, and no row
text is written into it on purpose. Read it before sending it with a bug report.

**AI translation fails or stalls**
- Check the API key / model name / base URL in Settings (one typo = every
  batch fails).
- Rate limits: lower parallel requests, enable auto-retry, reduce batch size.
- A circuit breaker stops a multi-translate language after 3 consecutive
  failed batches and writes a PARTIAL file — check the log panel for the
  reason, then rerun (existing translations are reused via TM).

**API key is empty after moving to another PC / Windows account**
Expected: the key is stored encrypted with Windows DPAPI, which only your
Windows account on the original machine can decrypt. A copied `config.json`
carries all preferences but not a usable key — enter the key again in
Settings on the new machine.

**Review marks / comments disappeared after moving a file**
Sidecars are keyed by the file's full path — moving or renaming the file
starts fresh state by design (two different files that merely share a name
never share review state). Keep files in place, or re-apply marks.

**Rows I expect are not visible**
Check the bottom-bar filters (untranslated/translated, review status,
commented, found rows, text filter) and View → Show empty keys. While any
filter hides rows, the status bar says "Showing X of Y"; if that is not there,
no filter is hiding anything.

**"Smart App Control blocked an app that may be unsafe"**
Smart App Control (Windows 11) blocks programs whose publisher it cannot verify,
and Smart Unreal Localization is not code-signed yet. Unlike SmartScreen, this
block has no "Run anyway". The app itself is not flagged as harmful — it simply
carries no signature. Until a signed release is available, the only ways to run it
on such a PC are to turn Smart App Control off (Windows Security → App & browser
control → Smart App Control) or to use another PC. On older Windows 11 builds
Smart App Control cannot be turned back on without reinstalling Windows — decide
with that in mind.

**Help → Check for updates says "No published release was found"**
The check reads the latest release on the project's GitHub page. It says this
when there is no published release yet, or when the page cannot be reached from
your network. Nothing is wrong with your copy.

**The app can't be built from source**
`dotnet build` needs the .NET 8 SDK. The solution has three projects
(app, LocresIO, LocresIO.Tests); `dotnet test` must be green — if a test
fails on your machine, report it before changing anything.

**Old `.temp-SUL-*` folders next to the exe**
Leftovers from pre-2026-07 versions. The app sweeps stale ones on startup;
they are safe to delete manually.
