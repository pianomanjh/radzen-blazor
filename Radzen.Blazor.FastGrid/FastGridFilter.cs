using System;
using System.Collections;
using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// One comparison: an operator and the values that operator takes.
    /// </summary>
    /// <remarks>
    /// Arity belongs to the operator - <c>IsNull</c> takes none, <c>Equals</c> one, <c>Between</c> two,
    /// <c>In</c> many - which is what makes those one shape here rather than three special cases, and
    /// what gives §31's ③ relative date tokens somewhere to live: a token is a <em>value</em>, not an
    /// operator, so nothing in this type has to learn about them.
    /// </remarks>
    public sealed class FastGridFilterCondition
    {
        static readonly object?[] Nothing = Array.Empty<object?>();

        /// <summary>A condition comparing against no values, for the operators that take none.</summary>
        public FastGridFilterCondition(FastGridFilterOperator filterOperator)
            : this(filterOperator, Nothing)
        {
        }

        /// <summary>A condition comparing against one value.</summary>
        public FastGridFilterCondition(FastGridFilterOperator filterOperator, object? value)
            : this(filterOperator, new[] { value })
        {
        }

        /// <summary>A condition comparing against the values given.</summary>
        /// <remarks>
        /// The list is taken as it is rather than trimmed or padded to the operator's arity. A filter
        /// that says less than its operator needs is not a filter to correct, it is one that
        /// <see cref="IsPresent" /> answers false for - which is how a half-typed range filters nothing
        /// rather than throwing.
        /// </remarks>
        public FastGridFilterCondition(FastGridFilterOperator filterOperator, IReadOnlyList<object?> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            Operator = filterOperator;
            Values = values;
        }

        /// <summary>How this condition compares.</summary>
        public FastGridFilterOperator Operator { get; }

        /// <summary>What it compares against - as many as <see cref="Operator" /> takes.</summary>
        public IReadOnlyList<object?> Values { get; }

        /// <summary>The first value, or null where there is none.</summary>
        public object? Value => Values.Count > 0 ? Values[0] : null;

        /// <summary>The second value, or null where there is none. <c>Between</c>'s upper bound.</summary>
        public object? SecondValue => Values.Count > 1 ? Values[1] : null;

        /// <summary>
        /// Whether this condition would actually narrow anything.
        /// </summary>
        /// <remarks>
        /// The rule §32 settled, generalised to arity: an operator that takes no values is a filter on
        /// its own, and one that takes values needs as many as it takes. An empty string and an empty
        /// check-box selection are both "nothing chosen" rather than "match nothing", which is what
        /// stops a grid emptying as the last box is unticked.
        /// </remarks>
        public bool IsPresent => Operator.Arity() switch
        {
            FastGridFilterArity.None => true,
            FastGridFilterArity.Many => Values.Count > 0 && AnyValue(),
            FastGridFilterArity.Two => Values.Count > 1 && Present(Values[0]) && Present(Values[1]),
            _ => Values.Count > 0 && Present(Values[0]),
        };

        bool AnyValue()
        {
            // In takes a list, and the list is normally the single value - a check-box selection arrives
            // as one object, not as one value per box.
            if (Values.Count == 1 && Values[0] is IEnumerable and not string)
            {
                return Present(Values[0]);
            }

            for (var i = 0; i < Values.Count; i++)
            {
                if (Present(Values[i]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool Present(object? value) => value switch
        {
            null => false,
            string text => text.Length > 0,
            ICollection collection => collection.Count > 0,
            IEnumerable sequence => Any(sequence),
            _ => true,
        };

        static bool Any(IEnumerable sequence)
        {
            var enumerator = sequence.GetEnumerator();

            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// What a column filters by: one condition, or two joined by an And or an Or.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rather than one because §31's worked case needs it - <em>"these three values or blank"</em> on
    /// a nullable column is <c>In [...] OR IsNull</c>, which no single condition says. Two rather than
    /// any number because nothing has asked for a third and the column menu will never offer one.
    /// </para>
    /// <para>
    /// <strong><see cref="First" /> gates.</strong> A column is filtered when the first condition is
    /// present; the second only joins. A filter whose first half is blank is a state nothing can author
    /// and every reader of this model would have to cope with - so a <c>FilterTemplate</c> author who
    /// sets only the second condition gets nothing, and that is a documented rule rather than a
    /// discovery.
    /// </para>
    /// </remarks>
    public sealed class FastGridFilter
    {
        /// <summary>A filter of one condition.</summary>
        public FastGridFilter(FastGridFilterCondition first)
            : this(first, null, LogicalFilterOperator.And)
        {
        }

        /// <summary>A filter of one or two conditions.</summary>
        public FastGridFilter(FastGridFilterCondition first, FastGridFilterCondition? second,
            LogicalFilterOperator logicalFilterOperator)
        {
            ArgumentNullException.ThrowIfNull(first);

            First = first;
            Second = second;
            LogicalFilterOperator = logicalFilterOperator;
        }

        /// <summary>The condition that decides whether the column is filtered at all.</summary>
        public FastGridFilterCondition First { get; }

        /// <summary>The condition joined to it, or null where there is only one.</summary>
        public FastGridFilterCondition? Second { get; }

        /// <summary>How the two are joined. Meaningless where <see cref="Second" /> is null.</summary>
        public LogicalFilterOperator LogicalFilterOperator { get; }

        /// <summary>Whether this filter would narrow anything - which is <see cref="First" />'s answer.</summary>
        public bool IsPresent => First.IsPresent;

        /// <summary>The second condition, but only when it would narrow anything of its own.</summary>
        internal FastGridFilterCondition? EffectiveSecond =>
            Second is { IsPresent: true } second ? second : null;
    }
}
