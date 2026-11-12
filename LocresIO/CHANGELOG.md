# Changelog

All notable changes to LocresIO are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow SemVer.

## [Unreleased]

### Changed
- **`LocresMerger.Merge` no longer carries a translation whose original text
  changed.** A key match alone is not proof: updates reword strings under the
  same key, and carrying the old line ships text that says the wrong thing. The
  match is now verified against `SourceStringHash`, which fingerprints the source
  text a localized file does not contain. Refused entries keep the new version's
  text, so the output stays correct for the updated game and those keys read as
  needing translation.
  **Breaking for callers who relied on the old behaviour** — restore it with
  `new LocresMergeOptions { RequireMatchingSourceHash = false }`.

### Added
- `LocresMergeOptions.RequireMatchingSourceHash` (default `true`).
- `LocresMergeResult.CarriedValues` and `RejectedValues` — the translations that
  moved and the ones that were refused, each with its text, so a caller can show
  or log them instead of taking a second pass over the document to work it out.

## [0.1.0] — 2026-07-03

Initial release.

### Added
- Strict reader for all `.locres` versions: v0 Legacy, v1 Compact,
  v2 Optimized (CRC32), v3 Optimized (CityHash64/UTF-16).
- Streaming writer with offset backpatching; atomic `Save(path)`
  (temp file + rename).
- `SaveWith` translation overlay: immutable document, empty/unknown
  overrides keep base values, per-save result counters.
- Version conversion (any → any) via `LocresSaveOptions.TargetVersion`.
- `LocresMerger.Merge`: new-game-version structure with carried
  translations, `NewKeys` and `SourceChangedKeys` diffs.
- Clean-room UE hash implementations: CityHash64 (Google v1.1 semantics,
  MIT) reduced to 32 bits, and StrCrc32 (per-UTF-16-unit CRC-32).
- Typed errors: `LocresFormatException` (byte offset + parse context),
  `LocresUnsupportedVersionException`.
- Latin-1 narrow-string reads (lossless where the reference tool corrupted
  0x80–0xFF bytes to `?`); ASCII-only narrow writes for engine parity.
- Format specification ([FORMAT.md](FORMAT.md)) and validation/benchmark
  data ([BENCHMARKS.md](BENCHMARKS.md)).
