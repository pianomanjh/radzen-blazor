using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Radzen;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    // The data path: paging, the LoadData escape hatch and asynchronous execution.
    //
    // Rule 3 of the spec governs everything here - a grid that pages over an in-memory list must not pay
    // for the existence of LoadData, OData or an async provider. So each of those is behind a test that
    // is false by construction for the common case: LoadData.HasDelegate, `Data is IQueryable<TItem>`,
    // `Data is ODataEnumerable<TItem>`. Nothing is materialized, counted or string-formatted unless one
    // of them is true.
    public partial class RadzenFastGrid<TItem> : IDisposable
    {
        [Inject]
        private IServiceProvider? Services { get; set; }

        /// <summary>Whether the grid pages its data and shows a pager.</summary>
        [Parameter] public bool AllowPaging { get; set; }

        /// <summary>Rows per page. Ignored unless <see cref="AllowPaging" /> is set.</summary>
        [Parameter] public int PageSize { get; set; } = 10;

        /// <summary>Raised when the page size changes through the pager.</summary>
        [Parameter] public EventCallback<int> PageSizeChanged { get; set; }

        /// <summary>Page sizes offered by the pager. No dropdown is shown when this is null.</summary>
        [Parameter] public IEnumerable<int>? PageSizeOptions { get; set; }

        /// <summary>How many numbered page buttons the pager shows.</summary>
        [Parameter] public int PageNumbersCount { get; set; } = 5;

        /// <summary>Where the pager appears.</summary>
        [Parameter] public PagerPosition PagerPosition { get; set; } = PagerPosition.Bottom;

        /// <summary>Horizontal alignment of the pager.</summary>
        [Parameter] public HorizontalAlign PagerHorizontalAlign { get; set; } = HorizontalAlign.Justify;

        /// <summary>Whether the pager shows its "page x of y" summary.</summary>
        [Parameter] public bool ShowPagingSummary { get; set; }

        /// <summary>Whether the pager stays visible when there is only one page.</summary>
        [Parameter] public bool PagerAlwaysVisible { get; set; }

        /// <summary>
        /// Total number of rows, for the pager. Only read on the <see cref="LoadData" /> path, where the
        /// grid sees one page at a time and cannot work the total out for itself.
        /// </summary>
        [Parameter] public int Count { get; set; }

        /// <summary>
        /// Called when the grid needs data, with the current skip, top and order-by. Supply this for a
        /// source the grid cannot compose over - REST, OData, gRPC, a stored procedure - and set
        /// <see cref="Data" /> and <see cref="Count" /> from the handler. With a composable
        /// <see cref="IQueryable{T}" /> you do not need it; see the package README.
        /// </summary>
        [Parameter] public EventCallback<LoadDataArgs> LoadData { get; set; }

        /// <summary>Whether column filters are applied. Columns still carry their filters when off.</summary>
        [Parameter] public bool AllowFiltering { get; set; }

        /// <summary>How string comparisons treat case. The provider decides by default.</summary>
        [Parameter] public FilterCaseSensitivity FilterCaseSensitivity { get; set; }

        /// <summary>
        /// How filters are presented. <c>Simple</c> is a text box per column; <c>CheckBoxList</c> is a
        /// multi-select of the column's distinct values. A column can override it.
        /// </summary>
        [Parameter] public FilterMode FilterMode { get; set; } = FilterMode.Simple;

        /// <summary>How the columns' filters combine.</summary>
        [Parameter] public LogicalFilterOperator LogicalFilterOperator { get; set; } = LogicalFilterOperator.And;

        /// <summary>
        /// Whether only the visible rows are rendered. Virtualization and paging solve the same problem
        /// and this one wins: with it on, <see cref="AllowPaging" /> is ignored and no pager is drawn.
        /// The grid needs a scrolling ancestor with a bounded height for it to do anything.
        /// </summary>
        [Parameter] public bool AllowVirtualization { get; set; }

        /// <summary>
        /// The row height virtualization assumes, in pixels, for sizing the spacers. The default is the
        /// height the Radzen themes actually render a row at, measured.
        /// </summary>
        [Parameter] public float ItemSize { get; set; } = 37;

        /// <summary>How many rows beyond the viewport to render. Zero leaves Virtualize's own default.</summary>
        [Parameter] public int VirtualizationOverscanCount { get; set; }

        Virtualize<TItem>? virtualize;

        // The total behind the scrollbar. Scrolling does not change it - only new data, a new filter or
        // a reload does - so it is counted once per query rather than once per window. Without this an
        // endless scroll runs a COUNT(*) for every window it fetches.
        int? virtualTotal;

        /// <summary>
        /// Whether the grid is actually paging. One rule, in one place: virtualization and paging solve
        /// the same problem, and reading AllowPaging directly anywhere else lets the two disagree - a
        /// pager under a virtualized body, or a window taken from within a page.
        /// </summary>
        internal bool Paging => AllowPaging && !AllowVirtualization;

        /// <summary>The underlying Virtualize component, or null when virtualization is off.</summary>
        public Virtualize<TItem>? Virtualize => AllowVirtualization ? virtualize : null;

        /// <summary>Whether a load is in flight. Only ever true on an asynchronous path.</summary>
        public bool IsLoading { get; private set; }

        /// <summary>The zero-based current page.</summary>
        public int CurrentPage => pageSize > 0 ? skip / pageSize : 0;

        // Rows a load produced. Null means nothing has been loaded and the view composes over Data
        // directly - the zero-allocation path, and the one every in-memory grid stays on.
        IReadOnlyList<TItem>? loaded;

        int? loadedCount;
        int skip;
        int pageSize;
        int declaredPageSize;
        bool initialized;
        bool loadDataInvoked;
        IEnumerable<TItem>? lastData;

        bool executorResolved;
        IAsyncQueryExecutor? executor;
        CancellationTokenSource? loadCts;

        object? isODataFor;
        bool isOData;

        IAsyncQueryExecutor? Executor
        {
            get
            {
                if (!executorResolved)
                {
                    executorResolved = true;
                    executor = Services?.GetService(typeof(IAsyncQueryExecutor)) as IAsyncQueryExecutor;
                }

                return executor;
            }
        }

        /// <inheritdoc />
        protected override Task OnParametersSetAsync()
        {
            var pagingChanged = false;

            if (!initialized)
            {
                initialized = true;
                declaredPageSize = PageSize;
                pageSize = PageSize;
            }
            else if (declaredPageSize != PageSize)
            {
                // The page size was changed from the outside rather than through the pager, so the
                // current offset means something different now. Start again from the first page - and
                // refetch, because the pager raised no event and both branches below short-circuit on
                // state that has not changed. Without this the grid served ten rows on a page of
                // twenty-five, and the pager counted pages nobody could reach.
                declaredPageSize = PageSize;
                pageSize = PageSize;
                skip = 0;
                pagingChanged = true;
            }

            var dataChanged = !ReferenceEquals(lastData, Data);

            // Before the LoadData branch, not after: a LoadData grid replaces Data on every load, and a
            // check-box list built from page one is wrong for every page after it.
            if (dataChanged)
            {
                lookups.Clear();
            }

            if (LoadData.HasDelegate)
            {
                // The handler assigns Data, which sets parameters again. Load once here and thereafter
                // only when something the handler cares about changes - a sort, a page, or Reload().
                if (loadDataInvoked)
                {
                    return pagingChanged ? RefreshAsync() : Task.CompletedTask;
                }

                loadDataInvoked = true;

                // While virtualizing the provider owns fetching, and it asks for a window. Loading here
                // as well would call the handler once with no window at all and throw the answer away.
                return AllowVirtualization ? Task.CompletedTask : InvokeLoadDataAsync();
            }

            if (!dataChanged && !pagingChanged)
            {
                return Task.CompletedTask;
            }

            lastData = Data;

            // Drop what any previous load produced. Deliberately not RefreshAsync on the ordinary path:
            // that renders, and the render ComponentBase queues after this returns would then be the
            // second of two - a whole extra pass over every row, measured at +94% allocation.
            loaded = null;
            loadedCount = null;

            // Virtualizing is the exception, and has to be: Virtualize is still holding the window it
            // fetched from the old source, and nothing else will ask it for another.
            return AllowVirtualization ? RefreshAsync() : BeginAsyncLoad() ?? Task.CompletedTask;
        }

        /// <summary>
        /// Re-reads the data for the current page and sort. Call this after the underlying source has
        /// changed in a way the grid cannot see - the usual companion to <see cref="LoadData" />.
        /// </summary>
        public Task Reload()
        {
            // The source may have changed in ways the grid cannot see - which is what this is for -
            // including gaining values a check-box list should now offer. A sort or a filter cannot
            // change them, so only this drops them.
            lookups.Clear();

            return RefreshAsync();
        }

        /// <summary>
        /// Serves one scroll window. The whole data path funnels through here when virtualizing: the
        /// LoadData handler is asked for the window, a supported queryable is counted and materialized
        /// asynchronously, and anything else is composed in memory.
        /// </summary>
        async ValueTask<ItemsProviderResult<TItem>> ProvideRows(ItemsProviderRequest request)
        {
            var top = request.Count > 0 ? request.Count : PageSize;

            if (LoadData.HasDelegate)
            {
                await InvokeLoadDataAsync(request.StartIndex, top);

                return new ItemsProviderResult<TItem>(Data ?? Enumerable.Empty<TItem>(), Count);
            }

            if (TryGetAsyncSource(out var async, out var queryable))
            {
                var source = (IQueryable<TItem>)Composed(queryable);

                try
                {
                    // request.CancellationToken already covers a superseded scroll, so a cancelled
                    // window propagates out to Virtualize rather than being swallowed here.
                    var window = await async.ToListAsync(source.Skip(request.StartIndex).Take(top),
                        request.CancellationToken);

                    if (virtualTotal is null)
                    {
                        virtualTotal = await async.CountAsync(source, request.CancellationToken);
                    }

                    return new ItemsProviderResult<TItem>(window, virtualTotal.Value);
                }
                catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
                {
                    // Only a superseded scroll is an empty answer. A cancellation carrying any other
                    // token - a disposed context, a command timeout, application shutdown - is a
                    // failure, and Virtualize would apply this as a real result: a grid with no rows,
                    // no scrollbar and no error.
                    return new ItemsProviderResult<TItem>(Array.Empty<TItem>(), 0);
                }
            }

            var rows = Composed(Data ?? Enumerable.Empty<TItem>());

            // Same reason as the query above: counting a filtered in-memory sequence walks it, and
            // scrolling must not walk the whole source once per window.
            virtualTotal ??= TotalCount();

            // Materialized, not handed over lazily: Virtualize keeps the result and re-enumerates it on
            // every render, so a deferred filter-and-sort would be re-run over the whole source each
            // time rather than over the window.
            return new ItemsProviderResult<TItem>(Page(rows, request.StartIndex, top).ToList(),
                virtualTotal.Value);
        }

        /// <summary>
        /// Filters a column by a value, and reloads. Passing null for the operator restores the column's
        /// default - Contains for a string column, Equals otherwise.
        /// </summary>
        public Task Filter(ColumnBase<TItem> column, object? value, FilterOperator? filterOperator = null)
        {
            if (column is null || !column.CanFilter)
            {
                return Task.CompletedTask;
            }

            column.SetFilter(value, filterOperator);

            // A narrower set has different pages; the row that was on page 3 may not exist any more.
            skip = 0;

            return RefreshAsync();
        }

        /// <summary>
        /// Applies what was typed into a column's filter box. The text is converted to the column's
        /// property type; text that is not a value of that type filters nothing rather than throwing,
        /// which is what a half-typed date or number looks like.
        /// </summary>
        Task OnFilterInput(ColumnBase<TItem> column, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Filter(column, null);
            }

            // The element type, not the property type: a filter on a list of dates is compared against a
            // date, and a conversion would have no idea what to do with the list.
            var declared = column.EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            if (type == typeof(string) || type == typeof(object))
            {
                return Filter(column, text);
            }

            try
            {
                // ConvertType rather than Convert.ChangeType, and Enum.Parse rather than either: neither
                // an enum nor a Guid converts from a string through IConvertible, so the framework call
                // throws for both and what was typed silently cleared the filter instead of applying it.
                return Filter(column, type.IsEnum
                    ? Enum.Parse(type, text, ignoreCase: true)
                    : ConvertType.ChangeType(text, declared, CultureInfo.CurrentCulture));
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException
                or ArgumentException)
            {
                return Filter(column, null);
            }
        }

        /// <summary>The filter presentation this column actually uses.</summary>
        internal FilterMode FilterModeOf(ColumnBase<TItem> column) => column.FilterMode ?? FilterMode;

        readonly Dictionary<ColumnBase<TItem>, IEnumerable> lookups = new();

        /// <summary>
        /// The values a column's check-box-list offers. Cached per column and dropped whenever the data
        /// changes, so the distinct query runs once rather than on every render of the filter row.
        /// </summary>
        internal IEnumerable FilterLookup(ColumnBase<TItem> column)
        {
            if (column.FilterLookupData is { } supplied)
            {
                return supplied;
            }

            if (lookups.TryGetValue(column, out var cached))
            {
                return cached;
            }

            // The same rule View() and TotalCount() follow: a source the executor owns is not touched
            // from the render thread. Running the distinct query here is a blocking round trip inside
            // BuildRenderTree, and on Entity Framework a second operation on a context that the awaited
            // page load is still using. The values are fetched after the render instead, and the column
            // offers nothing until they arrive.
            if (AsyncOwnsData)
            {
                pendingLookups.Add(column);

                return Array.Empty<object>();
            }

            var source = Data as IQueryable<TItem> ?? Data?.AsQueryable();
            var values = source is null ? null : column.DistinctValues(source);

            // Note the cast to IEnumerable before Cast<object>. On an IQueryable that overload resolves
            // to Queryable.Cast, which composes a Cast node into the provider's own tree - Entity
            // Framework then refuses to translate it ("expression of type SingleQueryingEnumerable<T>
            // cannot be used for return type IEnumerable<object>"). Enumerating first runs the distinct
            // query and boxes the answers in memory, which is where the boxing belongs.
            var materialized = values is null
                ? (IEnumerable)Array.Empty<object>()
                : Ordered(((IEnumerable)values).Cast<object>().Where(v => v != null).ToList());

            lookups[column] = materialized;

            return materialized;
        }

        readonly HashSet<ColumnBase<TItem>> pendingLookups = new();

        /// <summary>
        /// Fetches the check-box-list values of any column that asked for them during the render, using
        /// the executor rather than the render thread. Runs after the render, so the queries it starts
        /// cannot overlap the page load that the same render was drawn without.
        /// </summary>
        async Task LoadLookupsAsync()
        {
            if (pendingLookups.Count == 0 || !TryGetAsyncSource(out var async, out var queryable))
            {
                return;
            }

            var wanted = pendingLookups.ToList();

            pendingLookups.Clear();

            var token = loadCts?.Token ?? CancellationToken.None;
            var loaded = false;

            foreach (var column in wanted)
            {
                if (lookups.ContainsKey(column) || column.DistinctValues(queryable) is not { } values)
                {
                    continue;
                }

                try
                {
                    lookups[column] = Ordered(await ToObjectListAsync(async, values, token));
                    loaded = true;
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer load, which will ask again on its own render.
                    return;
                }
            }

            if (loaded)
            {
                StateHasChanged();
            }
        }

        static readonly MethodInfo ToObjectListMethod = typeof(RadzenFastGrid<TItem>)
            .GetMethod(nameof(ToObjectListOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>
        /// Awaits a distinct query whose element type is only known at run time. The executor's
        /// ToListAsync is generic, and the values are boxed after it returns rather than by composing a
        /// Cast into the provider's tree - which is what Entity Framework refuses to translate.
        /// </summary>
        static Task<List<object>> ToObjectListAsync(IAsyncQueryExecutor async, IQueryable values,
            CancellationToken token) =>
            (Task<List<object>>)ToObjectListMethod
                .MakeGenericMethod(values.ElementType)
                .Invoke(null, new object[] { async, values, token })!;

        static async Task<List<object>> ToObjectListOfAsync<TValue>(IAsyncQueryExecutor async,
            IQueryable values, CancellationToken token)
        {
            var items = await async.ToListAsync((IQueryable<TValue>)values, token);
            var boxed = new List<object>(items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is { } value)
                {
                    boxed.Add(value);
                }
            }

            return boxed;
        }

        internal RadzenPager? topPager;
        internal RadzenPager? bottomPager;

        /// <summary>
        /// Puts the pager back on the page the grid is actually showing. RadzenPager keeps its own
        /// offset and has no CurrentPage parameter to be told through, so every path that sends the grid
        /// back to page one - a sort, a filter, ClearFilters, ApplyFilters, GoToPage - left the pager
        /// highlighting the old page and paging onward from it.
        /// </summary>
        void SyncPagers()
        {
            var page = pageSize > 0 ? skip / pageSize : 0;

            SyncPager(topPager, page);
            SyncPager(bottomPager, page);
        }

        static void SyncPager(RadzenPager? pager, int page)
        {
            if (pager is null || pager.CurrentPage == page)
            {
                return;
            }

            pager.SetCurrentPage(page);
            pager.ChangeState();
        }

        /// <summary>
        /// Brings the offset back into range when the source has shrunk under it, so a grid parked on
        /// page five of a list that now has one page shows that page rather than nothing at all.
        /// </summary>
        bool ClampPage()
        {
            if (!Paging || pageSize <= 0 || skip == 0)
            {
                return false;
            }

            // Nothing has loaded yet, so the total the grid can see is a placeholder rather than a
            // shorter source. Clamping to it would send every asynchronous grid back to page one.
            if (AsyncOwnsData && loadedCount is null)
            {
                return false;
            }

            var total = TotalCount();
            var last = total == 0 ? 0 : (total - 1) / pageSize * pageSize;

            if (skip <= last)
            {
                return false;
            }

            skip = last;

            return true;
        }

        /// <inheritdoc />
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (ClampPage())
            {
                await RefreshAsync();
            }

            SyncPagers();

            await LoadLookupsAsync();
        }

        /// <summary>
        /// Sorts lookup values when they can be sorted. Comparer&lt;object&gt;.Default throws for a type
        /// that is not IComparable, which a collection of entities with no display member is - and that
        /// took down the grid's first render rather than merely leaving the list unsorted.
        /// </summary>
        static List<object> Ordered(List<object> values)
        {
            if (values.Count < 2 || values[0] is not IComparable)
            {
                return values;
            }

            try
            {
                values.Sort();
            }
            catch (InvalidOperationException)
            {
                // The first value being comparable says nothing about the rest. A column declared as
                // object can hold 1 and "n/a" at once, and Int32.CompareTo(object) throws - wrapped by
                // List.Sort as "failed to compare two elements", out of the middle of a render. An
                // unsorted list is a worse list, not a broken grid.
            }

            return values;
        }

        /// <summary>Applies a check-box-list selection. Nothing ticked is no filter, not an empty result.</summary>
        Task OnFilterSelection(ColumnBase<TItem> column, object? value)
        {
            if (value is not IEnumerable sequence || value is string)
            {
                return Filter(column, null, Radzen.FilterOperator.In);
            }

            // Typed as the column's element type, not List<object>: the predicate becomes
            // Contains<TElement>(selected, x), and a List<object> there is not an IEnumerable<TElement>,
            // so a provider cannot translate it and the comparison never binds.
            var type = Nullable.GetUnderlyingType(column.EffectiveFilterType) ?? column.EffectiveFilterType;
            var selected = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;

            foreach (var item in sequence)
            {
                selected.Add(item);
            }

            // An empty list is passed through rather than turned into null here: HasFilter is the single
            // rule for what counts as a filter, and it already treats an empty sequence as none.
            return Filter(column, selected, Radzen.FilterOperator.In);
        }

        /// <summary>Clears every column's filter, and reloads.</summary>
        public Task ClearFilters()
        {
            var cleared = false;

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].HasFilter)
                {
                    columns[i].SetFilter(null, null);
                    cleared = true;
                }
            }

            if (!cleared)
            {
                return Task.CompletedTask;
            }

            skip = 0;

            return RefreshAsync();
        }

        /// <summary>
        /// The filters the columns currently carry, in the descriptor form the rest of Radzen speaks -
        /// what `RadzenDataFilter` emits and what a `LoadData` handler receives. Empty when nothing is
        /// filtered, and never built unless something asks.
        /// </summary>
        public IReadOnlyList<FilterDescriptor> Filters => BuildFilters() ?? (IReadOnlyList<FilterDescriptor>)Array.Empty<FilterDescriptor>();

        /// <summary>
        /// Applies a set of descriptors to the columns they name, so a `RadzenDataFilter` or restored
        /// settings can drive the grid. Descriptors naming no column are ignored.
        /// </summary>
        public Task ApplyFilters(IEnumerable<FilterDescriptor> filters)
        {
            ArgumentNullException.ThrowIfNull(filters);

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].SetFilter(null, null);
            }

            foreach (var filter in filters)
            {
                var column = ColumnByFilterPath(filter.Property);

                column?.SetFilter(filter.FilterValue, filter.FilterOperator);
            }

            skip = 0;

            return RefreshAsync();
        }

        ColumnBase<TItem>? ColumnByFilterPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].CanFilter && string.Equals(columns[i].FilterPropertyPath, path, StringComparison.Ordinal))
                {
                    return columns[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The columns' filters as descriptors, or null when nothing is filtered - the common case, and
        /// the one that must allocate nothing.
        /// </summary>
        List<FilterDescriptor>? BuildFilters()
        {
            List<FilterDescriptor>? filters = null;

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.HasFilter)
                {
                    continue;
                }

                (filters ??= new List<FilterDescriptor>()).Add(new FilterDescriptor
                {
                    Property = column.FilterPropertyPath,

                    // Names a member of the collection's element, so the predicate becomes
                    // Customers.Any(c => c.Name ...) rather than a comparison against the collection.
                    FilterProperty = column.FilterMemberPath,
                    FilterValue = column.CurrentFilterValue,
                    FilterOperator = column.CurrentFilterOperator,
                    Type = column.FilterPropertyType,
                });
            }

            return filters;
        }

        /// <summary>Composes the columns' filters onto a queryable. Untouched when nothing is filtered.</summary>
        IQueryable<TItem> ApplyFilters(IQueryable<TItem> source)
        {
            var filters = ActiveFilters();

            // QueryableExtension builds a typed expression tree from the descriptors - the same one
            // RadzenDataGrid composes - so this still translates to SQL rather than parsing a string.
            return filters is null ? source : source.Where(filters, LogicalFilterOperator, FilterCaseSensitivity);
        }

        // Drawing the table asks what is filtered more than once: the pager counts, the body enumerates,
        // and a grid with a pager above and below counts twice. None of it can change while the table is
        // being written, and rebuilding the descriptors means rebuilding the filter expression tree with
        // them - so both are computed once for the render and dropped again after. A cache that outlived
        // the render would have to be invalidated by every path that touches a filter.
        bool drawing;
        List<FilterDescriptor>? drawingFilters;
        IEnumerable<TItem>? drawingComposed;
        IEnumerable<TItem>? drawingComposedOf;

        List<FilterDescriptor>? ActiveFilters() =>
            drawing ? drawingFilters : AllowFiltering ? BuildFilters() : null;

        void BeginDrawing()
        {
            drawingFilters = AllowFiltering ? BuildFilters() : null;
            drawingComposed = null;
            drawingComposedOf = null;
            drawing = true;
        }

        void EndDrawing()
        {
            drawing = false;
            drawingFilters = null;
            drawingComposed = null;
            drawingComposedOf = null;
        }

        /// <summary>Moves to a zero-based page and reloads.</summary>
        public Task GoToPage(int page)
        {
            skip = Math.Max(0, page) * pageSize;

            return RefreshAsync();
        }

        Task RefreshAsync()
        {
            if (AllowVirtualization)
            {
                // A new filter changes how many rows there are, so the cached total goes with it.
                virtualTotal = null;

                // Virtualize holds its own copy of the window, so a sort or filter that only re-renders
                // redraws the same rows: the refetch is what makes the provider compose the new query.
                return virtualize is null ? Task.CompletedTask : RefreshVirtualizedAsync();
            }

            if (LoadData.HasDelegate)
            {
                return InvokeLoadDataAsync();
            }

            var load = BeginAsyncLoad();

            if (load is not null)
            {
                return load;
            }

            // Nothing to load: the view composes over Data as it is drawn. Dropping what a previous
            // asynchronous load produced is all that is needed.
            loaded = null;
            loadedCount = null;

            StateHasChanged();

            return Task.CompletedTask;
        }

        /// <summary>
        /// Whether the asynchronous path owns this source - and if so, what will execute it. Cheap for an
        /// in-memory source: it is not an IQueryable, so the first test short-circuits before the
        /// executor is even resolved.
        /// </summary>
        bool TryGetAsyncSource([NotNullWhen(true)] out IAsyncQueryExecutor? executor,
            [NotNullWhen(true)] out IQueryable<TItem>? source)
        {
            if (Data is IQueryable<TItem> queryable && Executor is { } async && async.IsSupported(queryable))
            {
                executor = async;
                source = queryable;

                return true;
            }

            executor = null;
            source = null;

            return false;
        }

        /// <summary>
        /// Whether touching the source from the render thread would run a query.
        /// </summary>
        bool AsyncOwnsData => TryGetAsyncSource(out _, out _);

        /// <summary>
        /// Starts an asynchronous load, or returns null when this source cannot be executed that way -
        /// which is every in-memory source, and every queryable when no executor is registered.
        /// </summary>
        Task? BeginAsyncLoad() =>
            !AllowVirtualization && TryGetAsyncSource(out var async, out var queryable)
                ? LoadPageAsync(async, queryable)
                : null;

        async Task RefreshVirtualizedAsync()
        {
            await virtualize!.RefreshDataAsync();

            // The refetch updates Virtualize's own state but leaves the render it queues to whatever
            // happens next. A sort or filter re-renders anyway; Reload called from application code does
            // not, and without this the new rows sit in the component and never reach the screen.
            StateHasChanged();
        }

        async Task LoadPageAsync(IAsyncQueryExecutor async, IQueryable<TItem> source)
        {
            var token = BeginLoad();
            var filtered = ApplyFilters(source);
            var ordered = SortColumn?.ApplySort(filtered, SortDescending) ?? filtered;
            var paged = Paging ? ordered.Skip(skip).Take(pageSize) : ordered;

            IsLoading = true;
            StateHasChanged();

            try
            {
                var items = await async.ToListAsync(paged, token);

                // A page is a subset, so its length says nothing about the total and the total costs a
                // second round trip. An unpaged query is the whole set, so the list already is the count.
                // The count is of the filtered set, not the source: the pager counts what is on screen.
                var count = Paging ? await async.CountAsync(filtered, token) : items.Count;

                if (token.IsCancellationRequested)
                {
                    return;
                }

                loaded = items;
                loadedCount = count;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer load, which owns the outcome.
                return;
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsLoading = false;
                }
            }

            StateHasChanged();
        }

        Task InvokeLoadDataAsync() => Paging
            ? InvokeLoadDataAsync(skip, pageSize)
            : InvokeLoadDataAsync(null, null);

        async Task InvokeLoadDataAsync(int? start, int? count)
        {
            var args = new LoadDataArgs
            {
                Skip = start,
                Top = count,
                OrderBy = OrderBy(),
                Filters = BuildFilters(),
            };

            args.Filter = FilterString(args.Filters);

            IsLoading = true;

            try
            {
                await LoadData.InvokeAsync(args);
            }
            finally
            {
                IsLoading = false;
            }

            // The handler assigned Data; the grid renders it verbatim, already sorted and paged.
            loaded = null;
            loadedCount = null;
            lastData = Data;

            StateHasChanged();
        }

        CancellationToken BeginLoad()
        {
            var previous = loadCts;
            var current = new CancellationTokenSource();

            // Publish before cancelling: cancellation runs callbacks synchronously, and one that starts
            // another load would otherwise have its source overwritten when this frame resumes.
            loadCts = current;
            previous?.Cancel();

            return current.Token;
        }

        /// <summary>
        /// The sort expression in the string form `LoadData` and OData consume, or null when nothing is
        /// sorted. Built only for a `LoadData` handler - a grid composing over a queryable sorts with the
        /// column's own typed expression and never needs the string.
        /// </summary>
        string? OrderBy()
        {
            if (SortColumn?.PropertyPath is not { Length: > 0 } path)
            {
                return null;
            }

            var property = IsOData() ? path.Replace('.', '/') : path;

            return SortDescending ? property + " desc" : property + " asc";
        }

        /// <summary>
        /// The filter in the string form `LoadData` and OData consume. Built only for a `LoadData`
        /// handler; a grid composing over a queryable filters with the descriptors themselves.
        /// </summary>
        string? FilterString(IEnumerable<FilterDescriptor>? filters)
        {
            if (filters is null)
            {
                return null;
            }

            var composites = filters.Select(f => new CompositeFilterDescriptor
            {
                Property = f.Property,
                FilterValue = f.FilterValue,
                FilterOperator = f.FilterOperator,
                Type = f.Type,
            }).ToList();

            var text = IsOData()
                ? composites.ToODataFilterString<TItem>(LogicalFilterOperator, FilterCaseSensitivity)
                : composites.ToFilterString<TItem>(LogicalFilterOperator, FilterCaseSensitivity);

            return string.IsNullOrEmpty(text) ? null : text;
        }

        bool IsOData()
        {
            // Keyed on the instance, not computed once: on the LoadData path Data is replaced by every
            // load, and the first call may well see the empty placeholder the page started with.
            if (!ReferenceEquals(isODataFor, Data))
            {
                isODataFor = Data;
                isOData = Data is ODataEnumerable<TItem>;
            }

            return isOData;
        }

        IEnumerable<TItem> View()
        {
            if (loaded is not null)
            {
                return loaded;
            }

            // Nothing has loaded yet and the query belongs to the executor. Composing over it here
            // enumerates it on the render thread - a whole unpaged table pulled synchronously, for rows
            // the awaited load is about to replace.
            if (AsyncOwnsData)
            {
                return Array.Empty<TItem>();
            }

            var data = Data ?? Enumerable.Empty<TItem>();

            if (LoadData.HasDelegate)
            {
                // The handler sorted and paged already; sorting or paging it again would be wrong.
                return data;
            }

            data = Composed(data);

            return Paging ? Page(data, skip, pageSize) : data;
        }

        /// <summary>
        /// One page of a sequence. Composed onto the provider when the source is a queryable, so a
        /// database source is asked for the page - the alternative is streaming every filtered row
        /// across the wire and skipping to page three in memory. Filtering and sorting already compose;
        /// paging binding LINQ to Objects made them the only two that did.
        /// </summary>
        static IEnumerable<TItem> Page(IEnumerable<TItem> data, int start, int count) =>
            data is IQueryable<TItem> queryable
                ? queryable.Skip(start).Take(count)
                : data.Skip(start).Take(count);

        /// <summary>The same rule for counting: a provider answers with COUNT rather than a scan.</summary>
        static int Total(IEnumerable<TItem> data) =>
            data is IQueryable<TItem> queryable ? queryable.Count() : data.Count();

        /// <summary>
        /// Filters and sorts, without paging. Nothing is wrapped in a queryable unless something is
        /// actually filtered or sorted, so an unfiltered, unsorted grid enumerates its source directly.
        /// </summary>
        IEnumerable<TItem> Composed(IEnumerable<TItem> data)
        {
            if (drawing && ReferenceEquals(drawingComposedOf, data))
            {
                return drawingComposed!;
            }

            var composed = Compose(data);

            if (drawing)
            {
                drawingComposedOf = data;
                drawingComposed = composed;
            }

            return composed;
        }

        IEnumerable<TItem> Compose(IEnumerable<TItem> data)
        {
            var filters = ActiveFilters();

            if (filters is not null)
            {
                data = (data as IQueryable<TItem> ?? data.AsQueryable())
                    .Where(filters, LogicalFilterOperator, FilterCaseSensitivity);
            }

            if (SortColumn is not null)
            {
                // The column applies its own ordering, so it stays a typed expression the provider can
                // translate rather than a parsed string.
                var queryable = data as IQueryable<TItem> ?? data.AsQueryable();

                data = SortColumn.ApplySort(queryable, SortDescending) ?? data;
            }

            return data;
        }

        int TotalCount()
        {
            if (LoadData.HasDelegate)
            {
                return Count;
            }

            if (loadedCount is { } counted)
            {
                return counted;
            }

            // Same reason as View: Enumerable.Count() over an unloaded Entity Framework queryable is a
            // second full table scan, blocking the render thread for a number the load will supply.
            if (AsyncOwnsData)
            {
                return 0;
            }

            // A filtered grid must count what the filter left, which means composing and walking it.
            // Only pay that when something is actually filtered.
            if (ActiveFilters() is not null)
            {
                return Total(Composed(Data ?? Enumerable.Empty<TItem>()));
            }

            // Count() asks an ICollection<T> - and a non-generic ICollection - for its count rather than
            // walking it, so an unfiltered grid over a list pays nothing here.
            return Data is null ? 0 : Total(Data);
        }

        async Task OnPageChanged(PagerEventArgs args)
        {
            skip = args.Skip;

            await RefreshAsync();
        }

        async Task OnPageSizeChanged(int value)
        {
            pageSize = value;
            skip = 0;

            await PageSizeChanged.InvokeAsync(value);
            await RefreshAsync();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Cancel first: disposing alone leaves an in-flight query running against a component that
            // is gone, holding its context open until it finishes.
            loadCts?.Cancel();
            loadCts?.Dispose();
            loadCts = null;

            GC.SuppressFinalize(this);
        }
    }
}
