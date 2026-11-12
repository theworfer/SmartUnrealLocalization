using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Problems are described in the interface language, and follow a live switch
/// (audit 2026-11). The validators used to write English titles, explanations and
/// fix labels, so the Problems tab and every popover were English in all 15 languages.
/// </summary>
[Collection("AppState")]
public class ProblemLanguageUiTests
{
    [Fact]
    public Task A_problem_found_in_English_is_shown_in_Ukrainian_after_the_switch()
        => WithWindowAsync(async (window, vm) =>
        {
            string original = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Load("en");
                await vm.OpenFileAsync(MakeLocres("lang.locres", 3, i => $"Take the coins, line {i}."));
                await Settle();
                var row = vm.AllRows[0];
                row.Target = "Візьми монети";                     // lost the final full stop
                ValidationService.Instance.RunForRow(row);
                var en = row.Problems.Single(p => p.Kind == "Punctuation");
                Assert.StartsWith("Ending punctuation differs", en.Title);

                Loc.Instance.Load("uk");
                await Settle();
                var uk = row.Problems.Single(p => p.Kind == "Punctuation");
                Assert.StartsWith("Кінцева пунктуація відрізняється", uk.Title);
                Assert.Equal("Пунктуація як в оригіналі", uk.Fix!.Label);
            }
            finally { Loc.Instance.Load(original); }
        });
}
