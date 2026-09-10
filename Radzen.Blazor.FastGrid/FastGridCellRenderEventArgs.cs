using System;
using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The cell about to be drawn, and the attributes to draw it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of RadzenDataGrid's <c>DataGridCellRenderEventArgs</c>, which cannot be reused
    /// here: its column is typed as <c>RadzenDataGridColumn</c>.
    /// </para>
    /// <para>
    /// <b>One instance is reused for every cell of a render.</b> Read what you need inside the handler
    /// and write what you want onto <see cref="Attributes" />; do not keep the object, or anything it
    /// hands you, past the end of the call - by the next cell it describes that cell instead. The grid
    /// reads <see cref="Attributes" /> into the render tree before the handler is called again, so
    /// writing to it is always safe; only holding on to it is not.
    /// </para>
    /// <para>
    /// Measured, that is the whole difference between this hook being usable and not: one arguments
    /// object per cell costs 195 KB at 1000 rows x 5 columns before a handler does anything at all.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    public sealed class FastGridCellRenderEventArgs<TItem> : EventArgs
    {
        internal FastGridCellRenderEventArgs()
        {
        }

        internal FastGridCellRenderEventArgs(TItem? data, ColumnBase<TItem> column)
        {
            Data = data;
            Column = column;
        }

        /// <summary>The row the cell is in, or the type's default for a header or footer cell.</summary>
        public TItem? Data { get; private set; }

        /// <summary>The column the cell is in.</summary>
        public ColumnBase<TItem> Column { get; private set; } = null!;

        Dictionary<string, object>? attributes;

        /// <summary>
        /// HTML attributes to apply to the cell element. Allocated on first use and then kept, so a
        /// handler that writes to it pays for the dictionary once rather than once per cell.
        /// </summary>
        public IDictionary<string, object> Attributes => attributes ??= new Dictionary<string, object>();

        /// <summary>Whether anything was written, without allocating the dictionary to find out.</summary>
        internal bool HasAttributes => attributes is { Count: > 0 };

        /// <summary>The attributes to splat, or null when the handler wrote none.</summary>
        internal IDictionary<string, object>? Written => HasAttributes ? attributes : null;

        /// <summary>Points these arguments at the next cell, discarding what was written for the last.</summary>
        /// <remarks>
        /// Clearing here rather than after the splat is what makes a handler that throws harmless: the
        /// next cell starts empty whether or not the last one finished.
        /// </remarks>
        internal void Reset(TItem? data, ColumnBase<TItem> column)
        {
            Data = data;
            Column = column;

            attributes?.Clear();
        }
    }
}
