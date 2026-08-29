using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
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

        string? path;

        /// <inheritdoc />
        public override string? PropertyPath => path;

        /// <summary>
        /// The property to filter by, when it differs from the one displayed. Must be of the same type;
        /// a column filtered on an unrelated property is a different column.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? FilterBy { get; set; }

        string? filterPath;

        /// <inheritdoc />
        public override string? HeaderText => Title ?? path;

        /// <inheritdoc />
        public override string? FilterPropertyPath => filterPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TProp);

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            base.OnParametersSet();

            if (ReferenceEquals(property, Property) && format == Format && ReferenceEquals(sortBy, SortBy)
                && ReferenceEquals(filterBy, FilterBy))
            {
                return;
            }

            property = Property;
            sortBy = SortBy;
            filterBy = FilterBy;
            format = Format;

            // Compile to a Func<TItem, string> rather than reading the value as object. RenderTreeBuilder
            // has no generic AddContent<T>, so handing it a value type binds the object overload, which
            // boxes and then stringifies; producing the string directly skips the box.
            var compiled = Property.Compile();

            if (Format is not { Length: > 0 } f)
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
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, cellText!(item));

        /// <inheritdoc />
        public override IOrderedQueryable<TItem> ApplySort(IQueryable<TItem> source, bool descending)
        {
            var expression = SortBy ?? Property;

            return descending ? source.OrderByDescending(expression) : source.OrderBy(expression);
        }
    }
}
