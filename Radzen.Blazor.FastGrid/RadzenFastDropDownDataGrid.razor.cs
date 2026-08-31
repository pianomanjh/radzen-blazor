using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components.Forms;
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
    [CascadingTypeParameter(nameof(TItem))]
    public partial class RadzenFastDropDownDataGrid<TItem, TValue> : IRadzenFormComponent, IAsyncDisposable
    {
        [Inject] private IJSRuntime? JSRuntime { get; set; }

        IRadzenForm? form;

        /// <summary>The form this drop-down belongs to, so a validator can find it by name.</summary>
        [CascadingParameter]
        public IRadzenForm? Form
        {
            get => form;
            set
            {
                form = value;
                form?.AddComponent(this);
            }
        }

        /// <summary>The name a validator addresses this drop-down by.</summary>
        [Parameter] public string? Name { get; set; }

        /// <summary>The expression <see cref="Value" /> is bound to, which names the field to validate.</summary>
        [Parameter] public Expression<Func<TValue?>>? ValueExpression { get; set; }

        /// <inheritdoc />
        public FieldIdentifier FieldIdentifier { get; set; }

        /// <inheritdoc />
        public bool IsBound => ValueChanged.HasDelegate;

        /// <inheritdoc />
        public bool HasValue => Multiple
            ? SelectedItems.Count > 0
            : selected is not null || Value is not null;

        /// <inheritdoc />
        public object? GetValue() => Value;

        /// <summary>Moves focus to the drop-down.</summary>
        public ValueTask FocusAsync() => element.FocusAsync();

        /// <summary>Whether the drop-down is rendered at all.</summary>
        [Parameter] public bool Visible { get; set; } = true;

        /// <summary>
        /// The form field this drop-down sits in, when it is inside a RadzenFormField. Not supported
        /// here: the field's floating label needs notice of focus and value changes that this component
        /// does not raise.
        /// </summary>
        public Radzen.Blazor.IFormFieldContext? FormFieldContext => null;

        /// <summary>The rows the popup offers.</summary>
        [Parameter] public IEnumerable<TItem>? Data { get; set; }

        /// <summary>The column definitions, as <see cref="RadzenFastGrid{TItem}" /> columns.</summary>
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <summary>The total row count, for a <see cref="LoadData" /> popup.</summary>
        [Parameter] public int Count { get; set; }

        /// <summary>Raised when the popup needs a page. See <see cref="RadzenFastGrid{TItem}.LoadData" />.</summary>
        [Parameter] public EventCallback<LoadDataArgs> LoadData { get; set; }

        /// <summary>The selected value.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1721:Property names should not match get methods",
            Justification = "GetValue is IRadzenFormComponent's own shape, which FormComponent<T> carries too.")]
        [Parameter] public TValue? Value { get; set; }

        /// <summary>Raised when <see cref="Value" /> changes.</summary>
        [Parameter] public EventCallback<TValue?> ValueChanged { get; set; }

        /// <summary>Raised when the selection changes, with the same value <see cref="ValueChanged" /> carries.</summary>
        [Parameter] public EventCallback<object?> Change { get; set; }

        /// <summary>Raised when a row is selected, with the row itself.</summary>
        [Parameter] public EventCallback<TItem> RowSelect { get; set; }

        /// <summary>
        /// The member whose value is shown in the closed drop-down. Without it the row's
        /// <c>ToString</c> is used.
        /// </summary>
        /// <remarks>
        /// An expression rather than a property name, for the same reason the columns take one - only
        /// more so here, because this is read <em>per row</em>. Naming the member as a string meant
        /// splitting the path, looking the property up by name and invoking it reflectively for every
        /// row in the source every time a bound value had to be matched to its row. Measured over 1000
        /// rows: 224.6 us and 58.6 KB by name against 22.9 us and 35.2 KB through a delegate compiled
        /// once, and 483.0 us against 32.8 us for a nested path, which also allocated the split.
        /// </remarks>
        [Parameter] public Expression<Func<TItem, object?>>? TextProperty { get; set; }

        /// <summary>
        /// The member that supplies <see cref="Value" />. Without it the row itself is the value,
        /// which is what a drop-down bound to an entity wants.
        /// </summary>
        /// <remarks>Read per row when a bound value is matched to its row; see <see cref="TextProperty" />.</remarks>
        [Parameter] public Expression<Func<TItem, object?>>? ValueProperty { get; set; }

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

        /// <summary>
        /// The height of the scrolling area when virtualizing. Virtualize needs a bounded, scrollable
        /// ancestor, and a popup has none of its own.
        /// </summary>
        [Parameter] public string PopupHeight { get; set; } = "285px";

        /// <summary>Shown in the popup when there are no rows.</summary>
        [Parameter] public RenderFragment? EmptyTemplate { get; set; }

        /// <summary>Whether the popup is open.</summary>
        public bool Open { get; private set; }

        /// <summary>
        /// The rows currently chosen - one of them unless <see cref="Multiple" /> is set. A set, not a
        /// list: the grid looks membership up once per rendered row, and its own documentation asks for
        /// one as soon as more than a handful can be chosen.
        /// </summary>
        public ICollection<TItem> SelectedItems { get; } = new HashSet<TItem>();

        /// <summary>The popup's grid, once it has been opened at least once.</summary>
        public RadzenFastGrid<TItem>? Grid => grid;

        RadzenFastGrid<TItem>? grid;
        ElementReference element;
        TItem? selected;
        TValue? boundValue;
        IEnumerable<TItem>? lastData;
        bool valueRead;

        // True from the first open onwards. The grid is built lazily - a lookup nobody opens should
        // cost nothing on a busy form - but it is kept once built, so the sort, filter and page the user
        // left it on survive a close, and a LoadData source is not re-queried on every open.
        bool built;

        // Set when the popup needs positioning, and acted on after the render that fills it. The script
        // measures the panel to decide whether to open upwards, and StateHasChanged inside an event
        // handler only queues the batch - so calling it before the await measured an empty panel.
        bool positionPopup;

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
            : Text(selected!) ?? Unresolved() ?? string.Empty;

        /// <summary>The label for a value whose row has not been loaded.</summary>
        string? Unresolved() => Value is null ? null
            : ValueText is { } text ? text(Value)
            : Convert.ToString(Value, CultureInfo.CurrentCulture);

        /// <summary>Whether the closed drop-down has anything to show.</summary>
        bool ShowsSelection => Multiple ? SelectedItems.Count > 0 : selected is not null || Value is not null;

        string? Text(TItem item) => item is null
            ? null
            : Getter(ref textProperty, ref textGetter, TextProperty) is { } get
                ? Convert.ToString(get(item), CultureInfo.CurrentCulture)
                : item.ToString();

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            // On a Data change as well as a Value change. A value is routinely bound before its rows
            // arrive - the model is known and the lookup's source is still loading - and adopting only
            // on a value change left such a drop-down showing its placeholder for good.
            var valueChanged = !valueRead || !EqualityComparer<TValue?>.Default.Equals(boundValue, Value);
            var dataChanged = !ReferenceEquals(lastData, Data);

            if (!valueChanged && !dataChanged)
            {
                return;
            }

            valueRead = true;
            boundValue = Value;
            lastData = Data;

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

            // Only a source that is already in memory. Walking an IQueryable here would run an
            // unfiltered, unpaged query on the render thread - a scan of the whole table, to render one
            // label, in the component whose whole purpose is not to read that table. Such a lookup shows
            // SelectedText until the rows it needs are loaded, and adopts them when they are.
            if (Data is IQueryable && Data is not ICollection<TItem>)
            {
                return;
            }

            if (Multiple && value is System.Collections.IEnumerable many && value is not string)
            {
                var wanted = many.Cast<object>().ToHashSet();

                foreach (var item in Data)
                {
                    if (wanted.Count == 0)
                    {
                        // Every wanted row has been found; the rest of the source is not worth walking.
                        break;
                    }

                    if (ValueOf(item) is { } candidate && wanted.Remove(candidate))
                    {
                        SelectedItems.Add(item);
                    }
                }

                return;
            }

            selected = Data.FirstOrDefault(item => Equals(ValueOf(item), value));
        }

        /// <summary>
        /// What the closed drop-down shows for a value whose row is not loaded. The value itself, which
        /// is better than a placeholder that says nothing is chosen when something is.
        /// </summary>
        [Parameter] public Func<TValue, string?>? ValueText { get; set; }

        /// <summary>
        /// The chosen values, as a list of the element type <typeparamref name="TValue" /> asks for.
        /// </summary>
        /// <remarks>
        /// A List&lt;object&gt; is not an IEnumerable&lt;int&gt;, however assignable its contents are, so
        /// binding Multiple to anything but object would have failed the cast on the first selection.
        /// </remarks>
        object? Chosen()
        {
            var elementType = MultipleElementType;

            if (elementType is null)
            {
                // TValue says nothing about a sequence - it is object, or the caller bound Multiple to a
                // scalar. A list of the chosen values is the best answer available.
                return SelectedItems.Select(ValueOf).ToList();
            }

            var typed = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(elementType))!;

            foreach (var item in SelectedItems)
            {
                typed.Add(ValueOf(item));
            }

            // The collection TValue actually names, not just a List. A List<int> is not a HashSet<int>
            // or an int[], however assignable its contents are, and casting one to the other threw on
            // the first selection.
            if (typeof(TValue).IsAssignableFrom(typed.GetType()))
            {
                return typed;
            }

            if (typeof(TValue).IsArray)
            {
                var array = Array.CreateInstance(elementType, typed.Count);

                typed.CopyTo(array, 0);

                return array;
            }

            var collection = Activator.CreateInstance<TValue>();

            if (collection is System.Collections.IList list)
            {
                foreach (var value in typed)
                {
                    list.Add(value);
                }

                return collection;
            }

            // A set, or anything else that takes its contents through Add rather than through IList.
            var add = typeof(TValue).GetMethod("Add", new[] { elementType });

            if (add is null)
            {
                return typed;
            }

            foreach (var value in typed)
            {
                add.Invoke(collection, new[] { value });
            }

            return collection;
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

        object? ValueOf(TItem item) =>
            Getter(ref valueProperty, ref valueGetter, ValueProperty) is { } get ? get(item) : item;

        Expression<Func<TItem, object?>>? textProperty;
        Func<TItem, object?>? textGetter;
        Expression<Func<TItem, object?>>? valueProperty;
        Func<TItem, object?>? valueGetter;

        /// <summary>
        /// The compiled member reader, compiled on first use and kept until the expression changes.
        /// </summary>
        /// <remarks>
        /// Equivalent rather than ReferenceEquals, as the columns do it: Razor rebuilds the expression
        /// on every render, so reference equality never holds for one written in markup and every
        /// render would recompile.
        /// </remarks>
        static Func<TItem, object?>? Getter(ref Expression<Func<TItem, object?>>? cachedExpression,
            ref Func<TItem, object?>? cached, Expression<Func<TItem, object?>>? expression)
        {
            if (expression is null)
            {
                return null;
            }

            if (cached is null || !PropertyPathResolver.Equivalent(cachedExpression, expression))
            {
                cachedExpression = expression;
                cached = expression.Compile();
            }

            return cached;
        }

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

                // Marked in the grid in single mode as well, so reopening the lookup shows what is
                // chosen - and a screen reader gets aria-selected on the row of a role=grid popup.
                SelectedItems.Clear();
                SelectedItems.Add(item);
            }

            // Published before the popup is closed: closing awaits a JavaScript call, and a circuit that
            // drops during it would take the selection with it - the label already showing the new row
            // while the bound value still held the old one.
            await Publish();
            await RowSelect.InvokeAsync(item);

            if (!Multiple)
            {
                await ClosePopup();
            }
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

        Task TogglePopup() => Open ? ClosePopup() : OpenPopup();

        /// <summary>Opens the popup.</summary>
        public Task OpenPopup()
        {
            if (Disabled || Open)
            {
                return Task.CompletedTask;
            }

            Open = true;
            built = true;

            // Positioned after the render, not here. StateHasChanged inside an event handler only
            // queues the batch - the renderer produces it when the handler yields - so calling the
            // script now would have it measure an empty panel and decide to open downwards off the
            // bottom of the window where it should have flipped up.
            positionPopup = true;

            StateHasChanged();

            return Task.CompletedTask;
        }

        /// <summary>Closes the popup.</summary>
        public async Task ClosePopup()
        {
            if (!Open)
            {
                return;
            }

            Open = false;
            positionPopup = false;

            await Interop("Radzen.closePopup", PopupId);

            StateHasChanged();
        }

        /// <inheritdoc />
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!positionPopup)
            {
                return;
            }

            positionPopup = false;

            // The reference and the callback name are what let the script tell this component that the
            // popup was dismissed by a click elsewhere on the page. Without them the panel hid and Open
            // stayed true, so the next click closed a popup the user could not see and the one after
            // reopened it - three clicks to reopen a lookup.
            reference ??= DotNetObjectReference.Create(this);

            await Interop("Radzen.openPopup", element, PopupId, true, null, null, null, reference,
                nameof(OnPopupClose));
        }

        /// <summary>Called by the popup script when the popup is dismissed from the page.</summary>
        [JSInvokable]
        public void OnPopupClose()
        {
            if (!Open)
            {
                return;
            }

            Open = false;

            StateHasChanged();
        }

        DotNetObjectReference<RadzenFastDropDownDataGrid<TItem, TValue>>? reference;

        /// <summary>
        /// A popup script call that tolerates a circuit that has already gone. Nothing here is worth
        /// taking an event handler down for: the popup it addresses is gone with the circuit.
        /// </summary>
        async Task Interop(string identifier, params object?[] args)
        {
            if (JSRuntime is null)
            {
                return;
            }

            try
            {
                await JSRuntime.InvokeVoidAsync(identifier, args);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        Task CloseOnEscape(KeyboardEventArgs args) =>
            args.Key == "Escape" ? ClosePopup() : Task.CompletedTask;

        // Read at render time, so it arms the *next* keydown - which is how RadzenDropDownDataGrid does
        // it too. Without it Space paged the document down and ArrowDown scrolled it, jumping the form
        // out from under the popup that had just opened.
        bool preventKeydown;

        Task OnKeyDown(KeyboardEventArgs args)
        {
            preventKeydown = args.Key is " " or "ArrowDown" or "ArrowUp";

            return args.Key switch
            {
                "Escape" => ClosePopup(),
                "Enter" or " " or "ArrowDown" => OpenPopup(),
                _ => Task.CompletedTask,
            };
        }

        /// <summary>
        /// Destroys the popup the script created for this drop-down. Awaited rather than abandoned: a
        /// popup left behind is a detached element the script goes on positioning.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);

            await Interop("Radzen.destroyPopup", PopupId);

            reference?.Dispose();
            reference = null;
        }
    }
}
