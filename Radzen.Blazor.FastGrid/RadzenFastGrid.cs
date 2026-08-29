using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
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
        bool collectingColumns;

        /// <summary>The rows to display.</summary>
        [Parameter] public IEnumerable<TItem>? Data { get; set; }

        /// <summary>The column definitions.</summary>
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <summary>Whether column headers offer sorting.</summary>
        [Parameter] public bool AllowSorting { get; set; }

        /// <summary>Rows currently selected. Membership is looked up per row, which costs no allocation.</summary>
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

        internal void AddColumn(ColumnBase<TItem> column)
        {
            // Only while a collection pass is open. A column sets its parameters whenever the renderer
            // walks it, which is not only during collection; without this window the list would gain a
            // duplicate every time that happened, and the column count would depend on how many
            // registrations landed between the clear below and the table being drawn.
            if (collectingColumns)
            {
                columns.Add(column);
            }
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
            columns.Clear();

            collectingColumns = true;

            builder.OpenComponent<CascadingValue<RadzenFastGrid<TItem>>>(0);
            builder.AddAttribute(1, "Value", this);
            builder.AddAttribute(2, "IsFixed", true);
            builder.AddAttribute(3, "ChildContent", (RenderFragment)(inner =>
            {
                // The columns register while the renderer walks them ...
                inner.AddContent(0, ChildContent);

                // ... and Defer runs after, so the table below sees a populated column list.
                inner.OpenComponent<Defer>(1);
                inner.AddAttribute(2, "ChildContent", (RenderFragment)(deferred =>
                {
                    // Everything above has registered by now, so close the window before drawing.
                    collectingColumns = false;

                    // A column can leave the set between renders. The sort must not outlive it, or the
                    // grid keeps ordering by a column nothing on screen names and nothing can clear.
                    if (SortColumn is not null && !columns.Contains(SortColumn))
                    {
                        SortColumn = null;
                        SortDescending = false;
                    }

                    RenderTable(deferred);
                }));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        }

        void RenderTable(RenderTreeBuilder builder)
        {
            var cols = columns;

            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", string.IsNullOrEmpty(CssClass)
                ? "rz-data-grid rz-datatable"
                : "rz-data-grid rz-datatable " + CssClass);

            if (AllowPaging && PagerPosition.HasFlag(PagerPosition.Top))
            {
                RenderPager(builder, 40);
            }

            builder.OpenElement(2, "table");
            builder.AddAttribute(3, "class", "rz-grid-table rz-grid-table-fixed rz-grid-table-striped");

            RenderHead(builder, cols);
            RenderBody(builder, cols);

            builder.CloseElement();

            if (AllowPaging && PagerPosition.HasFlag(PagerPosition.Bottom))
            {
                RenderPager(builder, 60);
            }

            builder.CloseElement();
        }

        // A sequence number identifies a position in the source, so the two pager positions take
        // separate ranges rather than both writing the same numbers into one region.
        void RenderPager(RenderTreeBuilder builder, int sequence)
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

            builder.CloseComponent();
        }

        void RenderHead(RenderTreeBuilder builder, List<ColumnBase<TItem>> cols)
        {
            builder.OpenElement(4, "thead");
            builder.AddAttribute(5, "role", "rowgroup");
            builder.OpenElement(6, "tr");
            builder.AddAttribute(7, "role", "row");

            for (var i = 0; i < cols.Count; i++)
            {
                var column = cols[i];
                var sortable = AllowSorting && column.CanSort;

                builder.OpenElement(8, "th");
                builder.AddAttribute(9, "role", "columnheader");
                builder.AddAttribute(10, "scope", "col");
                builder.AddAttribute(11, "class", sortable
                    ? "rz-unselectable-text rz-sortable-column"
                    : "rz-unselectable-text");

                if (ReferenceEquals(SortColumn, column))
                {
                    builder.AddAttribute(12, "aria-sort", SortDescending ? "descending" : "ascending");
                }

                // The theme gives th padding:0 and hangs the header padding off a direct child div, so
                // this wrapper is load-bearing: without it the header row renders shorter than
                // RadzenDataGrid's. It is per column, not per row, so it costs nothing at scale.
                builder.OpenElement(13, "div");

                if (sortable)
                {
                    var captured = column;
                    builder.AddAttribute(14, "onclick",
                        EventCallback.Factory.Create<MouseEventArgs>(this, _ => SortBy(captured)));
                }

                builder.OpenElement(15, "span");
                builder.AddAttribute(16, "class", "rz-column-title");
                builder.OpenElement(17, "span");
                builder.AddAttribute(18, "class", "rz-column-title-content rz-text-truncate");
                builder.AddContent(19, column.HeaderText);
                builder.CloseElement();

                if (ReferenceEquals(SortColumn, column))
                {
                    builder.OpenElement(20, "span");
                    builder.AddAttribute(21, "class", SortDescending
                        ? "notranslate rz-sortable-column-icon rzi-grid-sort rzi-sort-desc"
                        : "notranslate rz-sortable-column-icon rzi-grid-sort rzi-sort-asc");
                    builder.CloseElement();
                }

                builder.CloseElement();
                builder.CloseElement();
                builder.CloseElement();
            }

            builder.CloseElement();

            if (AllowFiltering)
            {
                RenderFilterRow(builder, cols);
            }

            builder.CloseElement();
        }

        // Matches RadzenDataGrid's filter row exactly: a second header row whose th holds
        // div.rz-cell-filter > div.rz-cell-filter-content > span.rz-cell-filter-label directly, with no
        // title wrapper. The theme's th padding hangs off that first div, as it does off the title one.
        void RenderFilterRow(RenderTreeBuilder builder, List<ColumnBase<TItem>> cols)
        {
            builder.OpenElement(70, "tr");
            builder.AddAttribute(71, "role", "row");

            for (var i = 0; i < cols.Count; i++)
            {
                var column = cols[i];

                builder.OpenElement(72, "th");
                builder.AddAttribute(73, "role", "columnheader");
                builder.AddAttribute(74, "scope", "col");
                builder.AddAttribute(75, "class", "rz-unselectable-text");

                if (column.CanFilter || column.FilterTemplate is not null)
                {
                    builder.OpenElement(76, "div");
                    builder.AddAttribute(77, "class", "rz-cell-filter");
                    builder.OpenElement(78, "div");
                    builder.AddAttribute(79, "class", "rz-cell-filter-content");

                    if (column.FilterTemplate is not null)
                    {
                        builder.AddContent(80, column.FilterTemplate(column));
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

        void RenderFilterInput(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            var captured = column;

            builder.OpenElement(81, "span");
            builder.AddAttribute(82, "class", "rz-cell-filter-label");
            builder.AddAttribute(83, "style", "height:35px; width:100%;");

            builder.OpenElement(84, "input");
            builder.AddAttribute(85, "type", "text");
            builder.AddAttribute(86, "autocomplete", "off");
            builder.AddAttribute(87, "class", "rz-textbox");
            builder.AddAttribute(88, "style", "width: 100%;");
            builder.AddAttribute(89, "aria-label", column.HeaderText);
            builder.AddAttribute(90, "value", column.CurrentFilterValue);
            builder.AddAttribute(91, "onchange", EventCallback.Factory.CreateBinder<string?>(this,
                value => OnFilterInput(captured, value), column.CurrentFilterValue?.ToString()));
            builder.CloseElement();

            if (column.HasFilter)
            {
                builder.OpenElement(92, "button");
                builder.AddAttribute(93, "type", "button");
                builder.AddAttribute(94, "tabindex", "-1");
                builder.AddAttribute(95, "class", "notranslate rzi rz-cell-filter-clear");
                builder.AddAttribute(96, "style", "position:absolute;inset-inline-end:10px;");
                builder.AddAttribute(97, "onclick",
                    EventCallback.Factory.Create<MouseEventArgs>(this, _ => Filter(captured, null)));
                builder.AddContent(98, "close");
                builder.CloseElement();
            }

            builder.CloseElement();
        }

        void RenderBody(RenderTreeBuilder builder, List<ColumnBase<TItem>> cols)
        {
            builder.OpenElement(22, "tbody");
            builder.AddAttribute(23, "role", "rowgroup");

            var any = false;
            var rowClickable = RowClick.HasDelegate;
            var selection = Selection;

            foreach (var item in View())
            {
                any = true;

                builder.OpenElement(24, "tr");
                builder.AddAttribute(25, "role", "row");

                // No alternating class: rz-grid-table-striped stripes with :nth-child in CSS.
                if (selection is not null && selection.Contains(item))
                {
                    builder.AddAttribute(26, "class", "rz-data-row rz-state-highlight");
                    builder.AddAttribute(27, "aria-selected", "true");
                }
                else
                {
                    builder.AddAttribute(26, "class", "rz-data-row");
                }

                // A per-row delegate costs about 310 bytes, so it is only bound when something listens.
                if (rowClickable)
                {
                    var captured = item;
                    builder.AddAttribute(28, "onclick",
                        EventCallback.Factory.Create<MouseEventArgs>(this, _ => RowClick.InvokeAsync(captured)));
                }

                for (var i = 0; i < cols.Count; i++)
                {
                    var column = cols[i];

                    builder.OpenElement(29, "td");
                    builder.AddAttribute(30, "role", "gridcell");

                    // rz-cell-data belongs on the span, not here: the theme's rules for it are all
                    // descendant selectors, and RadzenDataGrid leaves the td unclassed. Carrying it in
                    // both places is inert under the shipped themes but would apply a custom
                    // `.rz-cell-data { padding: ... }` twice.
                    if (!string.IsNullOrEmpty(column.CssClass))
                    {
                        builder.AddAttribute(31, "class", column.CssClass);
                    }

                    builder.OpenElement(32, "span");
                    builder.AddAttribute(33, "class", "rz-cell-data");
                    column.RenderCell(builder, 34, item);
                    builder.CloseElement();

                    builder.CloseElement();
                }

                builder.CloseElement();
            }

            if (!any && EmptyTemplate is not null)
            {
                builder.OpenElement(35, "tr");
                builder.OpenElement(36, "td");
                builder.AddAttribute(37, "class", "rz-datatable-emptymessage");
                builder.AddAttribute(38, "colspan", cols.Count);
                builder.AddContent(39, EmptyTemplate);
                builder.CloseElement();
                builder.CloseElement();
            }

            builder.CloseElement();
        }
    }
}
