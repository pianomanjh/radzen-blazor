using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    // Keyboard navigation. The algorithm is here; the effect is in fastgrid.js.
    //
    // That split is the design. RadzenDataGrid's focusTableRow owns the index arithmetic, the clamping,
    // the highlight and the aria-activedescendant bookkeeping in ~156 lines of JavaScript, and C# caches
    // two integers - so both sides hold state and they drift. Its focus ring is wiped whenever selection
    // rewrites a row's class, and setting id= on a grid kills navigation outright, swallowed by a bare
    // catch. Here C# computes the new (row, cell) and calls down with it; the script swaps a class,
    // moves an id and scrolls. It decides nothing, so it cannot disagree.
    //
    // The cost model is the same one that rules the rest of this component. Nothing per cell - no
    // tabindex, no id, which is what rules out roving focus; nothing per row that is not already there,
    // so data-r is reused rather than joined by a second attribute; one listener rather than a delegate
    // per row; and no render per keystroke, via NonRenderingHandler.
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Whether arrow keys move a cursor across the grid's cells. Off by default: a grid that does
        /// not switch it on renders exactly what it rendered before - no tab stop, no handlers, and no
        /// <c>data-r</c> it was not already writing for delegated clicks.
        /// </summary>
        [Parameter] public bool AllowKeyboardNavigation { get; set; }

        /// <summary>
        /// The header's row index. It is row 0 to the user - Left and Right cross the headers and Enter
        /// sorts - but -1 here, so that every body row keeps the index <c>data-r</c> carries and the
        /// click listener already reports.
        /// </summary>
        internal const int HeaderRow = -1;

        /// <summary>
        /// The keys the browser must not act on itself. The grid scrolls the focused cell into view, so
        /// letting the container also scroll a line for an arrow key makes it jitter; Space and PageDown
        /// scroll the page. Tab is deliberately absent - the grid is one tab stop, and swallowing Tab
        /// would trap focus in it.
        /// </summary>
        static readonly string[] HandledKeys =
        {
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "Home", "End", "PageUp", "PageDown", "Enter", " ",
        };

        /// <summary>How far PageUp and PageDown move before the browser has been asked to measure.</summary>
        const int UnmeasuredPageStep = 10;

        bool hasFocus;
        int focusRow;
        int focusCell;

        /// <summary>
        /// The item focus is on, when <see cref="ItemKey" /> supplies one. A sort or a filter is an act
        /// on the rows whose whole purpose is to move the one being looked for, so focus goes with it;
        /// reordering or hiding a *column* is an act on the columns, and focus stays where it is on
        /// screen rather than chasing one across the table. The two point different ways on purpose.
        /// </summary>
        object? focusKey;

        /// <summary>Whether the scroll container currently holds DOM focus.</summary>
        bool focusWithin;

        bool navigationAttached;
        bool rtl;
        int viewportRows;

        /// <summary>How many rows the inline path drew, which is how far Down can go before it pages.</summary>
        int renderedRows;

        EventCallback<KeyboardEventArgs>? keyDown;
        EventCallback<FocusEventArgs>? gridFocus;
        EventCallback<FocusEventArgs>? gridBlur;

        /// <summary>The element that carries the tab stop, the keydown and <c>aria-activedescendant</c>.</summary>
        internal string ViewElementId => ElementId + "-view";

        /// <summary>
        /// Whether rows have to carry their index. This is what widens <c>data-r</c> beyond delegated
        /// clicks - but only under virtualization, and the measurement is why.
        /// </summary>
        /// <remarks>
        /// An attribute per row is not free, even one whose value is a pre-cached string. At 1000 rows
        /// it measured <b>+16 KB</b>, eight times the whole budget for this feature: the value costs
        /// nothing but the frame does, because <c>RenderTreeBuilder</c> rents its frame array from a
        /// pool and a thousand more frames push that rental into the next bucket. The same 16 KB is
        /// what delegated clicks pay for the same attribute, which is most of what a row click costs.
        /// <para>
        /// So the inline path does without it. The rendered data rows are the model's rows in order -
        /// a row-detail row is a sibling carrying <c>rz-expanded-row-content</c> and Virtualize's
        /// spacer carries no class at all - so the script finds the nth <c>tr.rz-data-row</c>, which
        /// costs no markup. Under virtualization that no longer holds: the rendered rows are a window
        /// and the index is a position in the whole data set, so those rows carry it - and there are
        /// tens of them rather than a thousand.
        /// </para>
        /// </remarks>
        internal bool RowsAreAddressed => ClicksAreDelegated || (AllowKeyboardNavigation && AllowVirtualization);

        /// <summary>Where the cursor is, for tests and for a caller that wants to know.</summary>
        internal (int Row, int Cell)? FocusedCell => hasFocus ? (focusRow, focusCell) : null;

        /// <summary>
        /// Cells in a row, counting the row-detail toggle. Every rendered cell is navigable, including
        /// that one: it is already a td with role=gridcell, and making it unreachable would be the
        /// markup lying. One rule also avoids upstream's trap, where ArrowRight means expand rather than
        /// move whenever a Template is supplied - which costs it horizontal navigation on exactly the
        /// grids that have the most columns.
        /// </summary>
        int NavigableCells => visibleColumns.Count + (ExpandColumn ? 1 : 0);

        /// <summary>
        /// Rows the cursor can reach. Under virtualization that is every row in the data, not the
        /// rendered window: the window edge is an implementation detail the user should never feel, and
        /// clamping to it - which is what upstream does - lets the index and the row drift apart with
        /// nothing to re-sync them.
        /// </summary>
        int NavigableRows => AllowVirtualization ? virtualTotal ?? 0 : renderedRows;

        void RenderNavigation(RenderTreeBuilder builder, int sequence)
        {
            builder.AddAttribute(sequence, "id", ViewElementId);

            // One tab stop for the whole grid, on the element that already carries role=grid. Not a
            // roving tabindex: that is an attribute frame on every cell, which is the shape that costs
            // frozen columns 1.10x, and it buys nothing aria-activedescendant does not.
            builder.AddAttribute(sequence + 1, "tabindex", "0");

            builder.AddAttribute(sequence + 2, "onkeydown", keyDown ??= EventCallback.Factory
                .Create<KeyboardEventArgs>(this, NonRenderingHandler.Wrap<KeyboardEventArgs>(OnNavigationKey)));

            // focus and blur rather than focusin and focusout: Blazor dispatches these two to the target
            // element only, so tabbing on to a filter box inside the container does not read as the grid
            // gaining focus - which it would with the bubbling pair, and the cursor would be painted
            // while the caret was somewhere else.
            builder.AddAttribute(sequence + 3, "onfocus", gridFocus ??= EventCallback.Factory
                .Create<FocusEventArgs>(this, NonRenderingHandler.Wrap<FocusEventArgs>(OnGridFocus)));

            builder.AddAttribute(sequence + 4, "onblur", gridBlur ??= EventCallback.Factory
                .Create<FocusEventArgs>(this, NonRenderingHandler.Wrap<FocusEventArgs>(OnGridBlur)));
        }

        async Task OnGridFocus(FocusEventArgs args)
        {
            focusWithin = true;

            // Re-measured on the way in rather than per keystroke, so a resized window is picked up
            // without asking the browser anything on the path that has to stay cheap.
            await MeasureNavigationAsync().ConfigureAwait(false);

            if (!hasFocus)
            {
                hasFocus = true;
                focusRow = NavigableRows > 0 ? 0 : HeaderRow;
                focusCell = 0;

                RememberItem();
            }

            await ShowFocusAsync().ConfigureAwait(false);
        }

        Task OnGridBlur(FocusEventArgs args)
        {
            focusWithin = false;

            // The position is kept and the paint is not. Tabbing out to a filter box and back is a
            // constant gesture, and starting over each time is the difference between keyboard support
            // existing and anyone using it.
            return HideFocusAsync();
        }

        async Task OnNavigationKey(KeyboardEventArgs args)
        {
            // Nothing to move over while a load is in flight, and the rows about to arrive are not the
            // rows a position would have been computed against.
            if (!AllowKeyboardNavigation || IsLoading || args is null)
            {
                return;
            }

            var cells = NavigableCells;
            var rows = NavigableRows;

            if (cells == 0)
            {
                return;
            }

            // A key arriving before the grid has a cursor establishes one rather than moving it: the
            // first row is where the cursor goes, not where it starts from. Ordinarily the focus event
            // has already done this, and this is the case where a key beat it.
            if (!hasFocus)
            {
                await MoveAsync(rows > 0 ? 0 : HeaderRow, 0).ConfigureAwait(false);

                return;
            }

            // A column picked out of the grid can leave the cursor past the last cell, and a shorter
            // page past the last row. Both are settled here rather than in every branch below.
            var row = focusRow == HeaderRow ? HeaderRow : Math.Min(focusRow, Math.Max(rows - 1, 0));
            var cell = Math.Min(focusCell, cells - 1);

            switch (args.Key)
            {
                case "ArrowDown":
                    if (row == HeaderRow)
                    {
                        await MoveAsync(rows > 0 ? 0 : HeaderRow, cell).ConfigureAwait(false);
                    }
                    else if (row + 1 < rows)
                    {
                        await MoveAsync(row + 1, cell).ConfigureAwait(false);
                    }
                    else
                    {
                        // Arrowing past the last row advances the page and lands on the first row of the
                        // next. Upstream simply stops, which on 11,700 rows makes the keyboard useless
                        // past the first page - and the check is a comparison here rather than a DOM
                        // measurement, because the offset and the count are already in C#.
                        await PageForwardAsync(cell).ConfigureAwait(false);
                    }

                    break;

                case "ArrowUp":
                    if (row == HeaderRow)
                    {
                        // Past the top of the arrow space, which the header is. Landing on the last row
                        // of the previous page mirrors Down landing on the first row of the next.
                        await PageBackAsync(cell).ConfigureAwait(false);
                    }
                    else
                    {
                        await MoveAsync(row == 0 ? HeaderRow : row - 1, cell).ConfigureAwait(false);
                    }

                    break;

                case "ArrowLeft":
                    await MoveAsync(row, Step(cell, rtl ? 1 : -1, cells)).ConfigureAwait(false);

                    break;

                case "ArrowRight":
                    await MoveAsync(row, Step(cell, rtl ? -1 : 1, cells)).ConfigureAwait(false);

                    break;

                // The first and last cell *in the row*, which is a deliberate divergence: upstream binds
                // these to the first and last row, which is the WAI-ARIA pattern's Ctrl+Home and
                // Ctrl+End. On a ten-column grid the row meaning is the more useful of the two and the
                // one fingers expect. Neither flips in RTL - "first cell in the row" is already logical.
                case "Home":
                    await (args.CtrlKey
                        ? MoveAsync(rows > 0 ? 0 : HeaderRow, 0)
                        : MoveAsync(row, 0)).ConfigureAwait(false);

                    break;

                case "End":
                    await (args.CtrlKey
                        ? MoveAsync(rows > 0 ? rows - 1 : HeaderRow, cells - 1)
                        : MoveAsync(row, cells - 1)).ConfigureAwait(false);

                    break;

                case "PageDown":
                    await PageAsync(row, cell, rows, forward: true).ConfigureAwait(false);

                    break;

                case "PageUp":
                    await PageAsync(row, cell, rows, forward: false).ConfigureAwait(false);

                    break;

                // Enter activates and Space selects. Upstream binds both to selection and offers no
                // keyboard route to a row click at all, so on a grid whose RowClick opens a detail page
                // a keyboard user can multi-select rows and never open one. Splitting the two keys is
                // the pattern's own answer, and Enter doing exactly what a mouse click does - which
                // includes selecting, when the grid selects on click - keeps the two gestures the same.
                case "Enter":
                    await ActivateAsync(row, cell).ConfigureAwait(false);

                    break;

                case " ":
                    await SelectFocusedAsync(row, cell).ConfigureAwait(false);

                    break;
            }
        }

        static int Step(int cell, int by, int cells) => Math.Clamp(cell + by, 0, cells - 1);

        async Task PageAsync(int row, int cell, int rows, bool forward)
        {
            var step = viewportRows > 0 ? viewportRows : UnmeasuredPageStep;
            var from = row == HeaderRow ? 0 : row;
            var target = forward ? from + step : from - step;

            if (row == HeaderRow && !forward)
            {
                await PageBackAsync(cell).ConfigureAwait(false);
            }
            else if (forward && (rows == 0 || row == rows - 1))
            {
                await PageForwardAsync(cell).ConfigureAwait(false);
            }
            else if (!forward && row == 0)
            {
                await MoveAsync(HeaderRow, cell).ConfigureAwait(false);
            }
            else
            {
                await MoveAsync(Math.Clamp(target, 0, Math.Max(rows - 1, 0)), cell).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The page boundary. Only paging crosses one - a virtualized grid has no pages, and its rows
        /// are all reachable by index whether or not they are currently rendered.
        /// </summary>
        async Task PageForwardAsync(int cell)
        {
            if (!Paging || pageSize <= 0)
            {
                return;
            }

            var total = TotalCount();

            if (skip + pageSize >= total)
            {
                return;
            }

            // Set before the load, not after: the render it causes runs OnAfterRenderAsync, which is
            // what puts the cursor on the row - so the position has to be true by then.
            focusRow = 0;
            focusCell = cell;
            focusKey = null;

            await GoToPage(CurrentPage + 1).ConfigureAwait(false);
        }

        async Task PageBackAsync(int cell)
        {
            if (!Paging || pageSize <= 0 || skip == 0)
            {
                return;
            }

            var previous = CurrentPage - 1;

            focusRow = Math.Max(pageSize - 1, 0);
            focusCell = cell;
            focusKey = null;

            await GoToPage(previous).ConfigureAwait(false);
        }

        async Task MoveAsync(int row, int cell)
        {
            if (row == focusRow && cell == focusCell && hasFocus)
            {
                // Still worth showing: a re-entered grid, or one whose class the last render rewrote,
                // has the position and not the paint.
                await ShowFocusAsync().ConfigureAwait(false);

                return;
            }

            hasFocus = true;
            focusRow = row;
            focusCell = cell;

            RememberItem();

            await ShowFocusAsync().ConfigureAwait(false);
        }

        async Task ActivateAsync(int row, int cell)
        {
            if (row == HeaderRow)
            {
                // Sorting is the most common thing anyone does to a business grid, and without the
                // header there is no keyboard route to it at all.
                var column = ResolveColumn(cell);

                if (column is not null && AllowSorting && column.CanSort)
                {
                    await SortBy(column).ConfigureAwait(false);
                }

                return;
            }

            if (NavigableItem(row) is not { } item)
            {
                return;
            }

            // The toggle cell activates the toggle, which is what is in it.
            if (ExpandColumn && cell == 0)
            {
                await ToggleRow(item).ConfigureAwait(false);
            }
            else
            {
                await OnRowClick(item).ConfigureAwait(false);
            }

            // The activation changed something the grid draws, unlike a move. The cursor is put back by
            // the re-assert that follows the render.
            StateHasChanged();
        }

        async Task SelectFocusedAsync(int row, int cell)
        {
            if (row == HeaderRow)
            {
                await ActivateAsync(row, cell).ConfigureAwait(false);

                return;
            }

            if (NavigableItem(row) is not { } item)
            {
                return;
            }

            await SelectRow(item).ConfigureAwait(false);

            StateHasChanged();
        }

        /// <summary>
        /// The item at a rendered row. Under virtualization the rendered rows are a window, so the index
        /// is offset by where that window starts rather than being a position in the view.
        /// </summary>
        TItem? NavigableItem(int row)
        {
            if (row < 0)
            {
                return default;
            }

            if (!AllowVirtualization)
            {
                return ResolveRow(row);
            }

            var index = row - virtualWindowStart;

            return virtualWindow is not null && index >= 0 && index < virtualWindow.Count
                ? virtualWindow[index]
                : default;
        }

        void RememberItem() =>
            focusKey = ItemKey is { } key && !AllowVirtualization && NavigableItem(focusRow) is { } item
                ? key(item)
                : null;

        /// <summary>
        /// Puts the cursor back where C# says it is, after any render while the grid holds focus.
        /// </summary>
        /// <remarks>
        /// The grid re-renders constantly for other reasons - a sort, a filter, a page, a resize, a
        /// parent's StateHasChanged - and each one rewrites the row's class attribute, wiping a class
        /// the script put there. Rather than defend the class, the grid says where focus is again. This
        /// is what upstream cannot do, because its focused index is a cache rather than the source of
        /// truth: its focus ring is lost every time selection changes a row's class.
        /// </remarks>
        async Task ReassertFocusAsync()
        {
            if (!AllowKeyboardNavigation || !focusWithin || !hasFocus)
            {
                return;
            }

            FollowItem();

            await ShowFocusAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Follows the focused item to wherever the current view put it. Falls back to the position -
        /// which is all a grid with no <see cref="ItemKey" /> has - when the item is not in the view at
        /// all, which is what a filter that excluded it leaves behind.
        /// </summary>
        void FollowItem()
        {
            if (focusKey is null || ItemKey is not { } key || focusRow == HeaderRow)
            {
                return;
            }

            var index = 0;

            foreach (var item in View())
            {
                if (Equals(key(item), focusKey))
                {
                    focusRow = index;

                    return;
                }

                index++;
            }
        }

        async Task ShowFocusAsync()
        {
            var script = await ModuleAsync().ConfigureAwait(false);

            if (script is null)
            {
                return;
            }

            try
            {
                await script.InvokeVoidAsync("focusCell", ViewElementId, focusRow, focusCell,
                    frozenStartRun, frozenEndRun, AllowVirtualization ? ItemSize : 0);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Every exception, for the reason the click listener catches every exception: this is
                // the paint, the component's own position is unaffected by it failing, and the ways it
                // fails are not enumerable from here - a module that did not load raises JSException, a
                // torn-down circuit raises JSDisconnectedException, which is not one, and bUnit's strict
                // mode raises a type this package cannot name.
            }
        }

        async Task HideFocusAsync()
        {
            var script = await ModuleAsync().ConfigureAwait(false);

            if (script is null)
            {
                return;
            }

            try
            {
                await script.InvokeVoidAsync("blurCell", ViewElementId);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // As above.
            }
        }

        /// <summary>
        /// Attaches the key guard and reads back the two things only the browser knows: the writing
        /// direction, because the pattern specifies visual direction and the arrows flip in RTL, and how
        /// many rows fit in the viewport, which is what PageUp and PageDown move by.
        /// </summary>
        /// <remarks>
        /// Both are measurements. The script is told which keys the grid handles and hands back what it
        /// sees; it decides nothing either time.
        /// </remarks>
        async Task AttachNavigationAsync()
        {
            if (!AllowKeyboardNavigation || navigationAttached)
            {
                return;
            }

            var script = await ModuleAsync().ConfigureAwait(false);

            if (script is null)
            {
                return;
            }

            navigationAttached = true;

            try
            {
                Apply(await script.InvokeAsync<NavigationMetrics?>("attachNavigation",
                    ViewElementId, HandledKeys));
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // The grid still navigates without the guard: the browser scrolls a line as well, which
                // is a jitter rather than a failure, and RTL falls back to logical arrows.
            }
        }

        async Task MeasureNavigationAsync()
        {
            var script = await ModuleAsync().ConfigureAwait(false);

            if (script is null)
            {
                return;
            }

            try
            {
                Apply(await script.InvokeAsync<NavigationMetrics?>("measureNavigation", ViewElementId));
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // As above: a grid that cannot be measured pages by UnmeasuredPageStep and reads LTR.
            }
        }

        void Apply(NavigationMetrics? metrics)
        {
            if (metrics is null)
            {
                return;
            }

            rtl = metrics.Rtl;
            viewportRows = metrics.Rows;
        }

        /// <summary>What the browser measured: the writing direction, and the viewport in rows.</summary>
        sealed class NavigationMetrics
        {
            public bool Rtl { get; set; }

            public int Rows { get; set; }
        }
    }
}
