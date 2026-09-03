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

        // Small-integer strings, so writing one into the markup costs a frame and not an allocation.
        // Shared: the rows' data-r, aria-rowindex, aria-colindex and aria-colcount all draw on it -
        // which is why it is not named for rows any more, even though it lives beside the feature that
        // needed it first.
        //
        // It grows to fit rather than stopping at a fixed size, and the reason is a measurement: the
        // table used to hold 512, a thousand-row grid therefore called ToString on 488 rows of every
        // render, and that came to 16 KB - which this branch spent a while attributing to the render
        // frame instead. Growing it takes a row-indexing attribute from +16 KB to +0.7 KB. The frame
        // really is nearly free; the string was not, and only because the table ran out.
        //
        // Doubling to a bound rather than to the index asked for, so a grid scrolled to row 900,000
        // does not build nine hundred thousand strings for the tens of rows it can show. Past the
        // bound it is ToString again, which is where it started - but a virtualized window is tens of
        // rows and a page is a page, so nothing renders enough of them for that to matter.
        const int MaxIndexStrings = 16384;

        static string[] indexStrings = CreateIndexStrings(512);

        static string[] CreateIndexStrings(int count)
        {
            var indexes = new string[count];

            for (var i = 0; i < count; i++)
            {
                indexes[i] = i.ToString(CultureInfo.InvariantCulture);
            }

            return indexes;
        }

        internal static string IndexString(int index)
        {
            // Read once. Another circuit may swap the table in between, and either the old one or the
            // new one answers correctly - they hold the same strings for the same indexes.
            var cache = indexStrings;

            if ((uint)index < (uint)cache.Length)
            {
                return cache[index];
            }

            if (index < 0 || index >= MaxIndexStrings)
            {
                return index.ToString(CultureInfo.InvariantCulture);
            }

            var size = cache.Length;

            while (size <= index)
            {
                size *= 2;
            }

            // Last writer wins, and a race costs a duplicate table rather than a wrong answer.
            cache = CreateIndexStrings(size);
            indexStrings = cache;

            return cache[index];
        }

        /// <summary>Whether a listener is currently bound to this grid's tbody.</summary>
        bool clicksAttached;

        async Task AttachClicksAsync()
        {
            if (!ClicksAreLive || AllowVirtualization || JSRuntime is null)
            {
                // A listener bound by an earlier render is still on the tbody while the markup has gone
                // back to per-cell handlers - switching virtualization on does exactly that - and every
                // click would then be raised twice. Only attach() detaches, and it is not going to run.
                await DetachClicksAsync().ConfigureAwait(false);

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

            try
            {
                var script = await ModuleAsync();

                if (script is null)
                {
                    return;
                }

                clickReference ??= DotNetObjectReference.Create(this);

                var attached = await script.InvokeAsync<bool>("attach", BodyElementId, clickReference,
                    new
                    {
                        click = kinds.Click,
                        doubleClick = kinds.DoubleClick,
                        contextMenu = kinds.ContextMenu,
                    });

                // Recorded once it is true of the DOM rather than before the call. Recorded first, a
                // re-attach that failed left the grid believing it listens for something it does not.
                attachedKinds = kinds;
                clicksAttached = attached;

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
                //
                // Detached first: a re-attach that threw leaves whatever the last successful one bound,
                // and the handlers below would then be the second answer to every click.
                await DetachClicksAsync().ConfigureAwait(false);

                FallBackToHandlers();
            }
        }

        /// <summary>Removes the listener, for a grid that has stopped delegating its clicks.</summary>
        async Task DetachClicksAsync()
        {
            if (!clicksAttached)
            {
                return;
            }

            clicksAttached = false;

            // The attempt is forgotten with the listener. Left set, a grid that starts delegating
            // again - virtualization switched back off - reaches the guard above with unchanged kinds
            // and returns, having neither a listener nor the per-cell handlers the delegating markup
            // leaves out. Every click, double click and toggle would be dead until something else
            // changed what the grid listens for.
            clickAttachAttempted = false;
            attachedKinds = default;

            try
            {
                if (await ModuleAsync().ConfigureAwait(false) is { } script)
                {
                    await script.InvokeVoidAsync("detach", BodyElementId);
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // As above: a circuit that cannot be reached has no listener left to remove.
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
    }
}
