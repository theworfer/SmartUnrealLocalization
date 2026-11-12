using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The optional validation notes switch on and off; the errors cannot (2026-10-21).
/// Edge whitespace raised 1 000+ notes per real file that users learned to
/// ignore, burying the findings that matter, so it is OFF by default.
/// </summary>
[Collection("AppState")]
public class ValidationSwitchTests : IDisposable
{
    private readonly ValidationService _v = ValidationService.Instance;
    private readonly AppConfig _cfg = new();

    public ValidationSwitchTests() => _v.Config = () => _cfg;
    public void Dispose() => _v.Config = () => AppConfig.Instance;

    private static TranslationRow Row(string source, string target)
        => new() { Namespace = "UI", Key = Guid.NewGuid().ToString("N"), Source = source, Target = target };

    [Fact]
    public void Edge_whitespace_is_off_by_default()
    {
        Assert.False(new AppConfig().CheckEdgeWhitespace);
        var row = Row("Play ", "Грати");
        _v.RunForRow(row);
        Assert.DoesNotContain(row.Problems, p => p.Kind == "Whitespace");
    }

    [Fact]
    public void Switching_a_note_on_and_off_adds_and_removes_its_findings()
    {
        var row = Row("Play ", "Грати");
        _cfg.CheckEdgeWhitespace = true;
        _v.RevalidateAll(new[] { row });
        Assert.Contains(row.Problems, p => p.Kind == "Whitespace");

        _cfg.CheckEdgeWhitespace = false;
        _v.RevalidateAll(new[] { row });
        Assert.DoesNotContain(row.Problems, p => p.Kind == "Whitespace");
    }

    [Fact]
    public void Errors_cannot_be_switched_off()
    {
        _cfg.CheckEdgeWhitespace = _cfg.CheckEndingPunctuation = _cfg.CheckLengthRatio = _cfg.CheckDuplicateSources = false;
        var row = Row("Hello <b>{name}</b>", "Привіт");
        _v.RevalidateAll(new[] { row });
        Assert.Contains(row.Problems, p => p.Severity == ProblemSeverity.Error);
        foreach (var kind in new[] { "TagMismatch", "TokenMismatch", "PlaceholderMismatch", "PluralIncomplete", "MixedScripts", "ContentMismatch" })
            Assert.True(_v.IsCheckEnabled(kind), kind + " must always run");
    }

    [Fact]
    public void Every_switchable_check_is_a_real_validator()
    {
        var kinds = _v.LightValidators.Select(v => v.Kind).Concat(_v.HeavyValidators.Select(v => v.Kind)).ToHashSet();
        foreach (var k in ValidationService.OptionalChecks.Keys) Assert.Contains(k, kinds);
    }
}
