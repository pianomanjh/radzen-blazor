using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// How a column's filter compares. This grid's own vocabulary, and §33 argues why it is not
    /// <see cref="Radzen.FilterOperator" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream's set minus <c>Custom</c>, which this grid has never implemented, plus
    /// <see cref="Between" />, which upstream has no value for and cannot be given one without editing a
    /// file this branch does not own - the identical constraint that made §31 keep <c>FilterMode</c>
    /// upstream's and put <c>FilterUI</c> beside it.
    /// </para>
    /// <para>
    /// Prefixed rather than bare because the namespace is <c>Radzen.FastGrid</c> and every column file
    /// already references <c>Radzen.FilterOperator</c>: a bare <c>FilterOperator</c> of our own would be
    /// an ambiguous reference in exactly those files. It reads with
    /// <see cref="FastGridSettings" />, <see cref="FastGridLookup" /> and <see cref="FastGridSort{T}" />.
    /// </para>
    /// <para>
    /// <strong>Each operator declares how many values it takes</strong>, which is what makes
    /// <see cref="In" />, <see cref="Between" /> and <see cref="IsNull" /> one shape rather than three
    /// special cases. See <see cref="FastGridFilterOperators.Arity" />.
    /// </para>
    /// </remarks>
    public enum FastGridFilterOperator
    {
        /// <summary>Equal to the value.</summary>
        Equals,

        /// <summary>Not equal to the value.</summary>
        NotEquals,

        /// <summary>Ordered strictly below the value.</summary>
        LessThan,

        /// <summary>Ordered at or below the value.</summary>
        LessThanOrEquals,

        /// <summary>Ordered strictly above the value.</summary>
        GreaterThan,

        /// <summary>Ordered at or above the value.</summary>
        GreaterThanOrEquals,

        /// <summary>Contains the value as a substring.</summary>
        Contains,

        /// <summary>Does not contain the value as a substring.</summary>
        DoesNotContain,

        /// <summary>Begins with the value.</summary>
        StartsWith,

        /// <summary>Ends with the value.</summary>
        EndsWith,

        /// <summary>One of the values. The check-box list is this operator's editor, not a mode - §31.</summary>
        In,

        /// <summary>None of the values.</summary>
        NotIn,

        /// <summary>Missing. Takes no value.</summary>
        IsNull,

        /// <summary>Present. Takes no value.</summary>
        IsNotNull,

        /// <summary>The empty string. Takes no value.</summary>
        IsEmpty,

        /// <summary>Anything but the empty string. Takes no value.</summary>
        IsNotEmpty,

        /// <summary>
        /// Within an inclusive range. Takes two values, and it is why this enum exists: upstream has no
        /// value for it, so §31 had to spend the model's second condition on a range that §33 gives an
        /// operator of its own.
        /// </summary>
        Between,

        /// <summary>
        /// The caller filters this column themselves. Composes no predicate here and produces no filter
        /// string, but is carried to a <c>LoadData</c> handler so that the handler can act on it.
        /// </summary>
        /// <remarks>
        /// §33 said this enum was "upstream's set minus <c>Custom</c>, which this grid has never
        /// implemented", and a test proved that wrong in the way that matters. The grid does not
        /// implement the *predicate* - it never did - but it does carry the descriptor, which is the
        /// entire mechanism: a handler cannot filter something it is not told about. Dropped from the
        /// vocabulary, a <c>Custom</c> descriptor fell through to the column's default and became a
        /// <c>Contains</c>, which is a filter nobody asked for.
        /// </remarks>
        Custom,
    }

    /// <summary>What each operator needs, in one place because several callers ask.</summary>
    public static class FastGridFilterOperators
    {
        /// <summary>
        /// How many values the operator compares against: none, one, two, or any number.
        /// </summary>
        /// <remarks>
        /// The rule the whole model is built on. A condition holds the values its operator takes, so
        /// nothing downstream needs to know that <c>Between</c> is special - it is an operator whose
        /// arity is two, beside one whose arity is none.
        /// </remarks>
        public static FastGridFilterArity Arity(this FastGridFilterOperator filterOperator) =>
            filterOperator switch
            {
                FastGridFilterOperator.IsNull or FastGridFilterOperator.IsNotNull
                    or FastGridFilterOperator.IsEmpty or FastGridFilterOperator.IsNotEmpty
                    => FastGridFilterArity.None,

                FastGridFilterOperator.In or FastGridFilterOperator.NotIn => FastGridFilterArity.Many,

                FastGridFilterOperator.Between => FastGridFilterArity.Two,

                _ => FastGridFilterArity.One,
            };

        /// <summary>
        /// The upstream operator this one means, for the descriptors a provider and a
        /// <c>RadzenDataFilter</c> speak.
        /// </summary>
        /// <remarks>
        /// <see cref="FastGridFilterOperator.Between" /> has no answer here and that is the point: it
        /// projects to a *pair* of descriptors rather than to one operator, which is what nesting is
        /// for. Callers that can only take a single operator ask <see cref="Upstream" /> and get null.
        /// </remarks>
        public static FilterOperator? Upstream(this FastGridFilterOperator filterOperator) =>
            filterOperator switch
            {
                FastGridFilterOperator.Equals => FilterOperator.Equals,
                FastGridFilterOperator.NotEquals => FilterOperator.NotEquals,
                FastGridFilterOperator.LessThan => FilterOperator.LessThan,
                FastGridFilterOperator.LessThanOrEquals => FilterOperator.LessThanOrEquals,
                FastGridFilterOperator.GreaterThan => FilterOperator.GreaterThan,
                FastGridFilterOperator.GreaterThanOrEquals => FilterOperator.GreaterThanOrEquals,
                FastGridFilterOperator.Contains => FilterOperator.Contains,
                FastGridFilterOperator.DoesNotContain => FilterOperator.DoesNotContain,
                FastGridFilterOperator.StartsWith => FilterOperator.StartsWith,
                FastGridFilterOperator.EndsWith => FilterOperator.EndsWith,
                FastGridFilterOperator.In => FilterOperator.In,
                FastGridFilterOperator.NotIn => FilterOperator.NotIn,
                FastGridFilterOperator.IsNull => FilterOperator.IsNull,
                FastGridFilterOperator.IsNotNull => FilterOperator.IsNotNull,
                FastGridFilterOperator.IsEmpty => FilterOperator.IsEmpty,
                FastGridFilterOperator.IsNotEmpty => FilterOperator.IsNotEmpty,
                FastGridFilterOperator.Custom => FilterOperator.Custom,
                _ => null,
            };

        /// <summary>
        /// What an upstream operator means here, so a column declaring one in markup keeps working.
        /// </summary>
        /// <remarks>
        /// Every upstream value maps onto exactly one of ours, <c>Custom</c> included - see the note on
        /// <see cref="FastGridFilterOperator.Custom" /> for why it has to. Nothing answers null today;
        /// the null arm is what a value added upstream later would take, and taking no filter from an
        /// operator this build cannot name is the safe reading of one.
        /// </remarks>
        public static FastGridFilterOperator? Owned(this FilterOperator filterOperator) =>
            filterOperator switch
            {
                FilterOperator.Equals => FastGridFilterOperator.Equals,
                FilterOperator.NotEquals => FastGridFilterOperator.NotEquals,
                FilterOperator.LessThan => FastGridFilterOperator.LessThan,
                FilterOperator.LessThanOrEquals => FastGridFilterOperator.LessThanOrEquals,
                FilterOperator.GreaterThan => FastGridFilterOperator.GreaterThan,
                FilterOperator.GreaterThanOrEquals => FastGridFilterOperator.GreaterThanOrEquals,
                FilterOperator.Contains => FastGridFilterOperator.Contains,
                FilterOperator.DoesNotContain => FastGridFilterOperator.DoesNotContain,
                FilterOperator.StartsWith => FastGridFilterOperator.StartsWith,
                FilterOperator.EndsWith => FastGridFilterOperator.EndsWith,
                FilterOperator.In => FastGridFilterOperator.In,
                FilterOperator.NotIn => FastGridFilterOperator.NotIn,
                FilterOperator.IsNull => FastGridFilterOperator.IsNull,
                FilterOperator.IsNotNull => FastGridFilterOperator.IsNotNull,
                FilterOperator.IsEmpty => FastGridFilterOperator.IsEmpty,
                FilterOperator.IsNotEmpty => FastGridFilterOperator.IsNotEmpty,
                FilterOperator.Custom => FastGridFilterOperator.Custom,
                _ => null,
            };
    }

    /// <summary>How many values an operator compares against.</summary>
    public enum FastGridFilterArity
    {
        /// <summary>The operator is the whole filter - <c>IsNull</c> and its three neighbours.</summary>
        None,

        /// <summary>One value, which is most of them.</summary>
        One,

        /// <summary>Two, which is <c>Between</c>.</summary>
        Two,

        /// <summary>Any number, which is <c>In</c> and <c>NotIn</c>.</summary>
        Many,
    }
}
