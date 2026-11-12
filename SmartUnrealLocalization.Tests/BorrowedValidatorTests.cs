using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Validators;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The checks only QC's local pass used to run now run live as validators
/// (validator stage 3, 2026-10-21): model chatter, leftover source language,
/// protected terms, the user's terminology rules, lost line structure.
/// </summary>
[Collection("AppState")]
public class BorrowedValidatorTests
{
    private static List<Problem> Run(IValidator v, string source, string target)
    {
        var found = new List<Problem>();
        v.Validate(new TranslationRow { Namespace = "UI", Key = "K", Source = source, Target = target }, found);
        return found;
    }

    [Fact]
    public void Model_chatter_is_an_error_with_a_fix_that_removes_it()
    {
        var p = Assert.Single(Run(new ModelChatterValidator(), "Hello there", "Translation: Привіт"));
        Assert.Equal(ProblemSeverity.Error, p.Severity);
        Assert.Equal("Привіт", p.Fix!.NewValue);
        Assert.Empty(Run(new ModelChatterValidator(), "Hello there", "Привіт"));
    }

    [Fact]
    public void A_target_that_is_only_chatter_is_flagged_without_an_empty_fix()
    {
        var p = Assert.Single(Run(new ModelChatterValidator(), "Hello", "Translation:"));
        Assert.Null(p.Fix);
    }

    [Fact]
    public void Leftover_language_needs_the_files_language_and_is_silent_without_it()
    {
        var v = ValidationService.Instance;
        string was = v.TargetLanguage;
        try
        {
            v.TargetLanguage = "";
            Assert.Empty(Run(new LanguageLeakValidator(), "We go", "Мы йдемо"));
            v.TargetLanguage = "Ukrainian";
            var p = Assert.Single(Run(new LanguageLeakValidator(), "We go", "Мы йдемо"));
            Assert.Equal(ProblemSeverity.Warning, p.Severity);
            Assert.Empty(Run(new LanguageLeakValidator(), "We go", "Ми йдемо"));
        }
        finally { v.TargetLanguage = was; }
    }

    [Fact]
    public void Lost_line_structure_is_a_warning_but_a_reflow_is_not()
    {
        Assert.Single(Run(new LineBreakValidator(), "Line one\nLine two", "Рядок один рядок два"));
        Assert.Empty(Run(new LineBreakValidator(), "One\nTwo\nThree", "Один\nДва три"));   // reflow: count differs only
    }

    [Fact]
    public void Protected_terms_and_terminology_rules_come_from_the_settings()
    {
        var cfg = AppConfig.Instance;
        var terms = cfg.ProtectedTerms.ToList();
        var rules = cfg.QcTermFixes.ToList();
        try
        {
            cfg.ProtectedTerms = new List<string> { "Dragonwilds" };
            cfg.QcTermFixes = new List<string> { "пролом=пробоїна" };

            Assert.Single(Run(new ProtectedTermValidator(), "Welcome to Dragonwilds", "Ласкаво просимо"));
            Assert.Empty(Run(new ProtectedTermValidator(), "Welcome to Dragonwilds", "Ласкаво просимо до Dragonwilds"));

            var t = Assert.Single(Run(new TerminologyValidator(), "A breach", "Пролом у стіні"));
            Assert.Equal("Пробоїна у стіні", t.Fix!.NewValue);
        }
        finally { cfg.ProtectedTerms = terms; cfg.QcTermFixes = rules; }
    }

    [Fact]
    public void The_borrowed_checks_run_in_the_live_pipeline()
    {
        var kinds = ValidationService.Instance.LightValidators.Select(v => v.Kind).ToHashSet();
        foreach (var k in new[] { "ModelChatter", "LineBreakMismatch", "LanguageLeak", "ProtectedTerm", "Terminology" })
            Assert.Contains(k, kinds);
    }
}
