using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Detects XML-style tags (<c>&lt;Name/&gt;</c>, <c>&lt;Tag&gt;...&lt;/Tag&gt;</c>)
    /// that appear in Source but not in Target — meaning the translator
    /// accidentally dropped a UE markup tag the engine relies on.
    ///
    /// <para>
    /// Severity = <c>Error</c>: a missing tag usually means the game won't
    /// render correctly (Unreal style spans, colour codes, line breaks).
    /// </para>
    ///
    /// <para>
    /// Fix uses <see cref="TagFixHelper"/>: any "broken" tags in Target
    /// (present in Target but absent from Source — typically translator
    /// typos like <c>&lt;Charcter/&gt;</c> instead of <c>&lt;Character/&gt;</c>)
    /// are REPLACED IN PLACE by the missing tags, paired positionally.
    /// Only when there are more missing than broken do the leftovers get
    /// appended at the end. Earlier version always appended, which left
    /// the broken tag next to the restored one — the bug the user
    /// screenshotted.
    /// </para>
    /// </summary>
    public sealed class TagMismatchValidator : IValidator
    {
        public string Kind => "TagMismatch";

        // Markup tags: XML-ish `<b>` plus code-shaped bracket tags `[b]`, `[/i]`,
        // `[color=red]`. Brace tokens `{...}` and printf `%s` are handled by
        // TokenMismatchValidator / PlaceholderMismatchValidator so the three stay
        // disjoint and their Fixes don't overlap.
        //
        // The bracket rule lives in LocalizationRules and is deliberately narrow:
        // a capitalised word in brackets ("Mysterious Orb [Charged]") is TEXT the
        // translator is meant to translate, not markup to preserve.
        private static readonly Regex Re = LocalizationRules.AnyMarkupTags;

        // Unclosed tag-like fragment: a `<` + tag-ish chars (letter or `/`) with NO
        // closing `>` before the next `<` / end. Catches a half-typed `<Tag/` so the
        // fix replaces it in place instead of appending a duplicate beside it.
        private static readonly Regex BrokenRe = new(@"<[/A-Za-z][^<>]*(?![^<>]*>)", RegexOptions.Compiled);

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            var tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return; // Untranslated rows aren't flagged (tracked by the Untranslated counter/filter).

            var result = TagFixHelper.Compute(src, tgt, Re, BrokenRe);
            if (result.MissingTags.Count == 0 && result.ExtraMatches.Count == 0) return;

            // An EXTRA tag with nothing missing used to return silently: the check
            // only ever asked "did the translation drop something?". A translation
            // that INVENTED a tag (a duplicated <b>, a stray [color=red] pasted in)
            // breaks rendering just as thoroughly, and was reported by nothing.
            if (result.MissingTags.Count == 0)
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Error,
                    Title = string.Format(Loc.T("val.tag.unexpected.title"), string.Join(", ", result.ExtraMatches.Select(m => m.Value).Distinct())),
                    Detail = Loc.T("val.tag.unexpected.detail"),
                    Range = result.FirstExtraRange,
                });
                return;
            }

            string title = result.DidReplace
                ? string.Format(Loc.T("val.tag.broken.title"), string.Join(", ", result.MissingTags))
                : string.Format(Loc.T("val.tag.missing.title"), string.Join(", ", result.MissingTags));

            string detail = result.DidReplace
                ? string.Format(Loc.T("val.tag.broken.detail"), result.ExtraMatches.Count, result.MissingTags.Count)
                : string.Format(Loc.T("val.tag.missing.detail"), result.MissingTags.Count);

            string fixLabel = result.DidReplace
                ? Loc.T("val.tag.fix.replace")
                : Loc.T("val.tag.fix.append");

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
