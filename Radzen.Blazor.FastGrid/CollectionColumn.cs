using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column bound to a collection of objects, listing a member of each: for example
    /// <c>Property="@(r =&gt; r.Accounts)" DisplayProperty="@(a =&gt; a.Name)"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="PropertyColumn{TItem, TProp}" /> already lists a collection of values. This exists
    /// for a collection of objects, where each member needs a member of its own selected - and the
    /// element type is a type parameter here, so that selection is an expression rather than a string.
    /// Razor infers <typeparamref name="TElement" /> from <see cref="Property" />, so the authoring form
    /// names neither type parameter.
    /// </para>
    /// <para>
    /// Filtering matches a row when any member matches, and offers the members in a check-box list.
    /// Sorting is off unless <see cref="SortBy" /> names something orderable: no provider can order rows
    /// by a collection.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TElement">The type of the collection's members.</typeparam>
    public sealed class CollectionColumn<TItem, TElement> : ColumnBase<TItem>
    {
        /// <summary>The collection this column lists.</summary>
        [Parameter, EditorRequired] public Expression<Func<TItem, IEnumerable<TElement>>> Property { get; set; } = default!;

        /// <summary>
        /// The member of each element to show. Without it the element's <c>ToString</c> is used, which
        /// for an entity is its type name.
        /// </summary>
        [Parameter] public Expression<Func<TElement, object?>>? DisplayProperty { get; set; }

        /// <summary>
        /// The member of each element to filter on. Defaults to <see cref="DisplayProperty" />, since
        /// filtering on what the reader can see is almost always what is meant.
        /// </summary>
        [Parameter] public Expression<Func<TElement, object?>>? FilterProperty { get; set; }

        /// <summary>What separates the members in the cell.</summary>
        [Parameter] public string Separator { get; set; } = ", ";

        /// <summary>Format string applied to each member.</summary>
        [Parameter] public string? Format { get; set; }

        /// <summary>
        /// What to sort by. A collection cannot be ordered, so a column without this is not sortable.
        /// </summary>
        [Parameter] public Expression<Func<TItem, object?>>? SortBy { get; set; }

        Expression<Func<TItem, IEnumerable<TElement>>>? property;
        Expression<Func<TElement, object?>>? displayProperty;
        Expression<Func<TElement, object?>>? filterProperty;
        Expression<Func<TItem, object?>>? sortBy;
        string? format;

        Func<TItem, IEnumerable<TElement>>? compiled;
        Func<TElement, object?>? member;
        string? sortPath;
        string? collectionPath;
        string? memberPath;
        Type memberType = typeof(TElement);

        /// <inheritdoc />
        public override string? PropertyPath => sortPath;

        /// <inheritdoc />
        public override string? FilterPropertyPath => collectionPath;

        /// <inheritdoc />
        public override string? FilterMemberPath => memberPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(IEnumerable<TElement>);

        /// <inheritdoc />
        public override Type FilterElementType => memberType;

        /// <inheritdoc />
        public override string? HeaderText => Title ?? collectionPath;

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            Derive();

            // After Derive, not before: the base picks the default filter operator from
            // FilterElementType, which is the member's type and is only known once the member selector
            // has been read. Called first, it would default a string member to Equals.
            base.OnParametersSet();
        }

        void Derive()
        {
            // Equivalent rather than ReferenceEquals, for the same reason as PropertyColumn: Razor
            // rebuilds every expression tree per render, so reference equality never holds in markup.
            if (format == Format
                && PropertyPathResolver.Equivalent(property, Property)
                && PropertyPathResolver.Equivalent(displayProperty, DisplayProperty)
                && PropertyPathResolver.Equivalent(filterProperty, FilterProperty)
                && PropertyPathResolver.Equivalent(sortBy, SortBy))
            {
                return;
            }

            property = Property;
            displayProperty = DisplayProperty;
            filterProperty = FilterProperty;
            sortBy = SortBy;
            format = Format;

            compiled = Property?.Compile();
            member = DisplayProperty?.Compile();

            collectionPath = Property is null ? null : PropertyPathResolver.For(Property);
            sortPath = SortBy is null ? null : PropertyPathResolver.For(SortBy);

            // Filtering follows what the reader sees unless told otherwise.
            var filterMember = FilterProperty ?? DisplayProperty;

            memberPath = filterMember is null ? null : PropertyPathResolver.For(filterMember);
            memberType = MemberSelector(filterMember)?.ReturnType ?? typeof(TElement);
        }

        /// <inheritdoc />
        public override bool CanSort => Sortable && sortPath is not null;

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending)
        {
            // The boxing conversion is stripped first, so the ordering is by the key's own type and a
            // provider sees ORDER BY that column rather than an untranslatable convert to object.
            var selector = SortBy is null ? null : Unbox(SortBy);

            return selector is null || source is null ? null : Projection.OrderBy(source, selector, descending);
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplyThenBy(IOrderedQueryable<TItem> source, bool descending)
        {
            var selector = SortBy is null ? null : Unbox(SortBy);

            return selector is null || source is null ? null : Projection.ThenBy(source, selector, descending);
        }

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, Text(item));

        /// <inheritdoc />
        public override string? CellTextOf(TItem item) => Text(item);

        string? Text(TItem item) =>
            compiled?.Invoke(item) is { } members
                ? CellText.Join(members, Separator, show ??= element => CellText.Of(Select(element), Format))
                : null;

        // Allocated once and never invalidated: it captures nothing but this, so it reads the current
        // display member and format through the component itself.
        Func<object?, string?>? show;

        /// <summary>
        /// The member of one element to show, or the element itself when no display member was named.
        /// The cast is what a non-generic join costs; the alternative is a copy of the loop per column
        /// type, which is what this shares away. A null member is left null rather than read through: a
        /// partly populated graph is not a reason to take the render down.
        /// </summary>
        object? Select(object? element) =>
            member is null || element is null ? element : member((TElement)element);

        /// <inheritdoc />
        /// <remarks>
        /// Fully typed: <typeparamref name="TElement" /> is a type parameter here, so the projection is
        /// an ordinary generic call and a provider sees SELECT DISTINCT over the member's own column.
        /// </remarks>
        public override IQueryable? DistinctValues(IQueryable<TItem> source)
        {
            if (source is null || Property is null)
            {
                return null;
            }

            var elements = source.SelectMany(Property);
            var selector = MemberSelector(FilterProperty ?? DisplayProperty);

            return selector is null
                ? elements.Distinct()
                : Projection.SelectDistinct(elements, selector);
        }

        /// <summary>
        /// The member selector with the boxing conversion stripped, so it selects the member's own type
        /// rather than <c>object</c> - which is what keeps the distinct query translatable.
        /// </summary>
        static LambdaExpression? MemberSelector(Expression<Func<TElement, object?>>? selector) =>
            selector is null ? null : Unbox(selector);

        /// <summary>
        /// The lambda retyped to what its body actually returns. A selector declared as returning
        /// <c>object</c> hides the member's real type two different ways: a value type is wrapped in a
        /// Convert, and a reference type is not wrapped at all - the tree simply carries a narrower body
        /// than the delegate's return type. Both have to be unwrapped, or the member looks like
        /// <c>object</c> and everything derived from its type is wrong.
        /// </summary>
        static LambdaExpression Unbox(LambdaExpression selector)
        {
            var body = PropertyPathResolver.Unwrap(selector.Body);

            return body.Type == selector.ReturnType ? selector : Expression.Lambda(body, selector.Parameters);
        }
    }
}
