using Avalonia;
using System;
using System.Linq;

namespace SmartUnrealLocalization;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // "--version": print and exit before the window, the config or the MCP
        // pipe exist — the release script uses it to check a published build starts.
        if (args.Contains("--version"))
        {
            var v = typeof(Program).Assembly.GetName().Version;
            Console.WriteLine($"Smart Unreal Localization {v?.Major}.{v?.Minor}.{Math.Max(0, v?.Build ?? 0)}");
            return;
        }

        // MCP stdio mode: chat clients (Claude Desktop/Code, Codex, Antigravity)
        // launch "SmartUnrealLocalization.exe --mcp" as their MCP server. It runs
        // WITHOUT Avalonia — a thin stdio↔named-pipe proxy to the running window.
        if (args.Contains("--mcp"))
        {
            Environment.Exit(Services.Mcp.McpStdioProxy.RunAsync(args).GetAwaiter().GetResult());
            return;
        }

        // Before anything can fail: a crash leaves a report and rescues unsaved
        // edits instead of vanishing (Services/CrashReporter).
        Services.CrashReporter.Install();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Monochrome marks (✓ ✕ ✗ ⚠ ℹ ⓧ ⓘ) must not be left to the platform's
            // font fallback, which picks Segoe UI Emoji or Segoe UI Symbol
            // depending on what the process loaded first. See Utils/Glyphs.
            .With(Utils.Glyphs.MonochromeFallback)
            .LogToTrace();
}
