using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Identifies the cell a cell-level event happened in.
    /// </summary>
    /// <remarks>
    /// Allocated when the event fires, never while rendering. RadzenDataGrid's own
    /// <c>DataGridCellMouseEventArgs</c> cannot be reused here: its column is typed as
    /// <c>RadzenDataGridColumn</c>, and its members are settable only from inside Radzen.Blazor.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    public sealed class FastGridCellEventArgs<TItem> : EventArgs
    {
        /// <summary>Creates the arguments for a cell event.</summary>
        /// <param name="data">The row the cell is in.</param>
        /// <param name="column">The column the cell is in.</param>
        public FastGridCellEventArgs(TItem data, ColumnBase<TItem> column)
        {
            Data = data;
            Column = column;
        }

        /// <summary>The row the cell is in.</summary>
        public TItem Data { get; }

        /// <summary>The column the cell is in.</summary>
        public ColumnBase<TItem> Column { get; }
    }
}
