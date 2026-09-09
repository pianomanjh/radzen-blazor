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

        /// <summary>Additional CSS class for the column's header cell.</summary>
        /// <remarks>
        /// The header's counterpart to <see cref="CssClass" /> and <see cref="FooterCssClass" />, which
        /// is what makes the three sections say the same thing three ways rather than two.
        /// </remarks>
        [Parameter] public string? HeaderCssClass { get; set; }

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
        /// The last resort is the member the column shows rather than <see cref="SortPath" />, which it
        /// was while the two were one string. A column displaying <c>First</c> and sorting by <c>Last</c>
        /// was offered in the picker as "Last", which describes the ordering rather than the cells - the
        /// same fault <see cref="PropertyColumn{TItem, TProp}.HeaderText" /> already had a comment about.
        /// </para>
        /// <para>
        /// <see cref="IdentitySource" /> rather than <see cref="Identity" />, and the difference is that
        /// <see cref="Identity" /> prefers a declared <see cref="UniqueID" /> - which is a storage key
        /// and not a label. An author writes one to tell two columns over a member apart, which is
        /// exactly the case least likely to carry a <see cref="Title" />, so reading it here would put
        /// "col_3" in front of a user. It is still the final fallback, because a column that names
        /// nothing else is better identified by its key than by an empty row in the list.
        /// </para>
        /// </remarks>
        public string PickerTitle => ColumnPickerTitle ?? Title ?? IdentitySource ?? UniqueID ?? string.Empty;

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

        /// <summary>
        /// Records what the picker chose, or null to forget it - which is what a reset does, so the
        /// declaration underneath is what answers again.
        /// </summary>
        /// <remarks>
        /// Nullable for the reset's sake and not for the picker's. Writing today's declared value
        /// instead would leave the override set, so a later edit to <c>Visible</c> in the markup would
        /// be shadowed by a choice the user made once and then undid.
        /// </remarks>
        internal void SetPicked(bool? visible) => pickedVisible = visible;

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
        //   header    HeaderCellClass(headerClass) HeaderCellStyle    HeaderCssClass, over the grid's
        //                                                              sortable/resizable set
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
        internal string HeaderCellClass(string headerClass)
        {
            var declared = string.IsNullOrEmpty(HeaderCssClass)
                ? headerClass
                : headerClass + " " + HeaderCssClass;

            return frozenClass is null ? declared : declared + " " + frozenClass;
        }

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
        /// measured, else what the markup said. Null when the column takes the grid's default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A drag cannot write to <see cref="Width" />. It is a parameter, so the next time the grid's
        /// parameters are set Blazor would put the markup's value back and the column would jump to its
        /// declared width - which is the ordinary Blazor rule about not treating a parameter as state,
        /// and here the symptom would be a resize that survives until the next unrelated re-render.
        /// </para>
        /// <para>
        /// A CSS length, because that is what it is written into the markup as - usually
        /// <c>"200px"</c>, but whatever <see cref="Width" /> was given when neither of the other two has
        /// happened. Public so that an export can size a column the way the reader sized it.
        /// </para>
        /// </remarks>
        public string? EffectiveWidth => resizedWidth ?? autoFitWidth ?? Width;

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
        /// How <see cref="FilterValue" /> is compared, in upstream's vocabulary. Defaults to
        /// <c>Contains</c> for a string column and <c>Equals</c> for every other type.
        /// </summary>
        /// <remarks>
        /// Kept, and kept working, so a column migrating from <c>RadzenDataGrid</c> compiles and behaves
        /// identically - every upstream value means exactly one of ours. Reach for
        /// <see cref="FilterOperatorOf" /> to say <c>Between</c>, which upstream has no value for. Where
        /// both are set the owned one wins, because it is the one that can say more.
        /// </remarks>
        [Parameter] public FilterOperator? FilterOperator { get; set; }

        /// <summary>
        /// How <see cref="FilterValue" /> is compared, in this grid's own vocabulary - §33.
        /// </summary>
        [Parameter] public FastGridFilterOperator? FilterOperatorOf { get; set; }

        /// <summary>
        /// The upper bound of a <c>Between</c>, or the value of a second condition joined to the first.
        /// </summary>
        /// <remarks>
        /// A <c>Between</c> takes two values and they are this and <see cref="FilterValue" />. Any other
        /// operator paired with <see cref="SecondFilterOperator" /> makes a second condition, joined by
        /// <see cref="LogicalFilterOperator" /> - which is how <c>In [...] OR IsNull</c> is authored.
        /// </remarks>
        [Parameter] public object? SecondFilterValue { get; set; }

        /// <summary>
        /// How <see cref="SecondFilterValue" /> is compared, when it is a condition of its own rather
        /// than a <c>Between</c>'s upper bound.
        /// </summary>
        [Parameter] public FastGridFilterOperator? SecondFilterOperator { get; set; }

        /// <summary>
        /// How this column's two conditions are joined. Not the grid's property of the same name, which
        /// joins whole columns - upstream draws the same distinction with the same two names.
        /// </summary>
        [Parameter] public LogicalFilterOperator LogicalFilterOperator { get; set; } = LogicalFilterOperator.And;

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
        /// <remarks>
        /// <strong>Hold the instance.</strong> On a column that offers §36's blank entry the values are
        /// wrapped, and that wrapping is cached on the reference this returns - so a collection built
        /// inside the markup expression rebuilds it on every render and gives the list control a new
        /// <c>Data</c> identity each time. The same rule §14 gives a <c>Lookup</c> and §10 gives
        /// <c>Data</c>, for the same reason.
        /// </remarks>
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
        object? declaredSecondFilterValue;
        FilterOperator? declaredFilterOperator;
        FastGridFilterOperator? declaredOwnedOperator;
        FastGridFilterOperator? declaredSecondOperator;
        LogicalFilterOperator declaredLogicalOperator = LogicalFilterOperator.And;

        /// <summary>
        /// What this column is filtering by right now, or null when it is not.
        /// </summary>
        /// <remarks>
        /// Replaced <c>CurrentFilterValue</c> and <c>CurrentFilterOperator</c> in §33. Neither could stay
        /// honest once a column could carry two conditions or a <c>Between</c>: there is no single "the
        /// value" to answer with, and keeping them as forwarding properties over the first condition
        /// would have put two sources of truth in the model on day one - the shape §32's review spent a
        /// finding on.
        /// </remarks>
        public FastGridFilter? CurrentFilter { get; private set; }

        FastGridFilter? activeFilter;

        /// <summary>
        /// The filter as of the composition being built: <see cref="CurrentFilter" /> with its relative
        /// dates read.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §34, and two names rather than one property that means different things at different times.
        /// <see cref="CurrentFilter" /> is what the user <em>authored</em> - tokens intact - and is what
        /// <c>CaptureSettings</c> writes and what a pill has to read, since <em>"Hired is in the last 7
        /// days"</em> cannot be built from two resolved dates. This is what the query takes.
        /// </para>
        /// <para>
        /// §10b's recurring finding is a rule read from two places that can <em>disagree</em>; these two
        /// are meant to differ, and the names are what say which is which. Before the first resolution -
        /// and for every filter holding no tokens, which is almost all of them - they are the same
        /// object.
        /// </para>
        /// </remarks>
        internal FastGridFilter? ActiveFilter => activeFilter ?? CurrentFilter;

        /// <summary>
        /// Reads this column's relative dates at <paramref name="now" />.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="Composition" /> as a composition begins, with one instant stamped for
        /// all of the columns: reading the clock per token lets two columns - or the two bounds of one
        /// range - land on opposite sides of midnight and produce a range that excludes its own start,
        /// which is a once-a-day fault with no reproduction.
        /// </remarks>
        internal void ResolveFilter(DateTimeOffset now) =>
            activeFilter = CurrentFilter is { } filter
                ? FilterResolution.Resolve(filter, EffectiveFilterType, now)
                : null;

        /// <summary>
        /// The condition that decides this column, or null when it is not filtered.
        /// </summary>
        /// <remarks>
        /// A read for the editors and the columns that special-case their own operator, not a second
        /// place the filter is *kept*: it derives from <see cref="CurrentFilter" /> and cannot disagree
        /// with it. That distinction is what §33 refused to blur when it declined to keep
        /// <c>CurrentFilterValue</c> as a settable forwarding property.
        /// </remarks>
        internal FastGridFilterCondition? FirstCondition => CurrentFilter?.First;

        /// <summary>
        /// What the <c>Simple</c> filter box shows for this column, or null when there is nothing to
        /// show.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One place, because it was two and they were compared against each other. The box rendered
        /// <c>FirstCondition?.Value</c> and the commit path compared the text it received against
        /// <see cref="AppliedFilterText" /> - which is null for every filter that did not come from a
        /// box. So merely focusing and leaving an untouched box raised <c>onchange</c> with the box's
        /// own text, failed the comparison, and re-applied it as a filter: a <c>Between</c> narrowed
        /// silently to an <c>Equals</c> on its lower bound. §34 made that worse rather than causing it -
        /// a relative token does not parse as a date, so the same blur cleared the filter outright.
        /// </para>
        /// <para>
        /// It still shows only the first value of a compound, which is §33's and is recorded rather than
        /// fixed: a box holds one value and a range has two. What is fixed is that showing it no longer
        /// destroys it.
        /// </para>
        /// </remarks>
        internal string? FilterBoxText => FirstCondition?.Value?.ToString();

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

                // Reached from the filter row, the filter callbacks, and - since §32 - once per stored
                // column per settings restore. Never per row or per cell, which is what keeps resolving
                // it on demand cheaper than a cache behind an invalidation rule.
                return FilterPropertyPath is { } path
                    ? PropertyPathResolver.TypeOf(typeof(TItem), path) ?? typeof(object)
                    : typeof(object);
            }
        }

        /// <summary>Whether this column can hold no value at all, which is what offers IsNull.</summary>
        /// <remarks>
        /// A reference type is nullable in the sense that matters here - a string column has rows with
        /// no string - and the C# annotation that would say otherwise is erased by the time a
        /// <see cref="Type" /> is all there is. §31 says nullable columns of any type also get IsNull
        /// and IsNotNull, and this is which columns those are.
        /// </remarks>
        internal bool FilterNullable
        {
            get
            {
                var declared = EffectiveFilterType;

                return !declared.IsValueType || Nullable.GetUnderlyingType(declared) is not null;
            }
        }

        /// <summary>
        /// The operators §35's menu offers for this column, in the order it lists them.
        /// </summary>
        /// <remarks>
        /// On the column rather than only on the type, and the browser is what said it had to be. A
        /// lookup column's <c>EffectiveFilterType</c> is its <em>key</em> type, so asking the type alone
        /// offered a lookup over <c>int?</c> the numeric operators - Less than, Between - for a column
        /// whose whole point is that it filters by <c>In</c> over ids and shows names. The type answers
        /// for the columns whose values are what they hold; a column that filters by something other
        /// than what it shows has to say so itself, which is the same reason
        /// <see cref="FilterValueFromText" /> and <see cref="SelectionOf" /> are here.
        /// <para>
        /// <strong>§36 asks <see cref="EditedAsSet" /> before it asks the type.</strong> A column whose
        /// author declared a check-box list filters by a set whatever it holds, and without this the
        /// declaration is inert under <c>FilterUI.Menu</c>: a string column would be offered
        /// <c>Contains</c> and <c>StartsWith</c> and no way to reach the list at all. That is the third
        /// way a column's values can be known - a lookup holds a map, an enum's type holds its members,
        /// and the author knows - and §31 listed only the first two.
        /// </para>
        /// </remarks>
        internal virtual FastGridFilterOperator[] MenuOperators =>
            EditedAsSet
                ? FastGridFilterOperators.Set(FilterNullable)
                : FastGridFilterOperators.Menu(EffectiveFilterType, FilterNullable);

        /// <summary>Whether this column can be filtered.</summary>
        public virtual bool CanFilter => Filterable && FilterPropertyPath is not null;

        /// <summary>
        /// Whether the column's current filter would actually narrow anything. An empty value filters
        /// nothing, except for the operators that are about emptiness themselves.
        /// </summary>
        public virtual bool HasFilter => CanFilter && CurrentFilter is { IsPresent: true };

        /// <summary>
        /// The text in the filter box that produced this column's first condition, or null when the
        /// filter came from anywhere else. The typed value cannot stand in for it: "3.0" and "3" are one
        /// value and two different things to have typed, and an unparseable "3-" filters by null the
        /// same as an empty box.
        /// </summary>
        internal string? AppliedFilterText { get; private set; }

        /// <summary>The same, for the second condition - a <c>Between</c>'s upper bound, usually.</summary>
        internal string? AppliedSecondFilterText { get; private set; }

        /// <summary>Sets the column's live filter. Called by the grid; does not reload on its own.</summary>
        /// <param name="filter">What to filter by, or null to clear.</param>
        /// <param name="text">
        /// The box text the first condition's value came from, or null for a filter that came from
        /// anywhere else. A parameter rather than a second assignment because the text belongs to the
        /// value: the two call sites that have one used to put it back on the line after this one, each
        /// under a comment explaining that this clears it, which is one rule written twice in the two
        /// places most likely to be copied from. Required rather than defaulted, so that a caller who
        /// has a text and forgets it is a build error rather than the same silent drop in a new place.
        /// </param>
        /// <param name="secondText">The same for the second value, and defaulted because most callers have none.</param>
        internal void SetFilter(FastGridFilter? filter, string? text, string? secondText = null)
        {
            CurrentFilter = filter;
            AppliedFilterText = text;
            AppliedSecondFilterText = secondText;

            // A resolution belongs to the filter it was read from. Dropped rather than recomputed,
            // because the instant to recompute it at belongs to a composition and none is being built
            // here - and until one is, ActiveFilter answering CurrentFilter is exactly right.
            activeFilter = null;
        }

        /// <summary>
        /// Sets a filter of one condition over one value, which is what every editor this grid draws
        /// today produces.
        /// </summary>
        internal void SetFilter(object? value, FastGridFilterOperator? filterOperator, string? text) =>
            SetFilter(new FastGridFilter(Condition(filterOperator ?? DefaultFilterOperator, value)), text);

        /// <summary>
        /// One condition, with the value placed as the operator's arity says. An operator that takes no
        /// values gets none rather than a null - the difference is what <c>IsPresent</c> reads.
        /// </summary>
        internal static FastGridFilterCondition Condition(FastGridFilterOperator filterOperator, object? value) =>
            filterOperator.Arity() == FastGridFilterArity.None
                ? new FastGridFilterCondition(filterOperator)
                : new FastGridFilterCondition(filterOperator, value);

        /// <summary>
        /// Whether this column's filter is edited as a check-box list rather than as a box.
        /// </summary>
        /// <remarks>
        /// §31: <c>FilterMode.CheckBoxList</c> stops being a mode and becomes the editor for
        /// <c>In</c>/<c>NotIn</c>. A check-box list is not a different kind of filtering, it is how a set
        /// is picked - so what the parameter really said all along was which operator the column uses,
        /// and <see cref="LookupColumnBase{TItem, TKey}" /> has answered <c>In</c> unconditionally since
        /// §14 for exactly that reason. This is that rule for the columns that get the mode from the
        /// grid instead of from their own type.
        /// </remarks>
        internal bool EditedAsSet => Grid?.FilterModeOf(this) == Radzen.FilterMode.CheckBoxList;

        /// <summary>
        /// The editor this column last <em>drew</em>, so a change of editor can be told from a filter
        /// arriving under the editor it already had. Null until the column has been drawn once.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole of §35's screening rule and the build is what found it needed
        /// one. A caller asking for <c>Equals 3</c> on a column that is already a check-box list has said
        /// what they want and gets it - the list shows nothing ticked, which is what
        /// <see cref="LookupColumnBase{TItem, TKey}" /> has done since §14. A filter that was authored in
        /// a box and then had the box taken away is the other thing: nothing on the screen can explain
        /// it and nothing can remove it.
        /// </remarks>
        internal Radzen.FilterMode? LastEditorMode { get; set; }

        /// <summary>How this column compares when nothing said otherwise.</summary>
        /// <remarks>
        /// <strong>Not <c>In</c> for a check-box-list column</strong>, though §31's "the mode maps onto
        /// the new model" reads as though it should be, and the build tried it. This answers for a
        /// <em>value</em> arriving without an operator - a descriptor with none set, a typed box, a
        /// declared <c>FilterValue</c> - and those values are scalars whatever the editor is; answering
        /// <c>In</c> turned <c>ApplyFilters</c>'s <c>Id = 3</c> into a <c>Contains</c> over a bare 3.
        /// <see cref="LookupColumnBase{TItem, TKey}" /> can answer <c>In</c> unconditionally because its
        /// values really are sets of ids. The mapping lives where the list actually edits - the list
        /// passes <c>In</c> itself - and not here.
        /// </remarks>
        internal virtual FastGridFilterOperator DefaultFilterOperator =>
            EffectiveFilterType == typeof(string)
                ? FastGridFilterOperator.Contains
                : FastGridFilterOperator.Equals;

        /// <summary>
        /// The operator this column's markup declares, in whichever vocabulary it was written.
        /// </summary>
        /// <remarks>
        /// The owned one wins where both are set, because it is the one that can say <c>Between</c>.
        /// Every upstream value maps, <c>Custom</c> included - the review found three comments here
        /// claiming otherwise and guarding code that could not run, left over from a build in which the
        /// vocabulary really was short of it.
        /// </remarks>
        internal FastGridFilterOperator? DeclaredFilterOperator =>
            FilterOperatorOf ?? FilterOperator?.Owned();

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

            // §34, and the reader it forgot. Storage learned to read `today-6d` and the box did not, so
            // the same six characters were a relative date coming out of a settings blob and nothing at
            // all coming out of the box beside it - which is a collision between the two vocabularies
            // after all, in the one place the section did not look for one.
            if (FastGridRelativeDate.AppliesTo(declared)
                && FastGridRelativeDate.TryParse(text, out var relative))
            {
                return relative;
            }

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
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Every exception, for the reason the sibling below gives at length: this is the same
                // optional path, doing the same job of dropping what will not parse, over a string
                // nobody here chose. It named four types until §32's review pointed out that a rule
                // argued twenty lines away was not being applied to its own neighbour -
                // ConvertType.ChangeType reaches a TypeConverter, which can raise anything.
                return null;
            }
        }

        /// <summary>
        /// A stored filter as something this column can actually filter by, or null when neither the
        /// value nor the text produces one - in which case the column restores unfiltered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>FastGridColumnSettings.FilterValues</c> was <c>object?</c>, so a settings blob that
        /// went through a serializer comes back holding whatever that serializer uses for "some value"
        /// - a <c>JsonElement</c> for <c>System.Text.Json</c>. Handed on unexamined, that value made the
        /// typed builders decline, and a decline is routed to the reflective one, where
        /// <c>Expression.Constant(value, type)</c> throws <em>inside the render</em>. §32 has the
        /// measured matrix; the short version is that a round-tripped date, int or enum filter took the
        /// circuit down and a check-box list quietly showed every row.
        /// </para>
        /// <para>
        /// One rule: reconstruct from the value, then from the text, drop what neither produces. Four
        /// attempts, and the order is argued rather than incidental:
        /// <list type="number">
        /// <item>the value is already the column's type;</item>
        /// <item>the value converts to it, <em>invariantly</em>;</item>
        /// <item>the value's string form converts to it, invariantly - which is what recovers a filter
        /// that has no text at all, and there is a whole restore path that never has any;</item>
        /// <item><paramref name="text" /> re-parsed by this column, in <see cref="CultureInfo.CurrentCulture" />.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Text is last because it is the lossier record - one person's typing in one culture, where the
        /// value is culture-free. And attempt 3 is a conversion to a <see cref="Type" /> rather than a
        /// call to <see cref="FilterValueFromText" /> for the same reason read the other way round: the
        /// string form it reads came out of a serializer and is invariant, while that method reads
        /// <see cref="CultureInfo.CurrentCulture" /> because what it exists to read is typing. Route 3
        /// through it and a stored <c>250.5</c> restores under de-DE as two and a half thousand.
        /// </para>
        /// </remarks>
        /// <param name="value">The stored value, in whatever shape it came back in.</param>
        /// <param name="filterOperator">The stored operator, or null for this column's default.</param>
        /// <param name="text">The stored box text, or null for a filter that never came from a box.</param>
        internal object? RestoredFilterValue(object? value, FastGridFilterOperator? filterOperator,
            string? text)
        {
            // The operator decides the *shape* the answer has to have, and both halves below are checked
            // against it. An In wants a sequence; everything else wants a single value. The review found
            // this missing in both directions: an In whose value was a scalar fell through to the text,
            // took a scalar from it and composed Constant(true) - §32's own "shows every row" row, back
            // again - and a lookup column with a stored scalar operator took a *list* from its name
            // matcher and handed it to Equals, which throws.
            var wantsSequence = (filterOperator ?? DefaultFilterOperator)
                is FastGridFilterOperator.In or FastGridFilterOperator.NotIn;

            if (value is not null
                && (wantsSequence ? RestoredSequence(value) : RestoredScalar(value)) is { } rebuilt)
            {
                return rebuilt;
            }

            // Attempt 4, and the same shape test. FilterValueFromText answers for the column rather than
            // for the operator - a lookup column's answer is always a list, whatever it was asked.
            return FilterValueFromText(text) is { } fromText
                && wantsSequence == fromText is IEnumerable and not string
                    ? fromText
                    : null;
        }

        /// <summary>
        /// Whether this column's type can be compared with <paramref name="filterOperator" /> at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A filter that cannot be built is refused here rather than further down, and the reason is
        /// §32's finding 1a arriving for the third time. The typed builder used to build
        /// <c>Expression.GreaterThan</c> over two strings and throw inside the render; §33 made it
        /// decline instead - and a decline is absorbed by the reflective builder, which threw the same
        /// exception one frame later. Declining moved the crash rather than removing it. Nothing but
        /// refusing the filter removes it.
        /// </para>
        /// <para>
        /// Only where the type is <em>known</em>. A column whose filter type will not resolve answers
        /// <c>object</c>, and the reflective builder may well resolve a real type this cannot see, so an
        /// unknown type allows everything and is no worse off than before.
        /// </para>
        /// </remarks>
        internal bool SupportsOperator(FastGridFilterOperator filterOperator)
        {
            var declared = EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            if (type == typeof(object))
            {
                return true;
            }

            var text = type == typeof(string);

            return filterOperator switch
            {
                FastGridFilterOperator.Contains or FastGridFilterOperator.DoesNotContain
                    or FastGridFilterOperator.StartsWith or FastGridFilterOperator.EndsWith
                    or FastGridFilterOperator.IsEmpty or FastGridFilterOperator.IsNotEmpty => text,

                FastGridFilterOperator.LessThan or FastGridFilterOperator.LessThanOrEquals
                    or FastGridFilterOperator.GreaterThan or FastGridFilterOperator.GreaterThanOrEquals
                    or FastGridFilterOperator.Between => Orderable(type),

                _ => true,
            };
        }

        static bool Orderable(Type type)
        {
            try
            {
                Expression.LessThan(Expression.Default(type), Expression.Default(type));

                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// The values a stored condition compares against, parsed against this column's own type, or
        /// null when they cannot be.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §33's replacement for §32's four attempts, and it is one parse rather than four guesses
        /// because the stored form is canonical text: a serializer cannot have changed what a string is.
        /// The arity rule decides the shape - <c>In</c> rebuilds the list its editor produced, so the
        /// check-box-list hole §32 left open closes here.
        /// </para>
        /// <para>
        /// The column's own type is load-bearing now in a way it was not, and that is the stated cost: a
        /// column whose type changed since the blob was written parses nothing and drops, and a column
        /// that cannot name its type at all - <c>EffectiveFilterType</c> answering <c>object</c> - keeps
        /// the text, which is §32's rule for those unchanged.
        /// </para>
        /// </remarks>
        internal IReadOnlyList<object?>? RestoredValues(FastGridFilterOperator filterOperator,
            IList<string?>? texts)
        {
            if (texts is null || texts.Count == 0)
            {
                return null;
            }

            var declared = EffectiveFilterType;
            var arity = filterOperator.Arity();

            if (arity == FastGridFilterArity.Many)
            {
                var parsed = Parsed(texts, declared);

                // Nothing parsed is nothing rebuilt, and saying so is what lets the text have its turn -
                // which on a lookup column is the difference between the name someone picked and a
                // filter over no ids at all, and those two show opposite halves of the data.
                if (parsed.Count == 0)
                {
                    return null;
                }

                // Rebuilt as the list the editor produced rather than as one value per box, because that
                // is the shape the predicate builders and the check-box list both read.
                return new object?[] { RestoredSelection(parsed) };
            }

            var wanted = arity == FastGridFilterArity.Two ? 2 : 1;

            if (texts.Count < wanted)
            {
                return null;
            }

            var values = new object?[wanted];

            for (var i = 0; i < wanted; i++)
            {
                if (FilterValueText.To(texts[i], declared) is not { } value)
                {
                    return null;
                }

                values[i] = value;
            }

            return values;
        }

        /// <summary>
        /// Parsed values as the list an <c>In</c> filters by.
        /// </summary>
        /// <remarks>
        /// Not <see cref="FilterValueFromSelection" />, which converts what a *picker* offered: on a
        /// lookup column that is entries carrying ids, and handing it the ids themselves produced an
        /// empty list and a filter that matched nothing. These values are already what the column
        /// filters by, so all this does is give them a typed list to live in.
        /// </remarks>
        internal virtual object RestoredSelection(IReadOnlyList<object?> values)
        {
            var declared = EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            // Typed, because the reflective builder puts this list straight into Contains<TElement> and
            // a List<object> there is not an IEnumerable<TElement> - so a provider cannot translate it.
            // Where closing List<> over a run-time type is unavailable the untyped list is enough, for
            // the reason FilterValueFromSelection gives at length.
            var list = DynamicCode.Supported
                ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!
                : new List<object?>();

            for (var i = 0; i < values.Count; i++)
            {
                list.Add(values[i]);
            }

            return list;
        }

        /// <summary>The texts as values, in order, dropping the ones that will not parse.</summary>
        /// <remarks>
        /// <para>
        /// A stored <c>null</c> is a null <em>value</em> and is kept as one. That distinction is the
        /// whole reason this reads the text rather than the parse result: a null entry and an entry that
        /// failed to parse both come back from <see cref="FilterValueText.To" /> as null, and collapsing
        /// them lost the blank entry on a lookup column - so a user who ticked "(none)" alongside two
        /// categories got two back, and the rows with no category vanished without the tick that hid
        /// them. <c>SelectedKeys</c> has always handled the null on the composition side; the review
        /// found that restore did not.
        /// </para>
        /// <para>
        /// Unparseable text is dropped rather than failing the whole list, which is the rule the
        /// predicate builders apply to an <c>In</c>: one unreadable id should not throw away the others.
        /// An empty result is no filter, which <c>IsPresent</c> answers for.
        /// </para>
        /// </remarks>
        static List<object?> Parsed(IList<string?> texts, Type declared)
        {
            var values = new List<object?>(texts.Count);

            for (var i = 0; i < texts.Count; i++)
            {
                if (texts[i] is null)
                {
                    values.Add(null);
                }
                else if (FilterValueText.To(texts[i], declared, relative: false) is { } value)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        /// <summary>
        /// The stored value of an <c>In</c> as a sequence this column can filter by, or null when it is
        /// not one.
        /// </summary>
        /// <remarks>
        /// A JSON array is not one - it arrives as a single opaque value - so it is dropped whole, and
        /// that is §32's stated hole, waiting on the format change. Newtonsoft's <c>JArray</c> is a
        /// sequence and its elements convert, so it survives without this knowing its name.
        /// <para>
        /// Only the shape is checked here, because the predicate builders convert the elements
        /// themselves. That is true of <c>FilterExpression.Listed</c> and <em>not</em> of the lookup
        /// columns, which is why they override this.
        /// </para>
        /// </remarks>
        internal virtual object? RestoredSequence(object value) =>
            value is IEnumerable and not string ? value : null;

        /// <summary>Attempts 1 to 3 of <see cref="RestoredFilterValue" />, for a single value.</summary>
        object? RestoredScalar(object value)
        {
            var declared = EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            // A column that cannot name the type it filters by cannot vouch for a value either, and
            // attempt 1 below would say yes to anything at all - object.IsInstanceOfType is true of
            // every value there is. Passing it on is what §32's gate calls the unsafe direction: the
            // review measured a round-tripped filter on such a column matching *nothing*, so the grid
            // hid every row and the stored blob looked intact. The text still gets its turn, and for a
            // column of unknown type FilterValueFromText answers with the text itself.
            if (type == typeof(object))
            {
                return null;
            }

            // Attempt 1.
            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            // Attempts 2 and 3. ToString() rather than a serializer's own unwrapping, because the string
            // form is the one thing every "some value" wrapper agrees on: JsonElement stringifies a
            // stored date to the ISO-8601 text it was written as, which converts.
            return Converted(value, type, declared) ?? Converted(value.ToString(), type, declared);
        }

        /// <summary>One value as <paramref name="type" />, or null when it will not convert.</summary>
        private protected static object? Converted(object? candidate, Type type, Type declared)
        {
            if (candidate is null)
            {
                return null;
            }

            try
            {
                // The same two-way split FilterValueFromText makes, for the same reason: an enum
                // converts through neither IConvertible nor ConvertType's own nullable-enum branch when
                // the column's own type is not nullable. Parse takes "Senior" and "1" alike, which is
                // both shapes a serializer writes an enum in.
                var converted = type.IsEnum
                    ? candidate is string name
                        ? Enum.Parse(type, name, ignoreCase: true)
                        : Enum.ToObject(type, candidate)
                    : ConvertType.ChangeType(candidate, declared, CultureInfo.InvariantCulture);

                // Checked rather than trusted, because ConvertType.ChangeType does not fail on a value
                // it cannot convert - its last line hands back whatever it was given unless the value is
                // IConvertible. A JsonElement is not, so attempt 2 "succeeded" with the JsonElement
                // still in hand and attempt 3 never ran. Returning something is not converting it.
                return type.IsInstanceOfType(converted) ? converted : null;
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Deliberately every exception, and §9's rule is why: this is an optional path whose
                // whole job is to drop what will not convert, and it runs over a value of a type nobody
                // here chose. Narrowing it to the four that were expected is how the fault this method
                // exists to fix reached a render in the first place - it was an ArgumentException from
                // Expression.Constant, three frames past the catch that was meant to be enough.
                return null;
            }
        }

        /// <summary>
        /// The values this column's check-box list offers of its own accord, or null to leave the grid
        /// to <see cref="FilterLookupData" /> and the distinct scan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>An enum answers here, and §36 is why.</strong> The type holds its members, so a scan
        /// for them is a query paid to get a worse answer: it would offer only the members the current
        /// rows happen to hold, which is the defect §14 rejected for lookups by name - a filter control
        /// whose options move as the data does moves under the reader.
        /// </para>
        /// <para>
        /// Built once per column and held, not read per render. §35's <c>FastGridFilterPresets</c>
        /// comment objects to <c>Enum.GetValues</c> and the objection is to a call <em>per render</em>;
        /// this is the lifetime <see cref="LookupColumnBase{TItem, TKey}" /> already gives its entries,
        /// rebuilt only when a culture change changes the blank's word.
        /// </para>
        /// </remarks>
        internal virtual IEnumerable? FilterValues
        {
            get
            {
                var declared = EffectiveFilterType;

                if (!(Nullable.GetUnderlyingType(declared) ?? declared).IsEnum)
                {
                    return null;
                }

                return FilterEntries(EnumMembers());
            }
        }

        List<object>? enumMembers;

        /// <summary>The enum's members, boxed once.</summary>
        List<object> EnumMembers()
        {
            if (enumMembers is not null)
            {
                return enumMembers;
            }

            var declared = EffectiveFilterType;
            var underlying = Nullable.GetUnderlyingType(declared) ?? declared;
            var members = Enum.GetValues(underlying);
            var built = new List<object>(members.Length);

            for (var i = 0; i < members.Length; i++)
            {
                if (members.GetValue(i) is { } member)
                {
                    built.Add(member);
                }
            }

            return enumMembers = built;
        }

        IEnumerable? entrySource;
        string? entryBlank;
        List<object>? entryList;

        /// <summary>
        /// Whether this column's check-box list offers an entry for the rows with nothing in them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Nullable is necessary and not sufficient</strong>, which is what both of §36's
        /// reviewers arrived at from opposite ends. <see cref="FilterNullable" /> answers whether the
        /// <em>type</em> can hold a null, and that is the right question for offering <c>IsNull</c> -
        /// an operator the grid composes itself. A blank in a check-box list is a null travelling in an
        /// <c>In</c> list, and only a column that composes its own predicate can carry one there: a
        /// column that hands its filter to the reflective route offers a box that ticks, commits, and
        /// narrows to no rows at all.
        /// </para>
        /// <para>
        /// So this is the narrower question, and the hook is <see cref="LookupColumnBase{TItem, TKey}" />'s
        /// own - it has answered it since §14, and <c>LookupCollectionColumn</c> refuses a blank through
        /// it for a reason that is about meaning rather than mechanism: <em>"has no brands at all" is a
        /// different question from "has an id that is null"</em>. Promoted here rather than copied,
        /// because two places deciding which columns offer a blank is the fault §36 was already fixing
        /// elsewhere.
        /// </para>
        /// </remarks>
        private protected virtual bool OffersBlank => FilterNullable;

        /// <summary>
        /// The values a check-box list draws, which is what was offered plus the blank entry on a
        /// column that offers one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §36. On the column rather than in <c>FilterLookup</c> because the entry has to be the
        /// <em>same object</em> the selection is mapped against - <see cref="SelectionOf" /> answers
        /// with it and <see cref="FilterValueFromSelection" /> reads it back - and the grid's scan cache
        /// holds values, not entries.
        /// </para>
        /// <para>
        /// Cached on the values it was given and on the word, so a culture change rebuilds it and a
        /// render does not. Nulls in the source collapse into the entry rather than sitting beside it:
        /// there is one blank however many rows have nothing.
        /// </para>
        /// </remarks>
        internal IEnumerable FilterEntries(IEnumerable values)
        {
            if (!OffersBlank)
            {
                return values;
            }

            var blank = Grid?.BlankFilterText ?? string.Empty;

            if (entryList is null || !ReferenceEquals(entrySource, values)
                || !string.Equals(entryBlank, blank, StringComparison.Ordinal))
            {
                entrySource = values;
                entryBlank = blank;
                entryList = BuildEntries(values, blank, EffectiveFilterType == typeof(string));
            }

            return entryList;
        }

        /// <summary>
        /// The blank first, then an entry per value, minus whatever the blank already stands for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Every value is wrapped, not only the blank.</strong> <c>DropDownBase</c> infers a
        /// multiple selection's element type from the first item in <c>Data</c> and casts the whole
        /// selection to it, so a lone sentinel at the head of a list of raw values makes upstream cast
        /// a value to the sentinel's type. A homogeneous list is what
        /// <see cref="LookupColumnBase{TItem, TKey}" /> has always handed it.
        /// </para>
        /// <para>
        /// Nulls collapse into the blank: there is one however many rows have nothing, and the scan and
        /// the async path both strip them before they get here anyway.
        /// </para>
        /// <para>
        /// <strong>The empty string collapses into it too, and only on a string column.</strong> This
        /// grid coalesces a null string to the empty one for every operator but <c>IsNull</c> -
        /// <c>FilterExpression.NotNull</c> is where - so on a string column <c>In [""]</c> already
        /// matches the rows with nothing in them. Leaving the scanned empty string in the list would
        /// put two entries beside each other that filter identically, one of them drawn as an empty
        /// row. One entry, saying what it does.
        /// </para>
        /// </remarks>
        static List<object> BuildEntries(IEnumerable values, string blank, bool text)
        {
            // First, and not sorted among the values: it is the absence of one. §14's own placement.
            var built = new List<object> { new FastGridFilterEntry(null, blank) };

            foreach (var value in values)
            {
                if (value is null || (text && value is string { Length: 0 }))
                {
                    continue;
                }

                built.Add(new FastGridFilterEntry(value, EntryText(value)));
            }

            return built;
        }

        /// <summary>What a value draws in a check-box list.</summary>
        /// <remarks>
        /// What the raw value drew before §36 wrapped it, which is what the filter row has always
        /// shown: a check-box list has no <c>TextProperty</c>, so upstream renders the item itself -
        /// and for an <see cref="Enum" /> that means <c>GetDisplayDescription</c>, which honours
        /// <c>[Display]</c> and is the wording <c>RadzenDataGrid</c>'s own list draws. Spelled here
        /// because a wrapped entry is no longer an <c>Enum</c> for upstream to recognise, and an enum
        /// column that reads one way when it is nullable and another when it is not would be one rule
        /// with two spellings - which is the fault §34's and §35's reviews each found once.
        /// </remarks>
        static string EntryText(object value) =>
            value is Enum member
                ? Radzen.Blazor.EnumExtensions.GetDisplayDescription(member)
                : value.ToString() ?? string.Empty;

        /// <summary>
        /// The entries this column is offering, or null where it wraps nothing - which is every column
        /// that cannot hold a blank.
        /// </summary>
        List<object>? Entries => entryList;

        /// <summary>
        /// What one filter value is called, for a reader looking at it outside any control.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §37's naming hook, and the rule it exists to keep is that <strong>a name costs no query</strong>.
        /// A pill draws on the render after a filter is applied, so resolving an id by scanning would
        /// spend §36's queries-per-open gate somewhere §36 never looks. Every override below answers
        /// from a map that is already in memory - an enum's type, or §14's lookup, resolved once at
        /// startup - or from the value itself.
        /// </para>
        /// <para>
        /// <strong>Not through <see cref="Entries" />.</strong> That list exists only once a check-box
        /// list has been drawn, so naming through it would make the same filter read one way before the
        /// menu was opened and another after. The blank is spelled here instead, from the same string
        /// the entry uses.
        /// </para>
        /// <para>
        /// §34's relative-date token falls to <c>ToString</c> and that is its designed answer: §34 named
        /// a pill as one of the two places a token "sits alone with no position to be judged by", and
        /// <c>today-6d</c> is the canonical text it settled on. A token belonging to one of the six
        /// presets reaches here whenever the filter around it is not one -
        /// <see cref="FastGridFilterPresets.Recognize" /> answers for a whole filter and declines any
        /// that carries a second condition - and prints as those words. An earlier draft of this remark
        /// said such a token "never reaches here", which was wrong and was found by the review.
        /// </para>
        /// </remarks>
        internal virtual string? FilterValueTextOf(object? value) => value switch
        {
            null => Grid?.BlankFilterText,

            // The same wording the check-box list draws, for the same reason EntryText gives: an enum
            // column that reads one way in a list and another in a pill would be one rule with two
            // spellings.
            Enum member => Radzen.Blazor.EnumExtensions.GetDisplayDescription(member),

            _ => value.ToString(),
        };

        /// <summary>
        /// Draws one slot of §35's filter menu editor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On the column because only the column knows what it filters by, which is the same reason
        /// <see cref="FilterValueFromText" /> is here. The default is the text input the filter row
        /// already draws, and it covers every type the grid can filter by: the conversion is that
        /// method's, so an enum, a <see cref="Guid" /> and §34's relative-date tokens all read here
        /// exactly as they read in the row.
        /// </para>
        /// <para>
        /// <strong>§3 rule 1 does not reach this.</strong> That rule is about rows and cells, and its
        /// reason is the per-row multiplier; a menu is one panel, opened by hand, with one or two of
        /// these in it. What the rule does still forbid is reaching a typed control by widening -
        /// <c>RadzenDatePicker&lt;object&gt;</c> is what upstream's own filter UI uses and it is what
        /// loses a <see cref="DateOnly" /> column its type.
        /// </para>
        /// </remarks>
        internal virtual void RenderFilterEditor(RenderTreeBuilder builder, int sequence,
            FilterEditor editor)
        {
            builder.OpenElement(sequence, "input");
            builder.AddAttribute(sequence + 1, "type", "text");
            builder.AddAttribute(sequence + 2, "autocomplete", "off");
            builder.AddAttribute(sequence + 3, "class", "rz-textbox");
            builder.AddAttribute(sequence + 4, "style", "width:100%;");
            builder.AddAttribute(sequence + 5, "aria-label", editor.AriaLabel);
            builder.AddAttribute(sequence + 6, "value", FilterEditorText(editor.Value));

            // Both events, and both only reach the draft - §31's rule is that the menu does not *filter*
            // as you type, not that it does not read what is typed. onchange is what a blur raises;
            // oninput is what makes Enter committable, because a keydown arrives before the change
            // event and would otherwise commit the value as it stood one keystroke ago.
            builder.AddAttribute(sequence + 7, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
                this, args => editor.Set(FilterValueFromText(args.Value as string))));

            // Through the non-rendering receiver, as the filter row's own typing is: a keystroke that
            // changes nothing on screen must not redraw the panel, and the panel sits over a table.
            builder.AddAttribute(sequence + 8, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(
                this, NonRenderingHandler.Wrap<ChangeEventArgs>(args =>
                {
                    editor.Set(FilterValueFromText(args.Value as string));

                    return Task.CompletedTask;
                })));

            builder.CloseElement();
        }

        /// <summary>
        /// A drafted value as the text an editor shows for it, which is invariant for a token and
        /// <see cref="CultureInfo.CurrentCulture" /> for everything else.
        /// </summary>
        /// <remarks>
        /// The two halves are <see cref="FilterValueFromText" /> read backwards, and they have to be:
        /// §32 settled that stored values are invariant and typed text is the current culture, and a
        /// token has exactly one spelling by §34's rule. Showing a token through the current culture
        /// would produce text the box beside it could not read back.
        /// </remarks>
        private protected static string? FilterEditorText(object? value) => value switch
        {
            null => null,
            FastGridRelativeDate relative => relative.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString(),
        };

        /// <summary>
        /// What the check-box list is bound to, which is the first condition's value unless the list
        /// offers something other than the values the column filters by.
        /// </summary>
        /// <remarks>
        /// <strong>Only a set operator answers.</strong> §32's browser pass recorded a circuit
        /// terminating when <c>FilterMode</c> was switched to <c>CheckBoxList</c> with a scalar filter
        /// applied: the value went to <c>RadzenDropDown</c> in <c>Multiple</c> mode, which casts it to a
        /// sequence, and a decimal is not one. The cast is upstream's and correct; what was wrong was
        /// handing a set editor a filter that is not a set.
        /// <see cref="LookupColumnBase{TItem, TKey}" /> has guarded this since §14 and the base did not.
        /// </remarks>
        internal object? FilterSelection =>
            CurrentFilter?.First is { } first && first.Operator.Arity() == FastGridFilterArity.Many
                ? SelectionOf(first.Value)
                : null;

        /// <summary>
        /// A filter value as the entries a check-box list is bound to.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="FilterSelection" /> because §35's menu needs the same mapping over a
        /// value that has not been committed - the draft. It was the review that made this necessary
        /// and not tidiness: the menu was reusing the filter row's control, which is bound to the
        /// committed filter and applies on every tick, so ticking a box filtered immediately and Apply
        /// then wrote the draft back over it. A menu that undoes the selection it is confirming.
        /// </remarks>
        internal virtual object? SelectionOf(object? value)
        {
            // §36: on a column that offers a blank the list is bound to entries rather than to the
            // values, so the ticks have to be found again - which is exactly what LookupColumnBase has
            // done since §14. Scanned rather than indexed, but not for §14's reason: a lookup's list is
            // bounded by the lookup and a scanned column's is not. What bounds this one is when it runs
            // - FilterSelection answers null unless a set filter is committed, so the scan happens on a
            // column somebody is actively filtering by a set, over the few values they ticked.
            // Where the column wraps nothing this is the identity it always was.
            if (Entries is not { } offered || value is not IEnumerable selected || value is string)
            {
                return value;
            }

            var ticked = new List<object>();

            foreach (var item in selected)
            {
                if (EntryFor(offered, item) is { } entry)
                {
                    ticked.Add(entry);
                }
            }

            return ticked;
        }

        /// <summary>The entry a filter value stands for, or null when none does.</summary>
        static object? EntryFor(List<object> offered, object? value)
        {
            for (var i = 0; i < offered.Count; i++)
            {
                if (offered[i] is FastGridFilterEntry entry && Equals(entry.Value, value))
                {
                    return offered[i];
                }
            }

            return null;
        }

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
            // The declared type rather than its underlying one, which is what this line used to strip.
            // §36's blank puts a null in here and a List<decimal> cannot hold one; a column that is not
            // nullable has no underlying type to lose, so nothing else changes shape.
            var values = DynamicCode.Supported
                ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(EffectiveFilterType))!
                : new List<object>();

            foreach (var item in selected)
            {
                // An entry goes back to the value it stands for, and the blank's is a null. §14's
                // SelectedKeys does the same for a lookup entry with no key, and the null then rides in
                // the In list all the way to List<T?>.Contains, which a provider translates as
                // x IN (...) OR x IS NULL.
                values.Add(item is FastGridFilterEntry entry ? entry.Value : item);
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
                // Told before recorded. SetParametersAsync throws on a null Grid so the conditional
                // cannot decline today, but a field saying "I have reported this" when nothing was told
                // would never report that identity again - and the two lines should not disagree about
                // whether the call is certain.
                Grid?.InvalidateColumnIdentities();
                reportedIdentity = identity;
            }

            if (!initialized)
            {
                // Both parameters may legitimately be null, so the first pass cannot be told from a
                // no-op by comparing them; it has to be marked.
                initialized = true;
                declaredVisible = Visible;
                declaredFilterValue = FilterValue;
                declaredSecondFilterValue = SecondFilterValue;
                declaredFilterOperator = FilterOperator;
                declaredOwnedOperator = FilterOperatorOf;
                declaredSecondOperator = SecondFilterOperator;
                declaredLogicalOperator = LogicalFilterOperator;
                CurrentFilter = DeclaredFilter();
                activeFilter = null;

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
            //
            // Both writes below drop the resolution beside the filter, as SetFilter does. Not reachable
            // today - every read of ActiveFilter is behind HasFilter and every route resolves before it
            // reads - but a rule applied to two of three writes to CurrentFilter is §10b's finding
            // waiting for a fourth caller.
            // The declaration wins whenever it changes, and takes the picker's override with it - the
            // same rule the filter value follows, for the same reason: markup that says Visible="false"
            // is not asking to be overruled by what someone ticked before.
            if (declaredVisible != Visible)
            {
                declaredVisible = Visible;
                pickedVisible = null;
            }

            // One comparison over the whole declaration rather than one per part. The parts are read
            // together to build a filter, so noticing them apart would rebuild it up to five times for
            // one markup change - and would have to decide what a half-changed declaration means.
            if (!Equals(declaredFilterValue, FilterValue)
                || !Equals(declaredSecondFilterValue, SecondFilterValue)
                || declaredFilterOperator != FilterOperator
                || declaredOwnedOperator != FilterOperatorOf
                || declaredSecondOperator != SecondFilterOperator
                || declaredLogicalOperator != LogicalFilterOperator)
            {
                declaredFilterValue = FilterValue;
                declaredSecondFilterValue = SecondFilterValue;
                declaredFilterOperator = FilterOperator;
                declaredOwnedOperator = FilterOperatorOf;
                declaredSecondOperator = SecondFilterOperator;
                declaredLogicalOperator = LogicalFilterOperator;

                CurrentFilter = DeclaredFilter();
                activeFilter = null;
                AppliedFilterText = null;
                AppliedSecondFilterText = null;
            }
        }

        /// <summary>
        /// The filter this column's markup declares, or null when it declares none.
        /// </summary>
        /// <remarks>
        /// A declared value with no operator takes the column's default, which is what it always did. A
        /// second operator makes a second condition; a <c>Between</c> takes both values into one, which
        /// is the whole reason it is an operator here rather than a pair.
        /// </remarks>
        FastGridFilter? DeclaredFilter()
        {
            // Nothing declared is nothing built, and §3's third rule is why this guard is not an
            // optimisation: without it every column allocated a filter, a condition and a one-element
            // array on its first parameter set - about 104 bytes each, on a grid that may not even allow
            // filtering. That is what the review named the commit's unattributed ~0.8 KB as, five
            // columns at a time, and it is the summary above finally being true.
            if (FilterValue is null && SecondFilterValue is null && FilterOperator is null
                && FilterOperatorOf is null && SecondFilterOperator is null)
            {
                return null;
            }

            var filterOperator = DeclaredFilterOperator ?? DefaultFilterOperator;

            if (filterOperator == FastGridFilterOperator.Between)
            {
                return new FastGridFilter(
                    new FastGridFilterCondition(filterOperator, new[] { FilterValue, SecondFilterValue }));
            }

            var first = Condition(filterOperator, FilterValue);

            return SecondFilterOperator is { } second
                ? new FastGridFilter(first, Condition(second, SecondFilterValue), LogicalFilterOperator)
                : new FastGridFilter(first);
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
        /// Deliberately not defaulted through <see cref="FilterPropertyPath" />, which three of the four
        /// columns that override this happen to answer identically. A filter path is a query and this is
        /// not, and coupling them would rebuild §10b's class of fault from the other side: a column
        /// given a <c>FilterBy</c> would change its name.
        /// </para>
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
        /// member <c>SortBy</c> - identity <c>null</c>, <c>SortPath</c> non-null - <em>silently stopped
        /// persisting</em> width, order, visibility and filter that it had persisted before §27. §27 said
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
        /// The column's own value for a row, boxed, or null where its text <em>is</em> its value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The companion to <see cref="CellTextOf" />, for a caller that wants the value rather than
        /// the rendering of it - which today is §40's export, where a number written as a number is a
        /// number Excel can sum and a number written as text is not.
        /// </para>
        /// <para>
        /// <strong>Null is the right answer for most columns, and that is the point rather than an
        /// omission.</strong> Only a column whose text is a <em>formatting</em> of an underlying value
        /// has a value worth preferring over that text. A lookup column's text is the name it resolved
        /// and its value is an id nobody asked to see; a collection column's text is a joined list and
        /// its value is a sequence; a template column has no text at all. For all three the text is the
        /// meaning, so they inherit this and the caller falls back to <see cref="CellTextOf" />.
        /// </para>
        /// <para>
        /// <strong>Returning the text here instead of null would be observationally identical today</strong>,
        /// because every caller reads this as <c>CellValueOf(item) ?? CellTextOf(item)</c> - a mutation
        /// making the default <c>CellTextOf(item)</c> changed no test, and investigating rather than
        /// patching it is §39's lesson. It stays null because null is the <em>statement</em>: this
        /// column has no value distinct from its text. A caller that ever wants to tell the two apart -
        /// "has a value" from "has a value equal to its text" - needs the distinction to have been
        /// preserved, and it cannot be reconstructed later.
        /// </para>
        /// <para>
        /// <strong>It boxes, and that is a considered exception to §3's fifth rule.</strong> That rule
        /// is about the render path, which this is not on: this is asked once per cell of an export
        /// that is already allocating a spreadsheet cell object per cell. Keeping it generic would mean
        /// a typed interface reaching through <see cref="ColumnBase{TItem}" />, which is a large change
        /// to the column hierarchy to avoid an allocation that is a rounding error beside the one it
        /// sits next to.
        /// </para>
        /// </remarks>
        /// <param name="item">The row.</param>
        public virtual object? CellValueOf(TItem item) => null;

        /// <summary>
        /// The .NET format string this column formats its values with, or null if it does not.
        /// </summary>
        /// <remarks>
        /// The third of the trio beside <see cref="CellTextOf" /> and <see cref="CellValueOf" />: the
        /// text, the value, and how the one became the other. §40's export needs it to give a
        /// spreadsheet a number format rather than exporting a bare number where the grid drew
        /// currency, and only a column that formats has one - which today is
        /// <see cref="PropertyColumn{TItem, TProp}" />.
        /// </remarks>
        public virtual string? CellFormat => null;

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
