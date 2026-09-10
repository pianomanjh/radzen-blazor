using System;
using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A relative date range the menu offers as one thing - §31's six, and §35's answer to §34's
    /// warning that a preset is not in the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The presets are the menu's vocabulary and the tokens are the model's.</strong> §34
    /// settled that a token resolves to a single instant, so <em>last 7 days</em> is not one - it is
    /// <c>Between today-6d today@end</c>, a pair. Nothing below reaches the model: a preset is a name
    /// for two <see cref="FastGridRelativeDate" />s, and what it writes is an ordinary
    /// <see cref="FastGridFilterOperator.Between" /> that <c>FilterExpression</c>, the descriptors and
    /// the OData string never learn the word for.
    /// </para>
    /// <para>
    /// <strong>Recognition is by shape.</strong> §34 flagged the alternative - a token that names the
    /// preset - as the thing that would refute it, and §35 refused: a single value supplying two bounds
    /// would make arity a property of the operator <em>and</em> of what sits in its slots, so the name
    /// would have to become a third field on <see cref="FastGridFilter" /> that every reader carries
    /// and ignores. Matching a pair costs the table below and the equality
    /// <see cref="FastGridRelativeDate" /> already implements.
    /// </para>
    /// <para>
    /// A hand-authored <c>Between today-6d today@end</c> therefore displays as <em>last 7 days</em>. It
    /// <em>is</em> that range, resolved at the same instant by the same rule, so the two spellings
    /// agreeing is the correct answer rather than a false positive.
    /// </para>
    /// </remarks>
    public enum FastGridFilterPreset
    {
        /// <summary><c>Between today today@end</c>.</summary>
        Today,

        /// <summary><c>Between today-1d today-1d@end</c>.</summary>
        Yesterday,

        /// <summary><c>Between today-6d today@end</c> - six days back plus today is seven.</summary>
        Last7Days,

        /// <summary><c>Between today-29d today@end</c>.</summary>
        Last30Days,

        /// <summary><c>Between month-start today@end</c>.</summary>
        ThisMonth,

        /// <summary><c>Between year-start today@end</c>.</summary>
        ThisYear,
    }

    /// <summary>The six presets as the token pairs they are, and the recognition that reads them back.</summary>
    public static class FastGridFilterPresets
    {
        /// <summary>Every preset, in the order the menu lists them.</summary>
        /// <remarks>
        /// A held list rather than an <c>Enum.GetValues</c> call, which allocates and reflects; the menu
        /// walks it once per open of a date column. Typed as a read-only list rather than exposed as the
        /// array it is, because a public array is a public setter on every one of its elements - and
        /// this one is what <see cref="Recognize" /> and the menu both read.
        /// </remarks>
        public static IReadOnlyList<FastGridFilterPreset> All => Presets;

        static readonly FastGridFilterPreset[] Presets =
        {
            FastGridFilterPreset.Today,
            FastGridFilterPreset.Yesterday,
            FastGridFilterPreset.Last7Days,
            FastGridFilterPreset.Last30Days,
            FastGridFilterPreset.ThisMonth,
            FastGridFilterPreset.ThisYear,
        };

        static readonly FastGridRelativeDate Today = new(FastGridRelativeDateAnchor.Today);

        static readonly FastGridRelativeDate TodayEnd =
            new(FastGridRelativeDateAnchor.Today, 0, FastGridRelativeDateUnit.Days, endOfDay: true);

        static readonly FastGridRelativeDate Yesterday =
            new(FastGridRelativeDateAnchor.Today, -1, FastGridRelativeDateUnit.Days, endOfDay: false);

        static readonly FastGridRelativeDate YesterdayEnd =
            new(FastGridRelativeDateAnchor.Today, -1, FastGridRelativeDateUnit.Days, endOfDay: true);

        static readonly FastGridRelativeDate SixDaysBack =
            new(FastGridRelativeDateAnchor.Today, -6, FastGridRelativeDateUnit.Days, endOfDay: false);

        static readonly FastGridRelativeDate TwentyNineDaysBack =
            new(FastGridRelativeDateAnchor.Today, -29, FastGridRelativeDateUnit.Days, endOfDay: false);

        static readonly FastGridRelativeDate MonthStart = new(FastGridRelativeDateAnchor.MonthStart);

        static readonly FastGridRelativeDate YearStart = new(FastGridRelativeDateAnchor.YearStart);

        /// <summary>The pair of tokens a preset means. Always a lower bound and an upper one.</summary>
        public static (FastGridRelativeDate Lower, FastGridRelativeDate Upper) Range(
            this FastGridFilterPreset preset) => preset switch
        {
            FastGridFilterPreset.Today => (Today, TodayEnd),
            FastGridFilterPreset.Yesterday => (Yesterday, YesterdayEnd),
            FastGridFilterPreset.Last7Days => (SixDaysBack, TodayEnd),
            FastGridFilterPreset.Last30Days => (TwentyNineDaysBack, TodayEnd),
            FastGridFilterPreset.ThisMonth => (MonthStart, TodayEnd),
            _ => (YearStart, TodayEnd),
        };

        /// <summary>The filter a preset writes: a <c>Between</c> over its two tokens.</summary>
        public static FastGridFilter Filter(this FastGridFilterPreset preset)
        {
            var (lower, upper) = preset.Range();

            return new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.Between, new object?[] { lower, upper }));
        }

        /// <summary>
        /// The preset a filter is, or null where it is not one of them.
        /// </summary>
        /// <remarks>
        /// On the tokens rather than on what they resolve to. <c>Between today-6d today@end</c> is
        /// <em>last 7 days</em> whatever day it is read on; two absolute dates that happen to span the
        /// last seven days today are a range that will not mean that tomorrow, and saying they are the
        /// preset is exactly the silent staleness §34 exists to prevent.
        /// </remarks>
        public static FastGridFilterPreset? Recognize(FastGridFilter? filter)
        {
            if (filter is not { Second: null, First: { Operator: FastGridFilterOperator.Between } first }
                || first.Value is not FastGridRelativeDate lower
                || first.SecondValue is not FastGridRelativeDate upper)
            {
                return null;
            }

            for (var i = 0; i < Presets.Length; i++)
            {
                var (candidateLower, candidateUpper) = Presets[i].Range();

                if (candidateLower.Equals(lower) && candidateUpper.Equals(upper))
                {
                    return Presets[i];
                }
            }

            return null;
        }
    }
}
