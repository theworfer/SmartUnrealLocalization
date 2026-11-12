# LocresIO

A clean-room, zero-dependency .NET library for reading, writing, converting and
merging Unreal Engine **`.locres`** localization files. All format versions
(v0 Legacy → v3 CityHash64), byte-exact against the engine's own output,
in-process and fast.

```
dotnet add package LocresIO
```

## Why

Every existing `.locres` toolchain shells out to an external executable or is
read-only. LocresIO is a from-scratch implementation of the documented binary
format (see [FORMAT.md](FORMAT.md)) under a permissive license:

| | LocresIO | UnrealLocres.exe |
|---|---|---|
| License | **MIT** | none (all rights reserved) |
| Usage | in-process library | external process + CSV round-trip |
| Versions | v0–v3 read/write/**convert** | v0–v3 read/write |
| Merge | refuses to carry a translation whose original was reworded (hash-verified, opt-out); reports carried **and** refused values; new namespaces kept | carries on key match alone; new namespaces silently dropped |
| Writes | atomic (temp + rename) | `.new` sibling file |
| Bad input | typed errors with byte offset | crashes / silent corruption |
| Unknown future version | refused safely | parsed wrong, corrupted on save |
| Open 6k entries | **~2 ms** | ~100 ms |
| Save 6k entries | **~5 ms** | ~330 ms |
| Save 250k × 4KB entries (1.9 GB) | **46 s** | 473 s |

Full numbers and methodology: [BENCHMARKS.md](BENCHMARKS.md).

## Correctness

Validated against ground truth, not just unit tests:

- **6,700+ hashes recomputed vs values written by Unreal Engine itself** into
  shipped game files (CityHash64/UTF-16 for v3, StrCrc32 for v2) — zero mismatches.
- **Byte-identical write parity** with the reference tool on real game files of
  every format version, including a 1.9 GB stress file (SHA-256 equal).
- 130+ unit tests: round-trips, version-conversion matrix, Unicode torture
  (emoji, CJK, RTL, combining marks), truncation fuzzing, override safety.

## Quick start

```csharp
using Locres.IO;

// Read
var doc = LocresDocument.Load("Game.locres");
Console.WriteLine($"{doc.Version}: {doc.EntryCount} entries");
foreach (var ns in doc.Namespaces)
    foreach (var e in ns.Entries)
        Console.WriteLine($"[{ns.Name}] {e.Key} = {e.Value}");

// Translate and save — the document is never mutated; empty/missing
// overrides keep the original value (untranslated rows can't blank strings)
var overrides = new Dictionary<(string Ns, string Key), string?>
{
    [("", "NewGame")] = "Нова гра",
    [("Menu", "Quit")] = "Вийти",
};
var result = doc.SaveWith(overrides, "Game.uk.locres");
Console.WriteLine($"{result.OverridesApplied} applied, {result.OverridesUnknownKey} unknown keys");

// Convert between format versions
doc.Save("Game.v2.locres", new LocresSaveOptions { TargetVersion = LocresVersion.Optimized });

// Carry translations into a new game version.
// A translation is carried only where the key matches AND SourceStringHash says
// the original text is unchanged — see "Merge safety" below.
var merge = LocresMerger.Merge(
    oldTranslated: LocresDocument.Load("Game.uk.locres"),
    newVersion:    LocresDocument.Load("Game.v2.0.locres"));
merge.Document.Save("Game.uk.v2.0.locres");
Console.WriteLine($"{merge.CarriedValues.Count} carried, " +
                  $"{merge.RejectedValues.Count} refused (the original was reworded), " +
                  $"{merge.NewKeys.Count} new keys need translation");

// Nothing is lost: the refused translations come back with their text, so you
// can show them for review instead of dropping someone's work.
foreach (var ((ns, key), staleText) in merge.RejectedValues)
    Console.WriteLine($"  {ns}/{key} was: {staleText}");
```

### Merge safety

`Merge` defaults to **`RequireMatchingSourceHash = true`**, and that default is
the point of the API.

A key match alone proves nothing. Game updates reword strings under the same key
constantly, and carrying the old line then ships text that says the wrong thing
— silently, in a file nobody re-reads. `SourceStringHash` is the only evidence a
localized `.locres` carries: it fingerprints the *source* text, which the file
itself does not contain. Same fingerprint, same original; different fingerprint,
the original changed.

Refused entries keep the **new** version's text in `Document`, so the output is
correct for the updated game and those keys read as needing translation.

Turn the check off only when the evidence is unusable — an old file whose hashes
another tool rebuilt or zeroed makes *every* entry look changed:

```csharp
var merge = LocresMerger.Merge(old, newer,
    new LocresMergeOptions { RequireMatchingSourceHash = false });
```

Before doing that, look at `RejectedValues`. If it holds essentially the whole
file, the hashes are the problem, not the translations.

Namespace/key pre-hash helpers are exposed too:

```csharp
uint v3 = Locres.IO.Hashing.LocresHash.CityHashUtf16("MyKey"); // CityHash64/UTF-16 → 32-bit
uint v2 = Locres.IO.Hashing.LocresHash.Crc32("MyKey");         // UE StrCrc32
```

## Guarantees

- **`SourceStringHash` is carried through verbatim** — the engine's stale-translation
  detection keeps working. It is never recomputed (it can't be — the source
  text isn't in the file).
- **Empty overrides never blank shipped strings** — `SaveWith` keeps the base
  value for null/empty overrides by contract.
- **Documents are immutable after load** — one loaded document can back many
  concurrent `SaveWith` calls (e.g. batch-translating into N languages).
- **Atomic saves** — a crash mid-write can never leave a truncated file.
- **Strict parsing** — malformed input fails with `LocresFormatException`
  carrying the byte offset and parse context; unknown version bytes are
  refused (`LocresUnsupportedVersionException`) instead of guessed.

## License

MIT — see [LICENSE](LICENSE). The CityHash64 implementation follows Google's
MIT-licensed CityHash (see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)).
LocresIO contains no code from Epic Games or from any unlicensed tool — the
format specification in [FORMAT.md](FORMAT.md) was assembled clean-room.

LocresIO powers [Smart Unreal Localization](https://github.com/theworfer/SmartUnrealLocalization),
a desktop editor for Unreal Engine localization with AI batch translation.
