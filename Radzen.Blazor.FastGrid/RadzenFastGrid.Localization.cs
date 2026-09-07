using System;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Radzen.FastGrid
{
    public partial class RadzenFastGrid<TItem>
    {
        StringResolver? strings;

        // Services is injected in the data half of this component and is nullable there, because a grid
        // rendered without a service provider - which is what a bare unit test is - still has to work.
        StringResolver Strings => strings ??= Services?.GetService<ILocalizer>() is { } custom
            ? new StringResolver(custom)
            : StringResolver.Default;

        /// <summary>
        /// The culture strings are resolved in when a grid does not name one, set by an ancestor.
        /// </summary>
        [CascadingParameter(Name = nameof(DefaultUICulture))]
        public CultureInfo? DefaultUICulture { get; set; }

        CultureInfo? uiCulture;

        /// <summary>
        /// The culture this grid resolves its strings in. Defaults to the cascaded
        /// <see cref="DefaultUICulture" />, then to the thread's, matching every other Radzen component.
        /// </summary>
#pragma warning disable BL0007
        [Parameter]
        public CultureInfo UICulture
        {
            get => uiCulture ?? DefaultUICulture ?? CultureInfo.CurrentUICulture;
            set => uiCulture = value;
        }
#pragma warning restore BL0007

        /// <summary>
        /// Resolves one of the grid's own strings: a custom <c>ILocalizer</c> first, then the consuming
        /// application's own <c>RadzenStrings</c> resources, then the ones shipped with Radzen.Blazor.
        /// </summary>
        public string Localize(string key) => Strings.Get(key, UICulture);

        /// <summary>
        /// The same, for a key <c>Radzen.Blazor</c> does not ship - falling back to English rather than
        /// to the key itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §35 needs seven strings that have no upstream key: <c>Between</c> and the six relative-date
        /// presets. <see cref="StringResolver" /> ends its chain by returning the key, which on screen
        /// reads <c>DataGrid_BetweenText</c> - worse than not localizing at all.
        /// </para>
        /// <para>
        /// The consequence is the good one, and it is why this is a fallback rather than a
        /// <c>.resx</c> of the component's own: the chain still looks in the consuming application's
        /// <c>Radzen.Blazor.RadzenStrings</c> first, so an application can translate all seven today by
        /// adding the keys - and if Radzen ever ships them, this grid picks them up with no change.
        /// A second resource file beside upstream's would be a second vocabulary in the one place a
        /// reader most wants there to be one, which is what §31 refused for <c>FilterMode</c>.
        /// </para>
        /// </remarks>
        public string Localize(string key, string fallback)
        {
            var resolved = Localize(key);

            return string.Equals(resolved, key, StringComparison.Ordinal) ? fallback : resolved;
        }

        // Every string below is "what the markup said, else what the resources say", which a component
        // parameter cannot express as an auto-property; BL0007 objects to the shape. Radzen.Blazor
        // suppresses it for the whole assembly for exactly this idiom, and this is the one file here
        // that needs it.
        //
        // The keys are RadzenDataGrid's own, deliberately. Every one of them is already translated into
        // the five cultures Radzen ships, so this grid inherits those translations rather than asking
        // anyone to retranslate strings they have already paid for - and an application that has
        // overridden one of them for RadzenDataGrid gets the same override here for free.
#pragma warning disable BL0007

        string? clearFilterText;

        /// <summary>The clear button's accessible name, on a column that carries a filter.</summary>
        [Parameter]
        public string ClearFilterText
        {
            get => clearFilterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ClearFilterText));
            set => clearFilterText = value;
        }

        string? filterValueAriaLabel;

        /// <summary>
        /// Sits between the column's title and the filter's value in the filter box's accessible name,
        /// so a screen reader hears which column the box belongs to rather than a bare value.
        /// </summary>
        [Parameter]
        public string FilterValueAriaLabel
        {
            get => filterValueAriaLabel
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_FilterValueAriaLabel));
            set => filterValueAriaLabel = value;
        }

        string? expandChildItemAriaLabel;

        /// <summary>
        /// The row-detail toggle's accessible name. One name for both states, as RadzenDataGrid has it:
        /// the button carries aria-expanded, and that is what conveys which way it will go.
        /// </summary>
        [Parameter]
        public string ExpandChildItemAriaLabel
        {
            get => expandChildItemAriaLabel
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ExpandChildItemAriaLabel));
            set => expandChildItemAriaLabel = value;
        }

        string? columnsText;

        /// <summary>The column picker's placeholder.</summary>
        [Parameter]
        public string ColumnsText
        {
            get => columnsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ColumnsText));
            set => columnsText = value;
        }

        string? allColumnsText;

        /// <summary>The column picker's select-all label.</summary>
        [Parameter]
        public string AllColumnsText
        {
            get => allColumnsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_AllColumnsText));
            set => allColumnsText = value;
        }

        string? columnsShowingText;

        /// <summary>What the picker says instead of listing names once there are too many.</summary>
        [Parameter]
        public string ColumnsShowingText
        {
            get => columnsShowingText
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ColumnsShowingText));
            set => columnsShowingText = value;
        }

        string? blankFilterText;

        /// <summary>
        /// What a lookup column's filter calls the entry standing for the rows carrying no id at all.
        /// Only a column whose key can be null offers one.
        /// </summary>
        /// <remarks>
        /// A spreadsheet's key rather than a grid's, because it is the only string in the resources
        /// that already means "the rows with nothing here" - and it is translated into every culture
        /// Radzen ships, which a key of this component's own would not be.
        /// </remarks>
        [Parameter]
        public string BlankFilterText
        {
            get => blankFilterText ?? Localize(nameof(Blazor.RadzenStrings.Spreadsheet_Blank));
            set => blankFilterText = value;
        }

        // The sixteen operator names, every one of them upstream's own key. §31's promise about
        // carrying knowledge across is what these are: an application that reworded one of them for
        // RadzenDataGrid gets the same wording here, and none of them needed translating again.

        string? equalsText;

        /// <summary>The filter menu's name for the operator matching equal to the value.</summary>
        [Parameter]
        public string EqualsText
        {
            get => equalsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_EqualsText));
            set => equalsText = value;
        }

        string? notEqualsText;

        /// <summary>The filter menu's name for the operator matching not equal to the value.</summary>
        [Parameter]
        public string NotEqualsText
        {
            get => notEqualsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_NotEqualsText));
            set => notEqualsText = value;
        }

        string? lessThanText;

        /// <summary>The filter menu's name for the operator matching ordered strictly below the value.</summary>
        [Parameter]
        public string LessThanText
        {
            get => lessThanText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_LessThanText));
            set => lessThanText = value;
        }

        string? lessThanOrEqualsText;

        /// <summary>The filter menu's name for the operator matching ordered at or below the value.</summary>
        [Parameter]
        public string LessThanOrEqualsText
        {
            get => lessThanOrEqualsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_LessThanOrEqualsText));
            set => lessThanOrEqualsText = value;
        }

        string? greaterThanText;

        /// <summary>The filter menu's name for the operator matching ordered strictly above the value.</summary>
        [Parameter]
        public string GreaterThanText
        {
            get => greaterThanText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_GreaterThanText));
            set => greaterThanText = value;
        }

        string? greaterThanOrEqualsText;

        /// <summary>The filter menu's name for the operator matching ordered at or above the value.</summary>
        [Parameter]
        public string GreaterThanOrEqualsText
        {
            get => greaterThanOrEqualsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_GreaterThanOrEqualsText));
            set => greaterThanOrEqualsText = value;
        }

        string? containsText;

        /// <summary>The filter menu's name for the operator matching containing the value.</summary>
        [Parameter]
        public string ContainsText
        {
            get => containsText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ContainsText));
            set => containsText = value;
        }

        string? doesNotContainText;

        /// <summary>The filter menu's name for the operator matching not containing the value.</summary>
        [Parameter]
        public string DoesNotContainText
        {
            get => doesNotContainText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_DoesNotContainText));
            set => doesNotContainText = value;
        }

        string? startsWithText;

        /// <summary>The filter menu's name for the operator matching beginning with the value.</summary>
        [Parameter]
        public string StartsWithText
        {
            get => startsWithText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_StartsWithText));
            set => startsWithText = value;
        }

        string? endsWithText;

        /// <summary>The filter menu's name for the operator matching ending with the value.</summary>
        [Parameter]
        public string EndsWithText
        {
            get => endsWithText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_EndsWithText));
            set => endsWithText = value;
        }

        string? inText;

        /// <summary>The filter menu's name for the operator matching one of the values.</summary>
        [Parameter]
        public string InText
        {
            get => inText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_InText));
            set => inText = value;
        }

        string? notInText;

        /// <summary>The filter menu's name for the operator matching none of the values.</summary>
        [Parameter]
        public string NotInText
        {
            get => notInText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_NotInText));
            set => notInText = value;
        }

        string? isNullText;

        /// <summary>The filter menu's name for the operator matching missing.</summary>
        [Parameter]
        public string IsNullText
        {
            get => isNullText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_IsNullText));
            set => isNullText = value;
        }

        string? isNotNullText;

        /// <summary>The filter menu's name for the operator matching present.</summary>
        [Parameter]
        public string IsNotNullText
        {
            get => isNotNullText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_IsNotNullText));
            set => isNotNullText = value;
        }

        string? isEmptyText;

        /// <summary>The filter menu's name for the operator matching the empty string.</summary>
        [Parameter]
        public string IsEmptyText
        {
            get => isEmptyText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_IsEmptyText));
            set => isEmptyText = value;
        }

        string? isNotEmptyText;

        /// <summary>The filter menu's name for the operator matching anything but the empty string.</summary>
        [Parameter]
        public string IsNotEmptyText
        {
            get => isNotEmptyText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_IsNotEmptyText));
            set => isNotEmptyText = value;
        }

        string? customFilterText;

        /// <summary>
        /// The filter menu's name for a filter the caller applies itself.
        /// </summary>
        /// <remarks>
        /// The menu never offers <c>Custom</c> - it composes no predicate here - but the operator's name
        /// is read wherever an operator's name is read, and the review found the catch-all arm labelling
        /// it "Between". An operator that means "you filter this" reading as a range is the kind of
        /// wrong label nobody checks.
        /// </remarks>
        [Parameter]
        public string CustomFilterText
        {
            get => customFilterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_CustomText));
            set => customFilterText = value;
        }

        string? secondFilterValueAriaLabel;

        /// <summary>The same, for a range's upper bound.</summary>
        [Parameter]
        public string SecondFilterValueAriaLabel
        {
            get => secondFilterValueAriaLabel
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_SecondFilterValueAriaLabel));
            set => secondFilterValueAriaLabel = value;
        }

        string? filterText;

        /// <summary>The filter menu panel's accessible name.</summary>
        [Parameter]
        public string FilterText
        {
            get => filterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_FilterText));
            set => filterText = value;
        }

        string? applyFilterText;

        /// <summary>The filter menu's Apply button.</summary>
        [Parameter]
        public string ApplyFilterText
        {
            get => applyFilterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_ApplyFilterText));
            set => applyFilterText = value;
        }

        string? filterToggleAriaLabel;

        /// <summary>The header filter icon's accessible name.</summary>
        [Parameter]
        public string FilterToggleAriaLabel
        {
            get => filterToggleAriaLabel
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_FilterToggleAriaLabel));
            set => filterToggleAriaLabel = value;
        }

        // The seven §35 needs and upstream has no key for - and §36's eighth. Each is the key an
        // application would add to its own RadzenStrings to translate it, with the English the resolver
        // falls back to.
        string? betweenText;

        /// <summary>The filter menu's name for an inclusive range.</summary>
        [Parameter]
        public string BetweenText
        {
            get => betweenText ?? Localize("DataGrid_BetweenText", "Between");
            set => betweenText = value;
        }

        string? selectAllFilterText;

        /// <summary>The check-box list's name for the box that ticks every value at once.</summary>
        /// <remarks>
        /// §36's one string, and the eighth of these. Upstream's own check-box list leaves
        /// <c>SelectAllText</c> unset, which renders the box with an empty <c>aria-label</c> - a control
        /// a screen reader cannot name. This grid is one tab stop with everything named
        /// (§12), so it is named here.
        /// </remarks>
        [Parameter]
        public string SelectAllFilterText
        {
            get => selectAllFilterText ?? Localize("DataGrid_SelectAllText", "Select all");
            set => selectAllFilterText = value;
        }

        string? todayFilterText;

        /// <summary>The relative-date preset for the current day.</summary>
        [Parameter]
        public string TodayFilterText
        {
            get => todayFilterText ?? Localize("DataGrid_TodayText", "Today");
            set => todayFilterText = value;
        }

        string? yesterdayFilterText;

        /// <summary>The relative-date preset for the day before.</summary>
        [Parameter]
        public string YesterdayFilterText
        {
            get => yesterdayFilterText ?? Localize("DataGrid_YesterdayText", "Yesterday");
            set => yesterdayFilterText = value;
        }

        string? last7DaysFilterText;

        /// <summary>The relative-date preset for the last seven days, today included.</summary>
        [Parameter]
        public string Last7DaysFilterText
        {
            get => last7DaysFilterText ?? Localize("DataGrid_Last7DaysText", "Last 7 days");
            set => last7DaysFilterText = value;
        }

        string? last30DaysFilterText;

        /// <summary>The relative-date preset for the last thirty days, today included.</summary>
        [Parameter]
        public string Last30DaysFilterText
        {
            get => last30DaysFilterText ?? Localize("DataGrid_Last30DaysText", "Last 30 days");
            set => last30DaysFilterText = value;
        }

        string? thisMonthFilterText;

        /// <summary>The relative-date preset running from the first of the month.</summary>
        [Parameter]
        public string ThisMonthFilterText
        {
            get => thisMonthFilterText ?? Localize("DataGrid_ThisMonthText", "This month");
            set => thisMonthFilterText = value;
        }

        string? thisYearFilterText;

        /// <summary>The relative-date preset running from the first of the year.</summary>
        [Parameter]
        public string ThisYearFilterText
        {
            get => thisYearFilterText ?? Localize("DataGrid_ThisYearText", "This year");
            set => thisYearFilterText = value;
        }

        // §37's six, of which two are upstream's keys used verbatim and four have no upstream key.
        // DataFilter_ClearFilterText is the interesting reuse: it is RadzenDataFilter's own name for
        // the button that clears everything, it is already "Clear all" in five cultures, and it means
        // exactly what the bar's button means.

        string? andFilterText;

        /// <summary>The word joining a filter's two conditions where both must hold.</summary>
        [Parameter]
        public string AndFilterText
        {
            get => andFilterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_AndOperatorText));
            set => andFilterText = value;
        }

        string? orFilterText;

        /// <summary>The word joining a filter's two conditions where either may hold.</summary>
        [Parameter]
        public string OrFilterText
        {
            get => orFilterText ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_OrOperatorText));
            set => orFilterText = value;
        }

        string? rangeFilterText;

        /// <summary>The word between a range's two bounds.</summary>
        /// <remarks>
        /// <strong>Not <see cref="AndFilterText" />, and the two agreeing in English is a coincidence
        /// of English.</strong> One joins two conditions and the other separates two bounds of one, and
        /// the languages that spell those differently would have no way to say so if this read the
        /// conjunction. It is also lower case where the conjunction is capitalised, because it sits
        /// inside a phrase rather than between two of them.
        /// </remarks>
        [Parameter]
        public string RangeFilterText
        {
            get => rangeFilterText ?? Localize("DataGrid_RangeText", "and");
            set => rangeFilterText = value;
        }

        string? moreFilterText;

        /// <summary>
        /// How a pill ends a set it has stopped naming. A composite format string taking the count.
        /// </summary>
        [Parameter]
        public string MoreFilterText
        {
            get => moreFilterText ?? Localize("DataGrid_MoreText", "or {0} more");
            set => moreFilterText = value;
        }

        string? removeFilterText;

        /// <summary>The accessible name of a pill's remove button, after the column's title.</summary>
        /// <remarks>
        /// Its own string rather than <see cref="ClearFilterText" />, which is <em>Clear</em> and is the
        /// name of the menu's button. A pill has no menu around it to be read in, so its button says
        /// what it removes.
        /// </remarks>
        [Parameter]
        public string RemoveFilterText
        {
            get => removeFilterText ?? Localize("DataGrid_RemoveFilterText", "Remove filter");
            set => removeFilterText = value;
        }

        string? clearAllFiltersText;

        /// <summary>The pill bar's button that clears every column at once.</summary>
        [Parameter]
        public string ClearAllFiltersText
        {
            get => clearAllFiltersText
                ?? Localize(nameof(Blazor.RadzenStrings.DataFilter_ClearFilterText));
            set => clearAllFiltersText = value;
        }

        string? activeFiltersText;

        /// <summary>The pill bar's accessible name.</summary>
        /// <remarks>
        /// §12 leaves nothing on this grid unnamed. A list of removable controls that a screen reader
        /// announces as "list, four items" is the same fault §36 found in an unnamed <em>Select all</em>
        /// box, one control wider.
        /// </remarks>
        [Parameter]
        public string ActiveFiltersText
        {
            get => activeFiltersText ?? Localize("DataGrid_ActiveFiltersText", "Active filters");
            set => activeFiltersText = value;
        }

        string? selectVisibleColumnsAriaLabel;

        /// <summary>The column picker's accessible name.</summary>
        [Parameter]
        public string SelectVisibleColumnsAriaLabel
        {
            get => selectVisibleColumnsAriaLabel
                ?? Localize(nameof(Blazor.RadzenStrings.DataGrid_SelectVisibleColumnsAriaLabel));
            set => selectVisibleColumnsAriaLabel = value;
        }

#pragma warning restore BL0007
    }
}
