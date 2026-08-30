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
        [Parameter] public ICollection<TItem>? Selection { get; set; }

        /// <summary>Raised when a row is clicked. No handler means no per-row delegate is allocated.</summary>
        [Parameter] public EventCallback<TItem> RowClick { get; set; }

        /// <summary>Extra CSS class for the grid element.</summary>
        [Parameter] public string? CssClass { get; set; }

        /// <summary>Content shown when there are no rows.</summary>
        [Parameter] public RenderFragment? EmptyTemplate { get; set; }

        /// <summary>The column currently sorted, if any.</summary>
        public ColumnBase<TItem>? SortColumn { get; private set; }

        /// <summary>Whether the current sort is descending.</summary>
        public bool SortDescending { get; private set; }

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

            // The sort must not outlive the column it orders by, or the grid keeps ordering by something
            // nothing on screen names and nothing can clear. Nor must the column's check-box-list values,
            // which would hold the column and everything it listed for as long as the grid lives.
            if (ReferenceEquals(SortColumn, column))
            {
                SortColumn = null;
                SortDescending = false;
            }

            lookups.Remove(column);
        }

        /// <summary>Sorts by the given column, toggling direction when it is already the sorted one.</summary>
        public Task SortBy(ColumnBase<TItem> column)
        {
            if (column is null || !column.CanSort)
            {
                return Task.CompletedTask;
            }

            SortDescending = ReferenceEquals(SortColumn, column) && !SortDescending;
            SortColumn = column;

            // A sort change moves the whole set, not just the page, so go back to the first page - the
            // row that was on page 3 is not on page 3 any more.
            skip = 0;

            return RefreshAsync();
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
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
            builder.AddAttribute(23, "class", "rz-grid-table rz-grid-table-fixed rz-grid-table-striped");

            RenderHead(builder);
            RenderBody(builder);

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

            builder.AddComponentReferenceCapture(sequence + 10, capture);
            builder.CloseComponent();
        }

        void RenderHead(RenderTreeBuilder builder)
        {
            builder.OpenElement(30, "thead");
            builder.AddAttribute(31, "role", "rowgroup");
            builder.OpenElement(32, "tr");
            builder.AddAttribute(33, "role", "row");

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var sortable = AllowSorting && column.CanSort;

                builder.OpenElement(34, "th");
                builder.AddAttribute(35, "role", "columnheader");
                builder.AddAttribute(36, "scope", "col");
                builder.AddAttribute(37, "class", sortable
                    ? "rz-unselectable-text rz-sortable-column"
                    : "rz-unselectable-text");

                if (ReferenceEquals(SortColumn, column))
                {
                    builder.AddAttribute(38, "aria-sort", SortDescending ? "descending" : "ascending");
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
                builder.AddContent(45, column.HeaderText);
                builder.CloseElement();

                if (ReferenceEquals(SortColumn, column))
                {
                    builder.OpenElement(46, "span");
                    builder.AddAttribute(47, "class", SortDescending
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

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

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

            builder.OpenElement(120, "tr");
            builder.AddAttribute(121, "role", "row");

            // No alternating class: rz-grid-table-striped stripes with :nth-child in CSS.
            if (selection is not null && selection.Contains(item))
            {
                builder.AddAttribute(122, "class", "rz-data-row rz-state-highlight");
                builder.AddAttribute(123, "aria-selected", "true");
            }
            else
            {
                builder.AddAttribute(122, "class", "rz-data-row");
            }

            // A per-row delegate costs about 310 bytes, so it is only bound when something listens.
            if (RowClick.HasDelegate)
            {
                builder.AddAttribute(124, "onclick", RowClickHandler(item));
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                builder.OpenElement(125, "td");
                builder.AddAttribute(126, "role", "gridcell");

                // rz-cell-data belongs on the span, not here: the theme's rules for it are all
                // descendant selectors, and RadzenDataGrid leaves the td unclassed. Carrying it in
                // both places is inert under the shipped themes but would apply a custom
                // `.rz-cell-data { padding: ... }` twice.
                if (!string.IsNullOrEmpty(column.CssClass))
                {
                    builder.AddAttribute(127, "class", column.CssClass);
                }

                builder.OpenElement(128, "span");
                builder.AddAttribute(129, "class", "rz-cell-data");
                column.RenderCell(builder, 34, item);
                builder.CloseElement();

                builder.CloseElement();
            }

            builder.CloseElement();
        }

        // The closure lives here rather than in RenderRow: a lambda capturing a local of RenderRow makes
        // the compiler allocate that method's display class on entry, for every row, whether or not the
        // branch that needs it is taken. Measured at 31 B/row - a fifth of the component's whole budget.
        EventCallback<MouseEventArgs> RowClickHandler(TItem item) =>
            EventCallback.Factory.Create<MouseEventArgs>(this, _ => RowClick.InvokeAsync(item));

        void RenderEmpty(RenderTreeBuilder builder)
        {
            if (EmptyTemplate is null)
            {
                return;
            }

            builder.OpenElement(140, "tr");
            builder.OpenElement(141, "td");
            builder.AddAttribute(142, "class", "rz-datatable-emptymessage");
            builder.AddAttribute(143, "colspan", columns.Count);
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

            builder.AddComponentReferenceCapture(116, captureVirtualize ??= CaptureVirtualize);
            builder.CloseComponent();
        }

        ItemsProviderDelegate<TItem>? provideRows;
        RenderFragment<TItem>? virtualRow;
        Action<object>? captureVirtualize;

        void CaptureVirtualize(object component) => virtualize = (Virtualize<TItem>)component;
    }
}
