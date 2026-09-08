using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Radzen.FastGrid
{
    /// <summary>§37's pill bar: what is filtered, said in words, above the scroll container.</summary>
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Whether the applied filters are shown as removable pills above the grid.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Off by default, as every feature here is. <c>Show</c> rather than <c>Allow</c> because it
        /// displays a thing rather than permitting one - the family already holds
        /// <see cref="ShowHeader" />, <c>ShowPagingSummary</c> and <c>ShowLoadingIndicator</c>.
        /// </para>
        /// <para>
        /// <strong>Independent of <see cref="FilterUI" />, and of <see cref="AllowFiltering" />.</strong>
        /// §31's reason for the first: a bar explains <em>applied filters</em>, and tying an
        /// explanation to an input feature would mean a team that prefers the row can never have one.
        /// The second follows from <see cref="ColumnBase{TItem}.HasFilter" />, which never consulted
        /// <see cref="AllowFiltering" /> - so a grid filtered by <c>ApplyFilters</c> with no filter UI
        /// at all still says what it is hiding, which is where an explanation is worth most.
        /// </para>
        /// </remarks>
        [Parameter] public bool ShowFilterPills { get; set; }

        /// <summary>The filter cell an id is written on, so a pill can send the cursor there.</summary>
        /// <remarks>
        /// Written only while <see cref="ShowFilterPills" /> is on and the row is the editor, so a grid
        /// that does not use the feature pays no attribute for it. Numbered by drawn index, which is
        /// what the pill has and what the filter row writes.
        /// </remarks>
        internal string FilterCellElementId(int index) => ElementId + "-fc-" + index;

        /// <summary>Whether a filter cell should carry that id on this render.</summary>
        /// <remarks>
        /// <strong>Per column, and the benchmark is why.</strong> Written for the feature and the mode
        /// alone, this cost about 1 KB a render on a grid with the bar on and nothing filtered - five
        /// concatenations and five attribute frames for ids no pill existed to use. A column with no
        /// filter has no pill, so it needs no name.
        /// </remarks>
        internal bool NamesFilterCellOf(ColumnBase<TItem> column) =>
            ShowFilterPills && AllowFiltering && FilterUI == FilterUI.Row && column.HasFilter;

        /// <summary>Whether anything is filtered, which is whether the bar is drawn at all.</summary>
        /// <remarks>
        /// <para>
        /// A walk rather than a held count. It runs once per render of a grid with the feature on, over
        /// the columns and not the rows, and a count maintained beside <c>SetFilter</c> would be a
        /// second thing that has to agree with <c>HasFilter</c> - §10b's recurring finding.
        /// </para>
        /// <para>
        /// <strong>Every column, not the drawn ones.</strong> A hidden column keeps its filter - which
        /// <see cref="ColumnBase{TItem}.Visible" /> says outright, and which is true however it came to
        /// be hidden, the picker included - and asking only the drawn columns
        /// meant a grid filtered by nothing but a hidden column drew no band, so the filter applied,
        /// nothing on screen said so, and <see cref="ClearFilters" />, which does walk every column,
        /// was behind the button the band was not drawing.
        /// </para>
        /// </remarks>
        bool AnyColumnFiltered()
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].HasFilter)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The chips themselves, inside the band <c>RenderBand</c> opens.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Above the scroller, and §37 measured why.</strong> A bar below the headers is inside
        /// <c>.rz-data-grid-data</c>, whose <c>thead</c> is sticky on the vertical axis only, so it
        /// slides out of view exactly when there are enough columns to want one - demonstrated at 960px
        /// of scroll on eleven columns. Making it sticky on both axes does not rescue it:
        /// <c>.rz-grid-table thead th</c> carries the <c>overflow: hidden</c> that gives a header cell
        /// its ellipsis, and a clipping ancestor is what a sticky child resolves <c>left</c> against.
        /// </para>
        /// <para>
        /// <strong>Nothing when nothing is filtered</strong> - §37's rule, and it still holds for the
        /// pills. It is the <em>band</em> that §39 made conditional on two things rather than one:
        /// this list is written only when something is filtered, and when it is not the band is drawn
        /// for the menu alone or not at all. The guard lives in <c>RenderBand</c> because it is now the
        /// band's question rather than the bar's, and an empty <c>role="list"</c> carrying an accessible
        /// name is what writing this unconditionally would leave behind.
        /// </para>
        /// <para>
        /// Every class that <em>paints</em> is upstream's and already styled in every shipped theme.
        /// <c>rz-datatable-header</c>, on the band, is the interesting one: the themes dress it as a
        /// toolbar band with the grid's own header padding and a bottom border, and no upstream
        /// component renders it. A sentence here claimed every class was upstream's; the review found
        /// this one and it is corrected rather than removed, because a nameless band is worse than an
        /// honest comment.
        /// </para>
        /// <para>
        /// <strong>Three names are this grid's own and none of them paints</strong> -
        /// <c>rz-filter-pills</c> on the band, <c>rz-filter-pill-hidden</c> on a pill whose column is
        /// not drawn, and <c>rz-filter-pill-notice</c> on what that pill says. They exist so a consumer
        /// can restyle them and the tests can find them, and every one of the three inherits its
        /// appearance from an upstream class beside it. What tells a hidden pill from a drawn one on
        /// sight is the glyph rather than the class, and what tells one to a screen reader is the pill's
        /// own <c>aria-label</c> - neither depends on a rule nobody has written.
        /// </para>
        /// </remarks>
        void RenderFilterPills(RenderTreeBuilder builder)
        {
            builder.OpenElement(20, "div");
            builder.AddAttribute(21, "class", "rz-chip-list rz-chip-list-horizontal");
            builder.AddAttribute(22, "role", "list");
            builder.AddAttribute(23, "aria-label", ActiveFiltersText);

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                var column = visibleColumns[i];

                if (column.HasFilter && column.CurrentFilter is { } filter)
                {
                    RenderFilterPill(builder, column, filter, i);
                }
            }

            // The hidden ones after the drawn ones, in a walk of their own. Their pills carry no drawn
            // index because there is no cell to send anyone to, and grouping them at the end keeps the
            // drawn pills in the order their columns are in for the reader who can see them.
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.IsVisible && column.HasFilter && column.CurrentFilter is { } filter)
                {
                    RenderHiddenFilterPill(builder, column, filter);
                }
            }

            RenderClearAllFilters(builder);

            builder.CloseElement();

            RenderHiddenColumnNotice(builder);
        }

        /// <summary>One column's filter, said in words, with a way to remove it and a way to edit it.</summary>
        /// <remarks>
        /// <para>
        /// A render tree rather than <c>RadzenChip</c> inside <c>RadzenChipList</c>. That pair is a
        /// <c>FormComponent</c> carrying selection, focus and keyboard semantics for a list of chips a
        /// reader picks from, which is not what this is - and it would cost three components per active
        /// filter where §3's third rule wants none. <c>RenderFilterIcon</c> made the same choice.
        /// </para>
        /// <para>
        /// <strong>The pill is a real tab stop</strong>, which is where this band disagrees with §35's
        /// header icon. §12's <em>one tab stop</em> is a rule about the grid's cells, reached by
        /// <c>aria-activedescendant</c>, which is why the icon sits at <c>tabindex="-1"</c>. This is
        /// outside <c>role="grid"</c>, a sibling of the pager, whose buttons have always been in the
        /// tab order - and a filter only a mouse can remove is the mouse-only control §31 refused.
        /// </para>
        /// </remarks>
        void RenderFilterPill(RenderTreeBuilder builder, ColumnBase<TItem> column,
            FastGridFilter filter, int index)
        {
            var editable = CanEditFilterOf();

            builder.OpenElement(30, "div");
            builder.AddAttribute(31, "class", "rz-chip-list-item");
            builder.AddAttribute(32, "role", "listitem");

            // Keyed on the column, so removing the third pill does not leave the fourth wearing the
            // third's handlers - the diff would otherwise match these by position.
            builder.SetKey(column);

            builder.OpenElement(33, "span");
            builder.AddAttribute(34, "class",
                "rz-chip rz-chip-base rz-shade-default rz-variant-filled");

            // A door to the editor, and only where there is one to open. Under FilterUI.Menu that is
            // the column's menu; under the row it is the box in the filter row, which focusing scrolls
            // into view. Where there is neither - AllowFiltering off, a filter that arrived through
            // ApplyFilters - the body is text: a control that looks clickable and does nothing is the
            // affordance fault §27's review caught in the column picker. A column that is not drawn has
            // no editor either and does not come through here at all: see RenderHiddenFilterPill.
            if (editable)
            {
                builder.AddAttribute(35, "role", "button");
                builder.AddAttribute(36, "tabindex", "0");
                builder.AddAttribute(37, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                    this, _ => EditFilterAsync(column, index)));
                builder.AddAttribute(38, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(
                    this, args => OnFilterPillKeyAsync(column, index, args)));
            }

            builder.OpenElement(39, "span");
            builder.AddAttribute(40, "class", "rz-chip-text");
            builder.AddContent(41, FilterPill.Phrase(this, column, filter));
            builder.CloseElement();

            RenderRemoveFilterButton(builder, 42, column);

            builder.CloseElement();
            builder.CloseElement();
        }

        /// <summary>The <c>x</c>, which is the same control on a drawn pill and on a hidden one.</summary>
        /// <remarks>
        /// The one part of the two pills that is identical rather than merely similar, and the part that
        /// matters most: it is the escape hatch, and a hidden column's filter has no other way out.
        /// Sequenced by the caller, because the two pills number their runs differently.
        /// </remarks>
        void RenderRemoveFilterButton(RenderTreeBuilder builder, int sequence, ColumnBase<TItem> column)
        {
            builder.OpenElement(sequence, "button");
            builder.AddAttribute(sequence + 1, "type", "button");
            builder.AddAttribute(sequence + 2, "class",
                "rz-button rz-button-xs rz-button-icon-only rz-variant-text rz-base rz-shade-lighter");
            builder.AddAttribute(sequence + 3, "aria-label", column.HeaderText + " " + RemoveFilterText);
            builder.AddAttribute(sequence + 4, "title", RemoveFilterText);
            builder.AddAttribute(sequence + 5, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => Filter(column, null)));

            // The body's click is on the element this button sits inside, so without this, removing a
            // filter also opens the editor of the column it was just removed from. The same rule the
            // header icon follows against the header cell's own sort click.
            builder.AddEventStopPropagationAttribute(sequence + 6, "onclick", true);

            builder.OpenElement(sequence + 7, "i");
            builder.AddAttribute(sequence + 8, "class", "notranslate rzi");
            builder.AddAttribute(sequence + 9, "aria-hidden", "true");
            builder.AddContent(sequence + 10, "close");
            builder.CloseElement();

            builder.CloseElement();
        }

        /// <summary>
        /// A filter on a column that is not drawn: the same pill, with the door closed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A pill rather than a cleared filter, and the difference is whose the filter is.</strong>
        /// Dropping a column's filter when it stops being drawn is the other answer, and it throws away
        /// something a reader authored on a grid they were reading - silently, on an action that says
        /// nothing about filtering. This says what is applied and leaves the choice. §45 decides it.
        /// </para>
        /// <para>
        /// The remove button is the same one, so the escape hatch is one click either way. What changes
        /// is the body: there is no filter cell to focus and no header to open a menu on, so it is a
        /// disclosure rather than a door - <c>aria-expanded</c> and <c>aria-controls</c> over the notice
        /// below, which is what a control that reveals something owes a screen reader and is what §39's
        /// menu trigger already does one element away.
        /// </para>
        /// <para>
        /// <strong>The sentence is on the pill and not only in the notice.</strong> A hidden pill whose
        /// accessible name is its phrase alone is indistinguishable from a drawn one, and the glyph
        /// beside it is <c>aria-hidden</c> - so the name carries the phrase and the sentence both, and
        /// nobody has to open the notice to be told. The glyph is <c>visibility_off</c>, which is the
        /// picker's own vocabulary for the same state.
        /// </para>
        /// </remarks>
        void RenderHiddenFilterPill(RenderTreeBuilder builder, ColumnBase<TItem> column,
            FastGridFilter filter)
        {
            var phrase = FilterPill.Phrase(this, column, filter);

            builder.OpenElement(80, "div");
            builder.AddAttribute(81, "class", "rz-chip-list-item");
            builder.AddAttribute(82, "role", "listitem");

            builder.SetKey(column);

            builder.OpenElement(83, "span");
            builder.AddAttribute(84, "class",
                "rz-chip rz-chip-base rz-shade-default rz-variant-filled rz-filter-pill-hidden");
            builder.AddAttribute(85, "role", "button");
            builder.AddAttribute(86, "tabindex", "0");
            builder.AddAttribute(87, "aria-label", phrase + " " + HiddenColumnFilterText);
            builder.AddAttribute(88, "aria-controls", HiddenColumnNoticeElementId);
            builder.AddAttribute(89, "aria-expanded",
                ReferenceEquals(hiddenColumnNoticeFor, column) ? "true" : "false");
            builder.AddAttribute(90, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => ToggleHiddenColumnNotice(column)));
            builder.AddAttribute(91, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(
                this, args => OnHiddenFilterPillKey(column, args)));

            builder.OpenElement(92, "i");
            builder.AddAttribute(93, "class", "notranslate rzi");
            builder.AddAttribute(94, "aria-hidden", "true");
            builder.AddContent(95, "visibility_off");
            builder.CloseElement();

            builder.OpenElement(96, "span");
            builder.AddAttribute(97, "class", "rz-chip-text");
            builder.AddContent(98, phrase);
            builder.CloseElement();

            RenderRemoveFilterButton(builder, 99, column);

            builder.CloseElement();
            builder.CloseElement();
        }

        // The column whose hidden-filter pill was last clicked, or null while none was.
        ColumnBase<TItem>? hiddenColumnNoticeFor;

        /// <summary>The notice's element id, so a hidden pill can name it in <c>aria-controls</c>.</summary>
        internal string HiddenColumnNoticeElementId => ElementId + "-hidden-filter";

        /// <summary>Says the pill's column is hidden, after the pills rather than beside one.</summary>
        /// <remarks>
        /// <para>
        /// <strong>Outside the chip list</strong>, which is a <c>role="list"</c> whose children the
        /// pills are. One notice for however many hidden pills there are, which is why it names no
        /// column: it answers whichever pill is <c>aria-expanded</c>, and every hidden pill already
        /// carries the same sentence in its own name.
        /// </para>
        /// <para>
        /// <strong>The element is always written and its text is not.</strong> An
        /// <c>aria-controls</c> pointing at an id that is not in the document is the fault §35 found in
        /// <c>Radzen.setPopupAriaExpanded</c>, one attribute over. Empty it is a zero-width flex item
        /// and the band stays the height §39 measured; the <c>flex-basis</c> that gives it a line of its
        /// own is written only when there is something on that line.
        /// </para>
        /// <para>
        /// <strong>Read back off the column rather than cleared by hand.</strong> The text is drawn only
        /// while its column is still both filtered and hidden, so showing the column, removing the
        /// filter and <em>Clear all filters</em> each take it away without knowing it exists - which is
        /// three places that would otherwise have to remember to. <c>RemoveColumn</c> is the fourth and
        /// is not one of them: a column that leaves the markup is gone rather than changed, so the
        /// reference is dropped there.
        /// </para>
        /// </remarks>
        void RenderHiddenColumnNotice(RenderTreeBuilder builder)
        {
            var showing = hiddenColumnNoticeFor is { } column && !column.IsVisible && column.HasFilter;

            builder.OpenElement(110, "div");
            builder.AddAttribute(111, "class", "rz-filter-pill-notice");
            builder.AddAttribute(112, "id", HiddenColumnNoticeElementId);

            if (showing)
            {
                builder.AddAttribute(113, "style", "flex-basis:100%");
                builder.AddContent(114, HiddenColumnFilterText);
            }

            builder.CloseElement();
        }

        /// <summary>Shows the notice for this column, or takes it away if it is already showing.</summary>
        void ToggleHiddenColumnNotice(ColumnBase<TItem> column) =>
            hiddenColumnNoticeFor = ReferenceEquals(hiddenColumnNoticeFor, column) ? null : column;

        /// <summary>Enter and Space toggle it, which is what a <c>role="button"</c> promises.</summary>
        void OnHiddenFilterPillKey(ColumnBase<TItem> column, KeyboardEventArgs args)
        {
            if (args.Key is "Enter" or " ")
            {
                ToggleHiddenColumnNotice(column);
            }
        }

        /// <summary>Forgets a column the notice was answering for, when the column leaves the grid.</summary>
        internal void DropHiddenColumnNotice(ColumnBase<TItem> column)
        {
            if (ReferenceEquals(hiddenColumnNoticeFor, column))
            {
                hiddenColumnNoticeFor = null;
            }
        }

        /// <summary>Whether this column's filter has an editor the bar can send a reader to.</summary>
        /// <remarks>
        /// <strong>Just <see cref="AllowFiltering" />, and the mutation loop is what shortened it.</strong>
        /// The first draft also asked whether the column could be filtered or carried a
        /// <c>FilterTemplate</c>, and both were dead: a pill is drawn only for a column whose
        /// <see cref="ColumnBase{TItem}.HasFilter" /> is true, and that already reads
        /// <c>CanFilter &amp;&amp;</c> a present filter. Deleting either changed no test because neither
        /// could ever be false here.
        /// <para>
        /// The consequence is worth stating, because §37's prose was wider than this: a
        /// <c>FilterTemplate</c> column with no filter path does not get an inert pill, it gets
        /// <em>no pill</em>. <c>AllowFiltering</c> off is the only way to reach the inert body.
        /// </para>
        /// </remarks>
        bool CanEditFilterOf() => AllowFiltering;

        void RenderClearAllFilters(RenderTreeBuilder builder)
        {
            builder.OpenElement(60, "button");
            builder.AddAttribute(61, "type", "button");
            builder.AddAttribute(62, "class",
                "rz-button rz-button-sm rz-variant-text rz-base rz-shade-default");
            builder.AddAttribute(63, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => ClearFilters()));
            builder.AddContent(64, ClearAllFiltersText);
            builder.CloseElement();
        }

        /// <summary>Enter and Space open the pill, which is what a <c>role="button"</c> promises.</summary>
        /// <remarks>
        /// Spelled out because this is a <c>span</c> rather than a <c>button</c>: it holds the remove
        /// button, and a button inside a button is markup no browser agrees about. Taking the role means
        /// taking the keys that come with it.
        /// </remarks>
        Task OnFilterPillKeyAsync(ColumnBase<TItem> column, int index, KeyboardEventArgs args) =>
            args.Key is "Enter" or " " ? EditFilterAsync(column, index) : Task.CompletedTask;

        /// <summary>Sends the reader to wherever this column's filter is authored.</summary>
        /// <remarks>
        /// §31's reason, which is the whole point of the body being a click target: <em>"the column's
        /// header may be scrolled out of view, and that is precisely when someone wants to adjust
        /// rather than remove."</em> §31 wrote it as <em>reopens that column's menu</em> and the same
        /// paragraph had just made pills independent of <see cref="FilterUI" />, under which there is
        /// no menu - so what survives is the intent rather than the mechanism.
        /// </remarks>
        Task EditFilterAsync(ColumnBase<TItem> column, int index)
        {
            if (FilterUI == FilterUI.Menu)
            {
                OpenFilterMenu(column, index);

                return Task.CompletedTask;
            }

            return FocusFilterCellAsync(index);
        }

        /// <summary>Puts the cursor in a column's filter box, which scrolls that column into view.</summary>
        /// <remarks>
        /// <strong>No <c>ConfigureAwait(false)</c>, on §35's finding.</strong> This is a JavaScript call
        /// from a UI event, so the await genuinely suspends; discarding the synchronization context
        /// resumes off the renderer's dispatcher, which terminates a circuit and which no bUnit test
        /// can see.
        /// </remarks>
        async Task FocusFilterCellAsync(int index)
        {
            if (await BrowserAsync() is { } seam)
            {
                await seam.FocusFilterAsync(FilterCellElementId(index));
            }
        }
    }
}
