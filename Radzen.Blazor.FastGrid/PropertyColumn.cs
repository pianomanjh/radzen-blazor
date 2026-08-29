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

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (ReferenceEquals(property, Property) && format == Format && ReferenceEquals(sortBy, SortBy))
            {
                return;
            }

            property = Property;
            sortBy = SortBy;
            format = Format;

            // Compile to a Func<TItem, string> rather than reading the value as object. RenderTreeBuilder
            // has no generic AddContent<T>, so handing it a value type binds the object overload, which
            // boxes and then stringifies; producing the string directly skips the box.
            var compiled = Property.Compile();

            cellText = Format is { Length: > 0 } f && typeof(IFormattable).IsAssignableFrom(typeof(TProp))
                ? item => ((IFormattable?)compiled(item))?.ToString(f, CultureInfo.CurrentCulture)
                : item => compiled(item)?.ToString();

            path = PropertyPathResolver.For(SortBy ?? Property);

            Title ??= path;
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
