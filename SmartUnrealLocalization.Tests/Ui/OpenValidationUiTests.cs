using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Opening a file validates it and says so. Two defects hid that: the progress
/// dialog's normal close after the work finished was read as Cancel, and a cancelled
/// open wipes the problems found — so no file showed a single problem until F8; and
/// a CSV or PO is read in one dispatcher turn, so its "Loaded N rows" log line was
/// a mere detail of "Reading …" and the status bar never moved past "Reading".
/// </summary>
[Collection("AppState")]
public class OpenValidationUiTests
{
    [Fact]
    public Task Problems_found_on_open_stay_on_the_rows_and_the_bar_says_loaded()
    {
        // A CSV with a translation that lost its tags: an error the open must report.
        string path = Path.Combine(Sandbox, "files", Guid.NewGuid().ToString("N"), "open.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "\"Smith.Offer\",\"I can sharpen your <i>blade</i>.\",\"Я можу нагострити твій клинок.\"\n" +
            "\"Menu.Quit\",\"Quit\",\"Вийти\"\n");

        return WithWindowAsync(async (window, vm) =>
        {
            Loc.Instance.Load("en");
            await vm.OpenFileAsync(path);
            await Settle();

            var broken = vm.AllRows.Single(r => r.Key == "Smith.Offer");
            Assert.Contains(broken.Problems, p => p.Severity == ProblemSeverity.Error);
            Assert.Equal(1, vm.CellRecoveredCount);                     // the status-bar error count
            Assert.Empty(vm.AllRows.Single(r => r.Key == "Menu.Quit").Problems);

            Assert.StartsWith("Loaded 2 rows", vm.StatusText);
        });
    }
}
