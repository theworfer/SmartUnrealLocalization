using System;
using System.IO;
using System.Text;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Write a whole file or nothing: the bytes go to a temp sibling first and
    /// replace the target only once they are complete.
    ///
    /// <para>Opening the target directly (<c>new StreamWriter(path, false)</c>,
    /// <c>File.WriteAllText</c>) truncates it on open, so a full disk, a crash or a
    /// power cut halfway left a cut-off CSV/PO where the user's previous good file
    /// had been. The temp sibling sits in the same folder, so the final move is a
    /// rename on the same volume.</para>
    /// </summary>
    public static class AtomicFile
    {
        public static void Write(string path, Action<Stream> write)
        {
            string full = Path.GetFullPath(path);
            string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(fs);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, full, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        public static void WriteAllText(string path, string text, Encoding encoding)
            => Write(path, s =>
            {
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0) s.Write(preamble);
                var bytes = encoding.GetBytes(text);
                s.Write(bytes);
            });
    }
}
