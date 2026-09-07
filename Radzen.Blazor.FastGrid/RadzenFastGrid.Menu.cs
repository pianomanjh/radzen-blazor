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
            builder.OpenElement(90, "button");
            builder.AddAttribute(91, "type", "button");
            builder.AddAttribute(92, "tabindex", "-1");
            builder.AddAttribute(93, "class", column.HasFilter
                ? "rz-filter-button rz-button rz-button-md rz-button-icon-only rz-variant-flat rz-base"
                    + " rz-shade-default rz-grid-filter-active"
                : "rz-filter-button rz-button rz-button-md rz-button-icon-only rz-variant-flat rz-base"
                    + " rz-shade-default");
            builder.AddAttribute(94, "aria-haspopup", "menu");
            builder.AddAttribute(95, "aria-label", column.HeaderText + " " + FilterToggleAriaLabel);

            // The header cell's own click sorts the column. The icon sits inside that click target, so
            // it has to stop - the same rule the resize handle and the drag handle already follow.
            builder.AddAttribute(96, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                this, _ => OpenFilterMenu(column, index)));
            builder.AddEventStopPropagationAttribute(97, "onclick", true);

            builder.AddElementReferenceCapture(98, iconCaptures[index]);

            builder.OpenElement(99, "i");
            builder.AddAttribute(100, "class", "notranslate rzi");
            builder.AddAttribute(101, "aria-hidden", "true");
            builder.AddContent(102, FilterIcon);
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

            menuOperator = first?.Operator;
            menuValue = first?.Value;
            menuSecondValue = first?.SecondValue;
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
            builder.AddAttribute(744, "role", "menu");
            builder.AddAttribute(745, "aria-label", FilterText);
            builder.AddAttribute(746, nameof(RadzenPopup.ChildContent),
                (RenderFragment)RenderFilterMenuBody);
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

            // A FilterTemplate is the whole editor, with no operator picker beside it: the template's
            // author sets value and operator themselves, so a picker would be a second control fighting
            // the first over one piece of state. §31.
            if (column.FilterTemplate is { } template)
            {
                builder.AddContent(2, template(column));
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
            var offered = FastGridFilterOperators.Menu(column.EffectiveFilterType, column.FilterNullable);

            builder.OpenElement(10, "ul");
            builder.AddAttribute(11, "class", "rz-listbox-list");

            for (var i = 0; i < offered.Length; i++)
            {
                var picked = offered[i];

                builder.OpenElement(12, "li");
                builder.AddAttribute(13, "role", "presentation");

                builder.OpenElement(14, "button");
                builder.AddAttribute(15, "type", "button");
                builder.AddAttribute(16, "class", menuOperator == picked && menuPreset is null
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
                for (var i = 0; i < FastGridFilterPresets.All.Length; i++)
                {
                    var preset = FastGridFilterPresets.All[i];

                    builder.OpenElement(19, "li");
                    builder.AddAttribute(20, "role", "presentation");

                    builder.OpenElement(21, "button");
                    builder.AddAttribute(22, "type", "button");
                    builder.AddAttribute(23, "class", menuPreset == preset
                        ? "rz-multiselect-item rz-state-highlight rz-filter-menu-item"
                        : "rz-multiselect-item rz-filter-menu-item");
                    builder.AddAttribute(24, "onclick", EventCallback.Factory.Create<MouseEventArgs>(
                        this, _ => ApplyFilterPresetAsync(column, preset)));
                    builder.AddContent(25, PresetText(preset));
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

            // The set editor is §36's, and until then a column filtering by In keeps the control the
            // filter row already gives it.
            if (arity == FastGridFilterArity.Many)
            {
                RenderFilterList(builder, column);
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
        FastGridFilterPreset? menuPreset =>
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

            await Filter(column, filter);
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
