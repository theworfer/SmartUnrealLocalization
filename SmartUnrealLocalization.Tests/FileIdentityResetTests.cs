using System.Reflection;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.ViewModels;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A guard against one specific, silent failure: a path that builds a file
/// session forgets to assign a piece of identity metadata, so the PREVIOUS
/// file's value stays behind.
///
/// <para>Nothing throws when that happens. The project pill shows the last
/// file's language; a PO header from the tab you just left rides into a locres
/// session and is used by a later Save As; a QC run pins its results to the
/// wrong file. Every value looks plausible because it is a real value — just
/// the wrong file's. "Merge to new version" hit this four fields at once,
/// because it assembles a session by hand rather than going through the normal
/// load.</para>
///
/// <para>The fix was one canonical <c>ResetFileIdentity()</c> that every such
/// path calls first, so a forgotten field yields a NEUTRAL value instead of the
/// wrong one. This test keeps it complete: <see cref="OpenFileSession"/> is the
/// schema of what belongs to a file, so every identity field on it must be
/// cleared by that method. Add a field there and forget the reset, and this
/// fails — which is the whole point, since the runtime symptom would not.</para>
///
/// <para>It reads the view model's SOURCE because the view model needs Avalonia
/// to instantiate. A blunt instrument, but it catches the exact regression, and
/// a blunt guard beats none for a bug whose symptom is a plausible wrong value.</para>
/// </summary>
public class FileIdentityResetTests
{
    /// <summary>
    /// Session fields that are deliberately NOT part of the identity reset.
    /// Each needs a reason; an unexplained entry here is how the guard rots.
    /// </summary>
    private static readonly Dictionary<string, string> NotIdentity = new()
    {
        ["Path"] = "the caller assigns OriginalFilePath immediately after the reset",
        ["Format"] = "every path assigns CurrentFormat explicitly; there is no neutral file format",
        ["FileName"] = "every path assigns CurrentFileName explicitly",
        ["Rows"] = "row data, replaced wholesale by SetRows",
        ["UndoState"] = "per-tab history, owned by UndoManager",
        ["IsActive"] = "tab-strip state, not file identity",
        ["IsPinned"] = "tab-strip state, not file identity",
        ["IsLoaded"] = "lazy-restore flag, not file identity",
        ["HasUnsavedChanges"] = "clearing it mid-load would drop the dirty flag of the tab being captured",
        ["LastEditRevision"] = "edit state, captured and restored together with HasUnsavedChanges (SaveCompletion)",
        ["DisplayName"] = "computed from FileName",
    };

    /// <summary>Session field → the view-model property that holds the same thing.</summary>
    private static readonly Dictionary<string, string> VmName = new()
    {
        ["PoHeader"] = "CurrentPoHeader",
        ["Document"] = "CurrentLocresDocument",
    };

    private static string ResetBody()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainWindowViewModel.cs"));
        var m = Regex.Match(source, @"private void ResetFileIdentity\(\)\s*\{(.*?)\n        \}",
                            RegexOptions.Singleline);
        Assert.True(m.Success, "ResetFileIdentity() not found — was it renamed? Update this guard with it.");
        return m.Groups[1].Value;
    }

    private static string RepoRoot() => TestPaths.RepoRoot();

    [Fact]
    public void Every_identity_field_of_a_session_is_cleared_by_the_reset()
    {
        string body = ResetBody();
        var missing = new List<string>();

        foreach (var prop in typeof(OpenFileSession).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (NotIdentity.ContainsKey(prop.Name)) continue;

            string vmProp = VmName.GetValueOrDefault(prop.Name, prop.Name);
            if (!Regex.IsMatch(body, $@"\b{Regex.Escape(vmProp)}\s*="))
                missing.Add($"{prop.Name} (expected an assignment to {vmProp})");
        }

        Assert.True(missing.Count == 0,
            "OpenFileSession carries file identity that ResetFileIdentity() does not clear, so a path "
          + "that forgets to assign it will silently keep the PREVIOUS file's value:\n  "
          + string.Join("\n  ", missing)
          + "\n\nEither clear it in ResetFileIdentity(), or add it to NotIdentity with a reason.");
    }

    [Fact]
    public void The_reset_also_clears_the_merge_tints()
    {
        // They describe ONE file too. Left behind, they paint another file's rows
        // green ("new key") and violet ("re-check this translation") — a game's
        // locres files share nearly all their keys, so the overlap is total.
        string body = ResetBody();
        Assert.Contains("_newKeySet", body);
        Assert.Contains("_sourceChangedKeySet", body);
        Assert.Contains("_mergeFlagsForPath", body);
    }

    [Fact]
    public void Every_path_that_builds_a_session_starts_from_the_reset()
    {
        // The reset only helps if it is actually called. These are the three
        // places that assemble a file session; a fourth added later should join
        // them here.
        string vm = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainWindowViewModel.cs"));
        string ops = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainWindowViewModel.FileOps.cs"));

        Assert.Contains("ResetFileIdentity();", vm);        // CloseCurrentFile
        Assert.Equal(2, Regex.Matches(ops, @"ResetFileIdentity\(\);").Count);  // load + merge
    }
}
