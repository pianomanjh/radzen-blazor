using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
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

        /// <summary>Whether the column picker offers this column. Ignored unless the grid allows picking.</summary>
        [Parameter] public bool Pickable { get; set; } = true;

        /// <summary>
        /// What the column picker calls this column, when its <see cref="Title" /> is not what should
        /// appear there.
        /// </summary>
        [Parameter] public string? ColumnPickerTitle { get; set; }

        /// <summary>
        /// The name the picker actually shows: <see cref="ColumnPickerTitle" />, else <see cref="Title" />,
        /// else the property path, so a column that names neither is still identifiable in the list.
        /// </summary>
        /// <remarks>
        /// Separate from the parameter rather than a fallback inside its getter, because a component
        /// parameter has to be an auto-property (BL0007) and this package builds warnings as errors.
        /// It is public because the picker names it through <c>TextProperty</c>, which reads it by name.
        /// </remarks>
        public string PickerTitle => ColumnPickerTitle ?? Title ?? PropertyPath ?? string.Empty;

        bool declaredVisible = true;

        // What the picker last said, or null while nothing has said anything. Kept apart from Visible for
        // the same reason the filter's applied text is: the parameter is the markup's word and a component
        // must not assign to it, so the runtime override lives beside it and yields whenever the
        // declaration changes underneath.
        bool? pickedVisible;

        /// <summary>Whether the column is drawn right now - the picker's answer if it has one.</summary>
        internal bool IsVisible => pickedVisible ?? Visible;

        /// <summary>Records what the picker chose. Called by the grid; does not redraw on its own.</summary>
        internal void SetPicked(bool visible) => pickedVisible = visible;

        /// <summary>
        /// Where the column sits among the others, overriding the order it was declared in. Columns
        /// without one keep their declared position, and the two orders interleave by index.
        /// </summary>
        [Parameter] public int? OrderIndex { get; set; }

        int? reorderedIndex;

        /// <summary>
        /// Where the column actually sits: where a drag put it, else what the markup said.
        /// </summary>
        /// <remarks>
        /// A drag cannot write to <see cref="OrderIndex" /> for the same reason it cannot write to
        /// <see cref="Width" />: it is a parameter, so the next parameter set would put the markup's
        /// value back and the columns would snap to their declared order on the next unrelated
        /// re-render.
        /// </remarks>
        internal int? EffectiveOrderIndex => reorderedIndex ?? OrderIndex;

        /// <summary>The position a drag settled on, or null when none has.</summary>
        internal int? ReorderedIndex => reorderedIndex;

        /// <summary>Records the position a drag settled on. Null restores the declared order.</summary>
        internal void SetReorderedIndex(int? index) => reorderedIndex = index;

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

        /// <summary>
        /// Whether the column offers a resize handle. Ignored unless the grid sets
        /// <c>AllowColumnResize</c>.
        /// </summary>
        [Parameter] public bool Resizable { get; set; } = true;

        /// <summary>
        /// Whether the column offers a drag handle for reordering. Ignored unless the grid sets
        /// <c>AllowColumnReorder</c>. A column that opts out can still be displaced by others moving
        /// around it - what it declines is being dragged, not being in an order.
        /// </summary>
        [Parameter] public bool Reorderable { get; set; } = true;

        /// <summary>Whether the column stays put while the grid is scrolled sideways.</summary>
        /// <remarks>
        /// Where a frozen column is pinned is the sum of the widths between it and its edge, so every
        /// frozen column before it needs a <see cref="Width" /> - its own is only needed by whatever
        /// comes after. A run therefore ends at the first column that declares no width, and the columns
        /// past that are drawn unfrozen rather than stuck to a position nobody worked out.
        /// </remarks>
        [Parameter] public bool Frozen { get; set; }

        /// <summary>Which edge a frozen column is pinned to.</summary>
        [Parameter] public FrozenColumnPosition FrozenPosition { get; set; } = FrozenColumnPosition.Left;

        // What the grid worked out for this column this render: the class list and the inset that pins
        // it. They depend on the column's neighbours, so the grid assigns them rather than the column
        // deriving them - it is the only thing that knows what is beside what.
        string? frozenClass;
        string? frozenInset;

        internal void SetFrozen(string? classList, string? inset)
        {
            frozenClass = classList;
            frozenInset = inset;
        }

        internal bool IsFrozen => frozenClass is not null;

        /// <summary>The frozen class list for this column, or null when it is not pinned.</summary>
        internal string? FrozenClass => frozenClass;

        string? frozenCellClass;
        string? frozenCellClassFor;
        string? frozenCellClassOver;

        /// <summary>
        /// The class of this column's <c>td</c>, frozen classes included - distinct from
        /// <see cref="CellClass" />, which is the inner span's. Memoized on the pair it is built from,
        /// so a frozen column costs one string for the whole grid rather than one per cell.
        /// </summary>
        internal string? CellElementClass
        {
            get
            {
                if (frozenClass is null)
                {
                    return string.IsNullOrEmpty(CssClass) ? null : CssClass;
                }

                if (string.IsNullOrEmpty(CssClass))
                {
                    return frozenClass;
                }

                if (!ReferenceEquals(frozenCellClassFor, frozenClass)
                    || !string.Equals(frozenCellClassOver, CssClass, StringComparison.Ordinal))
                {
                    frozenCellClassFor = frozenClass;
                    frozenCellClassOver = CssClass;
                    frozenCellClass = CssClass + " " + frozenClass;
                }

                return frozenCellClass;
            }
        }

        string? frozenCellStyle;
        string? frozenHeaderStyle;
        string? frozenFooterStyle;
        string? frozenStyleFor;
        string? frozenStyleOver;

        // The three places a frozen column is drawn, and the stacking each of them has to win.
        //
        // The body needs nothing beyond being positioned: an unfrozen cell there is static, so the
        // theme's own z-index on .rz-frozen-cell already puts the pinned one on top.
        //
        // The header and the footer are different, and for the same reason. The theme makes every cell
        // in them sticky - thead th at z-index 1, tfoot td at 2 - frozen or not. So a frozen cell there
        // ties with the ordinary ones beside it, and a tie is settled by document order: the column to
        // its right paints straight over the pinned one while every position and inset stays correct.
        // Each is raised one above its own siblings, and stays inside the stacking context its section
        // already creates, so neither can climb out over the rows.
        void ComposeFrozenStyles()
        {
            var basis = CellStyle;

            if (ReferenceEquals(frozenStyleFor, frozenInset)
                && string.Equals(frozenStyleOver, basis, StringComparison.Ordinal))
            {
                return;
            }

            frozenStyleFor = frozenInset;
            frozenStyleOver = basis;

            frozenCellStyle = string.IsNullOrEmpty(basis) ? frozenInset : basis + ";" + frozenInset;
            frozenHeaderStyle = frozenCellStyle + ";z-index:2";
            frozenFooterStyle = frozenCellStyle + ";z-index:3";
        }

        /// <summary>
        /// The style of this column's body cells with the frozen inset folded in. Composed once per
        /// column, so the inset is handed to every row rather than built for each of them.
        /// </summary>
        internal string? FrozenCellStyle
        {
            get
            {
                if (frozenInset is null)
                {
                    return CellStyle;
                }

                ComposeFrozenStyles();

                return frozenCellStyle;
            }
        }

        /// <summary>The same for a header cell, raised above the ordinary headers beside it.</summary>
        internal string? FrozenHeaderStyle
        {
            get
            {
                if (frozenInset is null)
                {
                    return CellStyle;
                }

                ComposeFrozenStyles();

                return frozenHeaderStyle;
            }
        }

        /// <summary>The same for a footer cell, whose siblings sit a level higher than a header's.</summary>
        internal string? FrozenFooterStyle
        {
            get
            {
                if (frozenInset is null)
                {
                    return CellStyle;
                }

                ComposeFrozenStyles();

                return frozenFooterStyle;
            }
        }

        string? resizedWidth;

        /// <summary>
        /// The width the column actually renders at: what a user dragged it to, else what the markup
        /// said.
        /// </summary>
        /// <remarks>
        /// A drag cannot write to <see cref="Width" />. It is a parameter, so the next time the grid's
        /// parameters are set Blazor would put the markup's value back and the column would jump to its
        /// declared width - which is the ordinary Blazor rule about not treating a parameter as state,
        /// and here the symptom would be a resize that survives until the next unrelated re-render.
        /// </remarks>
        internal string? EffectiveWidth => resizedWidth ?? Width;

        /// <summary>The width a drag settled on, or null when none has.</summary>
        internal string? ResizedWidth => resizedWidth;

        int elementIdIndex = -1;
        string? baseElementId;
        string? colElementId;
        string? resizerElementId;
        string? dragElementId;

        /// <summary>
        /// The ids the resize and reorder scripts resolve this column by, built once per position
        /// rather than per render. They only change when the column moves, which picking a column and
        /// dragging one both do.
        /// </summary>
        /// <remarks>
        /// Both scripts are handed <c>Base</c> and derive what they need themselves, by appending
        /// '-col', '-resizer' or '-drag'. Handing either of them a derived id instead leaves it looking
        /// for '-col-col': resize then finds no col, writes the width to the th, and under
        /// table-layout:fixed the colgroup wins and nothing moves - while the rest of the drag still
        /// works, so it looks like it ran.
        /// </remarks>
        internal (string Base, string Col, string Resizer, string Drag) ElementIds(string gridId, int index)
        {
            if (elementIdIndex != index || baseElementId is null)
            {
                elementIdIndex = index;
                baseElementId = string.Create(CultureInfo.InvariantCulture, $"{gridId}-{index}");
                colElementId = baseElementId + "-col";
                resizerElementId = baseElementId + "-resizer";
                dragElementId = baseElementId + "-drag";
            }

            return (baseElementId, colElementId!, resizerElementId!, dragElementId!);
        }

        /// <summary>Records the width a drag settled on. Null restores the declared width.</summary>
        internal void SetResizedWidth(string? width) => resizedWidth = width;

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

        /// <summary>
        /// The text in the filter box that produced <see cref="CurrentFilterValue" />, or null when the
        /// filter came from anywhere else. The typed value cannot stand in for it: "3.0" and "3" are one
        /// value and two different things to have typed, and an unparseable "3-" filters by null the
        /// same as an empty box.
        /// </summary>
        internal string? AppliedFilterText { get; set; }

        /// <summary>Sets the column's live filter. Called by the grid; does not reload on its own.</summary>
        internal void SetFilter(object? value, FilterOperator? filterOperator)
        {
            CurrentFilterValue = value;
            CurrentFilterOperator = filterOperator ?? DefaultFilterOperator;
            AppliedFilterText = null;
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
                declaredVisible = Visible;
                declaredFilterValue = FilterValue;
                declaredFilterOperator = FilterOperator;
                CurrentFilterValue = FilterValue;
                CurrentFilterOperator = FilterOperator ?? DefaultFilterOperator;

                // Only here, and deliberately. A declared sort is the grid's starting state, not a live
                // binding: honouring later changes would mean re-sorting - and, on the async path,
                // reloading - from inside the grid's own render pass.
                //
                // CanSort, like the two other routes into the sort list. A column that cannot be
                // ordered by has no header control, no icon and no aria-sort, so a sort declared beside
                // one is invisible and the user has no way to clear it - and for a collection-valued
                // property there is no comparer to order by at all. CanSort is readable here because
                // both derived columns resolve their paths before calling this.
                if (SortOrder is { } order && CanSort)
                {
                    Grid?.ApplyDeclaredSort(this, order);
                }

                return;
            }

            // The declared value is the authority whenever it changes, and the grid's own filtering owns
            // it in between. Tracking what was declared separately keeps this out of the parameter
            // itself, which a component must not assign to.
            // The declaration wins whenever it changes, and takes the picker's override with it - the
            // same rule the filter value follows, for the same reason: markup that says Visible="false"
            // is not asking to be overruled by what someone ticked before.
            if (declaredVisible != Visible)
            {
                declaredVisible = Visible;
                pickedVisible = null;
            }

            if (!Equals(declaredFilterValue, FilterValue))
            {
                declaredFilterValue = FilterValue;
                CurrentFilterValue = FilterValue;
                AppliedFilterText = null;
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

        /// <summary>
        /// The predicate this column's current filter composes, or null when the column cannot compose
        /// one and the grid should fall back to building it from the column's path by reflection.
        /// </summary>
        /// <remarks>
        /// The same reasoning as <see cref="ApplySort" />, and it matters more here: only the column
        /// knows the filtered property's type as a type rather than as a <see cref="Type" />, and that
        /// is the difference between an ordinary generic call and one closed by
        /// <c>MakeGenericMethod</c> - which an ahead-of-time compiler cannot see through. A column that
        /// composes its own filter is a column that works under AOT.
        /// </remarks>
        /// <param name="caseSensitivity">Whether string comparisons ignore case.</param>
        /// <param name="inMemory">
        /// Whether the source is LINQ to Objects, which decides how case-insensitive strings compare -
        /// a provider cannot translate the <see cref="StringComparison" /> overloads.
        /// </param>
        public virtual Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory) => null;

        /// <summary>
        /// The same filter as <see cref="ApplyFilter" />, as a delegate, or null when the column cannot
        /// compose one.
        /// </summary>
        /// <remarks>
        /// Only for a source that is already in memory, and worth having for exactly that: handing an
        /// expression tree to <c>Queryable.Where</c> over a list wraps it in an <c>EnumerableQuery</c>,
        /// which rewrites and recompiles the tree every time the result is enumerated. Measured at
        /// 1000 rows that is 1,117 us against 38 us.
        /// </remarks>
        public virtual Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity) => null;

        /// <summary>
        /// Orders an in-memory sequence by this column, or returns null when it cannot order - the same
        /// contract as <see cref="ApplySort" />, which the grid already skips over.
        /// </summary>
        public virtual IOrderedEnumerable<TItem>? ApplySortInMemory(System.Collections.Generic.IEnumerable<TItem> source,
            bool descending) => null;

        /// <summary>Adds this column to an in-memory ordering already begun.</summary>
        public virtual IOrderedEnumerable<TItem>? ApplyThenByInMemory(IOrderedEnumerable<TItem> source,
            bool descending) => null;

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
