using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

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
        /// The text actually drawn in the header. A derived column overrides this to supply a default
        /// when <see cref="Title" /> is not set; it must not assign to the parameter itself, since a
        /// parameter written from the component keeps its assigned value on the next parameter set and
        /// the header would then go stale.
        /// </summary>
        public virtual string? HeaderText => Title;

        /// <summary>Additional CSS class for the column's cells.</summary>
        [Parameter] public string? CssClass { get; set; }

        /// <summary>Whether the column offers sorting. Ignored when the column has no sortable path.</summary>
        [Parameter] public bool Sortable { get; set; } = true;

        /// <summary>Whether the column offers filtering. Ignored when the column has no filterable path.</summary>
        [Parameter] public bool Filterable { get; set; } = true;

        /// <summary>
        /// The value this column filters by. Setting it declares the initial filter; changing it later
        /// replaces whatever the grid's own filtering put there.
        /// </summary>
        [Parameter] public object? FilterValue { get; set; }

        /// <summary>
        /// How <see cref="FilterValue" /> is compared. Defaults to <c>Contains</c> for a string column
        /// and <c>Equals</c> for every other type.
        /// </summary>
        [Parameter] public FilterOperator? FilterOperator { get; set; }

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
        [Parameter] public IEnumerable? FilterLookupData { get; set; }

        /// <summary>
        /// The distinct values of this column across <paramref name="source" />, for a check-box-list
        /// filter. Composed as a query rather than materialized, so a provider can translate it.
        /// </summary>
        public virtual IQueryable? DistinctValues(IQueryable<TItem> source) => null;

        /// <summary>
        /// Replaces the built-in filter input for this column. The built-in one is a text box and
        /// nothing more - no operator menu, no date popup, no numeric range - so anything richer, and
        /// anything a computed column needs, goes here.
        /// </summary>
        [Parameter] public RenderFragment<ColumnBase<TItem>>? FilterTemplate { get; set; }

        object? declaredFilterValue;
        FilterOperator? declaredFilterOperator;

        /// <summary>The value the column is filtering by right now.</summary>
        public object? CurrentFilterValue { get; private set; }

        /// <summary>The operator the column is filtering with right now.</summary>
        public FilterOperator CurrentFilterOperator { get; private set; }

        /// <summary>
        /// The dotted path this column filters by. Defaults to <see cref="PropertyPath" />; a column with
        /// no path cannot be filtered, for the same reason it cannot be sorted.
        /// </summary>
        public virtual string? FilterPropertyPath => PropertyPath;

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

                // Reached only from the filter row and the filter callbacks, never per row or per cell,
                // so it is resolved on demand rather than cached behind an invalidation rule.
                return FilterPropertyPath is { } path
                    ? PropertyPathResolver.TypeOf(typeof(TItem), path) ?? typeof(object)
                    : typeof(object);
            }
        }

        /// <summary>Whether this column can be filtered.</summary>
        public virtual bool CanFilter => Filterable && FilterPropertyPath is not null;

        /// <summary>
        /// Whether the column's current filter would actually narrow anything. An empty value filters
        /// nothing, except for the operators that are about emptiness themselves.
        /// </summary>
        public bool HasFilter =>
            CanFilter &&
            (HasFilterValue
                || CurrentFilterOperator is Radzen.FilterOperator.IsNull or Radzen.FilterOperator.IsNotNull
                    or Radzen.FilterOperator.IsEmpty or Radzen.FilterOperator.IsNotEmpty);

        bool HasFilterValue => CurrentFilterValue switch
        {
            null => false,
            string text => text.Length > 0,

            // A check-box-list filter with nothing ticked is not a filter that matches nothing; it is no
            // filter. Testing for null only would leave the grid empty as soon as the last box is cleared.
            // The selection is a list in every path the grid itself builds, so the count answers without
            // an enumerator; the general case still has to walk one, and has to dispose it.
            ICollection collection => collection.Count > 0,
            IEnumerable sequence => Any(sequence),
            _ => true,
        };

        static bool Any(IEnumerable sequence)
        {
            var enumerator = sequence.GetEnumerator();

            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        /// <summary>Sets the column's live filter. Called by the grid; does not reload on its own.</summary>
        internal void SetFilter(object? value, FilterOperator? filterOperator)
        {
            CurrentFilterValue = value;
            CurrentFilterOperator = filterOperator ?? DefaultFilterOperator;
        }

        FilterOperator DefaultFilterOperator => EffectiveFilterType == typeof(string)
            ? Radzen.FilterOperator.Contains
            : Radzen.FilterOperator.Equals;

        bool filterInitialized;

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (!filterInitialized)
            {
                // Both parameters may legitimately be null, so the first pass cannot be told from a
                // no-op by comparing them; it has to be marked.
                filterInitialized = true;
                declaredFilterValue = FilterValue;
                declaredFilterOperator = FilterOperator;
                CurrentFilterValue = FilterValue;
                CurrentFilterOperator = FilterOperator ?? DefaultFilterOperator;

                return;
            }

            // The declared value is the authority whenever it changes, and the grid's own filtering owns
            // it in between. Tracking what was declared separately keeps this out of the parameter
            // itself, which a component must not assign to.
            if (!Equals(declaredFilterValue, FilterValue))
            {
                declaredFilterValue = FilterValue;
                CurrentFilterValue = FilterValue;
            }

            if (declaredFilterOperator != FilterOperator)
            {
                declaredFilterOperator = FilterOperator;
                CurrentFilterOperator = FilterOperator ?? DefaultFilterOperator;
            }
        }

        /// <summary>
        /// The dotted property path this column sorts, filters and persists by, or <c>null</c> when the
        /// authored expression is computed rather than a simple member access.
        /// </summary>
        public virtual string? PropertyPath => null;

        /// <summary>Whether this column can be sorted. False for a computed column with no explicit sort.</summary>
        public virtual bool CanSort => Sortable && PropertyPath is not null;

        /// <summary>Writes one cell for <paramref name="item" /> into <paramref name="builder" />.</summary>
        public abstract void RenderCell(RenderTreeBuilder builder, int sequence, TItem item);

        /// <summary>
        /// Applies this column's ordering to <paramref name="source" />. Overridden by columns that know
        /// their property type, so the ordering is a typed expression the provider can translate rather
        /// than a parsed string.
        /// </summary>
        public virtual IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending) => null;

        /// <inheritdoc />
        public override Task SetParametersAsync(ParameterView parameters)
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
