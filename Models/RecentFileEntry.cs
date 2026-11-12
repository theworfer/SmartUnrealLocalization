using System.IO;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Bound model for an entry in the Welcome view's Recent column.
    /// Always carries the ORIGINAL user-selected path (never a temp copy) so that
    /// re-opening across app restarts re-reads the source file.
    /// </summary>
    public class RecentFileEntry
    {
        /// <summary>Absolute path the user originally picked.</summary>
        public string Path { get; set; } = "";

        /// <summary>Display name — just the file's name (no directory).</summary>
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>Directory portion shown as a hint under the name.</summary>
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

        /// <summary>Optional row-count hint (populated after first open). Not persisted.</summary>
        public int? RowCountHint { get; set; }
    }
}
