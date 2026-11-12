using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Per-file reviewer comments — free-text notes tied to a row's stable
    /// <see cref="TranslationRow.Key"/> (NOT its index, which shifts on
    /// filter/sort). Persisted in a sidecar NEXT TO the opened file:
    /// <c>&lt;filepath&gt;.comments.json</c>. Comments are reviewer metadata —
    /// they are NEVER written into the exported .locres/.po/.csv.
    ///
    /// <para>
    /// Deliberately SEPARATE from <see cref="ReviewService"/> (different file,
    /// different field): a comment is not a review verdict, and editing the
    /// Target does not clear it. Mirrors ReviewService's lifecycle: LoadForFile
    /// on open / tab-switch, debounced Save, SaveNow on close.
    /// </para>
    ///
    /// <para>
    /// <b>One context, one lock.</b> The sidecar path, the map and the two flags
    /// below change ONLY under <c>_saveLock</c>, and a file switch is ONE
    /// critical section: flush the old file, then repoint and load the new one.
    /// It used to be three unlocked steps, and the pool-thread save timer could
    /// land between them — with the map already emptied but the path still on the
    /// old file, the "no comments left" rule DELETED that file's sidecar (audit
    /// 2026-09 §6; <c>SidecarRaceTests</c>).
    /// </para>
    /// </summary>
    public sealed class CommentService
    {
        private static readonly Lazy<CommentService> _lazy = new(() => new CommentService());
        public static CommentService Instance => _lazy.Value;

        private string _sidecarPath = "";
        private Dictionary<string, string> _byKey = new(StringComparer.Ordinal);
        private bool _dirty;        // changed since the load / the last write
        private bool _unreadable;   // exists but could not be read: never write over it
        private Timer? _saveTimer;
        private readonly object _saveLock = new();

        private CommentService() { }

        /// <summary>U+001F unit separator — cannot occur in real namespace/key text.</summary>
        private const char Sep = (char)0x1F;

        /// <summary>
        /// Sidecar map key (format version 2): namespace-aware, so the same Key
        /// in two namespaces no longer collides. The anonymous namespace maps to
        /// the bare key, which keeps .po/.csv files (no namespaces) on the old
        /// key shape.
        /// </summary>
        private static string ComposeKey(string? ns, string? key)
            => string.IsNullOrEmpty(ns) ? key ?? "" : ns + Sep + key;

        private static string SidecarFor(string filePath) => filePath + ".comments.json";

        /// <summary>
        /// Switch to a different file: flush the previous file's pending changes,
        /// then load the new file's sidecar into the in-memory Key→text map.
        /// Missing sidecar = no comments (not an error).
        /// </summary>
        public void LoadForFile(string? filePath)
        {
            lock (_saveLock)
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                FlushLocked();

                _byKey = new Dictionary<string, string>(StringComparer.Ordinal);
                _dirty = false;
                _unreadable = false;
                _sidecarPath = string.IsNullOrEmpty(filePath) ? "" : SidecarFor(filePath);
                if (_sidecarPath.Length == 0 || !File.Exists(_sidecarPath)) return;

                string json;
                try { json = File.ReadAllText(_sidecarPath); }
                catch (Exception ex)
                {
                    // Locked or unreadable right now. It still holds the user's
                    // comments, so this session must not write — or delete — it.
                    _unreadable = true;
                    Console.WriteLine($"[CommentService] LoadForFile('{filePath}') could not read the sidecar: {ex.Message}");
                    return;
                }
                CommentSidecar? loaded;
                try { loaded = JsonConvert.DeserializeObject<CommentSidecar>(json); }
                catch (Exception parseEx)
                {
                    // Never continue-empty over a corrupt sidecar — the next save
                    // would wipe every comment. Preserve it for recovery instead.
                    ReviewService.PreserveCorrupt(_sidecarPath, parseEx);
                    return;
                }
                if (loaded?.Comments == null) return;
                foreach (var (key, entry) in loaded.Comments)
                    if (entry?.Text is { Length: > 0 }) _byKey[key] = entry.Text;
            }
        }

        /// <summary>Apply loaded comments onto the rows (after SetRows).</summary>
        public void Hydrate(IEnumerable<TranslationRow> rows)
        {
            if (_byKey.Count == 0) return;
            bool migrated = false;
            foreach (var row in rows)
            {
                var composed = ComposeKey(row.Namespace, row.Key);
                if (_byKey.TryGetValue(composed, out var text))
                {
                    row.Comment = text;
                    continue;
                }

                // Version-1 sidecars stored the pre-LocresIO flattened key
                // ("ns/key", with a leading slash for the anonymous namespace).
                // Migrate hits to the v2 namespace-aware key.
                var legacy = (row.Namespace ?? "") + "/" + (row.Key ?? "");
                if (_byKey.TryGetValue(legacy, out text))
                {
                    lock (_saveLock) { _byKey.Remove(legacy); _byKey[composed] = text; _dirty = true; }
                    row.Comment = text;
                    migrated = true;
                }
            }
            if (migrated) SaveDebounced();
        }

        public string? Get(string? ns, string key)
            => _byKey.TryGetValue(ComposeKey(ns, key), out var t) ? t : null;

        /// <summary>Set (or, when blank, remove) a row's comment + schedule a save.</summary>
        public void Set(string? ns, string key, string? text)
        {
            if (string.IsNullOrEmpty(_sidecarPath) || string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrWhiteSpace(text)) { Remove(ns, key); return; }
            lock (_saveLock) { _byKey[ComposeKey(ns, key)] = text; _dirty = true; }
            SaveDebounced();
        }

        public void Remove(string? ns, string key)
        {
            if (string.IsNullOrEmpty(_sidecarPath)) return;
            bool removed;
            lock (_saveLock) { removed = _byKey.Remove(ComposeKey(ns, key)); if (removed) _dirty = true; }
            if (removed) SaveDebounced();
        }

        /// <summary>
        /// Copy the current file's comment sidecar next to a just-exported file.
        /// Comments are keyed by file PATH, so a Save/Export (which writes a new
        /// file at a new path) would otherwise leave the saved copy with no
        /// comments on reopen. Flushes pending edits first. No-op when the
        /// destination is the same file we're already tracking.
        /// </summary>
        public void MirrorTo(string? destFilePath) => MirrorFromTo(null, destFilePath);

        /// <summary>
        /// Same as <see cref="MirrorTo"/>, but from an EXPLICIT source file instead
        /// of the globally tracked one. Background saves must pass the source path
        /// captured with their snapshot: a tab switch mid-save re-points the global
        /// sidecar, and mirroring from it would copy tab B's comments next to tab
        /// A's export. Null source = the currently tracked file.
        /// </summary>
        public void MirrorFromTo(string? srcFilePath, string? destFilePath)
        {
            if (string.IsNullOrEmpty(destFilePath)) return;

            string src;
            if (srcFilePath == null)
            {
                if (string.IsNullOrEmpty(_sidecarPath)) return;
                SaveNow();
                src = _sidecarPath;
            }
            else
            {
                src = SidecarFor(srcFilePath);
                // Flush pending edits only if that file is still the tracked one.
                if (string.Equals(src, _sidecarPath, StringComparison.OrdinalIgnoreCase))
                    SaveNow();
            }

            try
            {
                var dest = SidecarFor(destFilePath);
                if (!string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(src))
                {
                    File.Copy(src, dest, overwrite: true);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CommentService] MirrorFromTo('{destFilePath}') failed: {ex.Message}"); }
        }

        /// <summary>Schedule a flush 500 ms after the last edit (coalesces typing bursts).</summary>
        public void SaveDebounced()
        {
            if (string.IsNullOrEmpty(_sidecarPath)) return;
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(500, Timeout.Infinite);
        }

        /// <summary>
        /// Flush immediately, if anything changed. Writes atomically (tmp →
        /// replace) so a crash can't corrupt the sidecar. When there are no
        /// comments left, the sidecar is deleted rather than left as an empty file.
        /// </summary>
        public void SaveNow()
        {
            lock (_saveLock) FlushLocked();
        }

        /// <summary>Write the current context. Caller holds <c>_saveLock</c>.</summary>
        private void FlushLocked()
        {
            if (string.IsNullOrEmpty(_sidecarPath) || !_dirty || _unreadable) return;
            try
            {
                if (_byKey.Count == 0)
                {
                    if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
                    _dirty = false;
                    return;
                }

                var sidecar = new CommentSidecar
                {
                    Version = 2, // v2 = namespace-aware keys (ns + U+001F + key)
                    Comments = _byKey.ToDictionary(
                        kv => kv.Key,
                        kv => new CommentEntry { Text = kv.Value, UpdatedUtc = DateTime.UtcNow }),
                };
                var json = JsonConvert.SerializeObject(sidecar, Formatting.Indented);

                var tmp = _sidecarPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _sidecarPath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommentService] SaveNow failed: {ex.Message}");
                WarnSaveFailedOnce(ex);
            }
        }

        private string? _lastWarnedPath;

        /// <summary>
        /// Comments silently vanishing is worse than a toast: the sidecar lives
        /// NEXT TO the opened file, and game folders are often read-only — the
        /// user keeps typing comments that never persist. Warn once per file
        /// (SaveNow retries on every debounce, so an unguarded toast would spam).
        /// </summary>
        private void WarnSaveFailedOnce(Exception ex)
        {
            if (string.Equals(_lastWarnedPath, _sidecarPath, StringComparison.OrdinalIgnoreCase)) return;
            _lastWarnedPath = _sidecarPath;
            var msg = $"Couldn't save reviewer comments next to the file ({ex.Message}) — is the folder read-only?";
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { ToastService.Instance.ShowError(msg); } catch { /* toast host not up yet */ }
            });
        }
    }

    /// <summary>On-disk shape of <c>&lt;file&gt;.comments.json</c>.
    /// Version 1 keyed comments by the flattened row Key; version 2 keys them
    /// namespace-aware (ns + U+001F + key). The loader accepts both — v1 keys
    /// migrate lazily during <see cref="CommentService.Hydrate"/>.</summary>
    public sealed class CommentSidecar
    {
        [JsonProperty("version")] public int Version { get; set; } = 2;
        [JsonProperty("comments")] public Dictionary<string, CommentEntry> Comments { get; set; } = new();
    }

    public sealed class CommentEntry
    {
        [JsonProperty("text")] public string Text { get; set; } = "";
        [JsonProperty("updatedUtc")] public DateTime UpdatedUtc { get; set; }
    }
}
