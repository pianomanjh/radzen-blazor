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
        /// Whether clicking a second column adds to the sort instead of replacing it. A click then
        /// cycles a column ascending, descending, then out of the sort altogether - which is the only
        /// way to remove one, since there is nowhere else to click.
        /// </summary>
        [Parameter] public bool AllowMultiColumnSorting { get; set; }

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

                if (!column.Visible)
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
            RefreshVisibleColumns();

            // Here and not in OnParametersSet: stored state names columns by property path, and no
            // column has registered by then. Defer has run, so by now every one of them has.
            if (settingsPending)
            {
                settingsPending = false;

                ApplySettings(appliedSettings!);
            }

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

            if (Paging && PagerPosition.HasFlag(PagerPosition.Top))
            {
                RenderPager(builder, 10, captureTopPager ??= p => topPager = (RadzenPager)p);
            }

            // 22, not 20: the top pager's band now runs to 20, and the numbers a region writes must
            // ascend in the order it writes them.
            builder.OpenElement(22, "table");
            builder.AddAttribute(23, "class", TableClass());

            RenderColumnGroup(builder);

            if (ShowHeader)
            {
                RenderHead(builder);
            }

            RenderBody(builder);
            RenderFoot(builder);

            builder.CloseElement();

            if (Paging && PagerPosition.HasFlag(PagerPosition.Bottom))
            {
                RenderPager(builder, 200, captureBottomPager ??= p => bottomPager = (RadzenPager)p);
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

            builder.OpenElement(24, "colgroup");

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(25, "col");

                if (column.ColStyle(column.Width ?? ColumnWidth) is { } style)
                {
                    builder.AddAttribute(26, "style", style);
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

        void RenderHead(RenderTreeBuilder builder)
        {
            builder.OpenElement(30, "thead");
            builder.AddAttribute(31, "role", "rowgroup");
            builder.OpenElement(32, "tr");
            builder.AddAttribute(33, "role", "row");

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
            builder.AddAttribute(69, "aria-label", column.HeaderText);
            builder.AddAttribute(70, "value", column.CurrentFilterValue);
            builder.AddAttribute(71, "onchange", EventCallback.Factory.CreateBinder<string?>(this,
                value => OnFilterInput(column, value), column.CurrentFilterValue?.ToString()));
            builder.CloseElement();

            if (column.HasFilter)
            {
                builder.OpenElement(72, "button");
                builder.AddAttribute(73, "type", "button");
                builder.AddAttribute(74, "tabindex", "-1");
                builder.AddAttribute(75, "class", "notranslate rzi rz-cell-filter-clear");
                builder.AddAttribute(76, "style", "position:absolute;inset-inline-end:10px;");
                builder.AddAttribute(77, "onclick",
                    EventCallback.Factory.Create<MouseEventArgs>(this, _ => Filter(column, null)));
                builder.AddContent(78, "close");
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        void RenderBody(RenderTreeBuilder builder)
        {
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

            builder.OpenElement(120, "tr");
            builder.AddAttribute(121, "role", "row");

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

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                builder.OpenElement(160, "td");
                builder.AddAttribute(161, "role", "gridcell");

                // rz-cell-data belongs on the span, not here: the theme's rules for it are all
                // descendant selectors, and RadzenDataGrid leaves the td unclassed. Carrying it in
                // both places is inert under the shipped themes but would apply a custom
                // `.rz-cell-data { padding: ... }` twice.
                if (!string.IsNullOrEmpty(column.CssClass))
                {
                    builder.AddAttribute(162, "class", column.CssClass);
                }

                // Per cell, so five times a per-row delegate at five columns. Bound only when something
                // listens - the measured cost of binding these unconditionally is 296 B per cell.
                if (cellClick)
                {
                    builder.AddAttribute(163, "onclick", CellClickHandler(item, column));
                }

                if (cellContextMenu)
                {
                    builder.AddAttribute(164, "oncontextmenu", CellContextMenuHandler(item, column));
                }

                // Memoized on the column, so this is a reference to the same string on every row, and
                // null - no attribute at all - for a column that aligns left and bounds nothing.
                if (column.CellStyle is { } cellStyle)
                {
                    builder.AddAttribute(165, "style", cellStyle);
                }

                // The title a narrow-screen theme shows once the table is stacked into cards. Constant
                // strings, so it allocates nothing - but it is a span and a text frame per cell, which
                // is why it is behind a flag rather than always emitted.
                if (Responsive)
                {
                    builder.OpenElement(166, "span");
                    builder.AddAttribute(167, "class", "rz-column-title");
                    builder.AddContent(168, column.HeaderText);
                    builder.CloseElement();
                }

                builder.OpenElement(169, "span");
                builder.AddAttribute(170, "class", column.CellClass);

                // The hover affordance for a truncated cell, and the most expensive thing on this list:
                // an attribute per cell, and the cell's text derived a second time to fill it, since
                // RenderCell writes into the builder rather than handing a string back. Opt-in for that
                // reason - a column that wants it everywhere can use a TemplateColumn instead.
                if (tooltips && column.CellTextOf(item) is { } text)
                {
                    builder.AddAttribute(171, "title", text);
                }

                column.RenderCell(builder, 34, item);
                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseElement();
        }

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
            builder.AddAttribute(143, "colspan", visibleColumns.Count);
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
