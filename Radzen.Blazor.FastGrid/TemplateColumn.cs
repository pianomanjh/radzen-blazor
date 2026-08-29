using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column whose cells are rendered by a template.
    /// </summary>
    /// <remarks>
    /// Measured at roughly 94 bytes per cell more than a <see cref="PropertyColumn{TItem, TProp}" />,
    /// because a template is a <see cref="RenderFragment" /> invoked per cell. That is the price of
    /// arbitrary cell content; prefer a property column where the cell is just a value.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    public sealed class TemplateColumn<TItem> : ColumnBase<TItem>
    {
        /// <summary>The content rendered for each cell.</summary>
        [Parameter] public RenderFragment<TItem>? Template { get; set; }

        /// <summary>
        /// The property path this column sorts and persists by. A template has no expression to derive
        /// one from, so it must be given explicitly for the column to be sortable.
        /// </summary>
        [Parameter] public string? SortProperty { get; set; }

        /// <inheritdoc />
        public override string? PropertyPath => SortProperty;

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
        {
            if (Template is not null)
            {
                builder.AddContent(sequence, Template(item));
            }
        }
    }
}
