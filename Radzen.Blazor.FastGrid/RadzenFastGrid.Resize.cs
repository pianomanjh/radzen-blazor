using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    // Column resizing. The drag itself has to run in the browser: on Blazor Server a pointermove
    // round trip per frame is not a drag, it is a slideshow.
    public partial class RadzenFastGrid<TItem>
    {
        [Inject] private IJSRuntime? JSRuntime { get; set; }

        DotNetObjectReference<RadzenFastGrid<TItem>>? selfReference;

        /// <summary>Whether columns can be resized by dragging the edge of their header.</summary>
        /// <remarks>
        /// Off by default, and nothing is emitted for it while it is off: no handle, no column ids, and
        /// no reference handed to the browser.
        /// </remarks>
        [Parameter] public bool AllowColumnResize { get; set; }

        /// <summary>Raised when a drag settles, with the column and the width it landed on.</summary>
        [Parameter] public EventCallback<FastGridColumnResizedEventArgs<TItem>> ColumnResized { get; set; }

        string? elementId;

        /// <summary>
        /// Identifies this grid's elements to the resize script. Created on first use, which is the
        /// first render of a grid that allows resizing and never for one that does not.
        /// </summary>
        string ElementId => elementId ??= "rzfg-" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// The ids the resize script resolves a column by: the <c>col</c> it writes widths to, and the
        /// handle it reads the dragged cell from. The '-col' suffix is the shape that script matches on.
        /// </summary>
        /// <remarks>Cached on the column, so a render that changes nothing allocates no strings.</remarks>
        internal (string Base, string Col, string Resizer) ColumnElementIds(int index) =>
            visibleColumns[index].ElementIds(ElementId, index);

        async Task StartColumnResize(int index, double clientX)
        {
            if (JSRuntime is null)
            {
                return;
            }

            selfReference ??= DotNetObjectReference.Create(this);

            await JSRuntime.InvokeVoidAsync("Radzen.startColumnResize",
                ColumnElementIds(index).Base, selfReference, index, clientX);
        }

        /// <summary>
        /// Called by the resize script when a drag settles and every visible column's width is known.
        /// </summary>
        /// <remarks>
        /// The method name is the script's, not this grid's: it is the same global that drives
        /// RadzenDataGrid and it invokes a fixed name on whatever reference it was handed.
        /// </remarks>
        [JSInvokable("RadzenGrid.OnColumnsResized")]
        public async Task OnColumnsResized(int columnIndex, double width, double[] widths)
        {
            if (widths is null)
            {
                return;
            }

            for (var i = 0; i < widths.Length && i < visibleColumns.Count; i++)
            {
                // Zero is the script's way of saying "this column was not pinned to a width", which is
                // every column it did not have to hold still. Overwriting those would freeze columns the
                // user never touched at whatever they happened to measure.
                if (widths[i] > 0)
                {
                    visibleColumns[i].SetResizedWidth(
                        string.Create(CultureInfo.InvariantCulture, $"{widths[i]}px"));
                }
            }

            if (columnIndex >= 0 && columnIndex < visibleColumns.Count)
            {
                await RaiseColumnResized(visibleColumns[columnIndex], width);
            }

            StateHasChanged();
        }

        /// <summary>The single-column form, used when the script could not read a full colgroup.</summary>
        [JSInvokable("RadzenGrid.OnColumnResized")]
        public async Task OnColumnResized(int columnIndex, double width)
        {
            if (columnIndex < 0 || columnIndex >= visibleColumns.Count)
            {
                return;
            }

            visibleColumns[columnIndex].SetResizedWidth(
                string.Create(CultureInfo.InvariantCulture, $"{width}px"));

            await RaiseColumnResized(visibleColumns[columnIndex], width);

            StateHasChanged();
        }

        async Task RaiseColumnResized(ColumnBase<TItem> column, double width)
        {
            if (ColumnResized.HasDelegate)
            {
                await ColumnResized.InvokeAsync(new FastGridColumnResizedEventArgs<TItem>(column, width));
            }

            // A width is state a user chose, so it belongs in the settings for the same reason a sort
            // does. This is not RefreshAsync: nothing about the data changed, only how wide it is drawn.
            if (SettingsChanged.HasDelegate)
            {
                await SettingsChanged.InvokeAsync(CaptureSettings());
            }
        }
    }

    /// <summary>The column a drag settled on, and the width it settled at.</summary>
    public sealed class FastGridColumnResizedEventArgs<TItem> : EventArgs
    {
        internal FastGridColumnResizedEventArgs(ColumnBase<TItem> column, double width)
        {
            Column = column;
            Width = width;
        }

        /// <summary>The column that was dragged.</summary>
        public ColumnBase<TItem> Column { get; }

        /// <summary>Its new width, in pixels.</summary>
        public double Width { get; }
    }
}
