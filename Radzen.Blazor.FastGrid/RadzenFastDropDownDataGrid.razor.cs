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
    /// <summary>How a drop-down's popup decides how wide it is. §29.</summary>
    /// <remarks>
    /// <see cref="Content" /> is <see cref="Columns" /> with a growth step in front of it, which is why
    /// one parameter carries all three rather than a switch for growing and a second for fitting. Both
    /// of the modes that do anything imply <see cref="AutoFitMode.OnDemand" /> and
    /// <see cref="AutoFitOverflow.Fit" /> on the popup's grid, so those are not part of this component's
    /// surface: there is no way to ask for a popup that fits and a grid that does not.
    /// </remarks>
    public enum PopupFit
    {
        /// <summary>
        /// Never. The popup is as wide as <c>PopupStyle</c> and the control make it, and its columns
        /// take an equal share of that - which is what a table with <c>table-layout: fixed</c> and no
        /// declared widths does. Nothing is measured and no script is imported.
        /// </summary>
        None,

        /// <summary>
        /// The popup keeps the width it has and its columns are apportioned by what is in them. The
        /// panel does not move; what changes is where its width is spent.
        /// </summary>
        Columns,

        /// <summary>
        /// The panel grows to what the columns need - bounded by <c>PopupWidth</c> if one is declared,
        /// and by the viewport regardless - and the columns are then apportioned within whatever it
        /// landed on. The bound only bites when the content wanted more than there was room for.
        /// </summary>
        Content
    }

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

        /// <summary>How the popup decides how wide it is. §29.</summary>
        /// <remarks>
        /// <see cref="FastGrid.PopupFit.None" /> by default, so a drop-down that says nothing behaves
        /// exactly as it did before this existed.
        /// </remarks>
        [Parameter] public PopupFit PopupFit { get; set; }

        /// <summary>
        /// How wide the popup is, as authored CSS. Under <see cref="FastGrid.PopupFit.Content" /> it is
        /// the <em>cap</em> on the growth rather than the width; under the other two modes it is the
        /// width.
        /// </summary>
        /// <remarks>
        /// Not two meanings. An author who writes this with <see cref="FastGrid.PopupFit.Content" /> has
        /// said "size to the content, but never past this", which is one sentence - so both parameters
        /// stay true rather than one of them being quietly ignored.
        /// <para>
        /// Supersedes any <c>width</c> in <see cref="PopupStyle" />, and is a content width for the same
        /// reason that one would be: the panel is <c>box-sizing: content-box</c>. Its <c>min-width</c>
        /// is <em>not</em> superseded - a floor and a width are different claims, and the floors compose.
        /// The viewport caps this as it caps everything else.
        /// </para>
        /// </remarks>
        [Parameter] public string? PopupWidth { get; set; }

        /// <summary>
        /// How many data rows the popup shows before it scrolls. Zero, the default, means
        /// <see cref="PopupHeight" /> decides instead.
        /// </summary>
        /// <remarks>
        /// Requires <see cref="PopupFit" /> to be something other than
        /// <see cref="FastGrid.PopupFit.None" />, because the height is measured rather than multiplied
        /// out and it is the fit that puts the code in the browser to measure it. Documented rather than
        /// worked around, as §13 documented <see cref="AutoFitMode.OnDemand" /> needing a resize handle.
        /// <para>
        /// Not <c>PageSize</c>, which keeps its own meaning: that is how many rows the query returns and
        /// this is how many are shown. They are only conflated today because a paging popup bounds its
        /// height with nothing at all, so the page size decides it by accident.
        /// </para>
        /// </remarks>
        [Parameter] public int MaxRows { get; set; }

        /// <summary>Shown in the popup when there are no rows.</summary>
        [Parameter] public RenderFragment? EmptyTemplate { get; set; }

        /// <summary>
        /// Said in the popup when there are no rows, where <see cref="EmptyTemplate" /> says nothing.
        /// </summary>
        /// <remarks>
        /// Null forwards null, which is what leaves the inner grid on its own localized default - the
        /// same shape every other string on that grid has. It is a parameter here so that a drop-down
        /// that wants different words, or none, can say so without reaching through.
        /// </remarks>
        [Parameter] public string? EmptyText { get; set; }

        /// <summary>Whether the popup is open.</summary>
        public bool Open { get; private set; }

        ICollection<TItem>? selectedItems;

        /// <summary>
        /// The rows currently chosen - one of them unless <see cref="Multiple" /> is set. A set, not a
        /// list: the grid looks membership up once per rendered row, and its own documentation asks for
        /// one as soon as more than a handful can be chosen.
        /// </summary>
        /// <remarks>
        /// The set compares rows by the id <see cref="ValueProperty" /> reads, not by instance, and that
        /// is the whole of what §19's measured fault needed. This same collection is handed to the popup
        /// grid as its <c>Selection</c>, so one comparer fixes both halves: the tick, which is the grid
        /// asking this set whether it holds the row being drawn, and the click, where
        /// <c>Remove</c> used to miss a row carried over from a re-materialised source and add the new
        /// instance beside it - two objects, one id, and a value published twice.
        /// <para>
        /// Built on first use rather than in a field initializer, because the comparer reads
        /// <see cref="ValueOf" /> and a field initializer cannot.
        /// </para>
        /// </remarks>
        public ICollection<TItem> SelectedItems =>
            selectedItems ??= new HashSet<TItem>(new RowIdentity<TItem>(ValueOf));

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

        /// <summary>
        /// The scrolling box the rows live in. Always named; only its <em>bounds</em> are conditional.
        /// </summary>
        /// <remarks>
        /// <c>Virtualize</c> needs a bounded scrolling ancestor and a popup has none of its own;
        /// <see cref="MaxRows" /> wants to write a height onto exactly the same box. The id is one
        /// string per popup render either way, and making it conditional too would take away the only
        /// handle a test has on the case where nothing bounds it.
        /// </remarks>
        string WrapperId => Id + "-rows";

        /// <summary>
        /// Whether anything bounds that box. <see cref="MaxRows" /> counts only alongside a
        /// <see cref="PopupFit" />, because it is the fit that measures the height - so a
        /// <c>MaxRows</c> without one has to bound nothing rather than quietly clamp the popup to
        /// <see cref="PopupHeight" />, which is the very default it exists to replace.
        /// </summary>
        bool HasWrapper => AllowVirtualization || (MaxRows > 0 && PopupIsSized);

        /// <summary>
        /// What the wrapper starts at. The script replaces the height when <see cref="MaxRows" /> is
        /// set, so this is what a popup falls back to when there is no script to replace it - which
        /// matters most for the virtualizing case, where an unbounded box makes <c>Virtualize</c> size
        /// its spacers to the whole row count and run the popup off the page.
        /// </summary>
        string? WrapperStyle => HasWrapper ? $"height:{PopupHeight};overflow:auto;" : null;

        /// <summary>Whether this drop-down takes its panel's width over. One character from
        /// <see cref="PopupFit" /> is too few, and "fits" reads as a claim about the screen.</summary>
        bool PopupIsSized => PopupFit != PopupFit.None;

        /// <summary>
        /// The mode the popup's grid is given, and it is deliberately not <see cref="AutoFitMode.Once" />.
        /// <see cref="AutoFitMode.OnDemand" /> is the mode the render loop does not fire, which leaves
        /// the drop-down as the only thing that decides when a fit happens - once per open, on the path
        /// that has to measure anyway.
        /// </summary>
        AutoFitMode PopupAutoFit => PopupIsSized ? AutoFitMode.OnDemand : AutoFitMode.None;

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

        Expression<Func<TItem, object?>>? hashedBy;

        /// <summary>
        /// Re-files the chosen rows when the member naming them changes.
        /// </summary>
        /// <remarks>
        /// <see cref="SelectedItems" /> hashes a row by the id <see cref="ValueProperty" /> reads, so a
        /// change to that expression leaves every entry filed under a name it no longer answers to -
        /// <c>Remove</c> and <c>Contains</c> begin missing, which is §19's fault reached by another
        /// road. Re-filed rather than cleared: which rows are chosen has not changed, only what they
        /// are called. Equivalent rather than reference equality, since Razor rebuilds the expression
        /// on every render.
        /// </remarks>
        void Rehash()
        {
            if (PropertyPathResolver.Equivalent(hashedBy, ValueProperty))
            {
                return;
            }

            hashedBy = ValueProperty;

            if (selectedItems is not { Count: > 0 } chosen)
            {
                return;
            }

            var carried = chosen.ToList();

            chosen.Clear();

            foreach (var row in carried)
            {
                chosen.Add(row);
            }
        }

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            // Without this a validator attached to this component silently passes: ValidatorBase gates
            // its whole body on FieldIdentifier.FieldName being set, and an auto-property is never set
            // by anything. FormComponent<T> does the same assignment; this component is not one, so it
            // has to do it itself.
            if (ValueExpression is not null && FieldIdentifier.FieldName is null)
            {
                FieldIdentifier = FieldIdentifier.Create(ValueExpression);
            }

            Rehash();

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

            // A data change on its own does not need re-answering once the value has an answer. The
            // scan below is linear and its comparison goes through ValueOf, which is typed to object -
            // so every element it walks boxes, and a source written in markup is a new instance on
            // every parent render. Measured on a closed drop-down nobody has opened: 1,368 B a render
            // over fifty rows and 24,171 B over a thousand, which is 24 bytes an element and seven
            // times the whole of the rest of the render.
            //
            // The reason Adopt runs on a data change at all is untouched: a value bound before its
            // rows arrive is not explained, so this is false and the scan runs on every render until
            // the row turns up. What stops is explaining an answer already held.
            //
            // What that costs is a source swapped for a genuinely different one going on showing the
            // row it found in the old one - until the value changes, which is the only thing that
            // dislodges it. This component has no Reload of its own, so there is no second way out and
            // saying there is would be worse than saying nothing. That is the lifetime rule §10 chose
            // for the check-box lists and §14 for its lookups, and it is chosen here for the same
            // reason: the alternative is a scan per render to notice a change that almost never
            // happens.
            //
            // Not gated on the value having stayed the same, which is what this first said: a value
            // that changed cannot be explained by the row that explained the old one, so the term
            // could not be reached with a different answer. The question that decides it is the same
            // one either way - is what is held the answer to this value.
            if (StillExplains(Value))
            {
                return;
            }

            Adopt(Value);
        }

        /// <summary>
        /// Whether what is already held answers <paramref name="value" /> - so that a source arriving
        /// as a new instance over the same rows does not have to be walked to find out.
        /// </summary>
        /// <remarks>
        /// One <c>ValueOf</c> call, against a scan that makes one per element.
        /// <para>
        /// <b>Only for a single value.</b> A multiple selection is re-found every time, which is the
        /// slower answer and the only safe one: the grid draws its ticks by asking a
        /// <c>HashSet&lt;TItem&gt;</c> whether it holds the row it is drawing, and that set compares by
        /// reference - so rows kept from a source that has since re-materialised are ticks that do not
        /// appear. That is true today whether or not anything is skipped, and §19 records it as its own
        /// fault to fix; what skipping would add is that the state never recovers, because a selection
        /// that has gone wrong still explains the value and so is never re-found.
        /// </para>
        /// <para>
        /// <c>Data</c> going away is not "already explained" either. <c>Adopt</c> clears what is held
        /// before returning for a null source, and this must not skip that.
        /// </para>
        /// </remarks>
        bool StillExplains(TValue? value)
        {
            // `Multiple` is redundant today and is kept deliberately: `selected` is only ever set by
            // the scalar branch, so the test below already answers false for a multiple selection. But
            // that is an accident of which field happens to be written, and the exclusion is a
            // correctness decision - see the remark above. Leaving it to be inferred is how it would
            // quietly stop being true.
            if (Multiple || value is null || Data is null)
            {
                return false;
            }

            return selected is not null && Equals(ValueOf(selected), value);
        }

        /// <summary>
        /// Finds the rows a bound value names, so a drop-down given a value renders its text rather than
        /// its placeholder. Only what is loaded can be found: with LoadData or a database source the
        /// value's row may not be on the current page, and the drop-down then shows the placeholder
        /// until the row arrives.
        /// </summary>
        void Adopt(TValue? value)
        {
            // Kept before the clear. With LoadData, Data is one page, so a row chosen on another page
            // is not here to be found again - and rebuilding the set from this page alone dropped it,
            // which dropped it from Value the next time a choice was published. The user watched a
            // tick disappear because they turned the page.
            var carried = SelectedItems.Count > 0 ? SelectedItems.ToList() : null;

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

                // The rows already held come first, so a value the current page cannot explain keeps
                // the row that explained it before.
                if (carried is not null)
                {
                    foreach (var item in carried)
                    {
                        if (ValueOf(item) is { } held && wanted.Remove(held))
                        {
                            SelectedItems.Add(item);
                        }
                    }
                }

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

            // Before the popup is opened, not after, and that ordering is the whole of §29.
            // `Radzen.openPopup` measures the panel exactly once and decides from that single rect
            // whether to flip above the control and whether to shift left, and nothing revisits it. So
            // the panel is given its final width here and opened at that width - which is also what
            // keeps the placement upstream's: the leftward expansion for a control near the right edge
            // is its own clamp, working correctly because what it measured was final. Sizing afterwards
            // would show the panel at one width, jump it to another, and re-run the vertical flip after
            // the user had already seen it.
            var sized = PopupIsSized && grid is not null
                && await grid.FitPopupAsync(new PopupChrome(PopupId, Id, HasWrapper ? WrapperId : null,
                    PopupFit == PopupFit.Content, PopupWidth, MaxRows));

            // syncWidth is off exactly when the width is already ours. It is not off merely because the
            // mode asks for a fit: a popup whose script never loaded has been sized by nothing, and
            // must still get the width upstream would have given it rather than shrinking to fit.
            await Interop("Radzen.openPopup", element, PopupId, !sized, null, null, null, reference,
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

            // A conditionally rendered lookup otherwise stays in the form's list after it is gone, and
            // a validator looking a component up by name finds a disposed one.
            Form?.RemoveComponent(this);

            await Interop("Radzen.destroyPopup", PopupId);

            reference?.Dispose();
            reference = null;
        }
    }
}
