using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// What the menu writes, as a rule about an operator and a type rather than as code inside a panel.
    /// </summary>
    /// <remarks>
    /// §35's *what the menu offers* is <see cref="FastGridFilterOperators.Menu" />; this is *what it
    /// commits*, and they differ on dates. Separated so both can be tested where they live: §33's review
    /// finding is that a test one layer above a rule is not a test of the rule, and "what does picking
    /// Equals on a date column write" is a question that needs no render tree to ask.
    /// </remarks>
    internal static class FilterMenu
    {
        /// <summary>
        /// The filter a picked operator and its values mean, or null where they do not narrow anything.
        /// </summary>
        /// <remarks>
        /// Null rather than an absent filter, so a half-typed range clears the column instead of
        /// filtering it to nothing - the rule <see cref="FastGridFilterCondition.IsPresent" /> already
        /// states, applied at the one place the menu commits.
        /// </remarks>
        internal static FastGridFilter? Compose(FastGridFilterOperator filterOperator, object? value,
            object? secondValue, Type effectiveType)
        {
            ArgumentNullException.ThrowIfNull(effectiveType);

            var condition = FastGridRelativeDate.AppliesTo(effectiveType)
                ? WholeDay(filterOperator, value, secondValue)
                : Literal(filterOperator, value, secondValue);

            return condition.IsPresent ? new FastGridFilter(condition) : null;
        }

        static FastGridFilterCondition Literal(FastGridFilterOperator filterOperator, object? value,
            object? secondValue) => filterOperator.Arity() switch
        {
            FastGridFilterArity.None => new FastGridFilterCondition(filterOperator),
            FastGridFilterArity.Two => new FastGridFilterCondition(filterOperator,
                new[] { value, secondValue }),
            _ => new FastGridFilterCondition(filterOperator, value),
        };

        /// <summary>
        /// A date column's operators, read the way a person reading the menu reads them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §31's table says date <c>Equals</c> means the whole day, and §34 made a value resolve to a
        /// single instant - so <c>Equals</c> at midnight matches midnight exactly and therefore nothing.
        /// §35 reconciles the two by having the menu write the range: this is that, and it is the same
        /// correction applied to the other three operators that name a day and mean a boundary.
        /// </para>
        /// <para>
        /// <strong>§35 argued this for <c>Equals</c> alone and the build found the other three.</strong>
        /// <c>After 5 March</c> at midnight keeps 5 March's afternoon, and a range ending at
        /// <c>31 March</c> at midnight drops 31 March - which is the identical failure, in the identical
        /// direction, to the one §34 spent a section making <c>@end</c> exist for. Leaving three of four
        /// literal would have been a rule the menu applied where it was written down rather than where
        /// it is true.
        /// </para>
        /// <para>
        /// <c>Before</c> takes the day's <em>start</em> rather than its end, and it needed saying: the
        /// literal reading keeps whatever time the value carried, so <c>Before 5 March 14:00</c> would
        /// have kept 5 March's morning. All four operators name a day here; what differs is which end
        /// of it they mean.
        /// </para>
        /// </remarks>
        static FastGridFilterCondition WholeDay(FastGridFilterOperator filterOperator, object? value,
            object? secondValue) => filterOperator switch
        {
            FastGridFilterOperator.Equals => new FastGridFilterCondition(
                FastGridFilterOperator.Between, new[] { StartOfDay(value), EndOfDay(value) }),

            FastGridFilterOperator.GreaterThan => new FastGridFilterCondition(
                FastGridFilterOperator.GreaterThan, EndOfDay(value)),

            FastGridFilterOperator.LessThan => new FastGridFilterCondition(
                FastGridFilterOperator.LessThan, StartOfDay(value)),

            FastGridFilterOperator.Between => new FastGridFilterCondition(
                FastGridFilterOperator.Between, new[] { StartOfDay(value), EndOfDay(secondValue) }),

            _ => Literal(filterOperator, value, secondValue),
        };

        /// <summary>The first instant of the day a value names, leaving what is not a date alone.</summary>
        internal static object? StartOfDay(object? value) => value switch
        {
            FastGridRelativeDate relative => relative.EndOfDay
                ? new FastGridRelativeDate(relative.Anchor, relative.Offset, relative.Unit, endOfDay: false)
                : relative,
            DateTime date => date.Date,
            DateTimeOffset date => new DateTimeOffset(date.Date, date.Offset),
            _ => value,
        };

        /// <summary>
        /// The last instant of the day a value names - 23:59:59.9999999, which is what <c>@end</c> means.
        /// </summary>
        /// <remarks>
        /// A <see cref="DateOnly" /> is returned untouched, for §34's reason: it has no end of day to
        /// reach, so <c>@end</c> is a no-op on it and this has to be the same no-op or the two
        /// vocabularies would disagree about one column.
        /// </remarks>
        internal static object? EndOfDay(object? value) => value switch
        {
            FastGridRelativeDate relative => relative.EndOfDay
                ? relative
                : new FastGridRelativeDate(relative.Anchor, relative.Offset, relative.Unit, endOfDay: true),
            DateTime date => date.Date.AddTicks(TimeSpan.TicksPerDay - 1),
            DateTimeOffset date => new DateTimeOffset(date.Date.AddTicks(TimeSpan.TicksPerDay - 1), date.Offset),
            _ => value,
        };
    }
}
