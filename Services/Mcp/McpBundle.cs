using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// One-click Claude Desktop install: generates a .mcpb bundle (MCP Bundle — a
    /// zip with a manifest.json) that points Claude Desktop at THIS installed exe
    /// with the --mcp argument. Generated on the user's machine so the absolute
    /// path is always correct — nothing is bundled, the app stays where it is.
    /// Double-clicking the file (or dragging it into Claude Desktop → Settings →
    /// Extensions) installs the connection.
    /// </summary>
    public static class McpBundle
    {
        /// <summary>Write the bundle next to the exe and return its full path.</summary>
        public static string Create()
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot resolve the application path.");
            string dir = Path.GetDirectoryName(exe)!;
            string bundlePath = Path.Combine(dir, "SmartUnrealLocalization.mcpb");

            var manifest = new JObject
            {
                // "dxt_version" is the original Desktop-Extensions schema key;
                // "manifest_version" is its MCPB successor. Emit both — clients
                // accept either, and the extra key is ignored by the other parser.
                ["dxt_version"] = "0.1",
                ["manifest_version"] = "0.2",
                ["name"] = "smart-unreal-localization",
                ["display_name"] = "Smart Unreal Localization",
                ["version"] = AppVersion(),
                ["description"] = "Control the Smart Unreal Localization editor: open files, read and translate rows, "
                                + "search/replace, manage settings, run the built-in AI translation, review rows and fix "
                                + "validation problems. The app window must be running with its MCP server enabled.",
                ["author"] = new JObject { ["name"] = "theworfer" },
                ["server"] = new JObject
                {
                    ["type"] = "binary",
                    ["entry_point"] = Path.GetFileName(exe),
                    ["mcp_config"] = new JObject
                    {
                        ["command"] = exe,
                        ["args"] = new JArray("--mcp"),
                    },
                },
            };

            if (File.Exists(bundlePath)) File.Delete(bundlePath); // Create mode refuses to overwrite
            using var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(manifest.ToString(Newtonsoft.Json.Formatting.Indented));
            return bundlePath;
        }

        /// <summary>The csproj &lt;Version&gt;, as major.minor.patch.</summary>
        private static string AppVersion()
        {
            var v = typeof(McpBundle).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }
}
