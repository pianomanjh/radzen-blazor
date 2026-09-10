using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>§35's filter menu: the header icon, the one panel, and the draft it edits.</summary>
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Where a column's filter is authored - a menu the header's icon opens, or a row of boxes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Defaults to <see cref="FilterUI.Menu" />, and that is a deliberate break with §31's
        /// "everything defaults to today's behaviour".</strong> The user's call, made once the menu
        /// existed to be looked at. A consumer who wants the old second header row of boxes writes
        /// <c>FilterUI="Row"</c>, and nothing else about their grid changes.
        /// </para>
        /// <para>
        /// Requires <see cref="AllowFiltering" />, which is what switches filtering on at all - so a
        /// grid that never opted into filtering is unaffected by this default.
        /// </para>
        /// </remarks>
        [Parameter] public FilterUI FilterUI { get; set; } = FilterUI.Menu;

        /// <summary>The header icon that opens the filter menu.</summary>
        /// <remarks>
        /// Upstream's default, and upstream's parameter name, so a consumer who has restyled
        /// <c>RadzenDataGrid</c>'s icon finds the same lever here.
        /// </remarks>
        [Parameter] public string FilterIcon { get; set; } = "filter_alt";

        /// <summary>Whether the menu is the editor, which needs filtering switched on as well.</summary>
        internal bool FilterMenuEnabled => AllowFiltering && FilterUI == FilterUI.Menu;

        /// <summary>
        /// The panel's element id, so the icon can name it and upstream can find it.
        /// </summary>
        /// <remarks>
        /// Explicit rather than left to <c>RadzenComponent.GetId()</c>, because two things need to agree
        /// on it: the icon's <c>aria-controls</c>, and <c>Radzen.setPopupAriaExpanded</c>, which looks
        /// the anchor up <em>by</em> that attribute and silently does nothing when it is missing. The
        /// review found the icon claiming <c>aria-haspopup="menu"</c> and never saying whether the menu
        /// was open, which is worse than not claiming it.
        /// </remarks>
        internal string FilterMenuElementId => ElementId + "-filter";

        // The panel. One for the grid, and null until the first open builds it - §31's rule, and §29's
        // reason: exactly one of these can be open, so a panel per column multiplies a cost by the
        // column count for a control that is singular by construction.
        RadzenPopup? menuPopup;
        Action<object>? captureMenuPopup;

        // The column the panel is currently pointed at. Null when it has never been opened, which is
        // also what says the body has nothing to draw yet.
        ColumnBase<TItem>? menuColumn;

        // The draft: one operator and up to two values, seeded when the panel is retargeted and thrown
        // away when it closes. §31 settled that the menu applies on Apply or Enter and never per
        // keystroke - "a half-typed between bound filters to nothing on every keystroke" - so there has
        // to be somewhere uncommitted for a half-typed bound to sit. Fields rather than an object,
        // because there is exactly one panel and §3 does not want an allocation for a thing that is one.
        FastGridFilterOperator? menuOperator;
        object? menuValue;
        object? menuSecondValue;

        // Set by a click on an icon and consumed by the render that follows it. The panel is opened
        // from OnAfterRenderAsync rather than from the click, which is §29's order for §29's reason:
        // RadzenPopup.ToggleAsync calls into JavaScript straight away, and JavaScript measures a panel
        // whose body this render has not written yet.
        int menuPending = -1;

        // One element reference per drawn column, so the panel has something to hang from. Rebuilt only
        // when the column count moves: a capture delegate per column per render is what §3's third rule
        // is about, and the pager's captureTopPager is the same pattern one control at a time.
        ElementReference[] iconElements = Array.Empty<ElementReference>();
        Action<ElementReference>[] iconCaptures = Array.Empty<Action<ElementReference>>();

        void RefreshIconCaptures(int count)
        {
            if (iconCaptures.Length >= count)
            {
                return;
            }

            var elements = new ElementReference[count];
            var captures = new Action<ElementReference>[count];

            Array.Copy(iconElements, elements, iconElements.Length);

            for (var i = 0; i < count; i++)
            {
                var index = i;

                captures[i] = reference => iconElements[index] = reference;
            }

            iconElements = elements;
            iconCaptures = captures;
        }

        /// <summary>The filter icon in a column's header.</summary>
        /// <remarks>
        /// <para>
        /// <strong>Upstream's own control, which §35 claimed and did not draw.</strong> §35's comment
        /// said "upstream's own class list, so a theme styles it unchanged" and then wrote a full
        /// <c>rz-button rz-button-md rz-button-icon-only rz-variant-flat</c> with a nested <c>i</c>
        /// inside it. <c>RadzenDataGridHeaderCell</c> draws neither: its filter icon is a bare
        /// <c>button</c> carrying <c>notranslate rzi rz-grid-filter-icon</c> with the glyph as its own
        /// text, and the themes size it through <c>--rz-grid-header-filter-icon-font-size</c> for
        /// exactly this job. Ours rendered as a boxed button a header row tall, which is what a
        /// button-sized control looks like where an icon-sized one belongs.
        /// </para>
        /// <para>
        /// <c>rz-filter-button</c> is gone with it, and it was borrowed from a third control: the
        /// themes scope it to <c>.rz-cell-filter-content .rz-filter-button</c>, the operator button
        /// inside a <em>filter row</em> cell, and one of those rules sets <c>.rzi { display: none }</c>
        /// - which is a rule that would have hidden this icon's glyph had the two ever met.
        /// </para>
        /// <para>
        /// <c>rz-grid-filter-active</c> stays: it is upstream's state class and the one thing §35 did
        /// take from the right control. Always visible on a filterable column - hover-only is invisible
        /// on touch and unreachable by keyboard without inventing a second mechanism.
        /// </para>
        /// <para>
        /// <c>tabindex="-1"</c> because §12 settled that this grid is one tab stop with the active cell
        /// named by <c>aria-activedescendant</c>, and eight icons in the tab order would undo that.
        /// Alt+Down is the keyboard route, which is Excel's own.
        /// </para>
        /// </remarks>
        void RenderFilterIcon(RenderTreeBuilder builder, ColumnBase<TItem> column, int index)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "type", "button");
            builder.AddAttribute(2, "tabindex", "-1");
            builder.AddAttribute(3, "class", column.HasFilter
                ? "notranslate rzi rz-grid-filter-icon rz-grid-filter-active"
                : "notranslate rzi rz-grid-filter-icon");
            builder.AddAttribute(4, "aria-haspopup", "menu");
            builder.AddAttribute(5, "aria-label", column.HeaderText + " " + FilterToggleAriaLabel);

            // Both, and both matter. aria-controls is what Radzen.setPopupAriaExpanded matches the
            // anchor on, so without it the attribute below is never maintained by the open and close.
            // It is written here as the closed state, which is what it is on the render that draws it.
            builder.AddAttribute(6, "aria-controls", FilterMenuElementId);
            builder.AddAttribute(7, "aria-expanded",
                ReferenceEquals(menuColumn, column) && menuPopup is { IsOpen: true } ? "true" : "false");

            // The header cell's own click sorts the column. The icon sits inside that click target, so
            // it has to stop - the same rule the resize handle and the drag handle already follow.
            builder.AddAttribute(8, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => OpenFilterMenu(column, index)));
            builder.AddEventStopPropagationAttribute(9, "onclick", true);

            builder.AddElementReferenceCapture(10, iconCaptures[index]);

            // The glyph is the button's own text, as it is upstream: the icon font renders the
            // ligature, and the aria-label above is what a screen reader reads instead of it.
            builder.AddContent(11, FilterIcon);

            builder.CloseElement();
        }

        /// <summary>Opens the menu for the column the keyboard cursor is on.</summary>
        /// <remarks>
        /// By drawn index rather than by column, because that is what the cursor holds and what the
        /// icon's element reference is stored under. A column with no icon - one that cannot be
        /// filtered - has nothing to open, and saying so by doing nothing is what leaves Alt+Down inert
        /// there rather than opening the wrong column's menu.
        /// </remarks>
        internal void OpenFilterMenuFor(int index)
        {
            if (index < 0 || index >= visibleColumns.Count)
            {
                return;
            }

            var column = visibleColumns[index];

            if (column.CanFilter || column.FilterTemplate is not null)
            {
                OpenFilterMenu(column, index);
            }
        }

        /// <summary>Points the panel at a column and asks the render after this one to open it.</summary>
        void OpenFilterMenu(ColumnBase<TItem> column, int index)
        {
            menuColumn = column;
            menuPending = index;

            SeedFilterDraft(column);

            StateHasChanged();
        }

        /// <summary>
        /// Loads the draft from what the column is actually filtering by.
        /// </summary>
        /// <remarks>
        /// From <c>CurrentFilter</c> and not from <c>ActiveFilter</c>: §34 made the first what the user
        /// authored and the second the resolution of it, and a menu that opened a relative filter as two
        /// resolved dates would be the staleness that section exists to prevent, wearing the user's own
        /// handwriting.
        /// </remarks>
        void SeedFilterDraft(ColumnBase<TItem> column)
        {
            var first = column.CurrentFilter?.First;

            menuValue = first?.Value;
            menuSecondValue = first?.SecondValue;

            // Only an operator this menu offers. A filter can arrive from markup, from a restored
            // setting or from ApplyFilters carrying one the menu does not list - LessThanOrEquals on a
            // date, say - and seeding it did two wrong things at once: no item in the list matched, so
            // the panel showed an editor under no selection, and editing that value committed through
            // the arm of the whole-day rule that leaves a date literal, which is the boundary loss §34
            // built @end to prevent. Dropping it asks the user to pick, and leaves the stored filter
            // alone until they do.
            menuOperator = first is { } condition && Offers(column, condition.Operator)
                ? condition.Operator
                : null;
        }

        static bool Offers(ColumnBase<TItem> column, FastGridFilterOperator filterOperator)
        {
            var offered = column.MenuOperators;

            for (var i = 0; i < offered.Length; i++)
            {
                if (offered[i] == filterOperator)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Opens the panel, once the render that wrote its body has happened.</summary>
        async Task OpenPendingFilterMenuAsync()
        {
            var index = menuPending;

            menuPending = -1;

            if (menuPopup is not { } popup || index < 0 || index >= iconElements.Length)
            {
                return;
            }

            // Closed first, and with the new anchor: RadzenPopup toggles off its own flag, so opening a
            // second column's menu while the first is open would close it and stop there.
            await popup.CloseAsync(iconElements[index]);
            await popup.ToggleAsync(iconElements[index]);

            // The icon's aria-expanded is written from C#, and opening the panel changes nothing the
            // grid has rendered - so without this the attribute stays "false" for as long as the menu is
            // open. Upstream's setPopupAriaExpanded writes the DOM directly, which is why only a test
            // sees the difference and a browser does not: two writers agreeing by luck is still one of
            // them being wrong.
            StateHasChanged();
        }

        /// <summary>The panel, rendered once the first open has asked for one.</summary>
        /// <remarks>
        /// <strong>Never removed once it is here.</strong> <c>Radzen.openPopup</c> moves the panel to
        /// <c>document.body</c> - which is what stops the sticky scroll container clipping it, and what
        /// keeps it outside §12's arrow space - and Blazor's diff tolerates attribute and child updates
        /// on a node JavaScript has reparented but resolves a removal against the logical parent it
        /// recorded. The body inside is what changes.
        /// </remarks>
        void RenderFilterMenu(RenderTreeBuilder builder)
        {
            if (menuColumn is null)
            {
                return;
            }

            // 300, after the bottom pager's 200 and the loading scrim: this is written last among the
            // grid's children and the numbers a run is written in have to ascend.
            RenderOverlayMenu(builder, 300, FilterMenuElementId, FilterText, RenderFilterMenuBody,
                EventCallback.Factory.Create(this, OnFilterMenuClosedAsync),
                captureMenuPopup ??= reference => menuPopup = (RadzenPopup)reference);
        }

        /// <summary>
        /// One popup panel, declared once, because there are two of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Extracted after the second copy dropped an attribute.</strong> §39's band menu was
        /// written from this block with the numbers changed and left <see cref="RadzenPopup.Close" />
        /// out, which is the fault §35's review found on the filter icon reappearing one panel over: the
        /// popup's own <c>OnClose</c> sets its <c>IsOpen</c>, but nothing tells the <em>grid</em> to
        /// re-render, so the trigger's <c>aria-expanded</c> stays as the last render wrote it until
        /// something else happens to redraw the grid.
        /// </para>
        /// <para>
        /// It is invisible in a browser, which is the point: <c>Radzen.setPopupAriaExpanded</c> writes
        /// that attribute into the DOM directly, so the screen is right while the render tree is wrong,
        /// and the two only disagree where nothing runs upstream's JavaScript - which is every test.
        /// §35 wrote the rule this breaks: <em>two writers agreeing by luck is still one of them being
        /// wrong.</em>
        /// </para>
        /// <para>
        /// Sequence numbers off a parameter rather than literals. Blazor wants them constant per code
        /// path and they are: each caller passes its own literal base, and this method is the only path
        /// under it.
        /// </para>
        /// </remarks>
        static void RenderOverlayMenu(RenderTreeBuilder builder, int sequence, string elementId,
            string ariaLabel, RenderFragment body, EventCallback close, Action<object> capture)
        {
            builder.OpenComponent<RadzenPopup>(sequence);
            builder.AddAttribute(sequence + 1, nameof(RadzenPopup.CloseOnClickOutside), true);
            builder.AddAttribute(sequence + 2, nameof(RadzenPopup.AutoFocusFirstElement), true);
            builder.AddAttribute(sequence + 3, nameof(RadzenPopup.Style), "display:none;");
            builder.AddAttribute(sequence + 4, "class", "rz-overlaypanel");
            builder.AddAttribute(sequence + 5, "id", elementId);
            builder.AddAttribute(sequence + 6, "role", "menu");
            builder.AddAttribute(sequence + 7, "aria-label", ariaLabel);
            builder.AddAttribute(sequence + 8, nameof(RadzenPopup.ChildContent), body);
            builder.AddAttribute(sequence + 9, nameof(RadzenPopup.Close), close);
            builder.AddComponentReferenceCapture(sequence + 10, capture);
            builder.CloseComponent();
        }

        void RenderFilterMenuBody(RenderTreeBuilder builder)
        {
            if (menuColumn is not { } column)
            {
                return;
            }

            builder.OpenElement(0, "div");

            // Keyed on the column, so the panel never carries the last one's controls - a date picker
            // left over from one column and handed another column's string is the shape §31 asked for
            // this key against.
            builder.SetKey(column);
            builder.AddAttribute(1, "class", "rz-overlaypanel-content");

            // §31: the menu applies on Apply or Enter. Bound on the panel rather than on the editors,
            // because there are up to two of them and because the operator buttons are a reasonable
            // place to press Enter from as well. The editors keep the draft current on every keystroke,
            // which is what makes this safe: a keydown arrives before the change event, so committing
            // from here without that would commit the value as it stood one keystroke ago.
            builder.AddAttribute(2, "onkeydown", EventCallback.Factory.Create<KeyboardEventArgs>(
                this, args => OnFilterMenuKeyAsync(column, args)));

            // A FilterTemplate is the whole editor, with no operator picker beside it: the template's
            // author sets value and operator themselves, so a picker would be a second control fighting
            // the first over one piece of state. §31.
            if (column.FilterTemplate is { } template)
            {
                builder.AddContent(3, template(column));
            }
            else
            {
                RenderFilterMenuOperators(builder, column);
                RenderFilterMenuEditors(builder, column);
            }

            RenderFilterMenuButtons(builder, column);

            builder.CloseElement();
        }

        void RenderFilterMenuOperators(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            var offered = column.MenuOperators;

            // Once for the whole list rather than once per item. Recognising a preset builds a filter to
            // ask about, so reading it seven times down this method allocated seven of them - which the
            // field comment at the top of this file argues against by name.
            var preset = DraftPreset();

            builder.OpenElement(10, "ul");
            builder.AddAttribute(11, "class", "rz-listbox-list");

            for (var i = 0; i < offered.Length; i++)
            {
                var picked = offered[i];

                builder.OpenElement(12, "li");
                builder.AddAttribute(13, "role", "presentation");

                builder.OpenElement(14, "button");
                builder.AddAttribute(15, "type", "button");
                builder.AddAttribute(16, "class", menuOperator == picked && preset is null
                    ? "rz-multiselect-item rz-state-highlight rz-filter-menu-item"
                    : "rz-multiselect-item rz-filter-menu-item");
                builder.AddAttribute(17, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                    this, _ => PickFilterOperator(picked)));
                builder.AddContent(18, OperatorText(picked));
                builder.CloseElement();

                builder.CloseElement();
            }

            // §31: the date operators are followed by the relative presets. They apply on pick, because
            // they need no value - an Apply step after one would be a button confirming a complete
            // sentence.
            if (FastGridRelativeDate.AppliesTo(column.EffectiveFilterType))
            {
                for (var i = 0; i < FastGridFilterPresets.All.Count; i++)
                {
                    var candidate = FastGridFilterPresets.All[i];

                    builder.OpenElement(19, "li");
                    builder.AddAttribute(20, "role", "presentation");

                    builder.OpenElement(21, "button");
                    builder.AddAttribute(22, "type", "button");
                    builder.AddAttribute(23, "class", preset == candidate
                        ? "rz-multiselect-item rz-state-highlight rz-filter-menu-item"
                        : "rz-multiselect-item rz-filter-menu-item");
                    builder.AddAttribute(24, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                        this, _ => ApplyFilterPresetAsync(column, candidate)));
                    builder.AddContent(25, PresetText(candidate));
                    builder.CloseElement();

                    builder.CloseElement();
                }
            }

            builder.CloseElement();
        }

        /// <summary>
        /// The editors for the picked operator's arity - none, one, or a range's two.
        /// </summary>
        /// <remarks>
        /// Nothing until an operator is picked, and that is not only tidiness: it is what stops a menu
        /// opening pre-filled on a column that is not filtered. A typed control cannot show
        /// <em>empty</em> for a non-nullable value type, so an editor drawn before the operator would
        /// open showing a zero or the first day of year one, and Apply would filter by a value nobody
        /// chose.
        /// </remarks>
        void RenderFilterMenuEditors(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            if (menuOperator is not { } filterOperator)
            {
                return;
            }

            var arity = filterOperator.Arity();

            if (arity == FastGridFilterArity.None)
            {
                return;
            }

            builder.OpenElement(30, "div");
            builder.AddAttribute(31, "class", "rz-filter-menu-editor");

            // The list bound to the *draft* rather than to the committed filter, which is the whole of
            // §35's review's first finding: bound the row's way, it applied on every tick and Apply then
            // wrote the draft back over it, so confirming a selection undid it. It also filtered per
            // keystroke, which §31 bans by name.
            if (arity == FastGridFilterArity.Many)
            {
                RenderFilterMenuList(builder, column);
            }
            else
            {
                column.RenderFilterEditor(builder, 32, new FilterEditor(menuValue,
                    value => menuValue = value,
                    column.HeaderText + FilterValueAriaLabel));

                if (arity == FastGridFilterArity.Two)
                {
                    column.RenderFilterEditor(builder, 40, new FilterEditor(menuSecondValue,
                        value => menuSecondValue = value,
                        column.HeaderText + SecondFilterValueAriaLabel));
                }
            }

            builder.CloseElement();
        }

        /// <summary>The check-box list, bound to the draft and to the operator the menu picked.</summary>
        /// <remarks>
        /// <para>
        /// <strong>§36's control, replacing the drop-down §35 left as a placeholder.</strong>
        /// <c>RadzenListBox</c> in <c>Multiple</c> mode draws a check box per value in a scrolling
        /// panel with a search box over it, which is the list §31 asked for and the one
        /// <c>RadzenDataGridHeaderCell</c> already draws for <c>FilterMode.CheckBoxList</c> - so its
        /// parameters here are upstream's own, for the reason §7's table gives.
        /// </para>
        /// <para>
        /// The row keeps the drop-down. A three-hundred-pixel scrolling list does not go in a header
        /// row, and the panel has room the row does not; what stays shared is the mapping that feeds
        /// them both, <see cref="ColumnBase{TItem}.SelectionOf" /> and
        /// <c>FilterValueFromSelection</c>.
        /// </para>
        /// <para>
        /// Not <c>RenderFilterList</c>, which exists for the filter row: that one reads
        /// <c>FilterSelection</c> - the committed filter - and its change handler calls <c>Filter</c>
        /// with <c>In</c> hard-coded. Both are right for a row and wrong here: the row has no Apply to
        /// wait for, and it has no operator list beside it, so <c>NotIn</c> was unreachable through it
        /// even though the menu offers it.
        /// </para>
        /// </remarks>
        void RenderFilterMenuList(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            builder.OpenComponent<RadzenListBox<IEnumerable>>(60);
            // Data before Value, and not only by convention: FilterLookup is what builds the column's
            // entries, and SelectionOf below maps the draft's values onto them. Written the other way
            // round the first draw of a list has nothing to map onto and shows no ticks - which the
            // second draw then heals, because §35's panel arms on click and opens after the render. So
            // no test can see it and this comment is the guard. One frame, on a column that opens
            // already filtered.
            builder.AddAttribute(61, nameof(RadzenListBox<IEnumerable>.Data), FilterLookup(column));
            builder.AddAttribute(62, nameof(RadzenListBox<IEnumerable>.Multiple), true);
            builder.AddAttribute(63, nameof(RadzenListBox<IEnumerable>.AllowClear), true);
            builder.AddAttribute(64, nameof(RadzenListBox<IEnumerable>.AllowFiltering), true);
            builder.AddAttribute(65, nameof(RadzenListBox<IEnumerable>.FilterCaseSensitivity),
                FilterCaseSensitivity.CaseInsensitive);

            // Bounded rendering, not bounded querying: the scan is one query and whole, and paging it
            // as the reader scrolls - which is what upstream's LoadData does - would make §36's
            // queries-per-open gate stop bounding anything.
            builder.AddAttribute(66, nameof(RadzenListBox<IEnumerable>.AllowVirtualization), true);
            builder.AddAttribute(67, nameof(RadzenListBox<IEnumerable>.AllowSelectAll), true);
            builder.AddAttribute(68, nameof(RadzenListBox<IEnumerable>.SelectAllText), SelectAllFilterText);
            builder.AddAttribute(69, nameof(RadzenListBox<IEnumerable>.Style),
                "width: 100%; height: 15rem");
            builder.AddAttribute(70, nameof(RadzenListBox<IEnumerable>.Value),
                column.SelectionOf(menuValue));
            builder.AddAttribute(71, nameof(RadzenListBox<IEnumerable>.Change),
                EventCallback.Factory.Create<object>(this, value => DraftSelection(column, value)));
            builder.CloseComponent();
        }

        /// <summary>What was ticked, in the values the column filters by, into the draft.</summary>
        void DraftSelection(ColumnBase<TItem> column, object? value) =>
            menuValue = value is IEnumerable sequence and not string
                ? column.FilterValueFromSelection(sequence)
                : null;

        void RenderFilterMenuButtons(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            builder.OpenElement(50, "div");
            builder.AddAttribute(51, "class", "rz-filter-menu-buttons");

            builder.OpenElement(52, "button");
            builder.AddAttribute(53, "type", "button");
            builder.AddAttribute(54, "class",
                "rz-button rz-button-md rz-variant-flat rz-light rz-shade-default");
            builder.AddAttribute(55, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => ClearFilterMenuAsync(column)));
            builder.AddContent(56, ClearFilterText);
            builder.CloseElement();

            builder.OpenElement(57, "button");
            builder.AddAttribute(58, "type", "button");
            builder.AddAttribute(59, "class",
                "rz-button rz-button-md rz-variant-flat rz-primary rz-shade-default");
            builder.AddAttribute(60, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => ApplyFilterMenuAsync(column)));
            builder.AddContent(61, ApplyFilterText);
            builder.CloseElement();

            builder.CloseElement();
        }

        /// <summary>What the menu calls an operator.</summary>
        /// <remarks>
        /// <para>
        /// Upstream's keys, deliberately and for §31's reason: every one of them is already translated
        /// into the cultures Radzen ships, an application that has reworded one for
        /// <c>RadzenDataGrid</c> gets the same wording here for nothing, and a reader carrying knowledge
        /// across is the promise a near-drop-in exists to keep.
        /// </para>
        /// <para>
        /// <c>Between</c> is the exception and the only one: upstream has no value for the operator, so
        /// it can have no string for it either - which is the same constraint that made §31 keep
        /// <c>FilterMode</c> upstream's and put <c>FilterUI</c> beside it, one layer down.
        /// </para>
        /// </remarks>
        internal string OperatorText(FastGridFilterOperator filterOperator) => filterOperator switch
        {
            FastGridFilterOperator.Equals => EqualsText,
            FastGridFilterOperator.NotEquals => NotEqualsText,
            FastGridFilterOperator.LessThan => LessThanText,
            FastGridFilterOperator.LessThanOrEquals => LessThanOrEqualsText,
            FastGridFilterOperator.GreaterThan => GreaterThanText,
            FastGridFilterOperator.GreaterThanOrEquals => GreaterThanOrEqualsText,
            FastGridFilterOperator.Contains => ContainsText,
            FastGridFilterOperator.DoesNotContain => DoesNotContainText,
            FastGridFilterOperator.StartsWith => StartsWithText,
            FastGridFilterOperator.EndsWith => EndsWithText,
            FastGridFilterOperator.In => InText,
            FastGridFilterOperator.NotIn => NotInText,
            FastGridFilterOperator.IsNull => IsNullText,
            FastGridFilterOperator.IsNotNull => IsNotNullText,
            FastGridFilterOperator.IsEmpty => IsEmptyText,
            FastGridFilterOperator.IsNotEmpty => IsNotEmptyText,
            FastGridFilterOperator.Custom => CustomFilterText,
            _ => BetweenText,
        };

        /// <summary>What the menu calls a relative-date preset.</summary>
        internal string PresetText(FastGridFilterPreset preset) => preset switch
        {
            FastGridFilterPreset.Today => TodayFilterText,
            FastGridFilterPreset.Yesterday => YesterdayFilterText,
            FastGridFilterPreset.Last7Days => Last7DaysFilterText,
            FastGridFilterPreset.Last30Days => Last30DaysFilterText,
            FastGridFilterPreset.ThisMonth => ThisMonthFilterText,
            _ => ThisYearFilterText,
        };

        /// <summary>Applies the draft when Enter is pressed anywhere in the panel.</summary>
        Task OnFilterMenuKeyAsync(ColumnBase<TItem> column, KeyboardEventArgs args) =>
            args.Key == "Enter" ? ApplyFilterMenuAsync(column) : Task.CompletedTask;

        /// <summary>
        /// Points the draft at another operator, keeping whatever values are already in it.
        /// </summary>
        /// <remarks>
        /// The first draft cleared the values an operator could not hold - the second when leaving
        /// <c>Between</c>, both when arriving at one that takes none - and the mutation loop showed
        /// every one of those writes to be unobservable, which they are:
        /// <see cref="FilterMenu.Compose" /> reads only as many values as the operator takes, and the
        /// draft is discarded on commit, so a value the current operator ignores can neither be
        /// committed nor survive the panel. What deleting them buys is the better behaviour anyway -
        /// a detour through <c>Equals</c> and back leaves the range's upper bound where the user
        /// typed it.
        /// </remarks>
        void PickFilterOperator(FastGridFilterOperator picked) => menuOperator = picked;

        /// <summary>The preset the draft currently is, or null where it is not one of the six.</summary>
        FastGridFilterPreset? DraftPreset() =>
            menuOperator == FastGridFilterOperator.Between
            && menuValue is FastGridRelativeDate
            && menuSecondValue is FastGridRelativeDate
                ? FastGridFilterPresets.Recognize(new FastGridFilter(
                    new FastGridFilterCondition(FastGridFilterOperator.Between,
                        new[] { menuValue, menuSecondValue })))
                : null;

        Task ApplyFilterPresetAsync(ColumnBase<TItem> column, FastGridFilterPreset preset) =>
            CommitFilterMenuAsync(column, preset.Filter());

        Task ApplyFilterMenuAsync(ColumnBase<TItem> column) => CommitFilterMenuAsync(column,
            menuOperator is { } filterOperator
                ? FilterMenu.Compose(filterOperator, menuValue, menuSecondValue,
                    column.EffectiveFilterType)
                : null);

        Task ClearFilterMenuAsync(ColumnBase<TItem> column) => CommitFilterMenuAsync(column, null);

        /// <summary>Closes the panel and applies what it holds - one reload, whichever button ran.</summary>
        /// <remarks>
        /// <strong>No <c>ConfigureAwait(false)</c> anywhere on this path</strong>, and the browser is
        /// what said so. Closing the panel is a JavaScript call, so the await genuinely suspends;
        /// discarding the synchronization context there resumes the reload on a thread pool thread, and
        /// <c>RefreshAsync</c> reaches <c>StateHasChanged</c> off the renderer's dispatcher - which
        /// terminates the circuit. Every test in the suite passed, because bUnit's renderer does not
        /// enforce the dispatcher the way a real one does.
        /// </remarks>
        async Task CommitFilterMenuAsync(ColumnBase<TItem> column, FastGridFilter? filter)
        {
            await CloseFilterMenuAsync();

            // Discarded here, which §35 promised and the build did not do. Reopening reseeds from
            // CurrentFilter, so nothing stale was ever *shown* - but a draft that outlives its panel is
            // a value the grid holds a reference to for as long as it lives, and the sentence that said
            // otherwise should be true rather than merely unfalsifiable.
            menuOperator = null;
            menuValue = null;
            menuSecondValue = null;

            await Filter(column, filter);
        }

        /// <summary>Puts the cursor back on the grid when the panel goes away.</summary>
        /// <remarks>
        /// <para>
        /// §35 said the panel returns focus to the grid rather than to the icon, and the build shipped
        /// nothing that did it - the review found the sentence describing upstream's behaviour rather
        /// than ours. Upstream restores whatever had focus when the popup opened, which on a mouse open
        /// is the icon on some browsers and nothing at all on others, and on an Escape close is a node
        /// inside the panel that is about to be hidden.
        /// </para>
        /// <para>
        /// The grid is the right answer because §12's one tab stop is the older promise: the icon is at
        /// <c>tabindex="-1"</c>, so leaving focus there leaves the page with a focused element Tab
        /// cannot reach again. Only for a grid that navigates, and only when it had the cursor - a
        /// mouse user who never touched the keyboard should not have the page scroll to the grid
        /// because a menu closed.
        /// </para>
        /// </remarks>
        Task OnFilterMenuClosedAsync()
        {
            // Same reason as the open: the icon says whether its menu is open, and closing is a thing
            // that happens to the popup rather than to the grid.
            StateHasChanged();

            return AllowKeyboardNavigation && hasFocus ? ShowFocusAsync() : Task.CompletedTask;
        }

        async Task CloseFilterMenuAsync()
        {
            if (menuPopup is { } popup)
            {
                await popup.CloseAsync();
            }
        }
    }
}
