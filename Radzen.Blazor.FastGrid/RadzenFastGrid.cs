using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

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
    public class RadzenFastGrid<TItem> : ComponentBase
    {
        readonly List<ColumnBase<TItem>> columns = new();
        bool collecting;

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
            if (collecting)
            {
                columns.Add(column);
            }
        }

        /// <inheritdoc />
        protected override void OnParametersSet() => collecting = true;

        /// <summary>Sorts by the given column, toggling direction when it is already the sorted one.</summary>
        public void SortBy(ColumnBase<TItem> column)
        {
            if (!column.CanSort)
            {
                return;
            }

            SortDescending = ReferenceEquals(SortColumn, column) && !SortDescending;
            SortColumn = column;
            StateHasChanged();
        }

        IEnumerable<TItem> View()
        {
            var data = Data ?? Enumerable.Empty<TItem>();

            if (SortColumn is null)
            {
                return data;
            }

            // The column applies its own ordering, so it stays a typed expression the provider can
            // translate rather than a parsed string.
            return data is IQueryable<TItem> queryable
                ? SortColumn.ApplySort(queryable, SortDescending) ?? data
                : SortColumn.ApplySort(data.AsQueryable(), SortDescending) ?? data;
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            columns.Clear();

            builder.OpenComponent<CascadingValue<RadzenFastGrid<TItem>>>(0);
            builder.AddAttribute(1, "Value", this);
            builder.AddAttribute(2, "IsFixed", true);
            builder.AddAttribute(3, "ChildContent", (RenderFragment)(inner =>
            {
                // The columns register while the renderer walks them ...
                inner.AddContent(0, ChildContent);

                // ... and Defer runs after, so the table below sees a populated column list.
                inner.OpenComponent<Defer>(1);
                inner.AddAttribute(2, "ChildContent", (RenderFragment)RenderTable);
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

            builder.OpenElement(2, "table");
            builder.AddAttribute(3, "class", "rz-grid-table rz-grid-table-fixed rz-grid-table-striped");

            RenderHead(builder, cols);
            RenderBody(builder, cols);

            builder.CloseElement();
            builder.CloseElement();
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
                builder.AddContent(19, column.Title);
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
                    builder.AddAttribute(31, "class", string.IsNullOrEmpty(column.CssClass)
                        ? "rz-cell-data"
                        : "rz-cell-data " + column.CssClass);

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
