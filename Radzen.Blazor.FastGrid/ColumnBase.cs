using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Base class for <see cref="RadzenFastGrid{TItem}" /> columns.
    /// </summary>
    /// <remarks>
    /// A column writes its cells straight into the grid's render tree. It deliberately does not return a
    /// <see cref="RenderFragment" /> per cell: that costs a delegate, a closure and a region frame on
    /// every cell, which is a large share of what makes the general-purpose grid expensive at scale.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    public abstract class ColumnBase<TItem> : ComponentBase, IDisposable
    {
        [CascadingParameter] internal RadzenFastGrid<TItem>? Grid { get; set; }

        /// <summary>Header text.</summary>
        [Parameter] public string? Title { get; set; }

        /// <summary>
        /// Replaces the header's text. It goes inside the theme's title spans, not instead of them, so
        /// the truncation and spacing the header depends on still apply to whatever is put here.
        /// </summary>
        [Parameter] public RenderFragment<ColumnBase<TItem>>? HeaderTemplate { get; set; }

        /// <summary>
        /// Content for this column's footer cell. The grid draws a footer row when any visible column
        /// has one, and empty cells for the columns that do not.
        /// </summary>
        /// <remarks>
        /// The template runs on every render. That is nothing for a label, and O(rows) for the reason
        /// most footers exist - an aggregate. <c>@people.Sum(p =&gt; p.Salary)</c> written here is a full
        /// scan per render, and a provider round trip per render if the source is an
        /// <see cref="IQueryable{T}" />. Compute it into a field when the data changes and render the
        /// field.
        /// </remarks>
        [Parameter] public RenderFragment<ColumnBase<TItem>>? FooterTemplate { get; set; }

        /// <summary>Additional CSS class for this column's footer cell.</summary>
        [Parameter] public string? FooterCssClass { get; set; }

        /// <summary>
        /// The text actually drawn in the header. A derived column overrides this to supply a default
        /// when <see cref="Title" /> is not set; it must not assign to the parameter itself, since a
        /// parameter written from the component keeps its assigned value on the next parameter set and
        /// the header would then go stale.
        /// </summary>
        public virtual string? HeaderText => Title;

        /// <summary>Additional CSS class for the column's cells.</summary>
        [Parameter] public string? CssClass { get; set; }

        /// <summary>Whether the column is drawn. A hidden column keeps any filter it carries.</summary>
        [Parameter] public bool Visible { get; set; } = true;

        /// <summary>
        /// Where the column sits among the others, overriding the order it was declared in. Columns
        /// without one keep their declared position, and the two orders interleave by index.
        /// </summary>
        [Parameter] public int? OrderIndex { get; set; }

        /// <summary>
        /// CSS width of the column - <c>"120px"</c>, <c>"20%"</c>. Written once onto the table's
        /// <c>colgroup</c> rather than onto every cell, so it costs nothing per row.
        /// </summary>
        [Parameter] public string? Width { get; set; }

        /// <summary>CSS <c>min-width</c> for the column's cells. Unlike <see cref="Width" />, a
        /// <c>col</c> element cannot carry this, so it goes in the cell style.</summary>
        [Parameter] public string? MinWidth { get; set; }

        /// <summary>CSS <c>max-width</c> for the column's cells.</summary>
        [Parameter] public string? MaxWidth { get; set; }

        /// <summary>Horizontal alignment of the column's cells and header.</summary>
        [Parameter] public TextAlign TextAlign { get; set; } = TextAlign.Left;

        /// <summary>How cell text wraps. Truncating adds the ellipsis, as RadzenDataGrid does.</summary>
        [Parameter] public WhiteSpace WhiteSpace { get; set; } = WhiteSpace.Truncate;

        /// <summary>
        /// The direction this column is sorted in when the grid first renders. Declaring it on more than
        /// one column sorts by the last of them, since the grid sorts by one column at a time. Later
        /// changes are ignored - call <see cref="RadzenFastGrid{TItem}.SortBy" /> to re-sort a live grid.
        /// </summary>
        [Parameter] public SortOrder? SortOrder { get; set; }

        // Constant per column, so they are chosen once here rather than composed per cell. Every result
        // is a literal: the class never allocates at all, and the style only when a width bound is set.
        static string ClassFor(WhiteSpace whiteSpace) => whiteSpace switch
        {
            WhiteSpace.Wrap => "rz-cell-data rz-text-wrap",
            WhiteSpace.Nowrap => "rz-cell-data rz-text-nowrap",
            _ => "rz-cell-data rz-text-truncate",
        };

        static string? StyleFor(TextAlign textAlign) => textAlign switch
        {
            TextAlign.Right => "text-align:right",
            TextAlign.Center => "text-align:center",
            TextAlign.Justify => "text-align:justify",
            TextAlign.Start => "text-align:start",
            TextAlign.End => "text-align:end",
            _ => null,
        };

        /// <summary>The class of this column's cell span, carrying its wrapping mode.</summary>
        internal string CellClass => ClassFor(WhiteSpace);

        string? cellStyle;
        bool cellStyleKnown;
        TextAlign cellStyleAlign;
        string? cellStyleMin;
        string? cellStyleMax;

        /// <summary>
        /// The inline style of this column's cells, or null when it has none - which is the common case,
        /// and the one that costs no attribute at all. Memoized: a data cell's style is the same on every
        /// row, so composing it per cell would be the sort of per-row string work this grid exists to
        /// avoid.
        /// </summary>
        internal string? CellStyle
        {
            get
            {
                // Tracked with a flag rather than by testing cellStyle for null, because null is the
                // answer for the commonest column there is - the memo would never engage for exactly
                // the case it exists to keep cheap.
                if (cellStyleKnown
                    && cellStyleAlign == TextAlign
                    && string.Equals(cellStyleMin, MinWidth, StringComparison.Ordinal)
                    && string.Equals(cellStyleMax, MaxWidth, StringComparison.Ordinal))
                {
                    return cellStyle;
                }

                cellStyleKnown = true;
                cellStyleAlign = TextAlign;
                cellStyleMin = MinWidth;
                cellStyleMax = MaxWidth;

                var align = StyleFor(TextAlign);
                var hasMin = !string.IsNullOrEmpty(MinWidth);
                var hasMax = !string.IsNullOrEmpty(MaxWidth);

                if (!hasMin && !hasMax)
                {
                    // The overwhelmingly common shape, and a literal rather than a built string.
                    return cellStyle = align;
                }

                var builder = new System.Text.StringBuilder();

                if (align is not null)
                {
                    builder.Append(align);
                }

                if (hasMin)
                {
                    Semicolon(builder).Append("min-width:").Append(MinWidth);
                }

                if (hasMax)
                {
                    Semicolon(builder).Append("max-width:").Append(MaxWidth);
                }

                return cellStyle = builder.ToString();
            }
        }

        static System.Text.StringBuilder Semicolon(System.Text.StringBuilder builder)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            return builder;
        }

        string? colStyle;
        string? colStyleWidth;

        /// <summary>
        /// The style of this column's <c>col</c> element, for the effective width the grid resolved -
        /// this column's own, or the grid's default. Memoized against that width.
        /// </summary>
        internal string? ColStyle(string? width)
        {
            if (string.IsNullOrEmpty(width))
            {
                return null;
            }

            if (colStyle is null || !string.Equals(colStyleWidth, width, StringComparison.Ordinal))
            {
                colStyleWidth = width;
                colStyle = "width:" + width;
            }

            return colStyle;
        }

        /// <summary>Whether the column offers sorting. Ignored when the column has no sortable path.</summary>
        [Parameter] public bool Sortable { get; set; } = true;

        /// <summary>Whether the column offers filtering. Ignored when the column has no filterable path.</summary>
        [Parameter] public bool Filterable { get; set; } = true;

        /// <summary>
        /// The value this column filters by. Setting it declares the initial filter; changing it later
        /// replaces whatever the grid's own filtering put there.
        /// </summary>
        [Parameter] public object? FilterValue { get; set; }

        /// <summary>
        /// How <see cref="FilterValue" /> is compared. Defaults to <c>Contains</c> for a string column
        /// and <c>Equals</c> for every other type.
        /// </summary>
        [Parameter] public FilterOperator? FilterOperator { get; set; }

        /// <summary>
        /// The member of a collection's element that the filter compares, as a dotted path, or null when
        /// the filter compares the element itself. Derived from a column's own expressions rather than
        /// authored; it is what <c>FilterDescriptor.FilterProperty</c> carries, which is what turns a
        /// comparison into <c>Accounts.Any(a =&gt; a.Name ...)</c>.
        /// </summary>
        public virtual string? FilterMemberPath => null;

        /// <summary>
        /// How this column's filter is presented, overriding the grid's <c>FilterMode</c>.
        /// </summary>
        [Parameter] public FilterMode? FilterMode { get; set; }

        /// <summary>
        /// The values offered by a check-box-list filter. Supply this to skip the distinct scan of the
        /// data - which is what a large or remote source wants - or to offer values the data has none of.
        /// </summary>
        [Parameter] public IEnumerable? FilterLookupData { get; set; }

        /// <summary>
        /// The distinct values of this column across <paramref name="source" />, for a check-box-list
        /// filter. Composed as a query rather than materialized, so a provider can translate it.
        /// </summary>
        public virtual IQueryable? DistinctValues(IQueryable<TItem> source) => null;

        /// <summary>
        /// Replaces the built-in filter input for this column. The built-in one is a text box and
        /// nothing more - no operator menu, no date popup, no numeric range - so anything richer, and
        /// anything a computed column needs, goes here.
        /// </summary>
        [Parameter] public RenderFragment<ColumnBase<TItem>>? FilterTemplate { get; set; }

        object? declaredFilterValue;
        FilterOperator? declaredFilterOperator;

        /// <summary>The value the column is filtering by right now.</summary>
        public object? CurrentFilterValue { get; private set; }

        /// <summary>The operator the column is filtering with right now.</summary>
        public FilterOperator CurrentFilterOperator { get; private set; }

        /// <summary>
        /// The dotted path this column filters by. Defaults to <see cref="PropertyPath" />; a column with
        /// no path cannot be filtered, for the same reason it cannot be sorted.
        /// </summary>
        public virtual string? FilterPropertyPath => PropertyPath;

        /// <summary>The CLR type of the filtered property, which decides how a value is compared.</summary>
        public virtual Type FilterPropertyType => typeof(object);

        /// <summary>
        /// The type a filter value is compared against. For a collection-valued column that is the
        /// element type, since the filter matches a row when any member matches - so a list of strings
        /// filters like a string, not like a list.
        /// </summary>
        public virtual Type FilterElementType => FilterPropertyType;

        /// <summary>
        /// <see cref="FilterElementType" />, or - when that is <c>object</c> and so says nothing - the
        /// type the column's filter path actually reaches on <typeparamref name="TItem" />.
        /// </summary>
        /// <remarks>
        /// A column declared as <c>PropertyColumn&lt;T, object&gt;</c>, or a template column with a
        /// SortProperty, knows only <c>object</c>. Comparing against that leaves what was typed as a
        /// string, and the predicate builder then puts a string constant where an int belongs:
        /// "argument types do not match", thrown from the filter box.
        /// </remarks>
        public Type EffectiveFilterType
        {
            get
            {
                var declared = FilterElementType;

                if (declared != typeof(object))
                {
                    return declared;
                }

                // Reached only from the filter row and the filter callbacks, never per row or per cell,
                // so it is resolved on demand rather than cached behind an invalidation rule.
                return FilterPropertyPath is { } path
                    ? PropertyPathResolver.TypeOf(typeof(TItem), path) ?? typeof(object)
                    : typeof(object);
            }
        }

        /// <summary>Whether this column can be filtered.</summary>
        public virtual bool CanFilter => Filterable && FilterPropertyPath is not null;

        /// <summary>
        /// Whether the column's current filter would actually narrow anything. An empty value filters
        /// nothing, except for the operators that are about emptiness themselves.
        /// </summary>
        public bool HasFilter =>
            CanFilter &&
            (HasFilterValue
                || CurrentFilterOperator is Radzen.FilterOperator.IsNull or Radzen.FilterOperator.IsNotNull
                    or Radzen.FilterOperator.IsEmpty or Radzen.FilterOperator.IsNotEmpty);

        bool HasFilterValue => CurrentFilterValue switch
        {
            null => false,
            string text => text.Length > 0,

            // A check-box-list filter with nothing ticked is not a filter that matches nothing; it is no
            // filter. Testing for null only would leave the grid empty as soon as the last box is cleared.
            // The selection is a list in every path the grid itself builds, so the count answers without
            // an enumerator; the general case still has to walk one, and has to dispose it.
            ICollection collection => collection.Count > 0,
            IEnumerable sequence => Any(sequence),
            _ => true,
        };

        static bool Any(IEnumerable sequence)
        {
            var enumerator = sequence.GetEnumerator();

            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        /// <summary>Sets the column's live filter. Called by the grid; does not reload on its own.</summary>
        internal void SetFilter(object? value, FilterOperator? filterOperator)
        {
            CurrentFilterValue = value;
            CurrentFilterOperator = filterOperator ?? DefaultFilterOperator;
        }

        FilterOperator DefaultFilterOperator => EffectiveFilterType == typeof(string)
            ? Radzen.FilterOperator.Contains
            : Radzen.FilterOperator.Equals;

        bool initialized;

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (!initialized)
            {
                // Both parameters may legitimately be null, so the first pass cannot be told from a
                // no-op by comparing them; it has to be marked.
                initialized = true;
                declaredFilterValue = FilterValue;
                declaredFilterOperator = FilterOperator;
                CurrentFilterValue = FilterValue;
                CurrentFilterOperator = FilterOperator ?? DefaultFilterOperator;

                // Only here, and deliberately. A declared sort is the grid's starting state, not a live
                // binding: honouring later changes would mean re-sorting - and, on the async path,
                // reloading - from inside the grid's own render pass.
                if (SortOrder is { } order)
                {
                    Grid?.ApplyDeclaredSort(this, order);
                }

                return;
            }

            // The declared value is the authority whenever it changes, and the grid's own filtering owns
            // it in between. Tracking what was declared separately keeps this out of the parameter
            // itself, which a component must not assign to.
            if (!Equals(declaredFilterValue, FilterValue))
            {
                declaredFilterValue = FilterValue;
                CurrentFilterValue = FilterValue;
            }

            if (declaredFilterOperator != FilterOperator)
            {
                declaredFilterOperator = FilterOperator;
                CurrentFilterOperator = FilterOperator ?? DefaultFilterOperator;
            }
        }

        /// <summary>
        /// The dotted property path this column sorts, filters and persists by, or <c>null</c> when the
        /// authored expression is computed rather than a simple member access.
        /// </summary>
        public virtual string? PropertyPath => null;

        /// <summary>Whether this column can be sorted. False for a computed column with no explicit sort.</summary>
        public virtual bool CanSort => Sortable && PropertyPath is not null;

        /// <summary>Writes one cell for <paramref name="item" /> into <paramref name="builder" />.</summary>
        public abstract void RenderCell(RenderTreeBuilder builder, int sequence, TItem item);

        /// <summary>
        /// The cell's text, for the grid's cell tooltip. Null when the column has no text to give - a
        /// template column's content is markup, not a string.
        /// </summary>
        /// <remarks>
        /// Deriving the text a second time is the cost of the tooltip: <see cref="RenderCell" /> writes
        /// into the builder rather than returning a string, and threading one back out of it would put
        /// an out parameter on the hot path for every caller who does not want the tooltip.
        /// </remarks>
        /// <param name="item">The row.</param>
        public virtual string? CellTextOf(TItem item) => null;

        /// <summary>
        /// Applies this column's ordering to <paramref name="source" />. Overridden by columns that know
        /// their property type, so the ordering is a typed expression the provider can translate rather
        /// than a parsed string.
        /// </summary>
        public virtual IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending) => null;

        /// <summary>
        /// Adds this column's ordering after one already applied, for a grid sorting by more than one
        /// column. Null when the column cannot be ordered by, exactly as <see cref="ApplySort" />.
        /// </summary>
        /// <param name="source">The already-ordered query.</param>
        /// <param name="descending">Whether to order descending.</param>
        public virtual IOrderedQueryable<TItem>? ApplyThenBy(IOrderedQueryable<TItem> source, bool descending) => null;

        /// <inheritdoc />
        public override Task SetParametersAsync(ParameterView parameters)
        {
            parameters.SetParameterProperties(this);

            if (Grid is null)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} must be placed inside a {nameof(RadzenFastGrid<TItem>)}.");
            }

            // Registration cannot be driven from here alone. The renderer skips SetParametersAsync
            // entirely when a retained component's parameters are all known-immutable and unchanged
            // (ParameterView.DefinitelyEquals), which is every column whose only parameters are strings -
            // so a grid that rebuilt its column list per render lost those columns on the second pass.
            // The column registers once and leaves when it is disposed, as RadzenDataGridColumn does.
            Grid.AddColumn(this);

            return base.SetParametersAsync(ParameterView.Empty);
        }

        /// <summary>A column renders nothing itself; the grid draws its header and cells.</summary>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }

        /// <summary>
        /// A column renders nothing, so its own output can never need refreshing. The grid reads the
        /// column's state directly and redraws itself; a render pass here would only queue an empty
        /// frame array for the renderer to diff against the last empty one, once per column per render.
        /// </summary>
        protected override bool ShouldRender() => false;

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        /// <summary>Leaves the grid. A derived column overrides this to release state of its own.</summary>
        /// <param name="disposing">Whether managed state should be released.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Grid?.RemoveColumn(this);
            }
        }
    }
}
