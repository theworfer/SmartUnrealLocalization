# The `.locres` binary format

Unreal Engine's compiled localization resource. This specification was
assembled clean-room from the publicly observable file layout and is the
authoritative reference for LocresIO's implementation. All integers are
**little-endian**. Offsets are absolute from the start of the file.

## Versions

| Byte | Name (LocresIO enum) | Introduced traits |
|------|----------------------|-------------------|
| 0 | `Legacy` | No header at all; values inline; namespace names always UTF-16 |
| 1 | `Compact` | Magic + version header; values deduplicated into a trailing string table |
| 2 | `Optimized` | + total entry count; + CRC32 pre-hashes for namespace/key names; + per-string ref counts |
| 3 | `OptimizedCityHash64` | Same layout as v2; pre-hashes use CityHash64/UTF-16 instead of CRC32 |

A file that does **not** begin with the magic GUID is a `Legacy` (v0) file
parsed from offset 0 — that is the format's own detection rule, not a
heuristic. An unknown version byte (> 3) must be refused: its layout cannot
be known, and re-saving a guessed parse corrupts data.

## Serialized string ("FString")

```
int32 Len
Len == 0 → empty string, no payload
Len >  0 → narrow: Len bytes, the LAST of which is the 0x00 terminator
Len <  0 → wide: |Len| UTF-16LE code units (2 bytes each), the last being 0x0000
```

Critical details:

- **`Len` INCLUDES the null terminator.** `"AsCiI"` serializes narrow as
  `06 00 00 00  41 73 43 69 49 00`.
- The empty string is conventionally written as `Len=1` + `0x00` (readers must
  also accept `Len=0`).
- A wide string's `|Len|` counts UTF-16 **code units**, so a surrogate pair
  contributes 2. `"юа"` → `FD FF FF FF  4E 04 30 04 00 00` (prefix −3).
- The engine writes narrow only for pure-ASCII content (every unit ≤ 0x7F);
  anything else is written wide in full. LocresIO follows this on write and
  decodes narrow bytes as **Latin-1** on read (byte-preserving superset of
  ASCII — some third-party files carry 0x80–0xFF narrow bytes).
- LocresIO strips exactly **one** trailing terminator on read and tolerates a
  missing one.

## Header (v1–v3)

```
offset 0:  byte[16] magic = 0E 14 74 75 67 4A 03 FC 4A 15 90 9D C3 37 7F 1B
offset 16: byte     version (1, 2, or 3)
offset 17: int64    localizedStringArrayOffset   ← backpatched after writing entries
(v2/v3)    int32    entriesCount                 ← total entries, backpatched
           int32    namespaceCount
```

Writers emit placeholders at offset 17, write all namespace/entry records,
append the string table, then seek back and patch the real table offset (and,
for v2/v3, the real entry count). `entriesCount` is derivable; readers should
not trust it (ecosystem tools write it loosely).

## Namespace/entry records (v1–v3)

Repeated `namespaceCount` times:

```
(v2/v3) uint32  namespaceNameHash     ← see "Pre-hashes"
        FString namespaceName
        int32   keyCount
        keyCount × {
            (v2/v3) uint32  keyNameHash
                    FString key
                    uint32  sourceStringHash    ← pass-through, see below
                    int32   localizedStringIndex → string table slot
        }
```

Duplicate namespace names and duplicate keys within a namespace are legal;
order is significant and must be preserved.

## String table (v1–v3)

At `localizedStringArrayOffset`:

```
int32 stringCount
stringCount × {
    FString localizedString
    (v2/v3) int32 refCount     ← number of entries pointing at this slot
}
```

Values are deduplicated **ordinal, case-sensitive, no normalization**, in
order of first appearance during the namespace/entry walk. `refCount` is
informational; readers ignore it, writers must emit the exact reference count.

## Legacy layout (v0)

```
int32 namespaceCount
namespaceCount × {
    FString namespaceName        ← ALWAYS written wide (UTF-16), even for ASCII
    int32   keyCount
    keyCount × {
        FString key
        uint32  sourceStringHash
        FString localizedValue   ← inline, no table
    }
}
```

## Pre-hashes (v2/v3)

Namespace and key name hashes are **always recomputed from the actual strings
on write** and ignored on read.

**v2 — UE StrCrc32.** Standard reflected CRC-32 (polynomial `0xEDB88320`,
initial register `0xFFFFFFFF`, final XOR `0xFFFFFFFF`), where every UTF-16
code unit is fed as **four** bytes — its 32-bit zero-extension, lowest byte
first. The two zero bytes still advance the CRC and must not be skipped.
No terminator, no case folding. `""` → `0`.

**v3 — CityHash64/UTF-16 reduced.** `CityHash64` (Google CityHash **v1.1**
semantics) over the UTF-16LE bytes of the string, without terminator,
reduced to 32 bits:

```
uint32 reduced = (uint32)h + (uint32)(h >> 32) * 23;
```

The empty string hashes to `0` as an explicit special case (CityHash64 of an
empty buffer is `K2 = 0x9AE16A3B2F90404F`, which does not reduce to zero).

## SourceStringHash

Each entry carries the hash of its **original source text** in the native
culture — data that is not present in the file itself. It can never be
recomputed from a localized file and must be carried through verbatim by any
tool that rewrites entries. The engine compares it with the hash of the
current source string to detect stale translations; dropping or recomputing
it makes the engine ignore the translation.

## Known ecosystem quirks LocresIO deliberately handles differently

| Behavior | UnrealLocres tool | LocresIO |
|---|---|---|
| Narrow bytes 0x80–0xFF | decoded as `?` (lossy ASCII) | decoded as Latin-1, lossless |
| Trailing NULs on read | strips all | strips exactly one |
| Unknown version byte | parsed as v3, corrupted on save | refused with a typed error |
| String table index out of range | unchecked crash | typed format error |
| Empty-valued entries | skipped on CSV export | preserved |
