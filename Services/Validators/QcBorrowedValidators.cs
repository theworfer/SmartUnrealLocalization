using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Checks that only the QC panel's local pass used to run (validator stage 3,
    //  2026-10-21). There is ONE checker: these now run live, on every
    //  edit, through the same validator pipeline as tags and placeholders — and
    //  reuse QcEngine's tested logic rather than a second copy of it.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>"Translation: …", wrapping quotes the source lacks — model output
    /// that leaked into the text. Error: it ships verbatim to players.</summary>
    public sealed class ModelChatterValidator : IValidator
    {
        public string Kind => "ModelChatter";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            string src = row.Source ?? "", tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;
            if (QcEngine.IsOnlyModelChatter(src, tgt))
            {
                output.Add(new Problem
                {
                    Kind = Kind, Severity = ProblemSeverity.Error,
                    Title = Loc.T("val.chatter.only.title"),
                    Detail = Loc.T("val.chatter.only.detail"),
                });
                return;
            }
            if (QcEngine.StripModelChatter(src, tgt) is not { } clean) return;
            output.Add(new Problem
            {
                Kind = Kind, Severity = ProblemSeverity.Error,
                Title = Loc.T("val.chatter.title"),
                Detail = Loc.T("val.chatter.detail"),
                Fix = new Fix { Label = Loc.T("val.chatter.fix"), OldValue = tgt, NewValue = clean },
            });
        }
    }

    /// <summary>Letters or frequent words of another language left in the
    /// translation. Needs the file's language; silent when it is unknown.</summary>
    public sealed class LanguageLeakValidator : IValidator
    {
        public string Kind => "LanguageLeak";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            string tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;
            string lang = ValidationService.Instance.TargetLanguage;
            if (QcEngine.LanguageLeak(tgt, lang, AppConfig.Instance.QcLeakExceptions) is not { } reason) return;
            output.Add(new Problem
            {
                Kind = Kind, Severity = ProblemSeverity.Warning,
                Title = Loc.T("val.leak.title"),
                Detail = string.Format(Loc.T("val.leak.detail"), reason),
            });
        }
    }

    /// <summary>A protected term (Settings → Translation Memory → Protected Terms)
    /// present in the source and missing from the translation.</summary>
    public sealed class ProtectedTermValidator : IValidator
    {
        public string Kind => "ProtectedTerm";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            string tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;
            var terms = AppConfig.Instance.ProtectedTerms;
            if (terms is null || terms.Count == 0) return;
            if (QcEngine.MissingProtectedTerms(row.Source ?? "", tgt, terms) is not { } reason) return;
            output.Add(new Problem
            {
                Kind = Kind, Severity = ProblemSeverity.Warning,
                Title = Loc.T("val.protected.title"),
                Detail = reason + ".",
            });
        }
    }

    /// <summary>The user's own "wrong=right" terminology rules (QC settings),
    /// with the replacement as a fix.</summary>
    public sealed class TerminologyValidator : IValidator
    {
        public string Kind => "Terminology";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            string tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;
            var rules = QcEngine.ParseTermFixes(AppConfig.Instance.QcTermFixes);
            if (QcEngine.ApplyTerminology(tgt, rules) is not { } hit) return;
            output.Add(new Problem
            {
                Kind = Kind, Severity = ProblemSeverity.Warning,
                Title = Loc.T("val.terminology.title"),
                Detail = string.Format(Loc.T("val.terminology.detail"), hit.Reason),
                Fix = new Fix { Label = Loc.T("val.terminology.fix"), OldValue = tgt, NewValue = hit.Fixed },
            });
        }
    }

    /// <summary>Every line break lost, or breaks the source does not have — a
    /// dialogue box or tooltip laid out as one line, or broken up.</summary>
    public sealed class LineBreakValidator : IValidator
    {
        public string Kind => "LineBreakMismatch";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            string tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;
            if (QcEngine.LineBreakProblem(row.Source ?? "", tgt) is not { } reason) return;
            output.Add(new Problem
            {
                Kind = Kind, Severity = ProblemSeverity.Warning,
                Title = Loc.T("val.lineBreak.title"),
                Detail = reason + ".",
            });
        }
    }
}
