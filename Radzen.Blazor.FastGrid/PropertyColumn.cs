using System;
using System.Collections;
using System.Collections.Generic;
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
    public sealed class PropertyColumn<TItem, TProp> : ColumnBase<TItem>
    {
        Expression<Func<TItem, TProp>>? property;
        Expression<Func<TItem, TProp>>? sortBy;
        Expression<Func<TItem, TProp>>? filterBy;
        Func<TItem, string?>? cellText;
        string? format;

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

        /// <inheritdoc />
        public override string? PropertyPath => path;

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
        public override string? HeaderText => Title ?? path;

        /// <inheritdoc />
        public override string? FilterPropertyPath => filterPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TProp);

        /// <inheritdoc />
        public override Type FilterElementType => ElementType ?? typeof(TProp);

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            base.OnParametersSet();

            // Equivalent rather than ReferenceEquals: Razor hands this a freshly built expression tree on
            // every render, so reference equality never holds for a column authored in markup and the
            // column recompiled per render. Measured at 5x the render cost of a grid that did not.
            if (format == Format
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

            // Built by a static method rather than inline: the lambdas below capture the compiled getter,
            // and a lambda capturing a local makes the compiler allocate the enclosing method's display
            // class on entry - here, on every parameter set of every column, taken branch or not.
            cellText = BuildCellText(Property.Compile(), Separator, Format is { Length: > 0 } ? Format : null);

            var propertyPath = PropertyPathResolver.For(Property);

            path = SortBy is null ? propertyPath : PropertyPathResolver.For(SortBy);

            // Filtering follows the displayed property, not the sort key: a column that displays First
            // and sorts by Last still filters on what the reader can see. A computed column has no path
            // of its own, so an explicit sort key is the only one it can offer.
            filterPath = FilterBy is not null ? PropertyPathResolver.For(FilterBy) : propertyPath ?? path;
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

            if (underlying is not null && typeof(IFormattable).IsAssignableFrom(underlying))
            {
                return Formatter(NullableFormatterMethod, underlying, get, format);
            }

            if (typeof(TProp).IsValueType && typeof(IFormattable).IsAssignableFrom(typeof(TProp)))
            {
                return Formatter(ValueFormatterMethod, typeof(TProp), get, format);
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

            if (!IsCollection)
            {
                return source.Select(Property).Distinct();
            }

            // TProp is the collection, so the element type is not a type parameter here and SelectMany
            // has to be built by hand. CollectionColumn<TItem, TElement> has it as a parameter and does
            // this as an ordinary generic call.
            return Projection
                .SelectMany(source, typeof(TItem), ElementType!, AsSequenceSelector())
                .Distinct();
        }

        /// <summary>
        /// The property expression retyped as returning <c>IEnumerable&lt;TElement&gt;</c>, which is what
        /// SelectMany's signature demands - a lambda returning <c>List&lt;T&gt;</c> is not the same
        /// delegate type, however assignable the values are.
        /// </summary>
        LambdaExpression AsSequenceSelector()
        {
            var sequenceType = typeof(IEnumerable<>).MakeGenericType(ElementType!);

            if (typeof(TProp) == sequenceType)
            {
                return Property;
            }

            // A widening reference conversion, which every provider strips before translating.
            return Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(TItem), sequenceType),
                Expression.Convert(Property.Body, sequenceType),
                Property.Parameters);
        }

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, cellText!(item));

        /// <summary>
        /// A collection column has nothing to order by - no provider can sort rows by a list - so it is
        /// sortable only when an explicit <see cref="SortBy" /> names something that can be. A column
        /// typed as <c>object</c> whose values happen to be collections cannot be recognised statically
        /// and stays sortable; give it a real type, or set <c>Sortable="false"</c>.
        /// </summary>
        public override bool CanSort => Sortable && path is not null && (!IsCollection || SortBy is not null);

        /// <summary>
        /// The element type of a collection-valued property, or null when the property is a single value.
        /// </summary>
        static Type? CollectionElementType(Type type)
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
        public override IOrderedQueryable<TItem> ApplySort(IQueryable<TItem> source, bool descending)
        {
            var expression = SortBy ?? Property;

            return descending ? source.OrderByDescending(expression) : source.OrderBy(expression);
        }
    }
}
