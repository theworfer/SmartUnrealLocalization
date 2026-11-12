using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Per-file review / edited-at sidecar persistence. Each opened .locres /
    /// .po / .csv has its own JSON sidecar under
    /// <c>%AppData%/SmartUnrealLocalization/reviews/{filename-no-ext}__{pathHash}.json</c>
    /// holding one <see cref="ReviewEntry"/> per (Namespace, Key) inside that
    /// file. The sidecar lives in AppData rather than next to the source file
    /// so it never bleeds into the user's project repo or Unreal build folder.
    ///
    /// <para>
    /// <b>Strict per-file identity</b>: the sidecar name includes a hash of the
    /// file's FULL PATH, so two different files that merely share a name (e.g.
    /// the same <c>Game.locres</c> from two game versions in different folders)
    /// get separate sidecars and never share review state. Review/edit metadata
    /// is applied ONLY from this exact file's own sidecar — it is never pulled
    /// from other files by matching keys (a coinciding Key in another file must
    /// not surface that file's Approved/Rejected here). Moving/renaming a file
    /// starts a fresh sidecar; that is the deliberate trade-off for isolation.
    /// </para>
    ///
    /// <para>
    /// <b>Hydration</b>: on file open, <see cref="LoadForFile"/> reads the
    /// sidecar into an in-memory <c>(Ns, Key) → ReviewEntry</c> dictionary.
    /// <see cref="Hydrate"/> then walks the row list and applies each entry's
    /// Status + EditedAt to the matching row. Lookup is O(1) per row; rows with
    /// no entry in THIS file's sidecar stay None / null.
    /// </para>
    ///
    /// <para>
    /// <b>Saves</b> are debounced 500 ms via a single timer so a bulk-mark
    /// operation flushes once, not N times. <see cref="SaveNow"/> forces an
    /// immediate flush — used when switching to a different file.
    /// </para>
    /// </summary>
    public sealed class ReviewService
    {
        private static readonly Lazy<ReviewService> _lazy = new(() => new ReviewService());
        public static ReviewService Instance => _lazy.Value;

        private string _currentSidecarPath = "";
        private Dictionary<(string Ns, string Key), ReviewEntry> _byKey = new();
        private Timer? _saveTimer;
        private readonly object _saveLock = new();

        private ReviewService() { }

        public static string ReviewsDir()
        {
            // Through AppConfig so every per-user path has ONE definition (and one
            // test seam). Resolves to the same %AppData%/SmartUnrealLocalization/reviews
            // as before — this is a de-duplication, not a relocation.
            var dir = Path.Combine(AppConfig.ConfigDir(), "reviews");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ── one context, one lock ───────────────────────────────────────────
        // The sidecar path, the map and the flags change ONLY under _saveLock,
        // and a file switch is ONE critical section. It used to repoint the path
        // and replace the map in separate unlocked steps, so the pool-thread
        // save timer could write A's reviews into B's sidecar (path already B,
        // map still A) or an empty sidecar over B before B was read (audit
        // 2026-09 §6; SidecarRaceTests reproduced the first).
        private bool _dirty;        // changed since the load / the last write
        private bool _unreadable;   // exists but could not be read: never write over it

        /// <summary>
        /// Switch the service to a different file. Flushes any pending save on
        /// the previous file before loading the new sidecar. Idempotent on the
        /// same path (no-op for null/empty <paramref name="filePath"/>).
        /// </summary>
        public void LoadForFile(string? filePath)
        {
            lock (_saveLock)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                FlushLocked();

                _byKey = new Dictionary<(string Ns, string Key), ReviewEntry>();
                _dirty = false;
                _unreadable = false;
                _currentSidecarPath = "";
                if (string.IsNullOrEmpty(filePath)) return;

                try
                {
                    // Name the sidecar by filename + a hash of the FULL PATH so two files
                    // that merely share a name (different folders / game versions) never
                    // collide on the same sidecar.
                    _currentSidecarPath = SidecarPathFor(filePath);
                    if (!File.Exists(_currentSidecarPath)) return;

                    string json;
                    try { json = File.ReadAllText(_currentSidecarPath); }
                    catch (Exception readEx)
                    {
                        // Locked or unreadable now: it still holds review work, so this
                        // session must not write over it.
                        _unreadable = true;
                        Console.WriteLine($"[ReviewService] could not read '{_currentSidecarPath}': {readEx.Message}");
                        return;
                    }
                    try
                    {
                        var loaded = JsonConvert.DeserializeObject<ReviewSidecar>(json);
                        if (loaded?.Entries != null)
                            foreach (var entry in loaded.Entries)
                                _byKey[(entry.Ns ?? "", entry.Key ?? "")] = entry;
                    }
                    catch (Exception parseEx)
                    {
                        // A corrupt sidecar must NOT be silently replaced-empty (the next
                        // save would wipe all review work). Preserve it for recovery.
                        PreserveCorrupt(_currentSidecarPath, parseEx);
                    }
                }
                catch (Exception ex)
                {
                    _unreadable = true;
                    Console.WriteLine($"[ReviewService] LoadForFile('{filePath}') failed: {ex.Message}");
                }
            }
        }
        /// <summary>
        /// Apply this file's review metadata to every row in <paramref name="rows"/>.
        /// ONLY this file's own sidecar is consulted — a row whose (Ns, Key) is not
        /// in it stays Review = None / EditedAt = null. Review state is never pulled
        /// from other files (no cross-file key matching), so a coinciding Key in an
        /// unrelated file can't surface its Approved/Rejected here.
        /// Direct field-style assignment via the partial setter is safe here because
        /// the row PropertyChanged subscription in the VM filters on Target only —
        /// Review and EditedAt updates won't re-trigger saves.
        /// </summary>
        public void Hydrate(IEnumerable<TranslationRow> rows)
        {
            if (string.IsNullOrEmpty(_currentSidecarPath)) return;

            bool migrated = false;
            foreach (var row in rows)
            {
                var ns = row.Namespace ?? "";
                var key = row.Key ?? "";
                if (!_byKey.TryGetValue((ns, key), out var entry))
                {
                    // Pre-LocresIO sidecars stored locres rows FLATTENED: Ns="",
                    // Key="ns/key" — including a leading slash for the anonymous
                    // namespace ("/key"). Migrate hits to the real identity so
                    // review work done before the switch is not lost.
                    var legacy = ("", ns + "/" + key);
                    if (!_byKey.TryGetValue(legacy, out entry)) continue;
                    lock (_saveLock)
                    {
                        _byKey.Remove(legacy);
                        entry.Ns = ns;
                        entry.Key = key;
                        _byKey[(ns, key)] = entry;
                        _dirty = true;
                    }
                    migrated = true;
                }
                row.Review = entry.Status;
                row.EditedAt = entry.EditedAt;
            }
            if (migrated) SaveDebounced();
        }

        /// <summary>
        /// Write a row's current Review / EditedAt back into the in-memory map
        /// and schedule a debounced flush to disk. Called by the VM on Target
        /// change and (Etap 11) on Mark Approved / Rejected / Clear Review.
        /// </summary>
        public void UpdateRow(TranslationRow row)
        {
            if (string.IsNullOrEmpty(_currentSidecarPath)) return;

            var ns = row.Namespace ?? "";
            var key = row.Key ?? "";

            // Mutations run under _saveLock: the debounce timer's SaveNow snapshots
            // _byKey on a pool thread, and an unguarded UI-thread mutation during a
            // bulk-mark could throw mid-enumeration → silently skipped save.
            lock (_saveLock)
            {
                // A row with no review and no edit history is not worth persisting.
                // Strip its entry so the sidecar stays minimal.
                if (row.Review == ReviewStatus.None && row.EditedAt == null)
                {
                    if (!_byKey.Remove((ns, key))) return;
                    _dirty = true;
                }
                else
                {
                    _byKey[(ns, key)] = new ReviewEntry
                    {
                        Ns = ns,
                        Key = key,
                        SourceSig = ComputeSourceSig(row.Source ?? ""),
                        Status = row.Review,
                        EditedAt = row.EditedAt,
                    };
                    _dirty = true;
                }
            }
            SaveDebounced();
        }

        /// <summary>Schedule a flush 500 ms after the last touch (coalesces bursts).</summary>
        public void SaveDebounced()
        {
            if (string.IsNullOrEmpty(_currentSidecarPath)) return;
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(500, Timeout.Infinite);
        }

        /// <summary>
        /// Flush pending writes immediately. Safe to call with nothing queued
        /// or with no current file (no-ops).
        /// </summary>
        public void SaveNow()
        {
            lock (_saveLock) FlushLocked();
        }

        /// <summary>Write the current context if it changed. Caller holds _saveLock.</summary>
        private void FlushLocked()
        {
            if (string.IsNullOrEmpty(_currentSidecarPath) || !_dirty || _unreadable) return;
            try
            {
                var sidecar = new ReviewSidecar { Entries = _byKey.Values.ToList() };
                // Atomic: temp sibling + move, so a crash mid-write can never
                // leave a corrupt sidecar behind (same pattern as CommentService).
                var tmp = _currentSidecarPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(sidecar, Formatting.Indented));
                File.Move(tmp, _currentSidecarPath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ReviewService] SaveNow failed: {ex.Message}");
            }
        }
        /// <summary>Where <paramref name="filePath"/>'s review sidecar lives: filename + a
        /// hash of the FULL path, so same-named files in different folders never share one.</summary>
        internal static string SidecarPathFor(string filePath)
        {
            var safeName = SafeName(Path.GetFileNameWithoutExtension(filePath));
            string fullPath;
            try { fullPath = Path.GetFullPath(filePath); } catch { fullPath = filePath; }
            return Path.Combine(ReviewsDir(), $"{safeName}__{ShortHash(fullPath)}.json");
        }

        /// <summary>Rename an unreadable persisted file to *.corrupt-&lt;ts&gt; so its data
        /// stays recoverable instead of being overwritten by the next save.</summary>
        internal static void PreserveCorrupt(string path, Exception cause)
        {
            Console.WriteLine($"[Persistence] '{path}' is unreadable ({cause.Message}) — preserving as .corrupt");
            try { File.Move(path, path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); }
            catch (Exception mv) { Console.WriteLine($"[Persistence] could not preserve corrupt file: {mv.Message}"); }
        }

        /// <summary>
        /// 16-hex-char SHA-256 prefix of the row's Source. Kept short because
        /// it's only a "did Source change since the review was filed?" marker —
        /// a future etap can surface "review stale, source has changed" in the
        /// UI by comparing this to the current row's source signature.
        /// </summary>
        private static string ComputeSourceSig(string source)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
            return Convert.ToHexString(bytes, 0, 8);
        }

        /// <summary>Stable 12-hex-char identity for a file's full path (case-folded for
        /// Windows). Used to disambiguate same-named files in different folders.</summary>
        private static string ShortHash(string s)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
            return Convert.ToHexString(bytes, 0, 6);
        }

        private static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "_default" : name;
        }
    }

    /// <summary>On-disk shape of one per-file review sidecar.</summary>
    public sealed class ReviewSidecar
    {
        public List<ReviewEntry> Entries { get; set; } = new();
    }

    /// <summary>One review record inside a sidecar.</summary>
    public sealed class ReviewEntry
    {
        public string? Ns { get; set; }
        public string? Key { get; set; }
        /// <summary>SHA-256 prefix of Source at the time of review — for future "source drift" detection.</summary>
        public string? SourceSig { get; set; }
        public ReviewStatus Status { get; set; }
        public DateTime? EditedAt { get; set; }
    }
}
