using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column bound to a property expression, for example <c>Property="@(o =&gt; o.Customer.Name)"</c>.
    /// </summary>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TProp">The property type.</typeparam>
    public sealed class PropertyColumn<TItem,
        // The column asks TProp whether it is a collection, which means asking for its interfaces. The
        // annotation is what tells a trimmer to keep them; without it the question is answered wrongly
        // rather than not at all, and a collection column would quietly render as its ToString.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TProp> : ColumnBase<TItem>
    {
        Expression<Func<TItem, TProp>>? property;
        Expression<Func<TItem, TProp>>? sortBy;
        Expression<Func<TItem, TProp>>? filterBy;
        Func<TItem, string?>? cellText;
        string? format;
        string? separator;

        /// <summary>The property this column displays.</summary>
        [Parameter, EditorRequired] public Expression<Func<TItem, TProp>> Property { get; set; } = default!;

        /// <summary>
        /// The property to sort by, when it differs from the one displayed. Required to make a computed
        /// column sortable, since a computed expression has no property path.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? SortBy { get; set; }

        /// <summary>Format string applied to the value, for example <c>"C"</c> or <c>"d"</c>.</summary>
        [Parameter] public string? Format { get; set; }

        /// <summary>
        /// What separates the members of a collection-valued property in the cell. Ignored for a scalar.
        /// </summary>
        [Parameter] public string Separator { get; set; } = ", ";

        /// <summary>
        /// Whether this column is bound to a collection rather than a single value. Its cells list the
        /// members and its filter matches a row when any member matches.
        /// </summary>
        public bool IsCollection => ElementType is not null;

        string? path;
        string? displayPath;

        /// <inheritdoc />
        public override string? SortPath => path;

        /// <summary>
        /// The property to filter by, when it differs from the one displayed. Must be of the same type;
        /// a column filtered on an unrelated property is a different column.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? FilterBy { get; set; }

        string? filterPath;

        // Once per closed generic type, not once per column: the interface walk allocates, and the
        // answer depends only on TProp. Measured at ~240 B per column when it was computed per instance.
        static readonly Type? ElementType = CollectionElementType(typeof(TProp));

        /// <inheritdoc />
        /// <remarks>
        /// The displayed property, not the sort key: a column showing First and sorting by Last is a
        /// column of first names, and heading it "Last" describes the ordering rather than the cells.
        /// </remarks>
        public override string? HeaderText => Title ?? displayPath;

        /// <inheritdoc />
        public override string? FilterPropertyPath => filterPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TProp);

        /// <inheritdoc />
        public override Type FilterElementType => ElementType ?? typeof(TProp);

        /// <inheritdoc />
        protected override void OnDerive()
        {
            // Equivalent rather than ReferenceEquals: Razor hands this a freshly built expression tree on
            // every render, so reference equality never holds for a column authored in markup and the
            // column recompiled per render. Measured at 5x the render cost of a grid that did not.
            if (format == Format
                && separator == Separator
                && PropertyPathResolver.Equivalent(property, Property)
                && PropertyPathResolver.Equivalent(sortBy, SortBy)
                && PropertyPathResolver.Equivalent(filterBy, FilterBy))
            {
                return;
            }

            property = Property;
            sortBy = SortBy;
            filterBy = FilterBy;
            format = Format;

            // Separator is baked into the delegate below, so it belongs in the guard above: without it
            // a column bound to a user's choice of separator kept the first one for good.
            separator = Separator;

            // Built by a static method rather than inline: the lambdas below capture the compiled getter,
            // and a lambda capturing a local makes the compiler allocate the enclosing method's display
            // class on entry - here, on every parameter set of every column, taken branch or not.
            // A column with no Property renders empty cells rather than throwing out of the render: the
            // parameter is EditorRequired, which is a warning, not a guarantee.
            cellText = Property is null
                ? null
                : BuildCellText(Property.Compile(), Separator, Format is { Length: > 0 } ? Format : null);

            var propertyPath = PropertyPathResolver.For(Property);

            displayPath = propertyPath;
            path = SortBy is null ? propertyPath : PropertyPathResolver.For(SortBy);

            // Filtering follows the displayed property, not the sort key: a column that displays First
            // and sorts by Last still filters on what the reader can see.
            //
            // A computed column therefore has no filter path at all, and must not borrow the sort key
            // as one. ApplyFilter composes its predicate from the display expression while this path is
            // what the reflective route filters by, so borrowing made the column filter a different
            // member depending on which route ran - and which route runs is decided by whether some
            // other column declined. It declines to filter instead, as it already declines to sort,
            // and FilterBy is how such a column is given something to filter on.
            filterPath = FilterBy is not null ? PropertyPathResolver.For(FilterBy) : propertyPath;

            // The compiled getters are derived state like everything else here, and stale ones would
            // filter and sort by the expression the column used to have.
            filterGetter = null;
            sortGetter = null;
        }

        /// <summary>
        /// The cell's text, as a delegate built once per column. Compiled to a
        /// <c>Func&lt;TItem, string&gt;</c> rather than read as an object: RenderTreeBuilder has no
        /// generic AddContent, so handing it a value type binds the object overload, which boxes and then
        /// stringifies. Producing the string directly skips the box.
        /// </summary>
        static Func<TItem, string?> BuildCellText(Func<TItem, TProp> get, string separator, string? format)
        {
            // A collection is listed, not stringified: List<string>.ToString() is the type name, which is
            // why every such column needed a Template that did nothing but string.Join. TProp = object
            // cannot say statically whether the value is a collection, so there the value decides.
            if (ElementType is not null || typeof(TProp) == typeof(object))
            {
                // Built here, not per cell: Join takes how a member is rendered as a delegate, and one
                // allocated inside the cell delegate would be one allocation per cell.
                Func<object?, string?> show = value => CellText.Of(value, format);

                return item => get(item) switch
                {
                    null => null,
                    string text => CellText.Of(text, format),
                    IEnumerable sequence => CellText.Join(sequence, separator, show),
                    var value => CellText.Of(value, format),
                };
            }

            if (format is null)
            {
                return item => get(item)?.ToString();
            }

            // A value type has to be formatted through a delegate typed at the value's own type, or the
            // cast to IFormattable boxes it - once per cell, for the whole life of the grid. The generic
            // method below calls the interface under a constraint, which the JIT compiles to a direct
            // call on the struct. A reference type needs none of this: casting one is free.
            var underlying = Nullable.GetUnderlyingType(typeof(TProp));

            // Closing that generic method over the value's type is the only part of this that needs code
            // generated at run time. Under Native AOT the fall-through below still formats correctly -
            // it just boxes to reach IFormattable, which is what this whole branch exists to avoid, so
            // it is a cost paid per cell rather than a feature lost.
            if (DynamicCode.Supported)
            {
                if (underlying is not null && typeof(IFormattable).IsAssignableFrom(underlying))
                {
                    return Formatter(NullableFormatterMethod, underlying, get, format);
                }

                if (typeof(TProp).IsValueType && typeof(IFormattable).IsAssignableFrom(typeof(TProp)))
                {
                    return Formatter(ValueFormatterMethod, typeof(TProp), get, format);
                }
            }

            if (typeof(IFormattable).IsAssignableFrom(typeof(TProp)))
            {
                return item => ((IFormattable?)(object?)get(item))?.ToString(format, CultureInfo.CurrentCulture);
            }

            // TProp says nothing about whether the value can be formatted. Ask the value.
            return item => CellText.Of(get(item), format);
        }

        static readonly MethodInfo ValueFormatterMethod = typeof(PropertyColumn<TItem, TProp>)
            .GetMethod(nameof(ValueFormatter), BindingFlags.NonPublic | BindingFlags.Static)!;

        static readonly MethodInfo NullableFormatterMethod = typeof(PropertyColumn<TItem, TProp>)
            .GetMethod(nameof(NullableFormatter), BindingFlags.NonPublic | BindingFlags.Static)!;

        static Func<TItem, string?> Formatter(MethodInfo method, Type valueType, Func<TItem, TProp> get,
            string format) =>
            (Func<TItem, string?>)method.MakeGenericMethod(valueType).Invoke(null, new object[] { get, format })!;

        static Func<TItem, string?> ValueFormatter<T>(Func<TItem, T> get, string format)
            where T : struct, IFormattable =>
            item => get(item).ToString(format, CultureInfo.CurrentCulture);

        static Func<TItem, string?> NullableFormatter<T>(Func<TItem, T?> get, string format)
            where T : struct, IFormattable =>
            item => get(item) is { } value ? value.ToString(format, CultureInfo.CurrentCulture) : null;

        /// <inheritdoc />
        /// <remarks>
        /// Composed rather than enumerated, so an Entity Framework source runs SELECT DISTINCT rather
        /// than pulling every row across the wire. A collection column offers its members.
        /// </remarks>
        public override IQueryable? DistinctValues(IQueryable<TItem> source)
        {
            if (source is null || Property is null)
            {
                return null;
            }

            // A non-collection column projects through its own typed expression below; only the
            // collection branch has to close SelectMany over an element type known at run time.
            if (IsCollection && !DynamicCode.Supported)
            {
                return null;
            }

            // FilterBy, not Property: the values offered have to be the ones the filter compares, or
            // the list shows one column's values and every choice filters another column by them.
            var selector = FilterBy ?? Property;

            if (!IsCollection)
            {
                // Queryable.Distinct by its full name, not the extension-method form. Radzen's own
                // Distinct(this IQueryable) is non-generic, C# prefers a non-generic candidate to a
                // generic one, and it therefore won - so this typed projection was going through the
                // reflective distinct and composing Cast nodes a provider then had to translate. Naming
                // the generic one keeps the element type, and keeps this off the reflective path.
                return Queryable.Distinct(source.Select(selector));
            }

            // TProp is the collection, so the element type is not a type parameter here and SelectMany
            // has to be built by hand. CollectionColumn<TItem, TElement> has it as a parameter and does
            // this as an ordinary generic call.
            return Projection
                .SelectMany(source, typeof(TItem), ElementType!, AsSequenceSelector(selector))
                .Distinct();
        }

        /// <summary>
        /// The property expression retyped as returning <c>IEnumerable&lt;TElement&gt;</c>, which is what
        /// SelectMany's signature demands - a lambda returning <c>List&lt;T&gt;</c> is not the same
        /// delegate type, however assignable the values are.
        /// </summary>
        [RequiresDynamicCode("Closes IEnumerable<> and Func<,> over an element type known at run time.")]
        static LambdaExpression AsSequenceSelector(Expression<Func<TItem, TProp>> selector)
        {
            var sequenceType = typeof(IEnumerable<>).MakeGenericType(ElementType!);

            if (typeof(TProp) == sequenceType)
            {
                return selector;
            }

            // A widening reference conversion, which every provider strips before translating.
            return Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(TItem), sequenceType),
                Expression.Convert(selector.Body, sequenceType),
                selector.Parameters);
        }

        /// <inheritdoc />
        public override string? CellTextOf(TItem item) => cellText?.Invoke(item);

        /// <summary>
        /// A collection column has nothing to order by: no provider can sort rows by a list, and
        /// <see cref="SortBy" /> here is typed at <typeparamref name="TProp" />, which for such a column
        /// is the collection - so the only sort key the type parameter admits is another uncomparable
        /// one, and offering it produced a clickable header that threw on the first click. Use
        /// <see cref="CollectionColumn{TItem, TElement}" />, whose SortBy names a member instead. A
        /// column typed as <c>object</c> whose values happen to be collections cannot be recognised
        /// statically and stays sortable; give it a real type, or set <c>Sortable="false"</c>.
        /// </summary>
        public override bool CanSort => Sortable && path is not null && !IsCollection;

        /// <summary>
        /// The element type of a collection-valued property, or null when the property is a single value.
        /// </summary>
        static Type? CollectionElementType(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
        {
            // IsEnumerable excludes string, which is a sequence of characters and would otherwise be
            // listed one letter at a time. An array needs no case of its own: GetElementType has one.
            if (!QueryableExtension.IsEnumerable(type))
            {
                return null;
            }

            var element = PropertyAccess.GetElementType(type);

            // GetElementType answers with the type itself when it finds no IEnumerable<T> to read an
            // element type from - a non-generic IEnumerable, whose members are only known as objects.
            return element == type ? typeof(object) : element;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Declines - and leaves the grid to build the predicate by reflection - in the three cases
        /// where <typeparamref name="TProp" /> is not the type being compared:
        /// <list type="bullet">
        /// <item>a collection-valued column, whose filter compares an <em>element</em> whose type is not
        /// a parameter here. <see cref="CollectionColumn{TItem, TElement}" /> has it as one;</item>
        /// <item>a column declared as <c>object</c>, where the real type is only reachable through the
        /// property path - the case this class already documents as worth giving a real type;</item>
        /// <item>a filter aimed at a member of a collection's element, which is the same problem.</item>
        /// </list>
        /// </remarks>
        public override Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            if (IsCollection || typeof(TProp) == typeof(object)
                || FilterMemberPath is not null || (FilterBy ?? Property) is not { } selector)
            {
                return null;
            }

            return FilterExpression<TItem, TProp>.For(selector, CurrentFilterOperator, CurrentFilterValue,
                caseSensitivity, inMemory);
        }

        // Compiled on first use rather than in Derive: a grid over a queryable never needs either, and
        // a compile is about 250 us - and, under Native AOT, an interpreted lambda rather than emitted
        // code. Cleared with the rest of the derived state when the expressions change.
        Func<TItem, TProp>? filterGetter;
        Func<TItem, TProp>? sortGetter;

        /// <inheritdoc />
        public override Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity)
        {
            if (IsCollection || typeof(TProp) == typeof(object)
                || FilterMemberPath is not null || (FilterBy ?? Property) is not { } selector)
            {
                return null;
            }

            return FilterExpression<TItem, TProp>.PredicateFor(filterGetter ??= selector.Compile(),
                CurrentFilterOperator, CurrentFilterValue, caseSensitivity);
        }

        /// <inheritdoc />
        public override IOrderedEnumerable<TItem>? ApplySortInMemory(IEnumerable<TItem> source,
            bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } selector)
            {
                return null;
            }

            sortGetter ??= selector.Compile();

            return descending ? source.OrderByDescending(sortGetter) : source.OrderBy(sortGetter);
        }

        /// <inheritdoc />
        public override IOrderedEnumerable<TItem>? ApplyThenByInMemory(IOrderedEnumerable<TItem> source,
            bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } selector)
            {
                return null;
            }

            sortGetter ??= selector.Compile();

            return descending ? source.ThenByDescending(sortGetter) : source.ThenBy(sortGetter);
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } expression)
            {
                return null;
            }

            return descending ? source.OrderByDescending(expression) : source.OrderBy(expression);
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplyThenBy(IOrderedQueryable<TItem> source, bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } expression)
            {
                return null;
            }

            return descending ? source.ThenByDescending(expression) : source.ThenBy(expression);
        }
    }
}
