using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    // Row and cell clicks, raised from one listener on the tbody rather than from a delegate per cell.
    //
    // A cell click bound in the render tree costs ~296 B per cell - 1,483 KB at 1000 rows x 5 columns,
    // by a distance the most expensive thing this grid can be asked to do. The browser already routes a
    // click to its ancestors, so the delegates buy nothing that delegation does not.
    //
    // The grid renders without them and puts them back if the script does not confirm it attached. That
    // is not belt and braces: it is what keeps the feature working under bUnit, which has no DOM
    // listeners at all, and in any browser where the module fails to load.
    public partial class RadzenFastGrid<TItem>
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        IJSObjectReference? clickModule;
        DotNetObjectReference<RadzenFastGrid<TItem>>? clickReference;
        bool clickAttachAttempted;

        /// <summary>
        /// Which events the attached listener was told to answer, so the grid can tell when what it
        /// needs has changed. A callback switched on after the first render - row detail is the ordinary
        /// case, since a Template can arrive with the data - would otherwise leave the listener answering
        /// the wrong set, and the feature simply would not work.
        /// </summary>
        (bool Click, bool DoubleClick, bool ContextMenu) attachedKinds;

        (bool Click, bool DoubleClick, bool ContextMenu) CurrentKinds => (
            RowClick.HasDelegate || CellClick.HasDelegate || SelectsOnRowClick || ExpandColumn,
            RowDoubleClick.HasDelegate,
            CellContextMenu.HasDelegate);

        /// <summary>
        /// Whether the click handlers have to be in the render tree. Starts false, so a grid in a
        /// browser renders the cheap shape from the very first frame and never renders the expensive
        /// one at all; a grid that cannot attach a listener renders once without handlers and then once
        /// with them.
        /// </summary>
        /// <remarks>
        /// The order matters more than it looks. Starting true and dropping the handlers on success
        /// would make every browser grid pay the full per-cell cost once and then re-render to undo it -
        /// the cost this exists to remove, still paid, plus a render. Starting false pays a second
        /// render only where the listener could not be attached, which is a test host or a broken
        /// deployment rather than the normal case.
        /// </remarks>
        bool clicksNeedHandlers;

        /// <summary>
        /// Whether anything in the body answers a pointer. The row-detail toggle counts: it is a button
        /// per row, and it was the last delegate per row this grid rendered.
        /// </summary>
        bool ClicksAreLive => RowClick.HasDelegate || RowDoubleClick.HasDelegate
            || CellClick.HasDelegate || CellContextMenu.HasDelegate || SelectsOnRowClick || ExpandColumn;

        /// <summary>
        /// Whether one listener answers for the whole body. Virtualization is excluded deliberately:
        /// there the grid renders a window of some tens of rows rather than all of them, so the cost
        /// this replaces is a few tens of kilobytes rather than one and a half megabytes - and the row
        /// index the listener needs cannot be derived, because Virtualize hands its ChildContent an
        /// item and no position.
        /// </summary>
        internal bool ClicksAreDelegated => ClicksAreLive && !AllowVirtualization && !clicksNeedHandlers;

        /// <summary>The id of the tbody the listener is attached to.</summary>
        internal string BodyElementId => ElementId + "-body";

        // Index strings for the rows' data-r, so writing one costs a frame and not an allocation. A grid
        // paging beyond this falls back to ToString for the rows past it rather than growing the table.
        static readonly string[] RowIndexStrings = CreateRowIndexStrings(512);

        static string[] CreateRowIndexStrings(int count)
        {
            var indexes = new string[count];

            for (var i = 0; i < count; i++)
            {
                indexes[i] = i.ToString(CultureInfo.InvariantCulture);
            }

            return indexes;
        }

        internal static string RowIndexString(int index) => index < RowIndexStrings.Length
            ? RowIndexStrings[index]
            : index.ToString(CultureInfo.InvariantCulture);

        async Task AttachClicksAsync()
        {
            if (!ClicksAreLive || AllowVirtualization || JSRuntime is null)
            {
                return;
            }

            var kinds = CurrentKinds;

            // Attach once, and again whenever what the grid listens for changes. The second case is not
            // hypothetical: switching row detail on after the first render leaves a toggle the listener
            // was never told about, and the button does nothing.
            if (clickAttachAttempted && (clicksNeedHandlers || kinds == attachedKinds))
            {
                return;
            }

            clickAttachAttempted = true;
            attachedKinds = kinds;

            try
            {
                clickModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
                clickReference ??= DotNetObjectReference.Create(this);

                var attached = await clickModule.InvokeAsync<bool>("attach", BodyElementId, clickReference,
                    new
                    {
                        click = kinds.Click,
                        doubleClick = kinds.DoubleClick,
                        contextMenu = kinds.ContextMenu,
                    });

                if (attached)
                {
                    return;
                }

                FallBackToHandlers();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Deliberately every exception. This path is an optimization with a correct fallback, so
                // there is no failure it can report that is worth more than the grid continuing to work
                // - and the ways it fails are not enumerable from here. A browser that could not fetch
                // the module raises JSException; a circuit torn down mid-import raises one of several
                // cancellation or disposal types; and bUnit's strict mode, which is the default and so
                // is what every consumer's test suite runs, raises a bUnit type this package cannot
                // name. Narrowing this once let that last one escape OnAfterRenderAsync and fail every
                // test that rendered a grid with a click handler.
                FallBackToHandlers();
            }
        }

        /// <summary>
        /// Puts the click handlers back in the markup, for a grid whose listener never attached.
        /// </summary>
        void FallBackToHandlers()
        {
            if (clicksNeedHandlers)
            {
                return;
            }

            clicksNeedHandlers = true;

            StateHasChanged();
        }

        /// <summary>
        /// Raised by the listener for one pointer event, with the row's index and the browser's own cell
        /// index. A click that missed a cell reports -1 and still counts as a row click.
        /// </summary>
        [JSInvokable("RadzenFastGrid.OnDelegatedPointer")]
        public async Task OnDelegatedPointer(string kind, int rowIndex, int cellIndex)
        {
            if (ResolveRow(rowIndex) is not { } item)
            {
                return;
            }

            var column = ResolveColumn(cellIndex);

            switch (kind)
            {
                case "click":
                    await OnRowClick(item).ConfigureAwait(false);

                    if (column is not null && CellClick.HasDelegate)
                    {
                        await CellClick.InvokeAsync(new FastGridCellEventArgs<TItem>(item, column));
                    }

                    break;

                case "toggle":
                    if (ExpandColumn)
                    {
                        await ToggleRow(item).ConfigureAwait(false);
                    }

                    break;

                case "dblclick":
                    await RowDoubleClick.InvokeAsync(item);

                    break;

                case "contextmenu":
                    if (column is not null)
                    {
                        await CellContextMenu.InvokeAsync(new FastGridCellEventArgs<TItem>(item, column));
                    }

                    break;
            }
        }

        /// <summary>
        /// The row at a rendered position. Read from the view rather than from a list kept alongside it:
        /// the view is what the render walked, so it cannot drift out of step with the markup, and a
        /// click is rare enough that walking to the index costs nothing worth saving.
        /// </summary>
        TItem? ResolveRow(int index)
        {
            if (index < 0)
            {
                return default;
            }

            var i = 0;

            foreach (var item in View())
            {
                if (i++ == index)
                {
                    return item;
                }
            }

            return default;
        }

        /// <summary>
        /// The column at a rendered cell index, allowing for the expand column the browser counts and
        /// the grid does not.
        /// </summary>
        ColumnBase<TItem>? ResolveColumn(int cellIndex)
        {
            if (cellIndex < 0)
            {
                return null;
            }

            var index = ExpandColumn ? cellIndex - 1 : cellIndex;

            return index >= 0 && index < visibleColumns.Count ? visibleColumns[index] : null;
        }

        async ValueTask DisposeClicksAsync()
        {
            if (clickModule is not null)
            {
                try
                {
                    await clickModule.InvokeVoidAsync("detach", BodyElementId);
                    await clickModule.DisposeAsync();
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                    // The circuit being gone already is the ordinary way this component is disposed, and
                    // there is nothing to release when it is. Every exception, for the same reason as the
                    // attach: teardown has no caller to report to, and an exception escaping here is
                    // unhandled in the circuit rather than handled anywhere.
                    //
                    // Narrower did not work. JSDisconnectedException derives from Exception and not from
                    // JSException, so catching the JS types missed the one case this is actually for, and
                    // every navigation away from a grid logged an unhandled circuit exception.
                }

                clickModule = null;
            }

        }
    }
}
