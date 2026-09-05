using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
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
        /// else what the column is identified by, so a column that names neither is still identifiable in
        /// the list.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Separate from the parameter rather than a fallback inside its getter, because a component
        /// parameter has to be an auto-property (BL0007) and this package builds warnings as errors.
        /// It is public because the picker names it through <c>TextProperty</c>, which reads it by name.
        /// </para>
        /// <para>
        /// The last resort is <see cref="Identity" /> rather than <see cref="SortPath" />, which it was
        /// while the two were one string. A column displaying <c>First</c> and sorting by <c>Last</c> was
        /// offered in the picker as "Last", which describes the ordering rather than the cells - the same
        /// fault <see cref="PropertyColumn{TItem, TProp}.HeaderText" /> already had a comment about.
        /// </para>
        /// </remarks>
        public string PickerTitle => ColumnPickerTitle ?? Title ?? Identity.Name ?? string.Empty;

        bool declaredVisible = true;

        // The identity this column last told the grid about, so that a parameter set which does not move
        // it costs a compare rather than a re-check of every column in the grid.
        string? reportedIdentity;

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

        /// <summary>
        /// The class of the span inside this column's body cell, carrying its wrapping mode. Not one of
        /// the four sections above: those class the cell element, this classes what is written into it.
        /// </summary>
        internal string CellContentClass => ClassFor(WhiteSpace);

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

        /// <summary>Records what the grid worked out about this column's pinning, for one render.</summary>
        /// <param name="classList">The frozen class list, or null for a column that is not pinned.</param>
        /// <param name="inset">The inset that pins it, or null.</param>
        /// <remarks>
        /// <paramref name="classList" /> has to be the *same instance* for a column whose pinning has not
        /// changed, which the grid gets right by handing back one of four literals rather than composing
        /// one: <see cref="BodyCellClass" /> memoizes on it by reference. Composing it instead would not
        /// break anything - this is called once per column per render, so the fold would still be reused
        /// from the second cell onwards - but it would cost one extra string per frozen column per
        /// render, silently, and nothing would fail.
        /// </remarks>
        internal void SetFrozen(string? classList, string? inset)
        {
            frozenClass = classList;
            frozenInset = inset;
        }

        internal bool IsFrozen => frozenClass is not null;

        // A column is drawn in four sections, and each of them asks for a class and a style. The table:
        //
        //   section   class                        style              what the class folds over
        //   header    HeaderCellClass(headerClass) HeaderCellStyle    the grid's sortable/resizable set
        //   filter    FilterCellClass              FilterCellStyle    one constant
        //   body      BodyCellClass                BodyCellStyle      CssClass
        //   footer    FooterCellClass              FooterCellStyle    FooterCssClass
        //
        // The header's is a method and the other three are properties, because the header's base class
        // is the grid's answer rather than the column's - whether the column offers sorting and
        // resizing is not something the column decides. There is no Section type and nothing enumerates
        // these: what moved out of the grid is the folding, not the choice of which member to call.
        //
        // Three of these four used to be composed by the grid, at the point of drawing, each folding
        // frozenClass into its own base class in its own way - so "a frozen column contributes a class
        // and an inset" was the caller's knowledge in three rows and the column's in one, and "the
        // filter row takes the header's style" was written nowhere but at the call site that did it.
        // §10 records that being got wrong: the filter row is a second row of the header rather than a
        // section of its own, and it was missed when the title row was fixed.
        //
        // The two halves of the table memoize differently, and the asymmetry is measured rather than
        // assumed.
        //
        // *No class fold is memoized except the body's.* The body's is read once per **cell**, so
        // composing it there would be per-row string work; the other three are read once per column per
        // render, which is what the grid was already paying when it folded them itself. Memoizing all
        // four was written first and cost 8 reference fields per column - 64 bytes each, 320 on a
        // five-column grid, on every grid whether or not anything is frozen - which gridbench read as
        // exactly +0.31 KB on rows that should not have moved. It saved three concatenations per frozen
        // column per render and was paid for by every column that is not frozen.
        //
        // *All three styles are memoized, and share one memo*, because they are one composition:
        // ComposeFrozenStyles builds the body's and derives the header's and footer's from it by
        // appending a z-index. Those five fields are not a per-section cost and predate this.

        string? bodyCellClass;
        string? bodyCellClassFor;
        string? bodyCellClassOver;

        /// <summary>
        /// The class of this column's body <c>td</c> - distinct from <see cref="CellContentClass" />,
        /// which is the inner span's.
        /// </summary>
        internal string? BodyCellClass
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

                if (!ReferenceEquals(bodyCellClassFor, frozenClass)
                    || !string.Equals(bodyCellClassOver, CssClass, StringComparison.Ordinal))
                {
                    bodyCellClassFor = frozenClass;
                    bodyCellClassOver = CssClass;
                    bodyCellClass = CssClass + " " + frozenClass;
                }

                return bodyCellClass;
            }
        }

        /// <summary>
        /// The class of this column's header <c>th</c>, over what the grid decided the header is -
        /// which depends on whether the column offers sorting and resizing, and so is the grid's to
        /// pass in rather than this column's to work out.
        /// </summary>
        /// <param name="headerClass">The header class the grid composed for this column.</param>
        internal string HeaderCellClass(string headerClass) =>
            frozenClass is null ? headerClass : headerClass + " " + frozenClass;

        const string FilterCellBaseClass = "rz-unselectable-text";

        /// <summary>
        /// The class of this column's filter <c>th</c>. Its base is one constant, so unlike the other
        /// three this folds over nothing the caller supplies.
        /// </summary>
        internal string FilterCellClass =>
            frozenClass is null ? FilterCellBaseClass : FilterCellBaseClass + " " + frozenClass;

        /// <summary>The class of this column's footer <c>td</c>.</summary>
        internal string? FooterCellClass =>
            frozenClass is null ? (string.IsNullOrEmpty(FooterCssClass) ? null : FooterCssClass)
            : string.IsNullOrEmpty(FooterCssClass) ? frozenClass
            : FooterCssClass + " " + frozenClass;

        string? frozenCellStyle;
        string? frozenHeaderStyle;
        string? frozenFooterStyle;
        string? frozenStyleFor;
        string? frozenStyleOver;

        // The three stackings the four sections need, and there are three rather than four because the
        // filter row shares the header's. The expand toggle is not a column and needs the same three:
        // RadzenFastGrid.Frozen.cs composes them as ToggleFrozenCellStyle, ToggleFrozenHeaderStyle and
        // ToggleFrozenFooterStyle, so a change to what a pinned cell has to clear belongs in both places.
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
        /// column per render, so the inset is handed to every row rather than built for each of them.
        /// </summary>
        internal string? BodyCellStyle
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
        internal string? HeaderCellStyle
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

        /// <summary>
        /// The same for a filter cell, which is the header's answer and not one of its own.
        /// </summary>
        /// <remarks>
        /// The filter row is a second row of the same <c>thead</c>, so its cells sit in the header's
        /// stacking and want the header's z-index. This exists rather than the grid reading
        /// <see cref="HeaderCellStyle" /> at the filter row because that identity is a fact about the
        /// markup, and §10 records it being got wrong exactly once - the filter row was missed when the
        /// title row was fixed, because nothing named it as a section.
        /// </remarks>
        internal string? FilterCellStyle => HeaderCellStyle;

        /// <summary>The same for a footer cell, whose siblings sit a level higher than a header's.</summary>
        internal string? FooterCellStyle
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
        /// The width the column actually renders at: what a user dragged it to, else what an auto-fit
        /// measured, else what the markup said.
        /// </summary>
        /// <remarks>
        /// A drag cannot write to <see cref="Width" />. It is a parameter, so the next time the grid's
        /// parameters are set Blazor would put the markup's value back and the column would jump to its
        /// declared width - which is the ordinary Blazor rule about not treating a parameter as state,
        /// and here the symptom would be a resize that survives until the next unrelated re-render.
        /// </remarks>
        internal string? EffectiveWidth => resizedWidth ?? autoFitWidth ?? Width;

        /// <summary>The width a drag settled on, or null when none has.</summary>
        internal string? ResizedWidth => resizedWidth;

        // The width an auto-fit measured. No getter: EffectiveWidth and CanAutoFit are the only readers
        // and both are here.
        string? autoFitWidth;

        /// <summary>
        /// Whether this column takes part in an auto-fit. Ignored unless the grid sets
        /// <c>AutoFitColumns</c>, and ignored for a column that declares its own <see cref="Width" />:
        /// the markup is an instruction and the grid does not overrule it.
        /// </summary>
        [Parameter] public bool AutoFit { get; set; } = true;

        /// <summary>
        /// How hard this column argues for its measured width when the grid is fitting to its container
        /// and there is not enough room. Only consulted under <c>AutoFitOverflow.Fit</c>.
        /// </summary>
        [Parameter] public AutoFitPriority AutoFitPriority { get; set; }

        /// <summary>Whether an auto-fit is allowed to measure and size this column.</summary>
        /// <param name="automatic">
        /// True for the one fit <c>AutoFitMode.Once</c> runs on its own, false when a user asked. An
        /// automatic fit leaves alone any column already carrying a width the user chose - a drag, or
        /// one restored from the settings, which is a drag from a previous visit. A fit somebody asked
        /// for takes that column too, because a fit that visibly did nothing to the column under the
        /// pointer is the worse answer.
        /// </param>
        internal bool CanAutoFit(bool automatic) =>
            AutoFit && string.IsNullOrEmpty(Width) && (!automatic || resizedWidth is null);

        /// <summary>Records the width an auto-fit measured.</summary>
        /// <param name="width">The measured width, or null for the column left bare.</param>
        /// <param name="replacingUserWidth">
        /// Whether to drop a width the user had chosen. True only for a fit a user asked for: a drag
        /// outranks a fit, so without this the column under the pointer would not move.
        /// </param>
        /// <remarks>
        /// The two widths are stored apart rather than in one slot because only one of them is a
        /// choice somebody made: a drag is captured into the settings and a fit is not, being derived
        /// from data that will not be the same data next time.
        /// <para>
        /// <c>resizedWidth</c> is also where a width restored from the settings lands, which is what
        /// makes clearing it unconditionally so expensive: the automatic fit would wipe every width a
        /// user had saved, and the next capture would then persist the absence.
        /// </para>
        /// </remarks>
        internal void SetAutoFitWidth(string? width, bool replacingUserWidth)
        {
            autoFitWidth = width;

            if (replacingUserWidth)
            {
                resizedWidth = null;
            }
        }

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
        /// Whether this column is still waiting for names it cannot draw a cell without.
        /// </summary>
        /// <remarks>
        /// The one automatic auto-fit defers while this is true. It measures what is on the page, and
        /// what is on the page meanwhile is a blank cell - so the column would settle at its header
        /// width and the names would arrive into a column too narrow for them, permanently.
        /// </remarks>
        internal virtual bool NamesOutstanding => false;

        /// <summary>
        /// Fetches the names this column asked for, after the render. True when the grid should redraw.
        /// </summary>
        internal virtual Task<bool> FetchNamesAsync(IFastGridQueryExecutor? executor,
            CancellationToken cancellationToken) => Task.FromResult(false);

        /// <summary>Drops resolved names, so the next render resolves them again.</summary>
        internal virtual void DropNames()
        {
        }

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
        /// The dotted path this column filters by. Defaults to <see cref="SortPath" />; a column with
        /// no path cannot be filtered, for the same reason it cannot be sorted.
        /// </summary>
        public virtual string? FilterPropertyPath => SortPath;

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
        public virtual bool HasFilter =>
            CanFilter &&
            (HasFilterValue
                || CurrentFilterOperator is Radzen.FilterOperator.IsNull or Radzen.FilterOperator.IsNotNull
                    or Radzen.FilterOperator.IsEmpty or Radzen.FilterOperator.IsNotEmpty);

        bool HasFilterValue => CurrentFilterValue switch
        {
            null => false,
            string text => text.Length > 0,

            // A check-box-list filter with nothing ticked is not a filter that matches nothing; it is no
            // filter. Testing for null only would leave the grid empty as soon as the last box is
            // cleared. A column where an empty selection can mean the other thing overrides HasFilter.
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
        internal string? AppliedFilterText { get; private set; }

        /// <summary>Sets the column's live filter. Called by the grid; does not reload on its own.</summary>
        /// <param name="value">The value to filter by.</param>
        /// <param name="filterOperator">How to compare it, or null for this column's default.</param>
        /// <param name="text">
        /// The box text the value came from, or null for a filter that came from anywhere else. A
        /// parameter rather than a second assignment because the text belongs to the value: the two
        /// call sites that have one used to put it back on the line after this one, each under a
        /// comment explaining that this clears it, which is one rule written twice in the two places
        /// most likely to be copied from. Required rather than defaulted, so that a caller who has a
        /// text and forgets it is a build error rather than the same silent drop in a new place.
        /// </param>
        internal void SetFilter(object? value, FilterOperator? filterOperator, string? text)
        {
            CurrentFilterValue = value;
            CurrentFilterOperator = filterOperator ?? DefaultFilterOperator;
            AppliedFilterText = text;
        }

        /// <summary>How this column compares when nothing said otherwise.</summary>
        internal virtual FilterOperator DefaultFilterOperator => EffectiveFilterType == typeof(string)
            ? Radzen.FilterOperator.Contains
            : Radzen.FilterOperator.Equals;

        /// <summary>
        /// The value a filter box's text means for this column, or null when it means nothing - a
        /// half-typed date or number, which filters nothing rather than throwing.
        /// </summary>
        /// <remarks>
        /// On the column because only the column knows what it filters by. The default converts to the
        /// filtered property's own type; a column whose cells show something other than what its rows
        /// carry has to translate instead.
        /// </remarks>
        internal virtual object? FilterValueFromText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // The element type, not the property type: a filter on a list of dates is compared against a
            // date, and a conversion would have no idea what to do with the list.
            var declared = EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            if (type == typeof(string) || type == typeof(object))
            {
                return text;
            }

            try
            {
                // ConvertType rather than Convert.ChangeType, and Enum.Parse rather than either: neither
                // an enum nor a Guid converts from a string through IConvertible, so the framework call
                // throws for both and what was typed silently cleared the filter instead of applying it.
                return type.IsEnum
                    ? Enum.Parse(type, text, ignoreCase: true)
                    : ConvertType.ChangeType(text, declared, CultureInfo.CurrentCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException
                or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// The values this column's check-box list offers of its own accord, or null to leave the grid
        /// to <see cref="FilterLookupData" /> and the distinct scan.
        /// </summary>
        internal virtual IEnumerable? FilterValues => null;

        /// <summary>
        /// What the check-box list is bound to, which is <see cref="CurrentFilterValue" /> unless the
        /// list offers something other than the values the column filters by.
        /// </summary>
        internal virtual object? FilterSelection => CurrentFilterValue;

        /// <summary>
        /// The value a check-box-list selection means for this column. The inverse of
        /// <see cref="FilterSelection" />, and the counterpart of <see cref="FilterValueFromText" />
        /// for the other filter control.
        /// </summary>
        /// <remarks>
        /// Typed as the column's element type, not <c>List&lt;object&gt;</c>: the reflective builder
        /// puts this list straight into <c>Contains&lt;TElement&gt;(selected, x)</c>, and a
        /// <c>List&lt;object&gt;</c> there is not an <c>IEnumerable&lt;TElement&gt;</c> - so a provider
        /// cannot translate it and the comparison never binds.
        /// <para>
        /// A column that composes its own predicate does not need that, because it retypes the values
        /// against the type parameter it already has. So with the switch off - where closing
        /// <c>List&lt;&gt;</c> over a run-time type is exactly what is unavailable - the untyped list
        /// is enough, and the only columns that would have needed the typed one have already declined
        /// to filter.
        /// </para>
        /// </remarks>
        internal virtual object FilterValueFromSelection(IEnumerable selected)
        {
            var declared = EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            var values = DynamicCode.Supported
                ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!
                : new List<object>();

            foreach (var item in selected)
            {
                values.Add(item);
            }

            return values;
        }

        bool initialized;

        /// <summary>
        /// Reads whatever this column derives from its own parameters - a compiled selector, a property
        /// path, a member's type - before the base reads any of it.
        /// </summary>
        /// <remarks>
        /// The order is the point, it has already cost this grid a bug, and it used to be five authors
        /// remembering it. The base picks a column's default filter operator from
        /// <see cref="EffectiveFilterType" />, which for a column declared as <c>object</c> is read off
        /// the filter path and for a collection column is the member's type - neither known until the
        /// column has read its own expressions. Derived afterwards, such a column defaulted to
        /// <c>Equals</c>, nothing recomputed it, and a declared <see cref="FilterValue" /> matched
        /// nothing for good. Every column here overrode <see cref="OnParametersSet" /> and called the
        /// base last, two of them under a comment explaining why - and the test suite's own
        /// <c>CompileCountingColumn</c> called it first, which happened not to matter for that column
        /// and is exactly why nobody saw it.
        /// <para>
        /// So <see cref="OnParametersSet" /> is sealed and this runs before it. A column that derives
        /// from another column calls <c>base.OnDerive()</c> where the base's own derivation has to come
        /// first, which is a rule between two siblings rather than one against the framework.
        /// </para>
        /// </remarks>
        protected virtual void OnDerive()
        {
        }

        /// <inheritdoc />
        /// <remarks>Sealed; a column derives its own state in <see cref="OnDerive" />.</remarks>
        protected sealed override void OnParametersSet()
        {
            OnDerive();

            // After OnDerive, because the member a column identifies itself by is derived there, and
            // above the !initialized branch below, because that branch returns - a column would then
            // report nothing on the one parameter set where its identity is certain to have moved.
            //
            // Pushed rather than pulled. The grid's check is gated on being told something changed, and
            // the set of columns changing is only half of what can change: a column's own Property or
            // UniqueID moving between renders can make two columns collide with nothing added or
            // removed. Reporting it here costs one ordinal compare per column per parameter set, and
            // the alternative - walking every column's identity on every render to find out - is the
            // cost the gate exists to avoid.
            var identity = Identity.Name;

            if (!string.Equals(reportedIdentity, identity, StringComparison.Ordinal))
            {
                reportedIdentity = identity;
                Grid?.InvalidateColumnIdentities();
            }

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
                // OnDerive has already run, which is the ordering this method now guarantees rather
                // than depends on.
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
        /// The ordering this column was handed, for the columns whose key type they do not carry - a
        /// template's, a collection's, a lookup's. Null for a column that composes its own ordering from
        /// a typed expression, which is <see cref="PropertyColumn{TItem, TProp}" />, and null for a
        /// column that cannot be ordered by at all.
        /// </summary>
        /// <remarks>
        /// It exists so that the four <c>Apply*</c> methods and <see cref="SortPath" /> are answered
        /// once rather than five times in each of three columns. Those five were verbatim in
        /// <see cref="TemplateColumn{TItem}" />, <c>CollectionColumn</c> and <c>LookupColumnBase</c>, and
        /// a sixth - <see cref="CanSort" /> - looks like it belongs with them and does not: a
        /// <see cref="FastGridSort{TItem}" /> over a computed key has a null <see cref="FastGridSort{TItem}.Path" />
        /// and can still order rows, so a column that can sort is not a column that has a path.
        /// </remarks>
        internal virtual FastGridSort<TItem>? SortSource => null;

        /// <summary>
        /// The dotted property path a remote sort travels under - what <c>OrderBy()</c> emits and what
        /// the grid's <c>Sorts</c> descriptors carry - or <c>null</c> when the authored expression is
        /// computed rather than a simple member access.
        /// </summary>
        /// <remarks>
        /// One thing, since §27. It was three: the sort's name, the default filter path, and the key a
        /// column's stored state was restored onto. The last of those is <see cref="Identity" /> now,
        /// and the reason it ever borrowed this is that a member called <c>PropertyPath</c> sounded
        /// general enough for each new consumer to read it as whatever that consumer needed.
        /// </remarks>
        public virtual string? SortPath => SortSource?.Path;

        /// <summary>
        /// Names this column across a reload, so its stored width, order, visibility and filter come
        /// back onto it rather than onto some other column.
        /// </summary>
        /// <remarks>
        /// Declared with <see cref="UniqueID" /> where the markup says so, and derived from
        /// <see cref="IdentitySource" /> where it does not. Two columns answering to one name is a
        /// markup fault the grid throws on, because the alternative is restoring the second column's
        /// state onto the first, which is a wrong answer on screen rather than lost state.
        /// </remarks>
        public ColumnIdentity Identity => ColumnIdentity.Of(UniqueID, IdentitySource);

        /// <summary>
        /// The member this column's cells are about, or <c>null</c> for a column whose content is not a
        /// member - a template, or an expression the resolver cannot walk.
        /// </summary>
        /// <remarks>
        /// Not a query path and nothing queries by it: it exists so that <see cref="IdentitySource" />
        /// can prefer what a column <em>shows</em> over what it orders by. That preference is the fix
        /// for §10b's second collision, where identity followed <c>SortBy</c> and a column showing
        /// <c>Last</c> while ordering by <c>First</c> answered to the same name as the column that
        /// really is <c>First</c>.
        /// <para>
        /// Internal, which locks an out-of-assembly column out of the derivation and out of nothing
        /// else: <see cref="UniqueID" /> is public, so such a column declares. Opening this would
        /// publish another member of the protocol §15's candidate 6 wants to publish once, and it is
        /// still waiting on §10.
        /// </para>
        /// </remarks>
        internal virtual string? DisplayPath => null;

        /// <summary>
        /// Names this column when nothing declares a <see cref="UniqueID" />: what it shows, and where
        /// it shows nothing nameable, what it orders by.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One rule rather than a per-column decision, and the second half is not a relapse into keying
        /// on the sort. Where a column <em>has</em> a displayed member, that member wins and the sort
        /// path never gets a say - which is the whole of §10b's second collision. Where it has none, the
        /// sort path is not a second name beating the real one; it is the only name in the markup.
        /// </para>
        /// <para>
        /// The fallback is load-bearing rather than tidy. A review found that without it a
        /// <see cref="PropertyColumn{TItem, TProp}" /> whose display is computed but which declares a
        /// member <c>SortBy</c> - identity <c>null</c>, <c>SortPath</c> non-null - **silently stopped
        /// persisting** width, order, visibility and filter that it had persisted before §27. §27 said
        /// "nothing changes for them" and that was the one shape where something did.
        /// </para>
        /// </remarks>
        internal string? IdentitySource => DisplayPath ?? SortPath;

        /// <summary>
        /// What this column is called in stored settings. Declared only where the grid cannot work it
        /// out - a template column, a column over a computed expression, or two columns over one member.
        /// </summary>
        /// <remarks>
        /// Unlike <c>RadzenDataGridColumn.UniqueID</c>, which the sibling's own <c>SetColumnDefaults</c>
        /// overwrites from <c>OnInitialized</c> whenever there is a <c>Property</c>, nothing here
        /// overwrites what the markup declared. An empty string is not a declaration, so a
        /// <c>UniqueID</c> bound to a value that has not arrived yet falls back rather than naming every
        /// such column alike.
        /// </remarks>
        [Parameter] public string? UniqueID { get; set; }

        /// <summary>Whether this column can be sorted. False for a computed column with no explicit sort.</summary>
        public virtual bool CanSort => Sortable && SortPath is not null;

        /// <summary>Writes one cell for <paramref name="item" /> into <paramref name="builder" />.</summary>
        /// <remarks>
        /// A column whose cell <em>is</em> its text overrides <see cref="CellTextOf" /> and leaves this
        /// alone. Four of them used to override both with the same expression written twice, and nothing
        /// checked that the two spellings agreed - which they have to, because the truncation tooltip
        /// shows <see cref="CellTextOf" /> for a cell this drew. Overriding this is for a column whose
        /// content is not a string at all, which is <see cref="TemplateColumn{TItem}" />.
        /// <para>
        /// It is virtual rather than abstract for that reason, and the trade has two halves. The
        /// compiler no longer requires a column to say how its cell is drawn, so one that overrides
        /// neither member draws an empty cell instead of failing to build - which is the same answer
        /// <see cref="CellTextOf" /> already gives by default. And the two columns whose overrides
        /// called their own field directly - a property column and a collection column - now reach it
        /// through one more virtual call per cell. It allocates nothing, so §3 does not rule it out,
        /// and gridbench reads the bare row unmoved at 154.55 KB; it is named here because per-cell
        /// work is the thing this file weighs everything against.
        /// </para>
        /// </remarks>
        /// <param name="builder">The render tree being written.</param>
        /// <param name="sequence">The sequence number for the content.</param>
        /// <param name="item">The row.</param>
        [SuppressMessage("Design", "CA1062:Validate arguments of public methods",
            Justification = "Runs once per cell. The rule exempts overrides, so the four this replaces never checked and nothing about the shipped behaviour changes by not checking here; adding a guard would be a branch per cell bought for a null the parameter's own annotation already rules out.")]
        public virtual void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, CellTextOf(item));

        /// <summary>
        /// The cell's text, for the grid's cell tooltip and - unless <see cref="RenderCell" /> is
        /// overridden - for the cell itself. Null when the column has no text to give: a template
        /// column's content is markup, not a string.
        /// </summary>
        /// <remarks>
        /// The tooltip derives the text a second time, and that is its cost: <see cref="RenderCell" />
        /// writes into the builder rather than returning a string, and threading one back out of it
        /// would put an out parameter on the hot path for every caller who does not want the tooltip.
        /// </remarks>
        /// <param name="item">The row.</param>
        public virtual string? CellTextOf(TItem item) => null;

        /// <summary>
        /// Applies this column's ordering to <paramref name="source" />. Overridden by columns that know
        /// their property type, so the ordering is a typed expression the provider can translate rather
        /// than a parsed string.
        /// </summary>
        /// <remarks>
        /// <b>This is the first of six methods that answer with something or with <c>null</c>, and
        /// <c>null</c> means the same thing in all six: this column cannot compose that.</b> Say that
        /// and nothing more - it is the whole of what a column decides, and the rest is the grid's.
        /// <para>
        /// What the grid does about it, so that declining is not a leap in the dark: a filter it cannot
        /// get from the column is built from the column's path by reflection instead, which costs that
        /// grid its ahead-of-time-compilation cleanliness; a sort it cannot get is left out, and the
        /// rest of the ordering stands. Where it still can, the grid first takes the composition to the
        /// other route rather than leaving the column out - because <b>a column may decline one route
        /// and not the other, and that is a different answer rather than a slower one.</b>
        /// </para>
        /// <para>
        /// No column in this library is like that, and it is worth knowing that the symmetry is a
        /// property of these columns rather than of the arrangement: each guards both of its sort
        /// methods on one condition and both of its filter methods on one condition, so it declines
        /// both routes or neither, and a decline currently costs a grid time and not a different
        /// answer. A column that broke the symmetry is the case those rules exist for, and is the thing
        /// to think hardest about before writing one.
        /// </para>
        /// </remarks>
        public virtual IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending) =>
            SortSource?.Apply(source, descending);

        /// <summary>
        /// Adds this column's ordering after one already applied, for a grid sorting by more than one
        /// column. <c>null</c> as for <see cref="ApplySort" />.
        /// </summary>
        /// <param name="source">The already-ordered query.</param>
        /// <param name="descending">Whether to order descending.</param>
        public virtual IOrderedQueryable<TItem>? ApplyThenBy(IOrderedQueryable<TItem> source, bool descending) =>
            SortSource?.ApplyThen(source, descending);

        /// <summary>
        /// The predicate this column's current filter composes. <c>null</c> as for
        /// <see cref="ApplySort" />.
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
        /// The same filter as <see cref="ApplyFilter" />, as a delegate. <c>null</c> as for
        /// <see cref="ApplySort" />.
        /// </summary>
        /// <remarks>
        /// Only for a source that is already in memory, and worth having for exactly that: handing an
        /// expression tree to <c>Queryable.Where</c> over a list wraps it in an <c>EnumerableQuery</c>,
        /// which rewrites and recompiles the tree every time the result is enumerated. Measured at
        /// 1000 rows that is 1,117 us against 38 us.
        /// </remarks>
        public virtual Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity) => null;

        /// <summary>
        /// Orders an in-memory sequence by this column. <c>null</c> as for <see cref="ApplySort" />.
        /// </summary>
        public virtual IOrderedEnumerable<TItem>? ApplySortInMemory(System.Collections.Generic.IEnumerable<TItem> source,
            bool descending) => SortSource?.Apply(source, descending);

        /// <summary>
        /// Adds this column to an in-memory ordering already begun. <c>null</c> as for
        /// <see cref="ApplySort" />.
        /// </summary>
        public virtual IOrderedEnumerable<TItem>? ApplyThenByInMemory(IOrderedEnumerable<TItem> source,
            bool descending) => SortSource?.ApplyThen(source, descending);

        /// <inheritdoc />
        /// <remarks>
        /// Sealed for the same reason <see cref="OnParametersSet" /> is, and it has to be: this is what
        /// runs it, so a column that overrode this and derived after calling the base could still write
        /// the ordering fault <see cref="OnDerive" /> exists to make unwritable. Registration happens
        /// here too, and a subclass that forgot to chain would leave itself out of the grid.
        /// </remarks>
        public sealed override Task SetParametersAsync(ParameterView parameters)
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
