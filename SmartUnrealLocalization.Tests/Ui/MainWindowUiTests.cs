using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Views;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The real MainWindow, headless — the checks that used to need the render
/// harness outside the repo. Each test says which reported defect it pins.
/// Shares the "AppState" collection: the window drives the same singletons
/// (config, sidecar services) as the tests there.
/// </summary>
[Collection("AppState")]
public class MainWindowUiTests
{
    // ── the 2026-09-01 crash ────────────────────────────────────────────

    private static void SeedQcCategories(MainWindowViewModel vm)
    {
        string[] kinds = { "TagMismatch", "Punctuation", "LengthExcessive" };
        QcSeverity[] sev = { QcSeverity.Critical, QcSeverity.Medium, QcSeverity.Low };
        var rows = vm.AllRows;
        for (int i = 0; i < 900; i++)
        {
            var r = rows[i * 3];
            r.Target = "translated " + i;
            r.QcProposal = new QcProposal
            {
                Namespace = r.Namespace ?? "", Key = r.Key, Before = r.Target, After = r.Target + ".",
                Kinds = new List<string> { kinds[i % 3] }, Severity = sev[i % 3], Reason = "test",
            };
        }
        vm.ShowLogs = true;
        vm.ActiveBottomTab = BottomTab.Qc;
        vm.RebuildQcGroups();
    }

    /// <summary>
    /// Selecting a grid row whose QC suggestion sits in a COLLAPSED category
    /// ABOVE the visible part of the QC list killed the process ("Invalid
    /// Arrange rectangle") — through Find Next, a click, or Enter after an edit.
    /// See Utils/ListReveal. Before the fix this test died with that exception.
    /// </summary>
    /// <summary>
    /// The window extends its client area over the title bar, and the platform
    /// draws the window's Title and Icon there — on top of the app's own header,
    /// covering the logo and the menu (seen in 1.0 prep, 2026-11-04). The taskbar
    /// icon comes from the exe's ApplicationIcon, so neither is needed here.
    /// </summary>
    [Fact]
    public Task The_window_draws_no_platform_title_or_icon_over_its_own_header()
        => WithWindowAsync(async (window, vm) =>
        {
            await Settle();
            Assert.True(string.IsNullOrEmpty(window.Title), $"Title must stay empty, was \"{window.Title}\"");
            Assert.Null(window.Icon);
        });

    [Fact]
    public Task Revealing_a_row_from_a_collapsed_category_above_the_view_does_not_crash()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("qc.locres", 3000));
            await Settle();
            SeedQcCategories(vm);
            await Settle();

            var qc = window.FindControl<ListBox>("QcListBox")!;
            var grid = window.FindControl<DataGrid>("MainDataGrid")!;
            vm.ToggleQcGroup(vm.QcGroups[^1]);
            await Settle();
            qc.ScrollIntoView(vm.QcRows[^1]);   // the test's own setup, not the code under test
            await Settle();

            var target = vm.QcGroups[0].Items[150].Row;
            grid.SelectedItem = target;
            await Settle();

            Assert.True(vm.QcGroups[0].IsExpanded);
            Assert.Same(target, (qc.SelectedItem as ProblemListItem)?.Row);
        });

    [Fact]
    public Task The_QC_list_survives_random_expand_select_scroll_and_rebuild()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("qcstress.locres", 3000));
            await Settle();
            SeedQcCategories(vm);
            await Settle();

            var qc = window.FindControl<ListBox>("QcListBox")!;
            var grid = window.FindControl<DataGrid>("MainDataGrid")!;
            var withProposal = vm.AllRows.Where(r => r.QcProposal != null).ToList();
            var rnd = new Random(12345);
            for (int i = 0; i < 60; i++)   // ~0.3 s per settled step on the software rasterizer
            {
                switch (rnd.Next(6))
                {
                    case 0: vm.ToggleQcGroup(vm.QcGroups[rnd.Next(vm.QcGroups.Count)]); break;
                    case 1: case 2: grid.SelectedItem = withProposal[rnd.Next(withProposal.Count)]; break;
                    case 3: if (vm.QcRows.Count > 0) qc.ScrollIntoView(vm.QcRows[rnd.Next(vm.QcRows.Count)]); break;
                    case 4: vm.RebuildQcGroups(); break;
                    case 5:   // two selections in one turn — holding Find Next
                        grid.SelectedItem = withProposal[rnd.Next(withProposal.Count)];
                        grid.SelectedItem = withProposal[rnd.Next(withProposal.Count)];
                        break;
                }
                if (rnd.Next(3) > 0) await Settle();
            }
            await Settle();
        });

    // ── the editor ──────────────────────────────────────────────────────

    /// <summary>
    /// A double-click inside the open editor — to select a word — bubbled up to
    /// the cell's "start editing" gesture, which re-seeded the editor from
    /// Target: everything typed since the edit began vanished (2026-10-20).
    /// </summary>
    [Fact]
    public Task Double_clicking_a_word_in_the_editor_keeps_what_was_typed()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("edit.locres", 50));
            await Settle();
            var row = vm.AllRows[0];
            typeof(MainWindow).GetMethod("StartEditingTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, new object[] { row });
            await Settle();
            row.EditValue = "typed but not committed";
            await Settle();

            var editor = window.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.Classes.Contains("gridEditor") && t.IsEffectivelyVisible);
            var center = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            for (int click = 0; click < 2; click++)
            {
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
            }
            await Settle();

            Assert.True(row.IsEditing);
            Assert.Equal("typed but not committed", row.EditValue);
        });

    // ── layout at the minimum width ─────────────────────────────────────

    [Theory]
    [InlineData("de")]
    [InlineData("ja")]
    public Task At_the_minimum_width_the_language_and_theme_buttons_stay_clear_of_the_caption_buttons(string lang)
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("a-file-with-a-rather-long-name-for-the-pill.locres", 50));
            Localization.Loc.Instance.Load(lang);
            await Settle();

            double w = window.Bounds.Width;
            var lang_ = (Control)window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "BtnLanguage").GetVisualParent()!;
            var p = lang_.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(lang_.IsEffectivelyVisible);
            Assert.True(p.X >= 0 && p.X + lang_.Bounds.Width <= w - 140 + 0.5,
                $"language+theme at {p.X:0}..{p.X + lang_.Bounds.Width:0}, must end by {w - 140:0} (the OS caption buttons)");

            // No two visible texts in the status bar overlap, and none leaves the window.
            var status = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("statusBar"));
            var texts = status.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0)
                .Select(t => new Rect(t.TranslatePoint(new Point(0, 0), window)!.Value, t.Bounds.Size))
                .ToList();
            foreach (var r in texts) Assert.True(r.X >= -0.5 && r.Right <= w + 0.5, $"status text at {r.X:0}..{r.Right:0} leaves the window");
            for (int i = 0; i < texts.Count; i++)
                for (int j = i + 1; j < texts.Count; j++)
                    Assert.False(texts[i].Intersects(texts[j]) && texts[i].Intersect(texts[j]).Width > 0.5,
                        $"status texts overlap at {texts[i]} and {texts[j]}");
        }, width: 900, height: 560);

    // ── saves and tabs ──────────────────────────────────────────────────

    /// <summary>An edit made while a save is being written stays unsaved (audit §5).</summary>
    [Fact]
    public Task An_edit_made_during_a_save_stays_unsaved()
        => WithWindowAsync(async (window, vm) =>
        {
            typeof(MainWindowViewModel).GetField("OpenFolderRequested", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(vm, null);
            await vm.OpenFileAsync(MakeLocres("save.locres", 20000));
            await Settle();
            string dest = Path.Combine(Sandbox, "files", Guid.NewGuid().ToString("N") + ".locres");

            vm.AllRows[0].Target = "first edit";
            var save = Task.Run(() => vm.SaveEditedData(true, dest));
            while (!File.Exists(dest) && !save.IsCompleted) await Task.Delay(1);
            vm.AllRows[1].Target = "edit during the write";
            Assert.True(await save);
            await Settle();
            Assert.True(vm.HasUnsavedChanges);

            Assert.True(await Task.Run(() => vm.SaveEditedData(true, dest)));
            await Settle();
            Assert.False(vm.HasUnsavedChanges);
        });

    /// <summary>Closing a tab loads a never-opened neighbour instead of showing
    /// it empty, and a vanished neighbour never takes other tabs down (audit §8).</summary>
    [Fact]
    public Task Closing_a_tab_loads_a_lazy_neighbour_and_keeps_other_tabs_edits()
        => WithWindowAsync(async (window, vm) =>
        {
            string a = MakeLocres("a.locres", 300), b = MakeLocres("b.locres", 500);
            await vm.OpenFileAsync(a);
            await Settle();
            vm.OpenSessions.Add(new OpenFileSession { Path = b, FileName = "b.locres", IsLoaded = false });
            vm.CloseActiveTab();
            for (int i = 0; i < 100 && vm.RowCount != 500; i++) await Task.Delay(20);
            await Settle();
            Assert.Equal("b.locres", vm.ActiveSession?.FileName);
            Assert.Equal(500, vm.RowCount);

            // A has an unsaved edit; B is active; B's neighbour has vanished.
            await vm.OpenFileAsync(a); await Settle();
            vm.AllRows[0].Target = "UNSAVED EDIT IN A"; await Settle();
            await vm.OpenFileAsync(MakeLocres("c.locres", 100)); await Settle();
            vm.OpenSessions.Add(new OpenFileSession { Path = Path.Combine(Sandbox, "gone.locres"), FileName = "gone.locres", IsLoaded = false });
            int before = vm.OpenSessions.Count;
            vm.CloseActiveTab();
            await Task.Delay(300); await Settle();

            // Only the closed tab went; the view fell back to a loaded tab, and A —
            // wherever it is — still holds its edit and its unsaved flag.
            Assert.Equal(before - 1, vm.OpenSessions.Count);
            Assert.NotNull(vm.ActiveSession);
            Assert.True(vm.ActiveSession!.IsLoaded);
            var tabA = vm.OpenSessions.Single(s => s.FileName == "a.locres");
            Assert.True(tabA.HasUnsavedChanges);
            var rowsA = ReferenceEquals(tabA, vm.ActiveSession) ? vm.AllRows.ToList() : tabA.Rows;
            Assert.Contains(rowsA, r => r.Target == "UNSAVED EDIT IN A");
        });
}
