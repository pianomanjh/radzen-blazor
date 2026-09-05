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
        /// How this column sorts.
        /// </summary>
        /// <remarks>
        /// A template has no expression of its own to order by, so it has to be told - and being told
        /// as a <see cref="FastGridSort{TItem}" /> rather than as a path is what makes the column able
        /// to sort at all. <see cref="SortProperty" /> names a path for a server to sort by, and a
        /// server is the only thing that ever sorted by it.
        /// </remarks>
        [Parameter] public FastGridSort<TItem>? SortBy { get; set; }

        /// <summary>
        /// The property path this column persists by, and sorts by when the sorting is done by a
        /// <c>LoadData</c> handler rather than by the grid.
        /// </summary>
        /// <remarks>
        /// On its own this sorts nothing locally, and used not to say so: the header was clickable, the
        /// sort was recorded, the indicator was drawn, and the rows did not move, because a path is not
        /// something the grid can order by without reaching members by name. Set <see cref="SortBy" />
        /// for a grid that sorts its own rows; <see cref="SortBy" />'s own path is used for
        /// <c>LoadData</c> when both are set, so one column can do both with one declaration.
        /// </remarks>
        [Parameter] public string? SortProperty { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// Not the base's answer, which is the sort's path alone: this column can name a path with no
        /// sort behind it, and a <c>LoadData</c> grid orders by exactly that.
        /// </remarks>
        public override string? SortPath => SortBy?.Path ?? SortProperty;

        /// <inheritdoc />
        /// <remarks>
        /// A path alone still makes the column sortable, because a <c>LoadData</c> grid sorts by it.
        /// </remarks>
        public override bool CanSort => Sortable && (SortBy is not null || SortProperty is not null);

        /// <inheritdoc />
        internal override FastGridSort<TItem>? SortSource => SortBy;

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
