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
        /// Where a column's filter is authored - a row of boxes under the headers, or a menu.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="FilterUI.Row" />, which is today's behaviour. Requires
        /// <see cref="AllowFiltering" />, which is what switches filtering on at all.
        /// </remarks>
        [Parameter] public FilterUI FilterUI { get; set; }

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
        /// Upstream's own class list, so a theme styles it unchanged, and upstream's
        /// <c>rz-grid-filter-active</c> for the state that says this column is filtered. Always visible
        /// on a filterable column: hover-only is invisible on touch and unreachable by keyboard without
        /// inventing a second mechanism.
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
                ? "rz-filter-button rz-button rz-button-md rz-button-icon-only rz-variant-flat rz-base"
                    + " rz-shade-default rz-grid-filter-active"
                : "rz-filter-button rz-button rz-button-md rz-button-icon-only rz-variant-flat rz-base"
                    + " rz-shade-default");
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

            builder.OpenElement(11, "i");
            builder.AddAttribute(12, "class", "notranslate rzi");
            builder.AddAttribute(13, "aria-hidden", "true");
            builder.AddContent(14, FilterIcon);
            builder.CloseElement();

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
            builder.OpenComponent<RadzenPopup>(300);
            builder.AddAttribute(740, nameof(RadzenPopup.CloseOnClickOutside), true);
            builder.AddAttribute(741, nameof(RadzenPopup.AutoFocusFirstElement), true);
            builder.AddAttribute(742, nameof(RadzenPopup.Style), "display:none;");
            builder.AddAttribute(743, "class", "rz-overlaypanel");
            builder.AddAttribute(748, "id", FilterMenuElementId);
            builder.AddAttribute(744, "role", "menu");
            builder.AddAttribute(745, "aria-label", FilterText);
            builder.AddAttribute(746, nameof(RadzenPopup.ChildContent),
                (RenderFragment)RenderFilterMenuBody);
            builder.AddAttribute(749, nameof(RadzenPopup.Close),
                EventCallback.Factory.Create(this, OnFilterMenuClosedAsync));
            builder.AddComponentReferenceCapture(747,
                captureMenuPopup ??= reference => menuPopup = (RadzenPopup)reference);
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

            // The check-box list proper is §36's. What this draws is the filter row's control bound to
            // the *draft* rather than to the committed filter, and the difference is the whole of the
            // review's first finding: bound the row's way, the list applied on every tick and Apply then
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

        /// <summary>The multiselect, bound to the draft and to the operator the menu picked.</summary>
        /// <remarks>
        /// Not <c>RenderFilterList</c>, which exists for the filter row: that one reads
        /// <c>FilterSelection</c> - the committed filter - and its change handler calls <c>Filter</c>
        /// with <c>In</c> hard-coded. Both are right for a row and wrong here: the row has no Apply to
        /// wait for, and it has no operator list beside it, so <c>NotIn</c> was unreachable through it
        /// even though the menu offers it.
        /// </remarks>
        void RenderFilterMenuList(RenderTreeBuilder builder, ColumnBase<TItem> column)
        {
            builder.OpenComponent<RadzenDropDown<IEnumerable>>(60);
            builder.AddAttribute(61, nameof(RadzenDropDown<IEnumerable>.Data), FilterLookup(column));
            builder.AddAttribute(62, nameof(RadzenDropDown<IEnumerable>.Multiple), true);
            builder.AddAttribute(63, nameof(RadzenDropDown<IEnumerable>.AllowClear), true);
            builder.AddAttribute(64, nameof(RadzenDropDown<IEnumerable>.AllowFiltering), true);
            builder.AddAttribute(65, nameof(RadzenDropDown<IEnumerable>.FilterCaseSensitivity),
                FilterCaseSensitivity.CaseInsensitive);
            builder.AddAttribute(66, nameof(RadzenDropDown<IEnumerable>.Style), "width: 100%");
            builder.AddAttribute(67, nameof(RadzenDropDown<IEnumerable>.Value),
                column.SelectionOf(menuValue));
            builder.AddAttribute(68, nameof(RadzenDropDown<IEnumerable>.Change),
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
        string OperatorText(FastGridFilterOperator filterOperator) => filterOperator switch
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
        string PresetText(FastGridFilterPreset preset) => preset switch
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

        void PickFilterOperator(FastGridFilterOperator picked)
        {
            menuOperator = picked;

            // The values are kept across a change of operator where the new one can hold them, so
            // switching Equals to Between keeps what was typed and asks only for the other bound.
            if (picked.Arity() != FastGridFilterArity.Two)
            {
                menuSecondValue = null;
            }

            if (picked.Arity() == FastGridFilterArity.None)
            {
                menuValue = null;
            }
        }

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
        Task OnFilterMenuClosedAsync() =>
            AllowKeyboardNavigation && hasFocus ? ShowFocusAsync() : Task.CompletedTask;

        async Task CloseFilterMenuAsync()
        {
            if (menuPopup is { } popup)
            {
                await popup.CloseAsync();
            }
        }
    }
}
