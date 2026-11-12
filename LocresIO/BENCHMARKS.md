# LocresIO — validation & benchmark results

Raw data for the public README. Collected 2026-07-02/03 on Windows 11 with the
dev oracle harness (`LocresOracle`, outside this repo), using
**UnrealLocres.exe** as the reference implementation. Exe timings include
process start — that is what a host application actually pays per call.

## Correctness validation (real game files, one per format version)

| File | Version | Entries | Hash check | Read parity | Write parity |
|---|---|---|---|---|---|
| V0.locres | v0 Legacy | 1,000 | n/a (format has none) | PASS, 0 mismatches | PASS, **byte-identical** |
| V1.locres | v1 Compact | 6,062 | n/a (format has none) | PASS, 0 mismatches | PASS, **byte-identical** |
| V2.locres | v2 Optimized | 332 | **PASS — 345 engine-written CRC32 hashes recomputed, 0 mismatches** | PASS, 0 mismatches | PASS, **byte-identical** |
| V3.locres | v3 CityHash64 | 6,320 | **PASS — 6,387 engine-written CityHash64 hashes recomputed, 0 mismatches** | PASS, 0 mismatches | PASS, **byte-identical** |

Hash checks compare LocresIO's clean-room StrCrc32/CityHash64 output against
hash values written by Unreal Engine itself into shipped game files.

Additional suites (all PASS):

- **Full conversion matrix** v{0,1,2,3} → v{0,1,2,3}: every converted file
  passes hash/read/write validation; write output byte-identical to the
  reference tool's output for the converted file.
- **Unicode torture**: Ukrainian (і ї є ґ), emoji, CJK, Arabic/RTL, combining
  marks, `\n` / `\r\n` / tabs, placeholder tags, interior NULs, very long strings,
  duplicate string-table values + ref counts.
- **Override safety**: null/empty override never blanks a base value;
  duplicate (namespace, key) entries update together; unknown keys counted
  separately.
- **Malformed input**: empty/random/truncated data, unsupported version byte,
  bad table offset, bad string length, implausible namespace count — all fail
  with typed, controlled errors.
- **Stream behavior**: stream load; non-seekable stream rejected for v1–v3
  (backpatch needs seek) and accepted for legacy; cancellation honored.

## Benchmarks — typical game files

Open = parse to document (LocresIO) vs export-to-CSV (exe).
Save = full-overlay translated save (LocresIO `SaveWith`) vs CSV import (exe).

| File | Open LocresIO | Open exe | Speedup | Save LocresIO | Save exe | Speedup |
|---|---|---|---|---|---|---|
| V0.locres (1k entries) | 0.4 ms | 85.0 ms | **~220×** | 1.1 ms | 118.4 ms | **~104×** |
| V1.locres (6k entries) | 2.2 ms | 92.0 ms | **~42×** | 5.3 ms | 239.6 ms | **~46×** |
| V2.locres (0.3k entries) | 0.3 ms | 88.9 ms | **~298×** | 1.0 ms | 102.6 ms | **~105×** |
| V3.locres (6.3k entries) | 2.2 ms | 100.2 ms | **~46×** | 4.7 ms | 326.6 ms | **~69×** |

Synthetic scale test: 100,000-entry v3 document (~7 MiB) — save 29 ms,
load 43 ms, ~37 MiB managed memory after load.

## Heavy stress (multi-gigabyte synthetic files)

| Scenario | Entries | Value length | .locres size |
|---|---|---|---|
| S1 | 100,000 | 2,048 chars | 394.5 MB |
| S2 | 150,000 | 4,096 chars | 1,177.7 MB |
| S3 | 250,000 | 4,096 chars | 1,962.9 MB |

S3 (heaviest) results:

| Operation | Time | Peak RAM |
|---|---|---|
| LocresIO open | 3.35 s | 3.9 GB |
| LocresIO save (copy) | **46.15 s** | 4.1 GB |
| UnrealLocres.exe export | 27.58 s | 2.1 GB |
| UnrealLocres.exe import | **473.38 s** | 4.3 GB |

SHA-256 of the original, LocresIO's saved copy, and the exe's `.new` output
were **all identical** on the 1.96 GB file. On write, LocresIO is ~10× faster
at this scale (the exe's linear string-table scan is O(n²)); open/export are
not a 1:1 comparison since the exe also writes a CSV.
