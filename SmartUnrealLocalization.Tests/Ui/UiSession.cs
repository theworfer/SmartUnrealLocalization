using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Views;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// One headless Avalonia app per test process, on its own UI thread, running
/// the REAL <see cref="App"/>, styles, themes and <see cref="MainWindow"/>.
///
/// <para>The tests that use it reproduce things only a real layout system can:
/// the 2026-09-01 QC-list crash, header/status-bar layout at the minimum width,
/// input gestures in the editor, saves racing edits. Until 2026-10-20 those
/// lived in a render harness outside the repository,
/// where nothing ran them by default.</para>
///
/// <para><b>Never the user's profile.</b> Before Avalonia starts, every per-user
/// path is pointed at a temporary sandbox and the configuration is reloaded
/// from it — defaults, with auto-save and the MCP server off, so a test can
/// neither write the real config nor open the pipe an MCP client dials.</para>
/// </summary>
internal static class UiSession
{
    public static readonly string Sandbox =
        Path.Combine(Path.GetTempPath(), "sul-ui-tests", Environment.ProcessId.ToString());

    private static readonly Lazy<Task> _started = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Task Start()
    {
        UseSandbox();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .With(Utils.Glyphs.MonochromeFallback)
                    .SetupWithoutStarting();
                ThemeManager.Apply(dark: true);
                Loc.Instance.Load("en");
                ThemeManager.ApplyFonts();
                ready.SetResult();
            }
            catch (Exception ex) { ready.SetException(ex); return; }
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        { IsBackground = true, Name = "Avalonia UI thread (tests)" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    /// <summary>Point every per-user file at the sandbox. Other tests in the
    /// shared collection reset the override, so each UI test calls this again.</summary>
    public static void UseSandbox()
    {
        Directory.CreateDirectory(Sandbox);
        AppConfig.ConfigDirOverride = Sandbox;
        var cfg = AppConfig.Instance;
        if (!string.Equals(cfg.AutoSavePath, Path.Combine(Sandbox, "autosave"), StringComparison.OrdinalIgnoreCase))
        {
            AppConfig.Instance.Reload();
            cfg = AppConfig.Instance;
        }
        cfg.AutoSaveEnabled = false;
        cfg.McpServerEnabled = false;
        cfg.AutoSavePath = Path.Combine(Sandbox, "autosave");
        cfg.ExportPath = Path.Combine(Sandbox, "export");
        cfg.RecentFiles.Clear();
        cfg.Language = "en";
    }

    /// <summary>Run <paramref name="body"/> on the UI thread with a fresh window.</summary>
    public static async Task WithWindowAsync(Func<MainWindow, MainWindowViewModel, Task> body,
                                             double width = 1280, double height = 800)
    {
        await _started.Value;
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            UseSandbox();
            Loc.Instance.Load("en");
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm, Width = width, Height = height };
            window.Show();
            await Settle();
            try { await body(window, vm); }
            finally
            {
                window.Close();
                vm.Cleanup();
                await Settle();
            }
        });
    }

    /// <summary>Let layout, render and posted work run to quiescence.</summary>
    public static async Task Settle()
    {
        for (int i = 0; i < 4; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// A real .locres (format v0, the plain legacy layout) with <paramref name="count"/>
    /// rows in one namespace — built byte by byte here, so these tests need no
    /// game files. Keys are hex-like, sources sentence-like.
    /// </summary>
    public static string MakeLocres(string name, int count, Func<int, string>? source = null)
    {
        string dir = Path.Combine(Sandbox, "files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(1);                       // namespaces
        Wide(w, "Game");
        w.Write(count);
        for (int i = 0; i < count; i++)
        {
            Str(w, $"K{i:X6}");
            w.Write((uint)i);             // source string hash: pass-through
            Str(w, source?.Invoke(i) ?? $"The line number {i} of the test file.");
        }
        return path;

        static void Str(BinaryWriter w, string s)
        {
            w.Write(s.Length + 1);
            foreach (char c in s) w.Write((byte)c);
            w.Write((byte)0);
        }
        static void Wide(BinaryWriter w, string s)
        {
            w.Write(-(s.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(s));
            w.Write((ushort)0);
        }
    }
}
