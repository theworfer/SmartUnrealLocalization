using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// The program's license, the third-party notices and every full license text
    /// from <c>licenses/</c>, read from resources embedded in the exe (see the csproj).
    /// Help → Licenses shows them; embedding means a copied-out exe still carries them.
    /// </summary>
    public static class LegalTexts
    {
        private const string Prefix = "SmartUnrealLocalization.Legal.";
        internal const string AppLicense = Prefix + "LICENSE";
        internal const string Notices = Prefix + "THIRD-PARTY-NOTICES.txt";
        private const string LicensePrefix = Prefix + "licenses.";

        /// <summary>One embedded text: its resource name and, for a file from
        /// <c>licenses/</c>, its file name (an identifier, shown as is).</summary>
        public sealed record Entry(string Resource, string FileName);

        /// <summary>The files from <c>licenses/</c>, by file name.</summary>
        public static IReadOnlyList<Entry> LicenseFiles() =>
            Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(LicensePrefix, System.StringComparison.Ordinal))
                .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
                .Select(n => new Entry(n, n.Substring(LicensePrefix.Length)))
                .ToList();

        public static string Read(string resource)
        {
            using var stream = Assembly.GetManifestResourceStream(resource);
            if (stream == null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static Assembly Assembly => typeof(LegalTexts).Assembly;
    }
}
