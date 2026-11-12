# FAQ

**Which .locres versions are supported?**
All of them: v0 (Legacy), v1 (Compact), v2 (Optimized/CRC32) and
v3 (Optimized CityHash64 — UE 4.27+ and all UE5). The version is detected
from the file and shown in the title pill; saving keeps the same version.
The engine's key hashes and `SourceStringHash` values are reproduced
byte-exactly (validated against real game files — see
[BENCHMARKS](../LocresIO/BENCHMARKS.md)).

**Do I need UnrealLocres.exe or an internet connection?**
No. Since 2026-07 the app uses its own in-process engine
([LocresIO](../LocresIO/README.md)). Nothing is downloaded at runtime;
everything except the AI calls works fully offline.

**Where does Save put my file?**
Manual saves go to a timestamped `Export_*` subfolder of your Export folder
(Settings → Files); auto-saves to `AutoSaved_*` under the Autosave folder.
Save As can also write to an exact path — set *Save As behavior* to
"Always ask".

**Why don't untranslated rows break my game?**
Rows with an empty Target keep the file's original text on save — by
contract, at the engine level. You can ship a partially translated file
safely at any point.

**What happens on a game update?**
Use File → *Merge to new version*. Your translations are carried onto the
new build's structure; green rows are new keys, violet rows had their
source text changed (re-check those translations).

**Some rows are missing after opening a .locres?**
Entries whose value is empty are hidden by default. Toggle
View → *Show empty keys* to see them; they are always preserved in the file.

**Which AI providers work?**
Gemini, OpenAI, Anthropic, and any OpenAI-compatible endpoint (one unified
API key / model / base URL in Settings). Batch size, parallel requests,
prompt caching and auto-retry are configurable.

**Can I translate with my Claude/ChatGPT SUBSCRIPTION instead of an API key?**
Yes — enable Settings → MCP Server and connect your AI chat client (Claude
Desktop/Code, Codex, Antigravity) to the app (User Guide §10). The chat's
model reads rows and writes translations through the app — validated,
undoable, and billed to your subscription, not an API key. Mind your plan's
usage limits on big files.

**Does the app connect to the internet on its own?**
No. It talks to your AI provider only when you start a translation or a review,
and to the project's repository only when you click **Help → Check for updates**
— that check reads the latest release number and downloads nothing.

**Is my API key stored safely?**
Yes — it is encrypted at rest with Windows DPAPI, tied to your Windows
account on this machine. `config.json` holds only the encrypted blob, so a
backup or a shared copy of the config can't leak the key. Consequence: the
key can't be carried to another PC or Windows account via the config file —
re-enter it in Settings there.

**Will the AI break my `<Tags>` and `{0}` placeholders?**
The prompt instructs the model to keep them intact, tag-only rows are
copied without an API call, and the tag validator flags any mismatch
after the run (red/yellow row tints + Problems tab).

**Where are my review marks and comments stored?**
In sidecar files (reviews in `%AppData%/SmartUnrealLocalization/reviews/`,
comments next to the opened file as `<file>.comments.json`). They are
metadata — never written into exported files — and are keyed to the file's
full path.

**Can I edit the same strings in Excel?**
Yes — copy the table (TSV) into Excel, edit, and paste the column back
(row counts must match), or export/import CSV.
