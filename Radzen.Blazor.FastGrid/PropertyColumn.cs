using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
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
        string? filterProperty;
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
        public bool IsCollection { get; private set; }

        string? path;

        /// <inheritdoc />
        public override string? PropertyPath => path;

        /// <summary>
        /// The property to filter by, when it differs from the one displayed. Must be of the same type;
        /// a column filtered on an unrelated property is a different column.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? FilterBy { get; set; }

        string? filterPath;

        // Once per closed generic type, not once per column: GetInterfaces() allocates, and the answer
        // depends only on TProp. Measured at ~240 B per column when it was computed per instance.
        static readonly Type? ElementType = CollectionElementType(typeof(TProp));

        /// <inheritdoc />
        public override string? HeaderText => Title ?? path;

        /// <inheritdoc />
        public override string? FilterPropertyPath => filterPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TProp);

        /// <inheritdoc />
        public override Type FilterElementType => filterMemberType ?? ElementType ?? typeof(TProp);

        Type? filterMemberType;

        /// <summary>
        /// The type at the end of <see cref="ColumnBase{TItem}.FilterProperty" />, which is what a filter
        /// value is compared against once the path is followed. Without it a filter on
        /// <c>Accounts</c>/<c>Name</c> would be converted to a Company and silently dropped.
        /// </summary>
        Type? ResolveFilterMemberType()
        {
            if (string.IsNullOrEmpty(FilterProperty))
            {
                return null;
            }

            var type = ElementType ?? typeof(TProp);

            foreach (var step in FilterProperty.Split('.'))
            {
                var member = type.GetProperty(step);

                if (member is null)
                {
                    return null;
                }

                type = member.PropertyType;
            }

            return type;
        }

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            base.OnParametersSet();

            if (ReferenceEquals(property, Property) && format == Format && ReferenceEquals(sortBy, SortBy)
                && ReferenceEquals(filterBy, FilterBy) && filterProperty == FilterProperty)
            {
                return;
            }

            property = Property;
            sortBy = SortBy;
            filterBy = FilterBy;
            filterProperty = FilterProperty;
            format = Format;
            filterMemberType = ResolveFilterMemberType();

            // Compile to a Func<TItem, string> rather than reading the value as object. RenderTreeBuilder
            // has no generic AddContent<T>, so handing it a value type binds the object overload, which
            // boxes and then stringifies; producing the string directly skips the box.
            var compiled = Property.Compile();

            // A collection is listed, not stringified: List<string>.ToString() is the type name, which
            // is why every such column needed a Template that did nothing but string.Join.
            IsCollection = ElementType is not null;

            if (IsCollection || typeof(TProp) == typeof(object))
            {
                var separator = Separator;
                var formatString = Format is { Length: > 0 } ? Format : null;

                // TProp = object cannot say statically whether the value is a collection, so the value
                // decides. A typed collection column takes the same path; the test is one type check.
                cellText = item => compiled(item) switch
                {
                    null => null,
                    string text => Text(text, formatString),
                    IEnumerable sequence => Join(sequence, separator, formatString),
                    var value => Text(value, formatString),
                };
            }
            else if (Format is not { Length: > 0 } f)
            {
                cellText = item => compiled(item)?.ToString();
            }
            else if (typeof(IFormattable).IsAssignableFrom(Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp)))
            {
                // Nullable<T> does not itself implement IFormattable even when T does, so the underlying
                // type is what decides. Casting to IFormattable boxes, but only on the format path.
                cellText = item => ((IFormattable?)(object?)compiled(item))?.ToString(f, CultureInfo.CurrentCulture);
            }
            else
            {
                // TProp is object, or another type that says nothing about the value. Ask the value.
                cellText = item =>
                {
                    var value = compiled(item);

                    return value is IFormattable formattable
                        ? formattable.ToString(f, CultureInfo.CurrentCulture)
                        : value?.ToString();
                };
            }

            var propertyPath = PropertyPathResolver.For(Property);

            path = SortBy is null ? propertyPath : PropertyPathResolver.For(SortBy);

            // Filtering follows the displayed property, not the sort key: a column that displays First
            // and sorts by Last still filters on what the reader can see. A computed column has no path
            // of its own, so an explicit sort key is the only one it can offer.
            filterPath = FilterBy is not null ? PropertyPathResolver.For(FilterBy) : propertyPath ?? path;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Composed rather than enumerated, so an Entity Framework source runs SELECT DISTINCT rather
        /// than pulling every row across the wire. A collection column projects its members, and
        /// <see cref="ColumnBase{TItem}.FilterProperty" /> then picks a member of each.
        /// </remarks>
        public override IQueryable? DistinctValues(IQueryable<TItem> source)
        {
            if (source is null || Property is null)
            {
                return null;
            }

            if (!IsCollection)
            {
                return string.IsNullOrEmpty(FilterProperty)
                    ? source.Select(Property).Distinct()
                    : Project(source.Select(Property), typeof(TProp), FilterProperty);
            }

            var elements = SelectMany(source);

            return string.IsNullOrEmpty(FilterProperty)
                ? elements.Distinct()
                : Project(elements, ElementType!, FilterProperty);
        }

        static readonly MethodInfo SelectManyMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.SelectMany)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                    .GetGenericArguments().Length == 2);

        /// <summary>
        /// <c>source.SelectMany(Property)</c>, built by hand because the element type is not a type
        /// parameter of this column and so cannot be written as a generic call.
        /// </summary>
        IQueryable SelectMany(IQueryable<TItem> source) => (IQueryable)SelectManyMethod
            .MakeGenericMethod(typeof(TItem), ElementType!)
            .Invoke(null, new object[] { source, AsSequenceSelector() })!;

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

        /// <summary>Projects a member of each element, for a lookup over objects rather than values.</summary>
        static IQueryable Project(IQueryable source, Type type, string member)
        {
            var parameter = Expression.Parameter(type, "x");
            var body = (Expression)parameter;

            foreach (var step in member.Split('.'))
            {
                var property = body.Type.GetProperty(step);

                if (property is null)
                {
                    return source;
                }

                body = Expression.Property(body, property);
            }

            var selector = Expression.Lambda(body, parameter);

            var select = SelectMethod.MakeGenericMethod(type, body.Type);
            var projected = (IQueryable)select.Invoke(null, new object[] { source, selector })!;

            return (IQueryable)DistinctMethod.MakeGenericMethod(body.Type)
                .Invoke(null, new object[] { projected })!;
        }

        static readonly MethodInfo SelectMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Select)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                    .GetGenericArguments().Length == 2);

        static readonly MethodInfo DistinctMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Distinct) && m.GetParameters().Length == 1);

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
            // A string is a sequence of characters and would otherwise be listed one letter at a time.
            // An array needs no case of its own: it implements IEnumerable<T>, which the loop below finds.
            if (type == typeof(string))
            {
                return null;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return type.GetGenericArguments()[0];
            }

            foreach (var contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return contract.GetGenericArguments()[0];
                }
            }

            return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
        }

        static string? Text(object? value, string? format) =>
            format is not null && value is IFormattable formattable
                ? formattable.ToString(format, CultureInfo.CurrentCulture)
                : value?.ToString();

        /// <summary>
        /// Lists the members of a sequence. A cell of a collection column allocates a string; that is
        /// unavoidable and still cheaper than the render fragment a template would have cost.
        /// </summary>
        static string Join(IEnumerable sequence, string separator, string? format)
        {
            var enumerator = sequence.GetEnumerator();

            try
            {
                if (!enumerator.MoveNext())
                {
                    return string.Empty;
                }

                var first = Text(enumerator.Current, format);

                if (!enumerator.MoveNext())
                {
                    // One member is the common case for a small collection, and needs no builder.
                    return first ?? string.Empty;
                }

                var builder = new StringBuilder(first).Append(separator).Append(Text(enumerator.Current, format));

                while (enumerator.MoveNext())
                {
                    builder.Append(separator).Append(Text(enumerator.Current, format));
                }

                return builder.ToString();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem> ApplySort(IQueryable<TItem> source, bool descending)
        {
            var expression = SortBy ?? Property;

            return descending ? source.OrderByDescending(expression) : source.OrderBy(expression);
        }
    }
}
