using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Models;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The QC panel is an AI review over every row in scope (2026-10-21): the
/// local Run and its risk bucket are gone, and five switches decide what is
/// kept out — each showing how many rows it skips, so the cost is visible
/// before anything is sent.
/// </summary>
[Collection("AppState")]
public class QcPanelUiTests
{
    private static async Task<QcPanel> OpenPanelAsync(Window window, ViewModels.MainWindowViewModel vm)
    {
        await vm.OpenFileAsync(MakeLocres("qc.locres", 20, i => $"Line number {i}"));
        await Settle();
        foreach (var r in vm.AllRows) r.Target = "Рядок " + r.Key;
        foreach (var r in vm.AllRows.Take(5)) r.Review = ReviewStatus.Approved;

        var panel = window.FindControl<QcPanel>("QcPanelHost")!;
        panel.Initialize(vm);
        vm.ShowQcPanel = true;
        await Settle();
        return panel;
    }

    private static string Text(QcPanel panel, string name) => panel.FindControl<TextBlock>(name)!.Text ?? "";

    [Fact]
    public Task The_local_run_is_gone_and_the_counts_show_what_will_be_sent()
        => WithWindowAsync(async (window, vm) =>
        {
            var panel = await OpenPanelAsync(window, vm);

            Assert.Null(panel.FindControl<Button>("BtnRun"));
            Assert.NotNull(panel.FindControl<Button>("BtnRunAi"));
            Assert.Equal("−5", Text(panel, "LblSkipApproved"));
            Assert.Contains("15", Text(panel, "LblRowsTotal"));
        });

    [Fact]
    public Task A_switch_changes_the_count_and_is_saved_as_a_setting()
        => WithWindowAsync(async (window, vm) =>
        {
            var panel = await OpenPanelAsync(window, vm);
            var approved = panel.FindControl<CheckBox>("ChkSkipApproved")!;
            bool was = AppConfig.Instance.QcSkipApproved;
            try
            {
                approved.IsChecked = false;
                approved.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();

                Assert.False(AppConfig.Instance.QcSkipApproved);
                Assert.Equal("", Text(panel, "LblSkipApproved"));
                Assert.Contains("20", Text(panel, "LblRowsTotal"));
            }
            finally { AppConfig.Instance.QcSkipApproved = was; }
        });

    [Fact]
    public Task With_checked_rows_as_the_scope_the_count_follows_the_selection()
        => WithWindowAsync(async (window, vm) =>
        {
            var panel = await OpenPanelAsync(window, vm);
            foreach (var r in vm.AllRows.Skip(10).Take(3)) r.IsSelected = true;
            var selected = panel.FindControl<RadioButton>("RbCheckSelected")!;
            selected.IsChecked = true;
            selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.Contains(": 3 ", Text(panel, "LblRowsTotal"));

            vm.AllRows[12].IsSelected = false;
            vm.UpdateSelectionCount();               // what the grid's checkbox does
            await Settle();
            Assert.Contains(": 2 ", Text(panel, "LblRowsTotal"));
        });

    [Fact]
    public Task The_count_line_follows_a_language_switch()
        => WithWindowAsync(async (window, vm) =>
        {
            var panel = await OpenPanelAsync(window, vm);
            string original = Localization.Loc.Instance.CurrentLanguage;
            try
            {
                Localization.Loc.Instance.Load("de");
                await Settle();
                Assert.Equal(string.Format(Localization.Loc.T("qc.ai.selected"), 15, 1), Text(panel, "LblRowsTotal"));
            }
            finally { Localization.Loc.Instance.Load(original); }
        });

    [Fact]
    public Task A_single_language_file_without_its_original_asks_for_it_instead_of_counting_everything_as_identical()
        => WithWindowAsync(async (window, vm) =>
        {
            // A translated .locres: the text sits in Source, Target is empty.
            await vm.OpenFileAsync(MakeLocres("only.locres", 20, i => $"Рядок {i}"));
            await Settle();
            var panel = window.FindControl<QcPanel>("QcPanelHost")!;
            panel.Initialize(vm);
            vm.ShowQcPanel = true;
            await Settle();

            Assert.Equal(Localization.Loc.T("qc.err.needOriginal"), Text(panel, "LblRowsTotal"));
            Assert.Equal("", Text(panel, "LblSkipIdentical"));
        });

    [Fact]
    public Task Mcp_get_qc_rows_counts_like_the_panel_and_refuses_a_single_language_file()
        => WithWindowAsync(async (window, vm) =>
        {
            await OpenPanelAsync(window, vm);
            var json = Services.Mcp.McpGuiServer.GetQcRows(vm, selectedOnly: false, offset: 0, count: 3);
            Assert.Equal(15, (int)json["to_review"]!);
            Assert.Equal(5, (int)json["skipped"]!["approved"]!);
            Assert.Equal(3, ((Newtonsoft.Json.Linq.JArray)json["rows"]!).Count);

            await vm.OpenFileAsync(MakeLocres("one.locres", 5, i => $"Рядок {i}"));
            await Settle();
            var ex = Assert.Throws<InvalidOperationException>(() => Services.Mcp.McpGuiServer.GetQcRows(vm, false, 0, 0));
            Assert.Contains("one language", ex.Message);
        });

    [Fact]
    public Task The_review_of_a_qc_fix_shows_the_text_that_was_reviewed()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("single.locres", 3, i => $"Рядок {i}"));
            await Settle();
            var row = vm.AllRows[0];
            var p = new QcProposal
            {
                Namespace = row.Namespace ?? "", Key = row.Key ?? "", Before = row.Source!, After = "Рядок нуль",
                ResultInSource = true, Kinds = new List<string> { "AiQc" }, Source = "model",
            };
            Services.Qc.QcService.Instance.MergeProposals(new[] { p });
            vm.RefreshQcState();

            // Stand in for the dialog: capture what it would show, keep everything.
            IReadOnlyList<Services.PlannedFix>? shown = null;
            var field = typeof(ViewModels.MainWindowViewModel).GetField("FixReviewRequested",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var saved = field.GetValue(vm);
            field.SetValue(vm, (Func<IReadOnlyList<Services.PlannedFix>, int, Task<List<Services.PlannedFix>?>>)
                ((fixes, _) => { shown = fixes; return Task.FromResult<List<Services.PlannedFix>?>(fixes.ToList()); }));
            try
            {
                vm.QcFixAllCommand.Execute(null);
                await Settle();
            }
            finally { field.SetValue(vm, saved); }

            var f = Assert.Single(shown!);
            Assert.Equal(row.Source, f.Before);        // not the empty Target cell
            Assert.Equal("Рядок нуль", row.Target);
        });
}
