using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// QC proposal store: keeps the current file's suggestions in memory and
    /// mirrors them into a sidecar NEXT TO the file (<c>&lt;file&gt;.qc.json</c>),
    /// so an unfinished review survives closing the app or rebooting the PC.
    /// Same lifecycle and safety rules as <see cref="CommentService"/>: atomic
    /// write (tmp → move), corrupt files preserved, never written into exports.
    /// </summary>
    public sealed class QcService
    {
        private static readonly Lazy<QcService> _lazy = new(() => new QcService());
        public static QcService Instance => _lazy.Value;
        private QcService() { }

        private readonly object _lock = new();
        private string _sidecarPath = "";
        private string _filePath = "";
        private QcSidecar _data = new();

        /// <summary>
        /// File the in-memory proposals belong to. A background QC run captures this
        /// and passes it back on merge: switching tabs mid-run must never write one
        /// file's findings into another file's sidecar.
        /// </summary>
        public string CurrentFile { get { lock (_lock) return _filePath; } }

        private static string SidecarFor(string filePath) => filePath + ".qc.json";
        private static (string, string) Id(string? ns, string? key) => (ns ?? "", key ?? "");

        /// <summary>
        /// (namespace, key) → row. Duplicate (ns,key) pairs are LEGAL in .locres,
        /// so a plain ToDictionary throws on real files — first occurrence wins,
        /// matching how the rest of the app resolves row identity.
        /// </summary>
        public static Dictionary<(string Ns, string Key), TranslationRow> IndexRows(IEnumerable<TranslationRow> rows)
        {
            var map = new Dictionary<(string Ns, string Key), TranslationRow>();
            foreach (var r in rows) map.TryAdd((r.Namespace ?? "", r.Key ?? ""), r);
            return map;
        }

        /// <summary>
        /// Raised when persisting the sidecar fails (read-only folder, full disk,
        /// file locked by a sync client). Silence here is the worst outcome: the
        /// user would keep reviewing, believing the work is saved, and lose it on
        /// exit. Reported once per distinct error so it cannot spam the log.
        /// </summary>
        public static Action<string>? SaveFailed;
        private string _lastSaveError = "";

        public DateTime? LastRunUtc { get { lock (_lock) return _data.LastRunUtc; } }
        public int LastCheckedRows { get { lock (_lock) return _data.LastCheckedRows; } }

        /// <summary>All proposals still awaiting a decision.</summary>
        public IReadOnlyList<QcProposal> Pending
        {
            get { lock (_lock) return _data.Proposals.Where(p => p.Verdict == QcVerdict.Pending).ToList(); }
        }

        public int PendingCount { get { lock (_lock) return _data.Proposals.Count(p => p.Verdict == QcVerdict.Pending); } }

        /// <summary>Switch to a file: flush the old sidecar, load the new one.</summary>
        public void LoadForFile(string? filePath)
        {
            lock (_lock)
            {
                SaveNowLocked();
                _data = new QcSidecar();
                _filePath = filePath ?? "";
                _sidecarPath = string.IsNullOrEmpty(filePath) ? "" : SidecarFor(filePath);

                if (_sidecarPath.Length > 0 && File.Exists(_sidecarPath))
                {
                    try
                    {
                        var loaded = JsonConvert.DeserializeObject<QcSidecar>(File.ReadAllText(_sidecarPath));
                        if (loaded?.Proposals != null)
                        {
                            // QC's local pass was removed (2026-10-21); the live
                            // validator reports those findings now. Its old pending
                            // suggestions would double every problem, so they go.
                            // Decided ones stay as history.
                            loaded.Proposals.RemoveAll(p => p.Verdict == QcVerdict.Pending && p.Source == "deterministic");
                            _data = loaded;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never continue-empty over a corrupt sidecar — the next save
                        // would wipe a whole review. Preserve it for recovery.
                        ReviewService.PreserveCorrupt(_sidecarPath, ex);
                    }
                }
            }
        }

        /// <summary>Attach the loaded proposals to the rows (drives the grid badge).</summary>
        public void Hydrate(IEnumerable<TranslationRow> rows)
        {
            Dictionary<(string, string), QcProposal> map;
            lock (_lock)
            {
                map = new Dictionary<(string, string), QcProposal>();
                foreach (var p in _data.Proposals)
                    if (p.Verdict == QcVerdict.Pending) map[Id(p.Namespace, p.Key)] = p;
            }
            // One proposal ↔ one row. Duplicate (ns,key) rows are legal in .locres;
            // attaching the SAME proposal object to several rows made a Fix on one
            // leave a stale badge (and a decided proposal) on its twins.
            var claimed = new HashSet<(string, string)>();
            foreach (var r in rows)
            {
                var id = Id(r.Namespace, r.Key);
                r.QcProposal = claimed.Add(id) && map.TryGetValue(id, out var p) ? p : null;
            }
        }

        /// <summary>
        /// Store a fresh run's results. Rows the user already decided on (Kept) stay
        /// decided unless their target changed — re-running QC must not resurrect
        /// suggestions the user dismissed.
        ///
        /// <para><paramref name="scopeKeys"/> limits the replacement to the rows that
        /// were actually checked: running QC on a handful of selected rows must not
        /// throw away the review work already sitting on every other row. Null means
        /// "the whole file was checked" (full replace).</para>
        /// <para>Returns false when the results were REFUSED because they belong to a
        /// file that is no longer the current one — the caller must then skip every
        /// follow-up effect (auto-fixes, bucket, tab switch), or they land on the
        /// wrong file (audit 2026-09 §7).</para>
        /// </summary>
        public bool SetRunResults(IEnumerable<QcProposal> proposals, int checkedRows,
            IReadOnlyCollection<(string Ns, string Key)>? scopeKeys = null, string? expectedFile = null,
            IReadOnlyDictionary<(string Ns, string Key), TranslationRow>? currentRows = null)
        {
            lock (_lock)
            {
                if (!BelongsToCurrentFile(expectedFile)) return false;

                // TryAdd, not ToDictionary: two dismissed proposals can legitimately
                // share a key (duplicate rows, or a new suggestion kept on top of an
                // older one) and ToDictionary would throw mid-run.
                var keptByKey = new Dictionary<(string, string), QcProposal>();
                foreach (var p in _data.Proposals.Where(p => p.Verdict == QcVerdict.Kept))
                    keptByKey.TryAdd(Id(p.Namespace, p.Key), p);

                var fresh = new List<QcProposal>();
                var inScope = scopeKeys is { Count: > 0 } ? new HashSet<(string, string)>(scopeKeys) : null;

                // Out-of-scope proposals survive a partial run untouched.
                if (inScope != null)
                {
                    fresh.AddRange(_data.Proposals.Where(p =>
                        p.Verdict == QcVerdict.Pending && !inScope.Contains(Id(p.Namespace, p.Key))));
                }

                // AI review results carry over a LOCAL re-run: the local pass cannot
                // re-derive them and they were paid for. Carried only while still
                // valid — a row edited since the review drops its stale suggestion.
                var aiCarry = new Dictionary<(string, string), QcProposal>();
                foreach (var p in _data.Proposals)
                {
                    if (p.Verdict != QcVerdict.Pending || !p.Kinds.Contains("AiQc")) continue;
                    var id = Id(p.Namespace, p.Key);
                    if (inScope != null && !inScope.Contains(id)) continue; // already preserved above
                    if (currentRows != null &&
                        (!currentRows.TryGetValue(id, out var row) || p.IsStaleFor(row)))
                        continue;
                    if (aiCarry.TryAdd(id, p)) fresh.Add(p);
                }

                foreach (var p in proposals)
                {
                    var id = Id(p.Namespace, p.Key);
                    if (keptByKey.TryGetValue(id, out var kept)
                        && string.Equals(kept.Before, p.Before, StringComparison.Ordinal)
                        && string.Equals(kept.After, p.After, StringComparison.Ordinal))
                    {
                        continue; // identical suggestion on an unchanged row — respect the earlier "Keep"
                    }
                    // The carried AI suggestion already covers this row (same target —
                    // staleness was checked); keep the AI one, absorb the local labels.
                    if (aiCarry.TryGetValue(id, out var carried))
                    {
                        foreach (var k in p.Kinds)
                            if (!carried.Kinds.Contains(k)) carried.Kinds.Add(k);
                        continue;
                    }
                    fresh.Add(p);
                }
                // Carry the decided ones over so the history (and the "don't ask
                // again" effect) survives the next run too.
                fresh.AddRange(keptByKey.Values);

                _data.Proposals = fresh;
                _data.LastRunUtc = DateTime.UtcNow;
                _data.LastCheckedRows = checkedRows;
                SaveNowLocked();
                return true;
            }
        }

        /// <summary>
        /// Guard for results produced by a background run: refuse them when the user
        /// has switched to another file in the meantime, so findings for file A can
        /// never land in file B's sidecar. Must be called under <c>_lock</c>.
        /// </summary>
        private bool BelongsToCurrentFile(string? expectedFile)
        {
            if (string.IsNullOrEmpty(expectedFile)) return true; // caller didn't pin a file
            if (string.Equals(expectedFile, _filePath, StringComparison.OrdinalIgnoreCase)) return true;
            Console.WriteLine($"[QC] discarded results for '{expectedFile}' — the open file is now '{_filePath}'.");
            return false;
        }

        /// <summary>
        /// Merge a second wave of proposals (the AI layer) into an existing run.
        /// An AI verdict supersedes the deterministic one for the same row: it saw
        /// the same problem plus the meaning, so its rewrite is the better offer.
        /// Rows the user already dismissed stay dismissed.
        /// </summary>
        public void MergeProposals(IEnumerable<QcProposal> proposals, string? expectedFile = null,
            int? checkedRows = null)
        {
            lock (_lock)
            {
                if (!BelongsToCurrentFile(expectedFile)) return;
                // The panel's AI run is a run: it dates the review and says how
                // many rows were looked at. MCP verdicts merge without that.
                if (checkedRows is int n)
                {
                    _data.LastRunUtc = DateTime.UtcNow;
                    _data.LastCheckedRows = n;
                }

                foreach (var p in proposals)
                {
                    var id = Id(p.Namespace, p.Key);
                    // Inspect EVERY proposal for this key, not just the first: a Kept
                    // one can sit behind a pending one, and the user's "no" must win.
                    var existing = _data.Proposals.Where(x => Id(x.Namespace, x.Key) == id).ToList();
                    if (existing.Any(x => x.Verdict == QcVerdict.Kept)) continue;

                    // Keep the deterministic findings' labels visible on the merged
                    // suggestion — otherwise the report loses "why" (e.g. LanguageLeak).
                    foreach (var old in existing)
                    {
                        if (old.Verdict == QcVerdict.Pending)
                            foreach (var k in old.Kinds)
                                if (!p.Kinds.Contains(k)) p.Kinds.Add(k);
                        _data.Proposals.Remove(old);
                    }
                    _data.Proposals.Add(p);
                }
                SaveNowLocked();
            }
        }

        /// <summary>
        /// The fix was applied but the row still carries findings it did not
        /// repair — turn the proposal into a flag-only one against the NEW text
        /// instead of closing it. Returns false when nothing is left to report.
        /// </summary>
        public bool DemoteToResidual(QcProposal proposal, string newTarget)
        {
            if (proposal.UnresolvedKinds.Count == 0) return false;
            lock (_lock)
            {
                proposal.Before = newTarget;
                proposal.After = newTarget;            // flag only: no replacement left to offer
                // The accepted fix now lives in Target, wherever the review read
                // the text from — so measure staleness there from here on.
                proposal.ResultInSource = false;
                proposal.Reason = proposal.UnresolvedReason;
                proposal.Kinds = new List<string>(proposal.UnresolvedKinds);
                proposal.Severity = proposal.UnresolvedSeverity;
                proposal.UnresolvedKinds = new List<string>();
                proposal.UnresolvedReason = "";
                proposal.Verdict = QcVerdict.Pending;
                SaveNowLocked();
            }
            return true;
        }

        /// <summary>Record the user's decision for one row's proposal.</summary>
        public void SetVerdict(QcProposal proposal, QcVerdict verdict)
        {
            lock (_lock)
            {
                proposal.Verdict = verdict;
                SaveNowLocked();
            }
        }

        /// <summary>Drop every proposal for the current file (panel's Clear button).</summary>
        public void ClearAll(IEnumerable<TranslationRow>? rows = null)
        {
            lock (_lock)
            {
                _data.Proposals.Clear();
                _data.LastRunUtc = null;
                _data.LastCheckedRows = 0;
                SaveNowLocked();
            }
            if (rows != null) foreach (var r in rows) r.QcProposal = null;
        }

        public void SaveNow() { lock (_lock) SaveNowLocked(); }

        private void SaveNowLocked()
        {
            if (_sidecarPath.Length == 0) return;
            try
            {
                if (_data.Proposals.Count == 0)
                {
                    if (File.Exists(_sidecarPath)) File.Delete(_sidecarPath);
                    return;
                }
                var json = JsonConvert.SerializeObject(_data, Formatting.Indented);
                var tmp = _sidecarPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _sidecarPath, overwrite: true);
                _lastSaveError = "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QC] Save failed: {ex.Message}");
                if (_lastSaveError != ex.Message)
                {
                    _lastSaveError = ex.Message;
                    // Outside the lock would be tidier, but this path is rare and
                    // the handler only appends a log line.
                    SaveFailed?.Invoke(ex.Message);
                }
            }
        }
    }
}
