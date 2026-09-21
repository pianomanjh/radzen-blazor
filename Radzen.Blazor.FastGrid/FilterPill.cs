using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Radzen.FastGrid
{
    /// <summary>
    /// What a pill says, as a rule about a column and a filter rather than as code inside a bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §37. Separated from the render for §33's review finding - a test one layer above a rule is not a
    /// test of the rule - so <em>"what does an <c>In</c> over four lookup ids read as"</em> is asked of
    /// this and not of a rendered band.
    /// </para>
    /// <para>
    /// <strong>The grid is passed in because the grid is the string table.</strong> Every word here is
    /// one it already owns: <c>OperatorText</c>, <c>PresetText</c> and the three §37 adds. A words
    /// struct beside them would be a second vocabulary in the one place §31 keeps refusing to have two.
    /// </para>
    /// </remarks>
    internal static class FilterPill
    {
        /// <summary>How many values an <c>In</c> names before it becomes a count.</summary>
        /// <remarks>
        /// A pill is a label, not a list: three values is something a reader recognises and four is
        /// something they parse, and the bar holds one of these per filtered column. §37 records the
        /// number as a judgement with no measurement behind it, and declines a parameter for it -
        /// configurability is not the bar §31 set.
        /// </remarks>
        internal const int NamedValues = 3;

        /// <summary>
        /// The phrase for a column's filter: its title, the operator's own word, and its values.
        /// </summary>
        /// <remarks>
        /// <para>
        /// From <c>CurrentFilter</c>, which the caller passes - what the user <em>authored</em>, never
        /// <c>ActiveFilter</c>'s resolution of it. §34 built that split for this: a pill rendering a
        /// relative filter as two absolute dates would be the staleness §34 exists to prevent, wearing
        /// the reader's own handwriting.
        /// </para>
        /// <para>
        /// <strong>The operator's own word, not a second wording of it.</strong> §31 wanted a pill to
        /// read <em>"Hired is between 1 Jan and 31 Mar"</em> and that sentence is not reachable from the
        /// strings §31 also said to use - upstream's vocabulary is <c>Equals</c>, <c>Greater than</c>,
        /// <c>In</c>, and has no <em>is</em> anywhere in it. §37 keeps the rule and drops the example.
        /// </para>
        /// </remarks>
        internal static string Phrase<TItem>(RadzenFastGrid<TItem> grid, ColumnBase<TItem> column,
            FastGridFilter filter)
        {
            var builder = new StringBuilder();

            builder.Append(column.HeaderText);

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            Clause(builder, grid, column, filter, filter.First);

            // The second condition, joined by the word the model joined it with. One pill either way:
            // the x clears a column, and there is no operation that removes half a filter.
            if (filter.EffectiveSecond is { } second)
            {
                builder.Append(' ')
                    .Append(filter.LogicalFilterOperator == LogicalFilterOperator.Or
                        ? grid.OrFilterText
                        : grid.AndFilterText)
                    .Append(' ');

                Clause(builder, grid, column, filter, second);
            }

            return builder.ToString();
        }

        /// <summary>One condition, as its operator's word followed by whatever that operator takes.</summary>
        /// <param name="builder">Where the clause is written.</param>
        /// <param name="grid">The string table.</param>
        /// <param name="column">The column whose values are being named.</param>
        /// <param name="filter">The whole filter, which is the only thing a preset can be a shape of.</param>
        /// <param name="condition">The condition to say.</param>
        /// <remarks>
        /// The first draft passed <c>null</c> here for the second condition, to stop a preset-shaped
        /// half being read as a preset - and the mutation loop showed that write unobservable, because
        /// the rule is already enforced where it belongs:
        /// <see cref="FastGridFilterPresets.Recognize" /> matches only a filter whose
        /// <see cref="FastGridFilter.Second" /> is null, so a filter with two conditions is declined
        /// whichever clause asks. §33's finding, from the other end - the guard was written one layer
        /// below the rule that already held.
        /// </remarks>
        static void Clause<TItem>(StringBuilder builder, RadzenFastGrid<TItem> grid,
            ColumnBase<TItem> column, FastGridFilter filter, FastGridFilterCondition condition)
        {
            // Before the operator and not beside it: a preset replaces the whole clause. "Last 7 days"
            // is what the reader picked, and "Between today-6d and today@end" is the same range spelled
            // as the model rather than as the menu.
            //
            // §35 warned that reading a hand-authored range as a preset stops being harmless "the day
            // something writes differently depending on which it thinks it is", and named the pills as
            // that day. They are not: this chooses a word. The x clears the column whichever word was
            // chosen, and the body reopens the menu, which reseeds from CurrentFilter and cannot see
            // what was printed here.
            if (FastGridFilterPresets.Recognize(filter) is { } preset)
            {
                builder.Append(grid.PresetText(preset));

                return;
            }

            builder.Append(grid.OperatorText(condition.Operator));

            switch (condition.Operator.Arity())
            {
                case FastGridFilterArity.None:
                    return;

                case FastGridFilterArity.Two:
                    builder.Append(' ').Append(column.FilterValueTextOf(condition.Value))
                        .Append(' ').Append(grid.RangeFilterText)
                        .Append(' ').Append(column.FilterValueTextOf(condition.SecondValue));

                    return;

                case FastGridFilterArity.Many:
                    builder.Append(' ');
                    Listed(builder, grid, column, condition);

                    return;

                default:
                    builder.Append(' ').Append(column.FilterValueTextOf(condition.Value));

                    return;
            }
        }

        /// <summary>
        /// A set's values, named up to <see cref="NamedValues" /> and counted past it.
        /// </summary>
        /// <remarks>
        /// The sequence is walked once and no list is built from it: a check-box selection arrives as
        /// one object holding many ids, and the count only matters relative to a constant, so the walk
        /// stops naming and starts counting rather than materialising to ask how long it is. §3's third
        /// rule, on a path that runs per filtered column per render.
        /// </remarks>
        static void Listed<TItem>(StringBuilder builder, RadzenFastGrid<TItem> grid,
            ColumnBase<TItem> column, FastGridFilterCondition condition)
        {
            // In carries its list as its single value, the way FilterValueText.From reads it - and a
            // string is a sequence this must never walk one character at a time.
            if (condition.Value is not IEnumerable sequence || condition.Value is string)
            {
                builder.Append(column.FilterValueTextOf(condition.Value));

                return;
            }

            var named = 0;
            var extra = 0;

            foreach (var value in sequence)
            {
                if (named == NamedValues)
                {
                    extra++;

                    continue;
                }

                if (named > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(column.FilterValueTextOf(value));
                named++;
            }

            if (extra > 0)
            {
                builder.Append(' ').Append(string.Format(grid.UICulture, grid.MoreFilterText, extra));
            }
        }
    }
}
