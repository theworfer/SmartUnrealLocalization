using System.Text.RegularExpressions;
using SmartUnrealLocalization.Utils;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// A busy clipboard must never close the app or eat text (audit 2026-11).
/// On Windows another program can hold the clipboard; Avalonia then throws after
/// about a second, inside an <c>async void</c> handler — which ended the process.
/// Cut also erased the targets after a copy that never happened.
/// </summary>
[Collection("AppState")]
public class ClipboardSafetyTests
{
    [Fact]
    public async Task A_failing_clipboard_operation_reports_false_instead_of_throwing()
    {
        Assert.False(await ClipboardSafe.TryAsync(() => throw new TimeoutException("Timeout opening clipboard.")));
        Assert.True(await ClipboardSafe.TryAsync(() => Task.CompletedTask));
        Assert.False(await ClipboardSafe.TrySetTextAsync(null, "x"));
        Assert.False((await ClipboardSafe.TryGetTextAsync(null)).Ok);
    }

    [Fact]
    public void Nothing_touches_the_clipboard_except_ClipboardSafe()
    {
        string root = TestPaths.RepoRoot();
        var direct = new Regex(@"\.(SetTextAsync|TryGetTextAsync|GetTextAsync|SetDataAsync|SetDataObjectAsync|TryGetDataAsync)\(");
        var offenders = new List<string>();
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services", "Utils" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.EndsWith("ClipboardSafe.cs", StringComparison.Ordinal)) continue;
                string code = Regex.Replace(File.ReadAllText(file), @"//[^\n]*", "");
                code = code.Replace("ClipboardSafe.TrySetTextAsync(", "").Replace("ClipboardSafe.TryGetTextAsync(", "");
                if (direct.IsMatch(code)) offenders.Add(Path.GetFileName(file));
            }
        Assert.True(offenders.Count == 0, "Direct clipboard calls (use Utils/ClipboardSafe): " + string.Join(", ", offenders));
    }

    [Fact]
    public Task Cut_keeps_the_text_when_the_clipboard_refuses_it()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("cut.locres", 4, i => $"Line {i}"));
            await Settle();
            foreach (var r in vm.AllRows) r.Target = "Рядок " + r.Key;
            vm.AllRows[1].IsSelected = true;
            vm.AllRows[2].IsSelected = true;
            vm.UpdateSelectionCount();
            try
            {
                vm.ClipboardWriteOverride = _ => Task.FromResult(false);      // another program holds it
                await vm.CutSelectedTargetCommand.ExecuteAsync(null);
                await Settle();
                Assert.All(new[] { vm.AllRows[1], vm.AllRows[2] }, r => Assert.StartsWith("Рядок", r.Target));

                string? copied = null;
                vm.ClipboardWriteOverride = t => { copied = t; return Task.FromResult(true); };
                await vm.CutSelectedTargetCommand.ExecuteAsync(null);
                await Settle();
                Assert.NotNull(copied);
                Assert.All(new[] { vm.AllRows[1], vm.AllRows[2] }, r => Assert.Equal("", r.Target));
            }
            finally { vm.ClipboardWriteOverride = null; }
        });
}
