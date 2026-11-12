using System;

namespace SmartUnrealLocalization.Services.Mcp
{
    /// <summary>
    /// Shared constants for the MCP bridge: the GUI app hosts a named-pipe
    /// JSON-lines server (<see cref="McpGuiServer"/>); the thin stdio proxy the
    /// chat clients launch (<c>SmartUnrealLocalization.exe --mcp</c>,
    /// <see cref="McpStdioProxy"/>) forwards every tool call over this pipe.
    ///
    /// <para>Wire format, one JSON object per line (UTF-8, no BOM):
    /// request  <c>{"op":"get_status","args":{…}}</c> →
    /// response <c>{"ok":true,"data":…}</c> or <c>{"ok":false,"error":"…"}</c>.</para>
    /// </summary>
    public static class McpPipe
    {
        /// <summary>
        /// Per-user pipe name: named pipes share a machine-wide namespace, so two
        /// Windows users running the app concurrently must not collide.
        /// </summary>
        public static string PipeName => "SmartUnrealLocalization.Mcp." + Environment.UserName;
    }
}
