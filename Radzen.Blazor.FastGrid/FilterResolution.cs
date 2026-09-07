using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A filter as of an instant: the same filter with its relative dates read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §34's one place. Everything past it - <c>FilterExpression</c>'s two builders, the descriptors,
    /// the string and OData forms - sees values it cannot tell from ones somebody typed, which is what
    /// makes "a token is a value" true rather than asserted. Resolving in each of those instead would be
    /// the same rule in four places, and §10b's recurring finding is a rule applied in one place and not
    /// in its neighbour.
    /// </para>
    /// <para>
    /// <strong>A filter holding no tokens resolves to itself.</strong> The same reference, nothing
    /// allocated and nothing copied - the common case is a grid with no relative filter anywhere and it
    /// must cost exactly what it costs today. §33's review spent a finding on <c>DeclaredFilter</c>
    /// allocating a filter, a condition and a one-element array for every column whether or not it
    /// declared one; this is that lesson applied before rather than after.
    /// </para>
    /// </remarks>
    internal static class FilterResolution
    {
        /// <summary>
        /// <paramref name="filter" /> with its relative dates read at <paramref name="now" />, or
        /// <paramref name="filter" /> itself where it holds none.
        /// </summary>
        internal static FastGridFilter Resolve(FastGridFilter filter, Type declared, DateTimeOffset now)
        {
            // The cheap answer first, and it is the answer almost every time: only a date column can
            // hold a token at all, so nothing else pays for the walk below.
            if (!FastGridRelativeDate.AppliesTo(declared))
            {
                return filter;
            }

            var first = Resolve(filter.First, declared, now);
            var second = filter.Second is { } declaredSecond
                ? Resolve(declaredSecond, declared, now)
                : null;

            return ReferenceEquals(first, filter.First) && ReferenceEquals(second, filter.Second)
                ? filter
                : new FastGridFilter(first, second, filter.LogicalFilterOperator);
        }

        /// <summary>
        /// One condition's values, read. The condition itself is untouched - the operator and its arity
        /// are what §34 promises not to disturb.
        /// </summary>
        /// <remarks>
        /// An <c>In</c> is left alone. Its single value is a typed list, and a relative date in a set of
        /// specific days says nothing a range does not say better - so a token never enters one, which
        /// is also why <see cref="FilterValueText" /> refuses to read one back into a list that has to
        /// be typed to <c>DateTime</c> to be translatable.
        /// </remarks>
        static FastGridFilterCondition Resolve(FastGridFilterCondition condition, Type declared,
            DateTimeOffset now)
        {
            if (condition.Operator.Arity() == FastGridFilterArity.Many)
            {
                return condition;
            }

            object?[]? resolved = null;

            for (var i = 0; i < condition.Values.Count; i++)
            {
                if (condition.Values[i] is not FastGridRelativeDate token)
                {
                    continue;
                }

                if (resolved is null)
                {
                    resolved = new object?[condition.Values.Count];

                    for (var j = 0; j < condition.Values.Count; j++)
                    {
                        resolved[j] = condition.Values[j];
                    }
                }

                resolved[i] = token.Resolve(declared, now);
            }

            return resolved is null
                ? condition
                : new FastGridFilterCondition(condition.Operator, resolved);
        }

        /// <summary>Whether a filter would read differently at a different moment.</summary>
        /// <remarks>
        /// Asked by the editors and by the tests, not by the composition - which finds out by resolving.
        /// </remarks>
        internal static bool IsRelative(FastGridFilter? filter) =>
            filter is not null && (IsRelative(filter.First) || IsRelative(filter.Second));

        static bool IsRelative(FastGridFilterCondition? condition)
        {
            if (condition is null)
            {
                return false;
            }

            for (var i = 0; i < condition.Values.Count; i++)
            {
                if (condition.Values[i] is FastGridRelativeDate)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
