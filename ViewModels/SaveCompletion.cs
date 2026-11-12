namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>
    /// Which unsaved-changes flag a FINISHED save may clear.
    ///
    /// <para>A .locres save snapshots the rows on the UI thread, writes on a
    /// background thread, and reports back later. It used to clear the live
    /// flag unconditionally, so two ordinary interleavings lost work without a
    /// warning (audit 2026-09 §5): an edit made while the file was being written
    /// was marked saved, and so was a different tab the user had switched to —
    /// either way closing it no longer asked. The answer now depends on the tab
    /// the snapshot came from and its edit revision at that moment.</para>
    /// </summary>
    internal static class SaveCompletion
    {
        internal enum Clear { Nothing, LiveFlag, SessionFlag }

        /// <param name="savedTab">The tab the snapshot was taken from (null when the app has no tabs).</param>
        /// <param name="activeTab">The tab active now.</param>
        /// <param name="savedRevision">The edit revision captured with the snapshot.</param>
        /// <param name="liveRevision">The active tab's edit revision now.</param>
        /// <param name="savedTabRevision">The saved tab's stored revision, if it is no longer active.</param>
        internal static Clear Decide(object? savedTab, object? activeTab, long savedRevision,
                                     long liveRevision, long savedTabRevision)
        {
            if (ReferenceEquals(savedTab, activeTab))
                return liveRevision == savedRevision ? Clear.LiveFlag : Clear.Nothing;
            if (savedTab is null) return Clear.Nothing;
            return savedTabRevision == savedRevision ? Clear.SessionFlag : Clear.Nothing;
        }
    }
}
