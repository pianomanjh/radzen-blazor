using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column that lists names and carries ids: the row holds <c>BrandIds</c> and the cell shows
    /// "Acme, Globex".
    /// </summary>
    /// <remarks>
    /// The same argument as <see cref="LookupColumn{TItem, TKey}" /> one cardinality up. Where the ids
    /// live behind a navigation collection rather than on the row, project them into one:
    /// <c>db.Products.Select(p =&gt; new ProductRow { BrandIds = p.Brands.Select(b =&gt; b.Id).ToList() })</c>
    /// makes the projection the row type and this an ordinary column over it - one query, no key, and
    /// the columns nobody renders dropped, which is this column's own argument applied one level up.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TKey">The type of the ids the row carries.</typeparam>
    public sealed class LookupCollectionColumn<TItem, TKey> : LookupColumnBase<TItem, TKey>
    {
        /// <summary>The ids this column resolves.</summary>
        [Parameter, EditorRequired]
        public Expression<Func<TItem, IEnumerable<TKey>>> Property { get; set; } = default!;

        /// <summary>What separates the names in the cell.</summary>
        [Parameter] public string Separator { get; set; } = ", ";

        Expression<Func<TItem, IEnumerable<TKey>>>? property;
        Func<TItem, IEnumerable<TKey>>? ids;
        string? path;

        /// <inheritdoc />
        public override string? FilterPropertyPath => path;

        /// <inheritdoc />
        /// <remarks>The id collection this column is bound to. See LookupColumn for why the id.</remarks>
        internal override string? DisplayPath => path;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(IEnumerable<TKey>);

        /// <inheritdoc />
        public override string? HeaderText => Title ?? path;

        /// <summary>
        /// No entry for the rows carrying nothing. "Has no brands at all" is a different question from
        /// "has an id that is null", and <c>In</c> over the elements does not ask it.
        /// </summary>
        private protected override bool OffersBlank => false;

        /// <inheritdoc />
        protected override void OnDerive()
        {
            if (!PropertyPathResolver.Equivalent(property, Property))
            {
                property = Property;
                path = PropertyPathResolver.For(Property);
                ids = Property?.Compile();
            }

            // Last, because the base resolves the lookup and this reads the ids it is resolved against.
            base.OnDerive();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Through the typed join, so a value-typed id reaches the cell without being boxed on the way.
        /// The joined string itself is unavoidable, and its builder is the one already shared across
        /// every collection cell in the grid.
        /// </remarks>
        public override string? CellTextOf(TItem item) =>
            ids?.Invoke(item) is { } members
                ? CellText.Join(members, Separator, name ??= NameOf)
                : null;

        // Allocated once and never invalidated: it captures nothing but this, so it reads the current
        // names through the column itself.
        Func<TKey, string?>? name;

        /// <inheritdoc />
        /// <remarks>
        /// Appended to the authored expression rather than rewriting it:
        /// <c>p =&gt; p.BrandIds != null &amp;&amp; p.BrandIds.Any(id =&gt; selected.Contains(id))</c>. Every
        /// generic argument there is <typeparamref name="TKey" />, which is a type parameter, so there
        /// is no <c>MakeGenericMethod</c> over a type known only at run time and nothing to guard with
        /// <c>DynamicCode</c>. A provider translates it as a subquery.
        /// <para>
        /// The null guard is upstream's, and matching it is the point: the descriptor this column
        /// reports is meant to mean the same thing wherever it is read.
        /// </para>
        /// </remarks>
        public override Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            if (Property is not { } selector
                || CurrentFilterOperator is not (Radzen.FilterOperator.In or Radzen.FilterOperator.NotIn))
            {
                return null;
            }

            var element = Expression.Parameter(typeof(TKey), "id");

            var any = Expression.Call(EnumerableAny, selector.Body,
                Expression.Lambda<Func<TKey, bool>>(
                    Expression.Call(Expression.Constant(SelectedKeys(), typeof(List<TKey?>)),
                        ListContains, element),
                    element));

            var present = Expression.AndAlso(
                Expression.NotEqual(selector.Body, Expression.Constant(null, typeof(IEnumerable<TKey>))),
                any);

            return Expression.Lambda<Func<TItem, bool>>(
                CurrentFilterOperator == Radzen.FilterOperator.NotIn ? Expression.Not(present) : present,
                selector.Parameters);
        }

        // Captured from a typed lambda rather than looked up by name: an ldtoken the compiler emits,
        // closed over TKey where TKey is still a type parameter. The Contains it wraps is the base's,
        // since both columns compose their In out of the same one.
        static readonly MethodInfo EnumerableAny =
            ((MethodCallExpression)((Expression<Func<IEnumerable<TKey>, bool>>)(
                members => members.Any(id => true))).Body).Method;

        /// <inheritdoc />
        public override Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity)
        {
            if (ids is not { } members
                || CurrentFilterOperator is not (Radzen.FilterOperator.In or Radzen.FilterOperator.NotIn))
            {
                return null;
            }

            var keys = SelectedKeys();

            // The null guard sits inside the negation, exactly as it does in the expression above: a row
            // carrying no ids at all is not one of the brands asked about, so NotIn keeps it. Written
            // the other way round the two routes answer differently for that row, which is how a
            // check-box-list filter over a List once disagreed with the same filter over a queryable.
            return CurrentFilterOperator == Radzen.FilterOperator.NotIn
                ? item => !(members(item) is { } carried && carried.Any(keys.Contains))
                : item => members(item) is { } carried && carried.Any(keys.Contains);
        }
    }
}
