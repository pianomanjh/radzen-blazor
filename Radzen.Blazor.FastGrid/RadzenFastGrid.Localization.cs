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
