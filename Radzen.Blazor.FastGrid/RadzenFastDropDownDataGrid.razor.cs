using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A drop-down whose popup is a <see cref="RadzenFastGrid{TItem}" />, for choosing a row out of a
    /// large table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <c>RadzenDropDownDataGrid</c>, and deliberately not a drop-in replacement for
    /// it. That component's columns are <c>RadzenDataGridColumn</c>, which name their property with a
    /// string; these are FastGrid's own columns, which name it with an expression - so the row type is a
    /// type parameter here and the authoring is checked at compile time. Everything the popup costs per
    /// row is the grid's cost, which is the point: a lookup over a few thousand rows renders for a
    /// fraction of what the general-purpose grid charges.
    /// </para>
    /// <para>
    /// It emits the same class names as the Radzen drop-down family and drives the same popup script,
    /// so a theme styles it with no extra work.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type shown in the popup.</typeparam>
    /// <typeparam name="TValue">The type of <see cref="Value" />.</typeparam>
    public partial class RadzenFastDropDownDataGrid<TItem, TValue> : IAsyncDisposable
    {
        [Inject] private IJSRuntime? JSRuntime { get; set; }

        /// <summary>The rows the popup offers.</summary>
        [Parameter] public IEnumerable<TItem>? Data { get; set; }

        /// <summary>The column definitions, as <see cref="RadzenFastGrid{TItem}" /> columns.</summary>
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <summary>The total row count, for a <see cref="LoadData" /> popup.</summary>
        [Parameter] public int Count { get; set; }

        /// <summary>Raised when the popup needs a page. See <see cref="RadzenFastGrid{TItem}.LoadData" />.</summary>
        [Parameter] public EventCallback<LoadDataArgs> LoadData { get; set; }

        /// <summary>The selected value.</summary>
        [Parameter] public TValue? Value { get; set; }

        /// <summary>Raised when <see cref="Value" /> changes.</summary>
        [Parameter] public EventCallback<TValue?> ValueChanged { get; set; }

        /// <summary>Raised when the selection changes, with the same value <see cref="ValueChanged" /> carries.</summary>
        [Parameter] public EventCallback<object?> Change { get; set; }

        /// <summary>Raised when a row is selected, with the row itself.</summary>
        [Parameter] public EventCallback<TItem> RowSelect { get; set; }

        /// <summary>
        /// The property whose value is shown in the closed drop-down. Without it the row's
        /// <c>ToString</c> is used.
        /// </summary>
        [Parameter] public string? TextProperty { get; set; }

        /// <summary>
        /// The property that supplies <see cref="Value" />. Without it the row itself is the value,
        /// which is what a drop-down bound to an entity wants.
        /// </summary>
        [Parameter] public string? ValueProperty { get; set; }

        /// <summary>Whether more than one row can be chosen. The popup stays open while choosing.</summary>
        [Parameter] public bool Multiple { get; set; }

        /// <summary>Shown when nothing is selected.</summary>
        [Parameter] public string? Placeholder { get; set; }

        /// <summary>What separates the chosen rows in the closed drop-down.</summary>
        [Parameter] public string Separator { get; set; } = ", ";

        /// <summary>Whether the drop-down can be opened.</summary>
        [Parameter] public bool Disabled { get; set; }

        /// <summary>Inline style for the drop-down.</summary>
        [Parameter] public string? Style { get; set; }

        /// <summary>Extra CSS class for the drop-down.</summary>
        [Parameter] public string? CssClass { get; set; }

        /// <summary>Inline style for the popup, which is where its width and height are set.</summary>
        [Parameter] public string PopupStyle { get; set; } = "display:none; min-width: 400px;";

        /// <summary>The element's tab index.</summary>
        [Parameter] public int TabIndex { get; set; }

        /// <summary>The accessible name of the drop-down.</summary>
        [Parameter] public string? Label { get; set; }

        /// <summary>Whether the popup's grid offers sorting.</summary>
        [Parameter] public bool AllowSorting { get; set; } = true;

        /// <summary>Whether the popup's grid offers filtering.</summary>
        [Parameter] public bool AllowFiltering { get; set; } = true;

        /// <summary>The filter presentation the popup's grid uses.</summary>
        [Parameter] public FilterMode FilterMode { get; set; }

        /// <summary>Whether the popup's grid pages.</summary>
        [Parameter] public bool AllowPaging { get; set; } = true;

        /// <summary>Rows per page in the popup.</summary>
        [Parameter] public int PageSize { get; set; } = 5;

        /// <summary>Whether the popup's grid virtualizes instead of paging.</summary>
        [Parameter] public bool AllowVirtualization { get; set; }

        /// <summary>Row height in pixels, when virtualizing.</summary>
        [Parameter] public int ItemSize { get; set; } = 37;

        /// <summary>Shown in the popup when there are no rows.</summary>
        [Parameter] public RenderFragment? EmptyTemplate { get; set; }

        /// <summary>Whether the popup is open.</summary>
        public bool Open { get; private set; }

        /// <summary>The rows currently chosen. Empty unless <see cref="Multiple" /> is set.</summary>
        public ICollection<TItem> SelectedItems { get; } = new List<TItem>();

        /// <summary>The popup's grid, once it has been opened at least once.</summary>
        public RadzenFastGrid<TItem>? Grid => grid;

        RadzenFastGrid<TItem>? grid;
        ElementReference element;
        TItem? selected;
        TValue? boundValue;
        bool valueRead;

        string Id { get; } = "rz-fastlookup-" + Guid.NewGuid().ToString("N");

        string PopupId => Id + "-popup";

        // The same panel class the Radzen drop-down family emits, so a theme styles this popup with the
        // rules it already has - including the wider multi-select panel.
        string PopupCssClass => Multiple ? "rz-multiselect-panel" : "rz-dropdown-panel";

        string RootCssClass => string.IsNullOrEmpty(CssClass)
            ? Disabled ? "rz-dropdown rz-state-disabled" : "rz-dropdown"
            : Disabled ? "rz-dropdown rz-state-disabled " + CssClass : "rz-dropdown " + CssClass;

        /// <summary>What the closed drop-down reads.</summary>
        string SelectedText => Multiple
            ? string.Join(Separator, SelectedItems.Select(Text))
            : Text(selected!) ?? string.Empty;

        string? Text(TItem item) => item is null
            ? null
            : string.IsNullOrEmpty(TextProperty)
                ? item.ToString()
                : Convert.ToString(PropertyAccess.GetItemOrValueFromProperty(item, TextProperty),
                    CultureInfo.CurrentCulture);

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            // Only when the bound value actually changed. Assigning it back from a selection would
            // otherwise re-read it on the next parameter set and undo a chip the user just removed.
            if (valueRead && EqualityComparer<TValue?>.Default.Equals(boundValue, Value))
            {
                return;
            }

            valueRead = true;
            boundValue = Value;

            Adopt(Value);
        }

        /// <summary>
        /// Finds the rows a bound value names, so a drop-down given a value renders its text rather than
        /// its placeholder. Only what is loaded can be found: with LoadData or a database source the
        /// value's row may not be on the current page, and the drop-down then shows the placeholder
        /// until the row arrives.
        /// </summary>
        void Adopt(TValue? value)
        {
            SelectedItems.Clear();
            selected = default;

            if (value is null || Data is null)
            {
                return;
            }

            if (Multiple && value is System.Collections.IEnumerable many && value is not string)
            {
                var wanted = many.Cast<object>().ToHashSet();

                foreach (var item in Data)
                {
                    if (ValueOf(item) is { } candidate && wanted.Contains(candidate))
                    {
                        SelectedItems.Add(item);
                    }
                }

                return;
            }

            selected = Data.FirstOrDefault(item => Equals(ValueOf(item), value));
        }

        /// <summary>
        /// The chosen values, as a list of the element type <typeparamref name="TValue" /> asks for.
        /// </summary>
        /// <remarks>
        /// A List&lt;object&gt; is not an IEnumerable&lt;int&gt;, however assignable its contents are, so
        /// binding Multiple to anything but object would have failed the cast on the first selection.
        /// </remarks>
        object Chosen()
        {
            var element = MultipleElementType;

            if (element is null)
            {
                return SelectedItems.Select(ValueOf).ToList();
            }

            var typed = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(element))!;

            foreach (var item in SelectedItems)
            {
                typed.Add(ValueOf(item));
            }

            return typed;
        }

        // Once per closed generic type: the answer depends only on TValue.
        static readonly Type? MultipleElementType = ElementOf(typeof(TValue));

        static Type? ElementOf(Type type)
        {
            if (type == typeof(object) || type == typeof(string))
            {
                return null;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return type.GetGenericArguments()[0];
            }

            foreach (var contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return contract.GetGenericArguments()[0];
                }
            }

            return null;
        }

        object? ValueOf(TItem item) => string.IsNullOrEmpty(ValueProperty)
            ? item
            : PropertyAccess.GetItemOrValueFromProperty(item, ValueProperty);

        async Task OnRowClick(TItem item)
        {
            if (Multiple)
            {
                if (!SelectedItems.Remove(item))
                {
                    SelectedItems.Add(item);
                }
            }
            else
            {
                selected = item;

                await ClosePopup();
            }

            await Publish();
            await RowSelect.InvokeAsync(item);
        }

        async Task Publish()
        {
            // Recorded before it is raised: the handler assigns Value back, and OnParametersSet must
            // see that as the value it just published rather than as a new one to adopt.
            boundValue = Multiple ? (TValue?)Chosen() : (TValue?)ValueOf(selected!);

            valueRead = true;
            Value = boundValue;

            await ValueChanged.InvokeAsync(boundValue);
            await Change.InvokeAsync(boundValue);
        }

        async Task TogglePopup()
        {
            if (Disabled)
            {
                return;
            }

            Open = !Open;

            // Rendered before the popup is positioned: the script measures the panel, and an empty one
            // is measured at the wrong height.
            StateHasChanged();

            if (JSRuntime is not null)
            {
                await JSRuntime.InvokeVoidAsync("Radzen.togglePopup", element, PopupId, true);
            }
        }

        /// <summary>Opens the popup.</summary>
        public Task OpenPopup() => Open ? Task.CompletedTask : TogglePopup();

        /// <summary>Closes the popup.</summary>
        public async Task ClosePopup()
        {
            if (!Open)
            {
                return;
            }

            Open = false;

            if (JSRuntime is not null)
            {
                await JSRuntime.InvokeVoidAsync("Radzen.closePopup", PopupId);
            }

            StateHasChanged();
        }

        Task CloseOnEscape(KeyboardEventArgs args) =>
            args.Key == "Escape" ? ClosePopup() : Task.CompletedTask;

        Task OnKeyDown(KeyboardEventArgs args) => args.Key switch
        {
            "Escape" => ClosePopup(),
            "Enter" or " " or "ArrowDown" => Open ? Task.CompletedTask : TogglePopup(),
            _ => Task.CompletedTask,
        };

        /// <summary>
        /// Destroys the popup the script created for this drop-down. Awaited rather than abandoned: a
        /// popup left behind is a detached element the script goes on positioning.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);

            if (JSRuntime is null)
            {
                return;
            }

            try
            {
                await JSRuntime.InvokeVoidAsync("Radzen.destroyPopup", PopupId);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, and with it the popup.
            }
        }
    }
}
