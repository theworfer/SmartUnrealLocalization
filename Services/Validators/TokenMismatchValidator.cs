using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Detects <c>{Token}</c> placeholders that appear in Source but not in
    /// Target — typically because the translator translated the token name
    /// itself (e.g. <c>{gold}</c> → <c>{золото}</c>) which breaks Unreal's
    /// string-format substitution.
    ///
    /// <para>
    /// Severity = <c>Error</c>: Unreal won't substitute a renamed token at
    /// runtime; the player sees the literal placeholder. Matches the plan's
    /// "TokenMismatch (Severity Error)" line.
    /// </para>
    ///
    /// <para>
    /// Fix uses <see cref="TagFixHelper"/>: extras (Target tokens absent
    /// from Source — typos like <c>{cont}</c> for <c>{count}</c>) are
    /// REPLACED IN PLACE by the missing tokens, paired positionally.
    /// Leftover missing tokens (when there's no broken counterpart) are
    /// appended at the end. Earlier append-only behaviour produced
    /// <c>{cont} … {count}</c> instead of just <c>{count}</c>.
    /// </para>
    /// </summary>
    public sealed class TokenMismatchValidator : IValidator
    {
        public string Kind => "TokenMismatch";

        // Curly-brace tokens only — markup tags are handled by TagMismatchValidator
        // and printf placeholders by PlaceholderMismatchValidator.
        private static readonly Regex Re = LocalizationRules.BraceToken;

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            var tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;

            var result = TagFixHelper.Compute(src, tgt, Re);
            if (result.MissingTags.Count == 0 && result.ExtraMatches.Count == 0) return;

            // A token the Source does not have — a duplicated {0}, or a name the
            // translator invented — makes Unreal substitute twice or print the
            // literal placeholder. Previously reported by nothing, because the
            // check returned early unless something was MISSING.
            if (result.MissingTags.Count == 0)
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Error,
                    Title = string.Format(Loc.T("val.token.unexpected.title"), string.Join(", ", result.ExtraMatches.Select(m => m.Value).Distinct())),
                    Detail = Loc.T("val.token.unexpected.detail"),
                    Range = result.FirstExtraRange,
                });
                return;
            }

            string title = result.DidReplace
                ? string.Format(Loc.T("val.token.broken.title"), string.Join(", ", result.MissingTags))
                : string.Format(Loc.T("val.token.missing.title"), string.Join(", ", result.MissingTags));

            string detail = result.DidReplace
                ? string.Format(Loc.T("val.token.broken.detail"), result.ExtraMatches.Count, result.MissingTags.Count)
                : string.Format(Loc.T("val.token.missing.detail"), result.MissingTags.Count);

            string fixLabel = result.DidReplace
                ? Loc.T("val.token.fix.replace")
                : Loc.T("val.token.fix.append");

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Error,
                Title = title,
                Detail = detail,
                Range = result.FirstExtraRange,
                Fix = new Fix
                {
                    Label = fixLabel,
                    OldValue = tgt,
                    NewValue = result.FixedTarget,
                },
            });
        }
    }
}
