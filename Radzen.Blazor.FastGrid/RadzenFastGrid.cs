using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using System.Threading.Tasks;
using System.Collections;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A read-only data grid that renders rows and cells inline, for large row counts.
    /// </summary>
    /// <remarks>
    /// Emits RadzenDataGrid's class names, so every Radzen theme - including custom ones - styles it
    /// with no extra work. It deliberately does not instantiate a component per row, cascade per row, or
    /// return a render fragment per cell; those are what make a general-purpose grid expensive at scale,
    /// and they are what inline editing needs. This grid does not edit.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    [CascadingTypeParameter(nameof(TItem))]
    public partial class RadzenFastGrid<TItem> : ComponentBase
    {
        readonly List<ColumnBase<TItem>> columns = new();

        // The columns actually drawn, in the order they are drawn, with their sort keys alongside.
        // Rebuilt once per render pass rather than per row - three render loops read it, and a row
        // reads it once per cell.
        readonly List<ColumnBase<TItem>> visibleColumns = new();

        // Scratch for the ordering pass, reused so a grid that declares an OrderIndex does not allocate
        // a list per render to apply it.
        readonly List<ColumnBase<TItem>?> placed = new();

        /// <summary>The rows to display.</summary>
        [Parameter] public IEnumerable<TItem>? Data { get; set; }

        /// <summary>The column definitions.</summary>
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <summary>Whether column headers offer sorting.</summary>
        [Parameter] public bool AllowSorting { get; set; }

        /// <summary>
        /// Rows currently selected. Membership is looked up per row, which costs no allocation - but the
        /// lookup is the collection's own, so a list of many selected rows is a scan per row. Pass a
        /// <see cref="HashSet{T}" /> when more than a handful can be selected at once.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only",
            Justification = "A Blazor parameter is assigned by the renderer and must have a setter.")]
        [Parameter] public ICollection<TItem>? Selection { get; set; }

        /// <summary>Raised when a row is clicked. No handler means no per-row delegate is allocated.</summary>
        [Parameter] public EventCallback<TItem> RowClick { get; set; }

        /// <summary>Raised when a row is double-clicked. Costs a per-row delegate only when handled.</summary>
        [Parameter] public EventCallback<TItem> RowDoubleClick { get; set; }

        /// <summary>
        /// Raised when a cell is clicked. This is a delegate per <em>cell</em> - measured at 296 bytes,
        /// which at five columns is five times what a row click costs - so it is bound only when handled.
        /// </summary>
        [Parameter] public EventCallback<FastGridCellEventArgs<TItem>> CellClick { get; set; }

        /// <summary>Raised when a cell is right-clicked. Per cell, and bound only when handled.</summary>
        [Parameter] public EventCallback<FastGridCellEventArgs<TItem>> CellContextMenu { get; set; }

        /// <summary>
        /// Whether each cell carries its value as a <c>title</c>, so a truncated cell reveals itself on
        /// hover. Off by default: it is an attribute per cell, and the cell's text has to be derived a
        /// second time to fill it.
        /// </summary>
        [Parameter] public bool ShowCellDataAsTooltip { get; set; }

        /// <summary>
        /// An extra CSS class for a row. Return one of a few constant strings and this costs nothing
        /// per row; return a freshly built string per row and it costs that string.
        /// </summary>
        [Parameter] public Func<TItem, string?>? RowClass { get; set; }

        /// <summary>An inline style for a row, on the same terms as <see cref="RowClass" />.</summary>
        [Parameter] public Func<TItem, string?>? RowStyle { get; set; }

        /// <summary>
        /// Called for every body cell before it is drawn, to add HTML attributes to it.
        /// </summary>
        /// <remarks>
        /// The one hook on this component that is per cell rather than per row or per column, so it is
        /// also the one to think twice about: setting it costs an arguments object for every cell of
        /// every row, and whatever the handler itself allocates. Unset it costs a null check hoisted out
        /// of the row loop. A class or style that depends only on the row is cheaper through
        /// <see cref="RowClass" />; one that depends only on the column, through the column's own
        /// <c>CssClass</c>.
        /// </remarks>
        [Parameter] public Action<FastGridCellRenderEventArgs<TItem>>? CellRender { get; set; }

        /// <summary>
        /// Called for every header cell before it is drawn, to add HTML attributes to it. Per column,
        /// so it costs the same whether the grid holds ten rows or a million.
        /// </summary>
        [Parameter] public Action<FastGridCellRenderEventArgs<TItem>>? HeaderCellRender { get; set; }

        /// <summary>
        /// Called for every footer cell before it is drawn, to add HTML attributes to it. Per column,
        /// and only for a grid that draws a footer at all.
        /// </summary>
        [Parameter] public Action<FastGridCellRenderEventArgs<TItem>>? FooterCellRender { get; set; }

        /// <summary>
        /// Whether the grid covers itself with a loading indicator while one of its own asynchronous
        /// loads is in flight. Nothing to wire up: the grid already knows, through
        /// <see cref="IsLoading" />.
        /// </summary>
        [Parameter] public bool ShowLoadingIndicator { get; set; } = true;

        /// <summary>What the loading indicator shows. A spinner matching RadzenDataGrid's without one.</summary>
        [Parameter] public RenderFragment? LoadingTemplate { get; set; }

        /// <summary>Whether one row or several can be selected at once.</summary>
        [Parameter] public DataGridSelectionMode SelectionMode { get; set; } = DataGridSelectionMode.Single;

        /// <summary>Whether clicking a row selects it.</summary>
        [Parameter] public bool AllowRowSelectOnRowClick { get; set; } = true;

        /// <summary>
        /// Raised with the new selection when a row click changes it. The grid renders from
        /// <see cref="Selection" /> and never writes to it, so use <c>@bind-Selection</c> - or handle
        /// this - for clicking to have a visible effect.
        /// </summary>
        [Parameter] public EventCallback<ICollection<TItem>> SelectionChanged { get; set; }

        /// <summary>Raised with the row a click added to the selection.</summary>
        [Parameter] public EventCallback<TItem> RowSelect { get; set; }

        /// <summary>Raised with the row a click removed from the selection.</summary>
        [Parameter] public EventCallback<TItem> RowDeselect { get; set; }

        // Selection is driven from the row click, so the row needs the handler when selection is live
        // even if nothing is listening to RowClick itself.
        bool SelectsOnRowClick => AllowRowSelectOnRowClick
            && (SelectionChanged.HasDelegate || RowSelect.HasDelegate || RowDeselect.HasDelegate);

        /// <summary>Extra CSS class for the grid element.</summary>
        [Parameter] public string? CssClass { get; set; }

        /// <summary>
        /// A key for each row, as QuickGrid's <c>ItemKey</c> does - typically the row's primary key.
        /// </summary>
        /// <remarks>
        /// Without one the diff matches rows by position, so re-sorting rewrites the text of every cell
        /// in place. With one it matches them by identity and moves the rows instead, which is fewer DOM
        /// mutations for a re-sort and none at all for a row that did not change. It is not free: the
        /// renderer builds a dictionary of the keys, and a value-typed key boxes once per row. Worth it
        /// where rows are reordered, not where they only scroll.
        /// </remarks>
        [Parameter] public Func<TItem, object>? ItemKey { get; set; }

        /// <summary>Default CSS width for columns that do not set their own.</summary>
        [Parameter] public string? ColumnWidth { get; set; }

        /// <summary>Whether the header row is drawn.</summary>
        [Parameter] public bool ShowHeader { get; set; } = true;

        /// <summary>Whether alternating rows are shaded. On by default, as in RadzenDataGrid.</summary>
        [Parameter] public bool AllowAlternatingRows { get; set; } = true;

        /// <summary>Which grid lines the table draws. Theme default unless set.</summary>
        [Parameter] public DataGridGridLines GridLines { get; set; } = DataGridGridLines.Default;

        /// <summary>The pager's density.</summary>
        [Parameter] public Density Density { get; set; } = Density.Default;

        /// <summary>
        /// Whether each cell repeats its column title, which is what lets a theme stack the table into
        /// cards on a narrow screen. The titles are constant strings, so this allocates nothing; it does
        /// add a span per cell, which is render time rather than memory.
        /// </summary>
        [Parameter] public bool Responsive { get; set; }

        /// <summary>Content shown when there are no rows.</summary>
        [Parameter] public RenderFragment? EmptyTemplate { get; set; }

        /// <summary>
        /// Detail content for an expanded row, drawn in a row of its own beneath it. Setting this is what
        /// turns row expansion on; nothing about it is paid for while it is null.
        /// </summary>
        /// <remarks>
        /// This is the one feature here whose use is not cheap: the toggle is a delegate per row, which
        /// is the same 310 bytes a row click costs, and the toggle column is an extra cell per row. Both
        /// are unavoidable - a row that can be expanded needs something to click.
        /// </remarks>
        [Parameter] public RenderFragment<TItem>? Template { get; set; }

        /// <summary>Whether the toggle column is drawn. Without it, expand rows through the API.</summary>
        [Parameter] public bool ShowExpandColumn { get; set; } = true;

        /// <summary>Whether expanding a row collapses the last one.</summary>
        [Parameter] public DataGridExpandMode ExpandMode { get; set; } = DataGridExpandMode.Single;

        /// <summary>Raised with the row that was expanded.</summary>
        [Parameter] public EventCallback<TItem> RowExpand { get; set; }

        /// <summary>Raised with the row that was collapsed.</summary>
        [Parameter] public EventCallback<TItem> RowCollapse { get; set; }

        // Allocated on the first expand: a grid whose rows are never expanded never holds the set, and
        // one with no Template never reaches the lookup at all.
        HashSet<TItem>? expandedRows;

        // One arguments object for every cell of every render, pointed at each cell in turn. Measured:
        // allocating one per cell costs 195 KB at 1000 x 5 before the handler does anything, and the
        // dictionary behind it another 1,300 - which would have made this hook as expensive as a cell
        // click, the most expensive thing this component offers. The header and footer hooks share it
        // because those rows are drawn either side of the body, never inside it.
        //
        // What it costs instead is a rule: the arguments describe the cell being drawn and nothing else,
        // so a handler must read them rather than keep them. Documented on the type.
        FastGridCellRenderEventArgs<TItem>? cellRenderArgs;

        FastGridCellRenderEventArgs<TItem> CellRenderArgs(TItem? item, ColumnBase<TItem> column)
        {
            cellRenderArgs ??= new FastGridCellRenderEventArgs<TItem>();

            cellRenderArgs.Reset(item, column);

            return cellRenderArgs;
        }

        /// <summary>Whether the given row is expanded.</summary>
        /// <param name="item">The row.</param>
        public bool IsRowExpanded(TItem item) => expandedRows is not null && expandedRows.Contains(item);

        /// <summary>Expands or collapses a row, raising the matching event.</summary>
        /// <param name="item">The row.</param>
        public async Task ToggleRow(TItem item)
        {
            if (item is null)
            {
                return;
            }

            if (IsRowExpanded(item))
            {
                expandedRows!.Remove(item);

                await RowCollapse.InvokeAsync(item).ConfigureAwait(false);
            }
            else
            {
                // Single mode collapses what was open, and says so: a row that leaves the screen without
                // an event is a row the caller still thinks is expanded.
                if (ExpandMode == DataGridExpandMode.Single && expandedRows is { Count: > 0 })
                {
                    var open = new List<TItem>(expandedRows);

                    expandedRows.Clear();

                    foreach (var previous in open)
                    {
                        await RowCollapse.InvokeAsync(previous).ConfigureAwait(false);
                    }
                }

                (expandedRows ??= new HashSet<TItem>()).Add(item);

                await RowExpand.InvokeAsync(item).ConfigureAwait(false);
            }

            StateHasChanged();
        }

        // Whether the toggle column is drawn at all. Read in four render paths, so it is one expression
        // rather than four that can drift.
        bool ExpandColumn => Template is not null && ShowExpandColumn;

        /// <summary>
        /// Whether clicking a second column adds to the sort instead of replacing it. A click then
        /// cycles a column ascending, descending, then out of the sort altogether - which is the only
        /// way to remove one, since there is nowhere else to click.
        /// </summary>
        [Parameter] public bool AllowMultiColumnSorting { get; set; }

        /// <summary>
        /// Whether a control above the grid lets the user choose which columns are drawn. Off by
        /// default; it costs one branch per render when off, and one drop-down when on.
        /// </summary>
        [Parameter] public bool AllowColumnPicking { get; set; }

        /// <summary>Whether the picker offers a select-all entry.</summary>
        [Parameter] public bool AllowPickAllColumns { get; set; } = true;

        /// <summary>Whether the picker offers a filter box, for a grid with many columns.</summary>
        [Parameter] public bool ColumnsPickerAllowFiltering { get; set; }

        /// <summary>How many column names the picker lists before it summarises the count instead.</summary>
        [Parameter] public int ColumnsPickerMaxSelectedLabels { get; set; } = 3;

        /// <summary>Raised with the columns that are drawn, whenever the picker changes them.</summary>
        [Parameter] public EventCallback<IEnumerable<ColumnBase<TItem>>> PickedColumnsChanged { get; set; }

        // The columns the picker offers and the subset currently drawn. Both are rebuilt from the
        // registered columns each time the picker is drawn, so a column added or removed after the first
        // render is offered or dropped without anything else having to notice.
        readonly List<ColumnBase<TItem>> pickable = new();
        readonly List<object> picked = new();

        /// <summary>Whether a sorted header shows its position in the sort.</summary>
        [Parameter] public bool ShowMultiColumnSortingIndex { get; set; }

        // The sort, in order of precedence. One entry is the overwhelmingly common case and the list
        // never grows past the column count, so it is walked rather than indexed.
        readonly List<(ColumnBase<TItem> Column, bool Descending)> sorts = new();

        /// <summary>The column sorted first, if any.</summary>
        public ColumnBase<TItem>? SortColumn => sorts.Count > 0 ? sorts[0].Column : null;

        /// <summary>Whether the first sort is descending.</summary>
        public bool SortDescending => sorts.Count > 0 && sorts[0].Descending;

        /// <summary>
        /// The sort as descriptors, in order of precedence - the form the rest of Radzen speaks, and
        /// what <c>LoadDataArgs.Sorts</c> carries. Empty when nothing is sorted.
        /// </summary>
        public IReadOnlyList<SortDescriptor> Sorts
        {
            get
            {
                if (sorts.Count == 0)
                {
                    return Array.Empty<SortDescriptor>();
                }

                var descriptors = new List<SortDescriptor>(sorts.Count);

                for (var i = 0; i < sorts.Count; i++)
                {
                    var (column, descending) = sorts[i];

                    if (column.PropertyPath is { Length: > 0 } path)
                    {
                        descriptors.Add(new SortDescriptor
                        {
                            Property = path,
                            SortOrder = descending ? SortOrder.Descending : SortOrder.Ascending,
                        });
                    }
                }

                return descriptors;
            }
        }

        /// <summary>The position of a column in the sort, or -1 when it is not sorted.</summary>
        internal int SortIndexOf(ColumnBase<TItem> column)
        {
            for (var i = 0; i < sorts.Count; i++)
            {
                if (ReferenceEquals(sorts[i].Column, column))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Registers a column. Called on the column's first parameter set, and idempotent after that:
        /// the list is never rebuilt, because the renderer does not re-set the parameters of a column
        /// whose parameters have not changed, and a rebuilt list would silently lose it.
        /// </summary>
        internal void AddColumn(ColumnBase<TItem> column)
        {
            if (!columns.Contains(column))
            {
                columns.Add(column);
            }
        }

        /// <summary>
        /// Unregisters a column, when the renderer disposes one that has left the markup.
        /// </summary>
        internal void RemoveColumn(ColumnBase<TItem> column)
        {
            if (!columns.Remove(column))
            {
                return;
            }

            // Not left for the next RefreshVisibleColumns: Virtualize renders its rows outside the
            // table's render pass, so this list can be read after a column has gone and before the
            // table redraws.
            visibleColumns.Remove(column);

            // The sort must not outlive the column it orders by, or the grid keeps ordering by something
            // nothing on screen names and nothing can clear. Nor must the column's check-box-list values,
            // which would hold the column and everything it listed for as long as the grid lives.
            var sorted = SortIndexOf(column);

            if (sorted >= 0)
            {
                sorts.RemoveAt(sorted);
            }

            lookups.Remove(column);
        }

        /// <summary>
        /// Sets the sort a column declared in markup. Called once, as the column registers, before the
        /// grid has drawn anything - so it publishes the state and does not reload.
        /// </summary>
        internal void ApplyDeclaredSort(ColumnBase<TItem> column, SortOrder order)
        {
            // Sorting by one column at a time means the last declaration wins; sorting by several means
            // they compose, in the order they were declared, which is the only order markup expresses.
            if (!AllowMultiColumnSorting)
            {
                sorts.Clear();
            }

            sorts.Add((column, order == SortOrder.Descending));
        }

        // Rebuilt at the start of each render pass. The common case - every column visible, none
        // declaring an OrderIndex - skips the ordering pass entirely.
        void RefreshVisibleColumns()
        {
            visibleColumns.Clear();

            var ordered = false;

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.IsVisible)
                {
                    continue;
                }

                ordered |= column.OrderIndex is not null;

                visibleColumns.Add(column);
            }

            if (!ordered)
            {
                return;
            }

            // A column that names an index is placed at it, and the rest fill what is left in the order
            // they were declared. Sorting on a key of "OrderIndex, or where it happens to sit" instead
            // reads the same for one column and differently for two: OrderIndex="0" on the third column
            // would leave it behind the first, which is not what naming a position means.
            placed.Clear();

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                placed.Add(null);
            }

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                if (column.OrderIndex is not { } index)
                {
                    continue;
                }

                var slot = Math.Clamp(index, 0, placed.Count - 1);

                // Two columns claiming one slot is resolved by declaration order, since this walks in it.
                // The wrap terminates: there are never more indexed columns than slots.
                while (placed[slot] is not null)
                {
                    slot = (slot + 1) % placed.Count;
                }

                placed[slot] = column;
            }

            var next = 0;

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                if (column.OrderIndex is not null)
                {
                    continue;
                }

                while (placed[next] is not null)
                {
                    next++;
                }

                placed[next] = column;
            }

            for (var i = 0; i < placed.Count; i++)
            {
                visibleColumns[i] = placed[i]!;
            }
        }

        /// <summary>Sorts by the given column, toggling direction when it is already the sorted one.</summary>
        /// <remarks>
        /// With <see cref="AllowMultiColumnSorting" /> a column already in the sort cycles descending and
        /// then out of it, and any other column is appended. Without it the grid sorts by one column and
        /// a click only ever toggles direction - there is no "unsorted" to cycle back to, because
        /// removing the only sort would leave the rows in an order nothing on screen explains.
        /// </remarks>
        /// <param name="column">The column to sort by.</param>
        public Task SortBy(ColumnBase<TItem> column)
        {
            if (column is null || !column.CanSort)
            {
                return Task.CompletedTask;
            }

            var sorted = SortIndexOf(column);

            if (!AllowMultiColumnSorting)
            {
                var descending = sorted >= 0 && !sorts[sorted].Descending;

                sorts.Clear();
                sorts.Add((column, descending));
            }
            else if (sorted < 0)
            {
                sorts.Add((column, false));
            }
            else if (!sorts[sorted].Descending)
            {
                sorts[sorted] = (column, true);
            }
            else
            {
                sorts.RemoveAt(sorted);
            }

            // A sort change moves the whole set, not just the page, so go back to the first page - the
            // row that was on page 3 is not on page 3 any more.
            skip = 0;

            return RefreshAsync();
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.OpenComponent<CascadingValue<RadzenFastGrid<TItem>>>(0);
            builder.AddAttribute(1, "Value", this);
            builder.AddAttribute(2, "IsFixed", true);
            builder.AddAttribute(3, "ChildContent", cascaded ??= RenderCascaded);
            builder.CloseComponent();
        }

        // Held rather than written inline: a lambda in the render path is a delegate allocated on every
        // render, and these two capture nothing but the component itself.
        RenderFragment? cascaded;
        RenderFragment? deferred;

        void RenderCascaded(RenderTreeBuilder builder)
        {
            // The columns register while the renderer walks them ...
            builder.AddContent(0, ChildContent);

            // ... and Defer runs after, so the table below sees a populated column list.
            builder.OpenComponent<Defer>(1);
            builder.AddAttribute(2, "ChildContent", deferred ??= RenderDeferred);
            builder.CloseComponent();
        }

        // Deferred so that a column added to the markup registers before the table that reads the list
        // is written. A column that was already there needs no pass of its own: it stays registered
        // until the renderer disposes it.
        void RenderDeferred(RenderTreeBuilder builder) => RenderTable(builder);

        void RenderTable(RenderTreeBuilder builder)
        {
            // Before the columns are gathered, not after: stored settings can hide a column, and the
            // drawn list is computed from what they leave. Applying them second showed the pre-settings
            // columns for one render, and on a grid over a plain queryable there is no reload behind it
            // to put that right.
            //
            // Here and not in OnParametersSet: stored state names columns by property path, and no
            // column has registered by then. Defer has run, so by now every one of them has.
            if (settingsPending)
            {
                settingsPending = false;

                ApplySettings(appliedSettings!);
            }

            RefreshVisibleColumns();

            BeginDrawing();

            try
            {
                RenderGrid(builder);
            }
            finally
            {
                EndDrawing();
            }
        }

        void RenderGrid(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", string.IsNullOrEmpty(CssClass)
                ? "rz-data-grid rz-datatable"
                : "rz-data-grid rz-datatable " + CssClass);

            if (AllowColumnPicking)
            {
                RenderColumnPicker(builder);
            }

            if (Paging && PagerPosition.HasFlag(PagerPosition.Top))
            {
                RenderPager(builder, 10, captureTopPager ??= p => topPager = (RadzenPager)p);
            }

            // 21, not 20: the top pager's band runs to 20, and the numbers a region writes must ascend
            // in the order it writes them.
            //
            // The scroll container, and the element that carries role=grid. Both jobs are load-bearing:
            // it is what a widened column overflows into rather than pushing the page sideways, and the
            // rowgroup/row/gridcell roles below it require a grid ancestor to mean anything. The theme
            // expects exactly this pair - .rz-data-grid is a flex column and this is its flex: 1 child.
            builder.OpenElement(21, "div");
            builder.AddAttribute(22, "class", "rz-data-grid-data");
            builder.AddAttribute(23, "role", "grid");

            builder.OpenElement(24, "table");
            builder.AddAttribute(25, "class", TableClass());

            // The grid role belongs to the container above; the table is scaffolding for it, and its own
            // implicit table role would otherwise sit between the grid and its rows.
            builder.AddAttribute(26, "role", "presentation");

            RenderColumnGroup(builder);

            if (ShowHeader)
            {
                RenderHead(builder);
            }

            RenderBody(builder);
            RenderFoot(builder);

            builder.CloseElement();
            builder.CloseElement();

            if (Paging && PagerPosition.HasFlag(PagerPosition.Bottom))
            {
                RenderPager(builder, 200, captureBottomPager ??= p => bottomPager = (RadzenPager)p);
            }

            if (ShowLoadingIndicator && IsLoading)
            {
                RenderLoading(builder);
            }

            builder.CloseElement();
        }

        // The scrim and the spinner RadzenDataGrid draws, in the elements its themes already style.
        // Both are positioned against the nearest positioned ancestor, which in both grids is the outer
        // .rz-datatable - so this covers the pagers as well as the table, exactly as it does there.
        //
        // Drawn from IsLoading, which the grid maintains for its own asynchronous loads, rather than
        // from a parameter the application has to keep in step. RadzenDataGrid needs IsLoading passed
        // in; here there is nothing to pass, and nothing to forget to reset on the failure path.
        void RenderLoading(RenderTreeBuilder builder)
        {
            builder.OpenElement(210, "div");
            builder.AddAttribute(211, "class", "rz-datatable-loading");
            builder.CloseElement();

            builder.OpenElement(212, "div");
            builder.AddAttribute(213, "class", "rz-datatable-loading-content");

            if (LoadingTemplate is { } loadingTemplate)
            {
                builder.AddContent(214, loadingTemplate);
            }
            else
            {
                builder.OpenElement(215, "i");
                builder.AddAttribute(216, "class", "notranslate rzi-circle-o-notch");
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        // A sequence number identifies a position in the source, and the numbers a region writes must
        // ascend in the order it writes them - the top pager's band therefore sits below the table's,
        // and the bottom pager's above everything the table emits. They descended before, which the
        // diff copes with by tearing the table down and rebuilding it whenever the pager appears.
        Action<object>? captureTopPager;
        Action<object>? captureBottomPager;

        // The reference is captured because RadzenPager keeps its own page offset and offers no
        // parameter to set it: the grid has to put it back when something other than the pager itself
        // moves the page.
        void RenderPager(RenderTreeBuilder builder, int sequence, Action<object> capture)
        {
            builder.OpenComponent<RadzenPager>(sequence);
            builder.AddAttribute(sequence + 1, nameof(RadzenPager.Count), TotalCount());
            builder.AddAttribute(sequence + 2, nameof(RadzenPager.PageSize), pageSize);
            builder.AddAttribute(sequence + 3, nameof(RadzenPager.PageNumbersCount), PageNumbersCount);
            builder.AddAttribute(sequence + 4, nameof(RadzenPager.HorizontalAlign), PagerHorizontalAlign);
            builder.AddAttribute(sequence + 5, nameof(RadzenPager.AlwaysVisible), PagerAlwaysVisible);
            builder.AddAttribute(sequence + 6, nameof(RadzenPager.ShowPagingSummary), ShowPagingSummary);
            builder.AddAttribute(sequence + 7, nameof(RadzenPager.PageChanged),
                EventCallback.Factory.Create<PagerEventArgs>(this, OnPageChanged));

            if (PageSizeOptions is not null)
            {
                builder.AddAttribute(sequence + 8, nameof(RadzenPager.PageSizeOptions), PageSizeOptions);
                builder.AddAttribute(sequence + 9, nameof(RadzenPager.PageSizeChanged),
                    EventCallback.Factory.Create<int>(this, OnPageSizeChanged));
            }

            builder.AddAttribute(sequence + 10, nameof(RadzenPager.Density), Density);
            builder.AddComponentReferenceCapture(sequence + 11, capture);
            builder.CloseComponent();
        }

        // The picker: one drop-down above the table, in RadzenDataGrid's own wrapper elements so the
        // themes style it unchanged. Sequence 700+ because it sits before everything else the grid
        // draws and must not collide with the pager beside it.
        //
        // RadzenDropDown in Multiple mode already draws a checkbox per item with a select-all and an
        // optional filter box, which is the whole control - the same reasoning as the check-box-list
        // filter, and the reason picking costs a drop-down rather than a popup of the grid's own.
        void RenderColumnPicker(RenderTreeBuilder builder)
        {
            RefreshPickable();

            builder.OpenElement(700, "div");
            builder.AddAttribute(701, "class", "rz-group-header");
            builder.OpenElement(702, "div");
            builder.AddAttribute(703, "class", "rz-column-picker");

            builder.OpenComponent<RadzenDropDown<IEnumerable<object>>>(704);
            builder.AddAttribute(705, nameof(RadzenDropDown<IEnumerable<object>>.Data), pickable);
            builder.AddAttribute(706, nameof(RadzenDropDown<IEnumerable<object>>.Multiple), true);
            builder.AddAttribute(707, nameof(RadzenDropDown<IEnumerable<object>>.TextProperty),
                nameof(ColumnBase<TItem>.PickerTitle));
            builder.AddAttribute(708, nameof(RadzenDropDown<IEnumerable<object>>.AllowSelectAll), AllowPickAllColumns);
            builder.AddAttribute(709, nameof(RadzenDropDown<IEnumerable<object>>.SelectAllText), AllColumnsText);
            builder.AddAttribute(710, nameof(RadzenDropDown<IEnumerable<object>>.SelectedItemsText), ColumnsShowingText);
            builder.AddAttribute(711, nameof(RadzenDropDown<IEnumerable<object>>.MaxSelectedLabels), ColumnsPickerMaxSelectedLabels);
            builder.AddAttribute(712, nameof(RadzenDropDown<IEnumerable<object>>.Placeholder), ColumnsText);
            builder.AddAttribute(713, nameof(RadzenDropDown<IEnumerable<object>>.AllowFiltering), ColumnsPickerAllowFiltering);
            builder.AddAttribute(714, nameof(RadzenDropDown<IEnumerable<object>>.FilterCaseSensitivity),
                FilterCaseSensitivity.CaseInsensitive);
            builder.AddAttribute(715, nameof(RadzenDropDown<IEnumerable<object>>.Value), picked);
            builder.AddAttribute(716, nameof(RadzenDropDown<IEnumerable<object>>.Change),
                EventCallback.Factory.Create<object>(this, OnColumnsPicked));

            // Built per render rather than held, and the delegate above with it. Holding either would
            // save one small allocation per grid render - nothing, beside the per-row costs this grid
            // exists to remove - and buys a label that keeps announcing the culture the page was first
            // drawn in. Blazor re-renders a child whenever its parent draws it, so a held parameter
            // would not have saved the drop-down a render either.
            builder.AddAttribute(717, nameof(RadzenDropDown<IEnumerable<object>>.InputAttributes),
                new Dictionary<string, object> { ["aria-label"] = SelectVisibleColumnsAriaLabel });
            builder.CloseComponent();

            builder.CloseElement();
            builder.CloseElement();
        }

        // Rebuilt rather than kept in step by hand: a column can register or go at any render, and a
        // list that only grew would offer a column that no longer exists.
        void RefreshPickable()
        {
            pickable.Clear();
            picked.Clear();

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.Pickable)
                {
                    continue;
                }

                pickable.Add(column);

                if (column.IsVisible)
                {
                    picked.Add(column);
                }
            }
        }

        async Task OnColumnsPicked(object value)
        {
            // A set rather than a scan per column: the picker on a wide grid is the one place this could
            // be quadratic, and it costs one small allocation on an event a user raises by hand.
            var chosen = new HashSet<object>();

            if (value is IEnumerable<object> selection)
            {
                foreach (var column in selection)
                {
                    chosen.Add(column);
                }
            }

            // Only the pickable columns are told. A column the picker never offered - Pickable="false" -
            // keeps whatever the markup said, rather than being hidden by its absence from the list.
            for (var i = 0; i < pickable.Count; i++)
            {
                pickable[i].SetPicked(chosen.Contains(pickable[i]));
            }

            RefreshPickable();

            if (PickedColumnsChanged.HasDelegate)
            {
                await PickedColumnsChanged.InvokeAsync(VisibleColumnsPicked()).ConfigureAwait(false);
            }

            // Through the same funnel as every other user-driven change, so a grid persisting settings
            // stores the new visibility without the picker knowing anything about settings.
            await RefreshAsync().ConfigureAwait(false);
        }

        List<ColumnBase<TItem>> VisibleColumnsPicked()
        {
            var chosen = new List<ColumnBase<TItem>>(pickable.Count);

            for (var i = 0; i < pickable.Count; i++)
            {
                if (pickable[i].IsVisible)
                {
                    chosen.Add(pickable[i]);
                }
            }

            return chosen;
        }

        // Widths live here and nowhere else. A width on every td is a frame per cell; one col per column
        // is a frame per column, and the browser applies it to the whole column either way. Written only
        // when some column actually has a width, so a grid that sets none pays nothing for the element.
        void RenderColumnGroup(RenderTreeBuilder builder)
        {
            var any = false;

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                if (!string.IsNullOrEmpty(visibleColumns[i].Width ?? ColumnWidth))
                {
                    any = true;

                    break;
                }
            }

            if (!any)
            {
                return;
            }

            builder.OpenElement(27, "colgroup");

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(28, "col");

                if (column.ColStyle(column.Width ?? ColumnWidth) is { } style)
                {
                    builder.AddAttribute(29, "style", style);
                }

                builder.CloseElement();
            }

            builder.CloseElement();
        }

        // Composed per render, not per row, and only when something is off the default - so the ordinary
        // grid hands the same literal back every time.
        string TableClass()
        {
            var striped = AllowAlternatingRows ? " rz-grid-table-striped" : null;
            var lines = GridLines switch
            {
                DataGridGridLines.Both => " rz-grid-gridlines-both",
                DataGridGridLines.None => " rz-grid-gridlines-none",
                DataGridGridLines.Horizontal => " rz-grid-gridlines-horizontal",
                DataGridGridLines.Vertical => " rz-grid-gridlines-vertical",
                _ => null,
            };

            if (striped is not null && lines is null)
            {
                return "rz-grid-table rz-grid-table-fixed rz-grid-table-striped";
            }

            if (striped is null && lines is null)
            {
                return "rz-grid-table rz-grid-table-fixed";
            }

            return "rz-grid-table rz-grid-table-fixed" + striped + lines;
        }

        // Drawn only when a visible column asks for it, so a grid with no footer emits no tfoot. Per
        // column and once per render, whatever the row count - the cost of a footer is whatever the
        // templates in it do, not the row itself.
        void RenderFoot(RenderTreeBuilder builder)
        {
            var any = false;

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                if (visibleColumns[i].FooterTemplate is not null)
                {
                    any = true;

                    break;
                }
            }

            if (!any)
            {
                return;
            }

            builder.OpenElement(180, "tfoot");
            builder.AddAttribute(181, "role", "rowgroup");
            builder.AddAttribute(182, "class", "rz-datatable-tfoot");
            builder.OpenElement(183, "tr");
            builder.AddAttribute(184, "role", "row");

            RenderExpandSpacer(builder, 179, "td");

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(185, "td");
                builder.AddAttribute(186, "role", "gridcell");
                builder.AddAttribute(187, "scope", "col");

                if (!string.IsNullOrEmpty(column.FooterCssClass))
                {
                    builder.AddAttribute(188, "class", column.FooterCssClass);
                }

                if (column.CellStyle is { } footerStyle)
                {
                    builder.AddAttribute(189, "style", footerStyle);
                }

                if (FooterCellRender is { } footerCellRender)
                {
                    var args = CellRenderArgs(default, column);

                    footerCellRender(args);

                    if (args.Written is { } written)
                    {
                        builder.AddMultipleAttributes(178, written);
                    }
                }

                // The span is written for every column, with or without a template: the theme's footer
                // padding hangs off it, and a bare td renders a shorter cell beside its neighbours.
                builder.OpenElement(190, "span");
                builder.AddAttribute(191, "class", "rz-column-footer");

                if (column.FooterTemplate is { } footerTemplate)
                {
                    builder.AddContent(192, footerTemplate(column));
                }

                builder.CloseElement();
                builder.CloseElement();
            }

            builder.CloseElement();
            builder.CloseElement();
        }

        // The filter and footer rows only reserve the toggle column's space. Written once so the three
        // rows that have to agree on it cannot drift: a row short of a cell puts every column after it
        // under the wrong header.
        void RenderExpandSpacer(RenderTreeBuilder builder, int sequence, string element)
        {
            if (!ExpandColumn)
            {
                return;
            }

            builder.OpenRegion(sequence);
            builder.OpenElement(0, element);
            builder.AddAttribute(1, "class", "rz-col-icon");
            builder.CloseElement();
            builder.CloseRegion();
        }

        void RenderHead(RenderTreeBuilder builder)
        {
            builder.OpenElement(30, "thead");
            builder.AddAttribute(31, "role", "rowgroup");
            builder.OpenElement(32, "tr");
            builder.AddAttribute(33, "role", "row");

            // A region, not a bare cell: a tr's attributes and its children share one ascending sequence
            // space, and there is no number free between the tr's role attribute and the first column
            // header. A region opens a space of its own, and costs one frame per render.
            if (ExpandColumn)
            {
                builder.OpenRegion(34);
                builder.OpenElement(0, "th");
                builder.AddAttribute(1, "role", "columnheader");
                builder.AddAttribute(2, "class", "rz-col-icon rz-unselectable-text");
                builder.AddAttribute(3, "scope", "col");
                builder.OpenElement(4, "span");
                builder.AddAttribute(5, "class", "rz-column-title");
                builder.CloseElement();
                builder.CloseElement();
                builder.CloseRegion();
            }

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];
                var sortable = AllowSorting && column.CanSort;
                var sorted = SortIndexOf(column);

                builder.OpenElement(34, "th");
                builder.AddAttribute(35, "role", "columnheader");
                builder.AddAttribute(36, "scope", "col");
                builder.AddAttribute(37, "class", sortable
                    ? "rz-unselectable-text rz-sortable-column"
                    : "rz-unselectable-text");

                if (sorted >= 0)
                {
                    builder.AddAttribute(38, "aria-sort",
                        sorts[sorted].Descending ? "descending" : "ascending");
                }

                if (column.CellStyle is { } headerStyle)
                {
                    builder.AddAttribute(48, "style", headerStyle);
                }

                if (HeaderCellRender is { } headerCellRender)
                {
                    var args = CellRenderArgs(default, column);

                    headerCellRender(args);

                    if (args.Written is { } written)
                    {
                        builder.AddMultipleAttributes(33, written);
                    }
                }

                // The theme gives th padding:0 and hangs the header padding off a direct child div, so
                // this wrapper is load-bearing: without it the header row renders shorter than
                // RadzenDataGrid's. It is per column, not per row, so it costs nothing at scale.
                builder.OpenElement(39, "div");

                if (sortable)
                {
                    builder.AddAttribute(40, "onclick",
                        EventCallback.Factory.Create<MouseEventArgs>(this, _ => SortBy(column)));
                }

                builder.OpenElement(41, "span");
                builder.AddAttribute(42, "class", "rz-column-title");
                builder.OpenElement(43, "span");
                builder.AddAttribute(44, "class", "rz-column-title-content rz-text-truncate");

                // The template replaces the title text, not the wrapper: the theme hangs the header's
                // truncation and spacing off these two spans, so content placed outside them loses both.
                if (column.HeaderTemplate is { } headerTemplate)
                {
                    builder.AddContent(49, headerTemplate(column));
                }
                else
                {
                    builder.AddContent(45, column.HeaderText);
                }

                builder.CloseElement();

                // The position in the sort, as RadzenDataGrid shows it - a RadzenBadge there, the markup
                // that badge produces here, since a component per sorted header buys nothing this grid
                // wants. One is not worth showing: the number only means anything against another.
                if (sorted >= 0 && ShowMultiColumnSortingIndex && sorts.Count > 1)
                {
                    builder.OpenElement(50, "span");
                    builder.AddAttribute(51, "class",
                        "rz-badge rz-badge-info rz-variant-filled rz-shade-lighter rz-badge-pill");
                    builder.AddContent(52, sorted + 1);
                    builder.CloseElement();
                }

                if (sorted >= 0)
                {
                    builder.OpenElement(46, "span");
                    builder.AddAttribute(47, "class", sorts[sorted].Descending
                        // rzi-sort as well as the direction class, which is what RadzenDataGrid emits.
                        // The direction rule wins for both glyph and colour either way, but matching the
                        // class list exactly is what keeps a custom theme's rules applying to both.
                        ? "notranslate rz-sortable-column-icon rzi-grid-sort rzi-sort rzi-sort-desc"
                        : "notranslate rz-sortable-column-icon rzi-grid-sort rzi-sort rzi-sort-asc");
                    builder.CloseElement();
                }

                builder.CloseElement();
                builder.CloseElement();
                builder.CloseElement();
            }

            builder.CloseElement();

            if (AllowFiltering)
            {
                RenderFilterRow(builder);
            }

            builder.CloseElement();
        }

        // Matches RadzenDataGrid's filter row exactly: a second header row whose th holds
        // div.rz-cell-filter > div.rz-cell-filter-content > span.rz-cell-filter-label directly, with no
        // title wrapper. The theme's th padding hangs off that first div, as it does off the title one.
        void RenderFilterRow(RenderTreeBuilder builder)
        {
            builder.OpenElement(50, "tr");
            builder.AddAttribute(51, "role", "row");

            RenderExpandSpacer(builder, 53, "th");

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(52, "th");
                builder.AddAttribute(53, "role", "columnheader");
                builder.AddAttribute(54, "scope", "col");
                builder.AddAttribute(55, "class", "rz-unselectable-text");

                if (column.CanFilter || column.FilterTemplate is not null)
                {
                    builder.OpenElement(56, "div");
                    builder.AddAttribute(57, "class", "rz-cell-filter");
                    builder.OpenElement(58, "div");
                    builder.AddAttribute(59, "class", "rz-cell-filter-content");

                    if (column.FilterTemplate is not null)
                    {
                        builder.AddContent(60, column.FilterTemplate(column));
                    }
                    else if (FilterModeOf(column) == FilterMode.CheckBoxList)
                    {
                        RenderFilterList(builder, column);
                    }
                    else
                    {
                        RenderFilterInput(builder, column);
                    }

                    builder.CloseElement();
                    builder.CloseElement();
                }

                builder.CloseElement();
            }

            builder.CloseElement();
        }

        // A multi-select of the column's distinct values, filtering with In. RadzenDropDown in Multiple
        // mode already draws a check box per item, so this is the check-box list without a popup, a
        // toggle button or an apply step of its own.
        void RenderFilterList(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            builder.OpenComponent<RadzenDropDown<IEnumerable>>(80);
            builder.AddAttribute(81, nameof(RadzenDropDown<IEnumerable>.Data), FilterLookup(column));
            builder.AddAttribute(82, nameof(RadzenDropDown<IEnumerable>.Multiple), true);
            builder.AddAttribute(83, nameof(RadzenDropDown<IEnumerable>.AllowClear), true);
            builder.AddAttribute(84, nameof(RadzenDropDown<IEnumerable>.AllowFiltering), true);
            builder.AddAttribute(85, nameof(RadzenDropDown<IEnumerable>.FilterCaseSensitivity),
                FilterCaseSensitivity.CaseInsensitive);
            builder.AddAttribute(86, nameof(RadzenDropDown<IEnumerable>.Style), "width: 100%");
            builder.AddAttribute(87, nameof(RadzenDropDown<IEnumerable>.Value), column.CurrentFilterValue);
            builder.AddAttribute(88, nameof(RadzenDropDown<IEnumerable>.Change),
                EventCallback.Factory.Create<object>(this, value => OnFilterSelection(column, value)));
            builder.CloseComponent();
        }

        void RenderFilterInput(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            builder.OpenElement(61, "span");
            builder.AddAttribute(62, "class", "rz-cell-filter-label");
            builder.AddAttribute(63, "style", "height:35px; width:100%;");

            builder.OpenElement(64, "input");
            builder.AddAttribute(65, "type", "text");
            builder.AddAttribute(66, "autocomplete", "off");
            builder.AddAttribute(67, "class", "rz-textbox");
            builder.AddAttribute(68, "style", "width: 100%;");
            builder.AddAttribute(69, "aria-label",
                column.HeaderText + FilterValueAriaLabel + column.CurrentFilterValue);
            builder.AddAttribute(70, "value", column.CurrentFilterValue);

            // onchange is bound whether or not the filter applies as you type, because it is what a
            // blur and an Enter raise. Typing adds oninput on top of it rather than replacing it, so
            // turning the feature on cannot cost the box the event that commits it.
            builder.AddAttribute(71, "onchange", EventCallback.Factory.CreateBinder<string?>(this,
                value => OnFilterCommitted(column, value), column.CurrentFilterValue?.ToString()));

            if (FilterAsYouType)
            {
                // Not a binder, and not the component as receiver. A keystroke that is going to be
                // superseded must not redraw a thousand rows to show the same thing: measured, three
                // keystrokes into a bound box cost three full renders before the pause even ended,
                // which is most of what the pause exists to avoid. The non-rendering receiver drops
                // them; the render that matters comes from the reload the filter actually triggers.
                // CreateBinder cannot carry it - it wraps the delegate, so the receiver is lost - and
                // the box needs no binder anyway: the value attribute above already tracks the filter.
                builder.AddAttribute(72, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this,
                    NonRenderingHandler.Wrap<ChangeEventArgs>(
                        args => OnFilterTyped(column, args.Value as string))));
            }

            builder.CloseElement();

            if (column.HasFilter)
            {
                builder.OpenElement(73, "button");
                builder.AddAttribute(74, "type", "button");
                builder.AddAttribute(75, "tabindex", "-1");
                builder.AddAttribute(76, "class", "notranslate rzi rz-cell-filter-clear");
                builder.AddAttribute(77, "style", "position:absolute;inset-inline-end:10px;");
                builder.AddAttribute(78, "aria-label", ClearFilterText);
                builder.AddAttribute(79, "onclick",
                    EventCallback.Factory.Create<MouseEventArgs>(this, _ => Filter(column, null)));
                builder.AddContent(80, "close");
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        // The toggle's name, resolved once for the whole body rather than once per row. Measured: at
        // 1000 rows, a ResourceManager lookup per row cost 24 KB and 8% of the render - the one thing
        // in this feature's a11y that was not free, and the reason it is a field rather than a property
        // read in the loop. Refreshed here because every render arrives through this method, so a grid
        // whose culture changes redraws with the new name; Virtualize's later windows reuse the string
        // this render resolved, which is the same one they would have looked up.
        string? togglerLabel;

        void RenderBody(RenderTreeBuilder builder)
        {
            togglerLabel = ExpandColumn ? ExpandChildItemAriaLabel : null;

            builder.OpenElement(100, "tbody");
            builder.AddAttribute(101, "role", "rowgroup");

            if (AllowVirtualization)
            {
                RenderVirtualizedRows(builder);
            }
            else
            {
                var any = false;

                foreach (var item in View())
                {
                    any = true;

                    RenderRow(builder, item);
                }

                if (!any)
                {
                    RenderEmpty(builder);
                }
            }

            builder.CloseElement();
        }

        void RenderRow(RenderTreeBuilder builder, TItem item)
        {
            var selection = Selection;
            var selected = selection is not null && selection.Contains(item);

            // One lookup for the row, read by the toggle and by the detail row below it. Costs nothing
            // when no Template is set, since the set is never allocated.
            var expanded = Template is not null && IsRowExpanded(item);

            builder.OpenElement(120, "tr");
            builder.AddAttribute(121, "role", "row");

            // Set on the tr, before its children: SetKey applies to the element most recently opened.
            if (ItemKey is { } key)
            {
                builder.SetKey(key(item));
            }

            // No alternating class: rz-grid-table-striped stripes with :nth-child in CSS.
            builder.AddAttribute(122, "class", RowClassFor(item, selected));

            if (selected)
            {
                builder.AddAttribute(123, "aria-selected", "true");
            }

            if (RowStyle is { } rowStyle && rowStyle(item) is { } style)
            {
                builder.AddAttribute(124, "style", style);
            }

            // A per-row delegate costs about 310 bytes, so it is only bound when something listens.
            if (RowClick.HasDelegate || SelectsOnRowClick)
            {
                builder.AddAttribute(125, "onclick", RowClickHandler(item));
            }

            if (RowDoubleClick.HasDelegate)
            {
                builder.AddAttribute(126, "ondblclick", RowDoubleClickHandler(item));
            }

            var tooltips = ShowCellDataAsTooltip;
            var cellClick = CellClick.HasDelegate;
            var cellContextMenu = CellContextMenu.HasDelegate;

            // Read once for the row rather than per cell, on the same reasoning as the two above: an
            // unset hook has to cost a null check, not a property access times five columns.
            var cellRender = CellRender;

            // The toggle. A delegate per row, which is what makes this the one expensive feature on the
            // list - but only for a grid that sets a Template, and nothing above reaches it otherwise.
            if (ExpandColumn)
            {
                builder.OpenElement(130, "td");
                builder.AddAttribute(131, "role", "gridcell");
                builder.AddAttribute(132, "class", "rz-col-icon");
                builder.OpenElement(135, "button");
                builder.AddAttribute(136, "type", "button");
                builder.AddAttribute(137, "tabindex", "-1");
                builder.AddAttribute(138, "aria-expanded", expanded ? "true" : "false");
                builder.AddAttribute(139, "aria-label", togglerLabel);
                builder.AddAttribute(140, "class",
                    "rz-button rz-button-sm rz-button-icon-only rz-variant-text rz-base rz-shade-default");
                builder.AddAttribute(141, "onclick", ToggleHandler(item));
                builder.AddEventStopPropagationAttribute(142, "onclick", true);

                builder.OpenElement(143, "span");
                builder.AddAttribute(144, "class", expanded
                    ? "notranslate rz-row-toggler rzi-chevron-circle-down"
                    : "rz-row-toggler rzi-chevron-circle-right");
                builder.CloseElement();

                builder.CloseElement();
                builder.CloseElement();
            }

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(145, "td");
                builder.AddAttribute(146, "role", "gridcell");

                // rz-cell-data belongs on the span, not here: the theme's rules for it are all
                // descendant selectors, and RadzenDataGrid leaves the td unclassed. Carrying it in
                // both places is inert under the shipped themes but would apply a custom
                // `.rz-cell-data { padding: ... }` twice.
                if (!string.IsNullOrEmpty(column.CssClass))
                {
                    builder.AddAttribute(147, "class", column.CssClass);
                }

                // Per cell, so five times a per-row delegate at five columns. Bound only when something
                // listens - the measured cost of binding these unconditionally is 296 B per cell.
                if (cellClick)
                {
                    builder.AddAttribute(148, "onclick", CellClickHandler(item, column));
                }

                if (cellContextMenu)
                {
                    builder.AddAttribute(149, "oncontextmenu", CellContextMenuHandler(item, column));
                }

                // Memoized on the column, so this is a reference to the same string on every row, and
                // null - no attribute at all - for a column that aligns left and bounds nothing.
                if (column.CellStyle is { } cellStyle)
                {
                    builder.AddAttribute(150, "style", cellStyle);
                }

                // Last of the td's attributes, so a handler can override any of them - which is the
                // point of a render hook, and matches where RadzenDataGrid splats its own.
                if (cellRender is not null)
                {
                    var args = CellRenderArgs(item, column);

                    cellRender(args);

                    if (args.Written is { } written)
                    {
                        // AddMultipleAttributes rather than a loop of AddAttribute, and the difference
                        // is not style: the renderer only resolves duplicate attribute names on an
                        // element this was called for. Writing the pairs by hand instead is 56 bytes a
                        // cell cheaper - it avoids boxing the dictionary's enumerator - and silently
                        // costs the hook the ability to override an attribute the grid wrote, which is
                        // half of what a render hook is for. Measured at 274 KB per 1000 x 5; paid.
                        builder.AddMultipleAttributes(151, written);
                    }
                }

                // The title a narrow-screen theme shows once the table is stacked into cards. Constant
                // strings, so it allocates nothing - but it is a span and a text frame per cell, which
                // is why it is behind a flag rather than always emitted.
                if (Responsive)
                {
                    builder.OpenElement(152, "span");
                    builder.AddAttribute(153, "class", "rz-column-title");
                    builder.AddContent(154, column.HeaderText);
                    builder.CloseElement();
                }

                builder.OpenElement(155, "span");
                builder.AddAttribute(156, "class", column.CellClass);

                // The hover affordance for a truncated cell, and the most expensive thing on this list:
                // an attribute per cell, and the cell's text derived a second time to fill it, since
                // RenderCell writes into the builder rather than handing a string back. Opt-in for that
                // reason - a column that wants it everywhere can use a TemplateColumn instead.
                if (tooltips && column.CellTextOf(item) is { } text)
                {
                    builder.AddAttribute(157, "title", text);
                }

                column.RenderCell(builder, 34, item);
                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseElement();

            // A row of its own beneath the data row, spanning every column including the toggle. Only
            // for the rows actually expanded, so this is per expanded row rather than per row.
            if (expanded)
            {
                builder.OpenElement(172, "tr");
                builder.AddAttribute(173, "role", "row");
                builder.AddAttribute(174, "class", "rz-expanded-row-content");

                builder.OpenElement(175, "td");
                builder.AddAttribute(176, "role", "gridcell");
                builder.AddAttribute(177, "colspan", visibleColumns.Count + (ExpandColumn ? 1 : 0));

                builder.OpenElement(178, "div");
                builder.AddAttribute(179, "class", "rz-expanded-row-template");
                builder.AddAttribute(193, "style", "position:sticky");
                builder.AddContent(194, Template!(item));
                builder.CloseElement();

                builder.CloseElement();
                builder.CloseElement();
            }
        }

        // Held in its own method for the reason RowClickHandler is: a lambda capturing a local of
        // RenderRow makes the compiler allocate that method's display class on entry, for every row,
        // whether or not the branch that needs it runs.
        EventCallback<MouseEventArgs> ToggleHandler(TItem item) =>
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => ToggleRow(item));

        // Composing the row's class costs a string per row unless the result is memoized, and a caller
        // returning one of a handful of constants - which is what a "highlight the overdue ones" rule
        // does - hits this on every row after the first. ReferenceEquals rather than string equality:
        // a caller that builds a fresh string per row pays for it, and should not silently look free.
        string? memoRowClass;
        bool memoRowSelected;
        string? memoRowComposed;

        string RowClassFor(TItem item, bool selected)
        {
            var extra = RowClass?.Invoke(item);

            if (string.IsNullOrEmpty(extra))
            {
                return selected ? "rz-data-row rz-state-highlight" : "rz-data-row";
            }

            if (ReferenceEquals(memoRowClass, extra) && memoRowSelected == selected)
            {
                return memoRowComposed!;
            }

            memoRowClass = extra;
            memoRowSelected = selected;

            return memoRowComposed = (selected ? "rz-data-row rz-state-highlight " : "rz-data-row ") + extra;
        }

        // The closure lives here rather than in RenderRow: a lambda capturing a local of RenderRow makes
        // the compiler allocate that method's display class on entry, for every row, whether or not the
        // branch that needs it is taken. Measured at 31 B/row - a fifth of the component's whole budget.
        EventCallback<MouseEventArgs> RowClickHandler(TItem item) =>
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => OnRowClick(item));

        EventCallback<MouseEventArgs> RowDoubleClickHandler(TItem item) =>
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => RowDoubleClick.InvokeAsync(item));

        EventCallback<MouseEventArgs> CellClickHandler(TItem item, ColumnBase<TItem> column) =>
            EventCallback.Factory.Create<MouseEventArgs>(this,
                _ => CellClick.InvokeAsync(new FastGridCellEventArgs<TItem>(item, column)));

        EventCallback<MouseEventArgs> CellContextMenuHandler(TItem item, ColumnBase<TItem> column) =>
            EventCallback.Factory.Create<MouseEventArgs>(this,
                _ => CellContextMenu.InvokeAsync(new FastGridCellEventArgs<TItem>(item, column)));

        async Task OnRowClick(TItem item)
        {
            if (SelectsOnRowClick)
            {
                await SelectRow(item).ConfigureAwait(false);
            }

            await RowClick.InvokeAsync(item).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies a click to the selection and raises what changed. The grid computes the new selection
        /// rather than writing to <see cref="Selection" />: a component that mutated the collection its
        /// caller handed it would change state the caller never asked it to change, and a caller reading
        /// the parameter back would see a different collection than the one it bound.
        /// </summary>
        async Task SelectRow(TItem item)
        {
            var current = Selection;
            var selected = current is not null && current.Contains(item);

            List<TItem> next;

            if (SelectionMode == DataGridSelectionMode.Single)
            {
                // Clicking the selected row again leaves it selected, as RadzenDataGrid does: single
                // selection is a choice, and there is no way back to "nothing chosen" by clicking.
                if (selected)
                {
                    return;
                }

                if (current is not null && RowDeselect.HasDelegate)
                {
                    foreach (var previous in current)
                    {
                        await RowDeselect.InvokeAsync(previous).ConfigureAwait(false);
                    }
                }

                next = new List<TItem> { item };

                await RowSelect.InvokeAsync(item).ConfigureAwait(false);
            }
            else
            {
                next = current is null ? new List<TItem>() : new List<TItem>(current);

                // Exactly one row changes, and it is the one that was clicked - so which event to raise
                // is known, rather than something to be worked out by comparing the two collections.
                if (selected)
                {
                    next.Remove(item);

                    await RowDeselect.InvokeAsync(item).ConfigureAwait(false);
                }
                else
                {
                    next.Add(item);

                    await RowSelect.InvokeAsync(item).ConfigureAwait(false);
                }
            }

            await SelectionChanged.InvokeAsync(next).ConfigureAwait(false);
        }

        void RenderEmpty(RenderTreeBuilder builder)
        {
            if (EmptyTemplate is null)
            {
                return;
            }

            builder.OpenElement(140, "tr");
            builder.OpenElement(141, "td");
            builder.AddAttribute(142, "class", "rz-datatable-emptymessage");
            builder.AddAttribute(143, "colspan", visibleColumns.Count + (ExpandColumn ? 1 : 0));
            builder.AddContent(144, EmptyTemplate);
            builder.CloseElement();
            builder.CloseElement();
        }

        // Virtualize renders a fragment per visible row, which is a delegate the inline path does not
        // pay - but only for the rows on screen, which is the whole point. The cells stay inline.
        void RenderVirtualizedRows(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Virtualize<TItem>>(110);
            builder.AddAttribute(111, nameof(Virtualize<TItem>.ItemsProvider),
                provideRows ??= ProvideRows);

            // The spacers Virtualize puts above and below the window are divs by default, which is not
            // valid inside a tbody; the rendered rows would be laid out as though the table had none.
            builder.AddAttribute(112, nameof(Virtualize<TItem>.SpacerElement), "tr");
            builder.AddAttribute(113, nameof(Virtualize<TItem>.ItemSize), ItemSize);

            if (VirtualizationOverscanCount > 0)
            {
                builder.AddAttribute(114, nameof(Virtualize<TItem>.OverscanCount), VirtualizationOverscanCount);
            }

            builder.AddAttribute(115, nameof(Virtualize<TItem>.ChildContent),
                virtualRow ??= item => rows => RenderRow(rows, item));

            // Virtualize owns the body while it is on, so the empty row the inline path writes is
            // unreachable - without this an empty virtualized grid showed a header over nothing.
            if (EmptyTemplate is not null)
            {
                builder.AddAttribute(117, nameof(Virtualize<TItem>.EmptyContent),
                    virtualEmpty ??= RenderEmpty);
            }

            builder.AddComponentReferenceCapture(116, captureVirtualize ??= CaptureVirtualize);
            builder.CloseComponent();
        }

        ItemsProviderDelegate<TItem>? provideRows;
        RenderFragment<TItem>? virtualRow;
        RenderFragment? virtualEmpty;
        Action<object>? captureVirtualize;

        void CaptureVirtualize(object component) => virtualize = (Virtualize<TItem>)component;
    }
}
