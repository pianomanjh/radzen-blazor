using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    // Column reordering. Like resize, the drag runs in the browser - it carries a floating copy of the
    // header under the pointer, which is not something a round trip per frame can do - and the grid is
    // told only where it settled.
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>Whether columns can be moved by dragging their header.</summary>
        /// <remarks>
        /// Off by default, and nothing is emitted for it while it is off: no handle, no column index on
        /// the header, and no id on the grid for the script to attach to.
        /// </remarks>
        [Parameter] public bool AllowColumnReorder { get; set; }

        /// <summary>Raised before a drop is applied, and able to call it off.</summary>
        [Parameter] public EventCallback<FastGridColumnReorderingEventArgs<TItem>> ColumnReordering { get; set; }

        /// <summary>Raised after a drop has moved the column.</summary>
        [Parameter] public EventCallback<FastGridColumnReorderedEventArgs<TItem>> ColumnReordered { get; set; }

        // Which column the pointer picked up, or null while nothing is being dragged. The drop is
        // reported by the header it landed on, so the source has to outlive the mousedown that set it.
        int? draggedColumnIndex;

        async Task StartColumnReorder(int index)
        {
            draggedColumnIndex = index;

            if (JSRuntime is null)
            {
                return;
            }

            selfReference ??= DotNetObjectReference.Create(this);

            await JSRuntime.InvokeVoidAsync("Radzen.startColumnReorder",
                ColumnElementIds(index).Base, ElementId, selfReference);
        }

        // The drop, as the mouse reports it: whichever header the button came up over. A mouseup with
        // nothing picked up is every ordinary click on a header, so it has to be cheap and silent.
        Task EndColumnReorder(int index)
        {
            if (draggedColumnIndex is not { } from)
            {
                return Task.CompletedTask;
            }

            draggedColumnIndex = null;

            return ReorderColumn(from, index);
        }

        /// <summary>
        /// Called by the reorder script when a touch drag settles, with the header it was released
        /// over.
        /// </summary>
        /// <remarks>
        /// Touch has no mouseup to land on, so the script resolves the drop itself through
        /// <c>elementFromPoint</c> and reads the position off the header's <c>data-column-index</c>.
        /// The method name is the script's, not this grid's.
        /// </remarks>
        [JSInvokable("RadzenGrid.OnColumnReorderEnded")]
        public Task OnColumnReorderEnded(int columnIndex) => EndColumnReorder(columnIndex);

        /// <summary>Moves the column drawn at <paramref name="from" /> to position <paramref name="to" />.</summary>
        /// <remarks>
        /// Public because it is the whole feature without a browser: a drag ends here, and so does a
        /// caller arranging columns itself.
        /// </remarks>
        /// <param name="from">The position of the column to move.</param>
        /// <param name="to">The position to move it to.</param>
        public async Task ReorderColumn(int from, int to)
        {
            if (from == to || from < 0 || to < 0
                || from >= visibleColumns.Count || to >= visibleColumns.Count)
            {
                return;
            }

            var moved = visibleColumns[from];

            if (ColumnReordering.HasDelegate)
            {
                var reordering = new FastGridColumnReorderingEventArgs<TItem>(moved, visibleColumns[to]);

                await ColumnReordering.InvokeAsync(reordering);

                if (reordering.Cancel)
                {
                    return;
                }
            }

            // Every visible column is given its position outright, rather than only the one that moved.
            //
            // RadzenDataGrid reorders by removing the column from its own list and inserting it again,
            // which this grid cannot do: that list is rebuilt from column registration, so the move
            // would be undone by anything that re-registers. Writing the whole arrangement down instead
            // survives that, and survives a round trip through the settings - but it has to be the whole
            // arrangement. Recording an index for the moved column alone would leave the others to the
            // interleaving rule in RefreshVisibleColumns, which fills gaps in declaration order and so
            // would answer a one-column drag with a different arrangement than the one dragged to.
            visibleColumns.RemoveAt(from);
            visibleColumns.Insert(to, moved);

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                visibleColumns[i].SetReorderedIndex(i);
            }

            if (ColumnReordered.HasDelegate)
            {
                await ColumnReordered.InvokeAsync(new FastGridColumnReorderedEventArgs<TItem>(moved, to));
            }

            // Where a column sits is state a user chose, so it is stored for the same reason a width is.
            // Not RefreshAsync: nothing about the data changed, only the order it is drawn in.
            if (SettingsChanged.HasDelegate)
            {
                await SettingsChanged.InvokeAsync(CaptureSettings());
            }

            StateHasChanged();
        }
    }

    /// <summary>A drop that has not been applied yet, and the chance to call it off.</summary>
    public sealed class FastGridColumnReorderingEventArgs<TItem> : EventArgs
    {
        internal FastGridColumnReorderingEventArgs(ColumnBase<TItem> column, ColumnBase<TItem> toColumn)
        {
            Column = column;
            ToColumn = toColumn;
        }

        /// <summary>The column being dragged.</summary>
        public ColumnBase<TItem> Column { get; }

        /// <summary>The column it was dropped on, whose place it is about to take.</summary>
        public ColumnBase<TItem> ToColumn { get; }

        /// <summary>Set to true to leave the order alone.</summary>
        public bool Cancel { get; set; }
    }

    /// <summary>The column a drag moved, and where it landed.</summary>
    public sealed class FastGridColumnReorderedEventArgs<TItem> : EventArgs
    {
        internal FastGridColumnReorderedEventArgs(ColumnBase<TItem> column, int orderIndex)
        {
            Column = column;
            OrderIndex = orderIndex;
        }

        /// <summary>The column that was dragged.</summary>
        public ColumnBase<TItem> Column { get; }

        /// <summary>Its new position among the columns being drawn.</summary>
        public int OrderIndex { get; }
    }
}
