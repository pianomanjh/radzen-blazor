using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
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
            if (!initialized)
            {
                initialized = true;
                declaredPageSize = PageSize;
                pageSize = PageSize;
            }
            else if (declaredPageSize != PageSize)
            {
                // The page size was changed from the outside rather than through the pager, so the
                // current offset means something different now. Start again from the first page.
                declaredPageSize = PageSize;
                pageSize = PageSize;
                skip = 0;
            }

            if (LoadData.HasDelegate)
            {
                // The handler assigns Data, which sets parameters again. Load once here and thereafter
                // only when something the handler cares about changes - a sort, a page, or Reload().
                if (loadDataInvoked)
                {
                    return Task.CompletedTask;
                }

                loadDataInvoked = true;

                return InvokeLoadDataAsync();
            }

            if (ReferenceEquals(lastData, Data))
            {
                return Task.CompletedTask;
            }

            lastData = Data;
            lookups.Clear();

            // Drop what any previous load produced. Deliberately not RefreshAsync: that renders, and the
            // render ComponentBase queues after this returns would then be the second of two - a whole
            // extra pass over every row, measured at +94% allocation before this was split out.
            loaded = null;
            loadedCount = null;

            return BeginAsyncLoad() ?? Task.CompletedTask;
        }

        /// <summary>
        /// Re-reads the data for the current page and sort. Call this after the underlying source has
        /// changed in a way the grid cannot see - the usual companion to <see cref="LoadData" />.
        /// </summary>
        public Task Reload() => RefreshAsync();

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
            // date, and Convert.ChangeType would have no idea what to do with the list.
            var type = Nullable.GetUnderlyingType(column.FilterElementType) ?? column.FilterElementType;

            if (type == typeof(string) || type == typeof(object))
            {
                return Filter(column, text);
            }

            try
            {
                return Filter(column, Convert.ChangeType(text, type, CultureInfo.CurrentCulture));
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
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

            var source = Data as IQueryable<TItem> ?? Data?.AsQueryable();
            var values = source is null ? null : column.DistinctValues(source);

            var materialized = values is null
                ? (IEnumerable)Array.Empty<object>()
                // Materialized here rather than left lazy: the list box reads it on every render, and
                // Cast/Where run in memory anyway once the provider has answered the distinct query.
                : values.Cast<object>().ToList().Where(v => v != null).OrderBy(v => v).ToList();

            lookups[column] = materialized;

            return materialized;
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
            var type = Nullable.GetUnderlyingType(column.FilterElementType) ?? column.FilterElementType;
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
                    FilterProperty = string.IsNullOrEmpty(column.FilterProperty) ? null : column.FilterProperty,
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
            if (!AllowFiltering)
            {
                return source;
            }

            var filters = BuildFilters();

            // QueryableExtension builds a typed expression tree from the descriptors - the same one
            // RadzenDataGrid composes - so this still translates to SQL rather than parsing a string.
            return filters is null ? source : source.Where(filters, LogicalFilterOperator, FilterCaseSensitivity);
        }

        /// <summary>Moves to a zero-based page and reloads.</summary>
        public Task GoToPage(int page)
        {
            skip = Math.Max(0, page) * pageSize;

            return RefreshAsync();
        }

        Task RefreshAsync()
        {
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
        /// Starts an asynchronous load, or returns null when this source cannot be executed that way -
        /// which is every in-memory source, and every queryable when no executor is registered.
        /// </summary>
        Task? BeginAsyncLoad() =>
            Data is IQueryable<TItem> queryable && Executor is { } async && async.IsSupported(queryable)
                ? LoadPageAsync(async, queryable)
                : null;

        async Task LoadPageAsync(IAsyncQueryExecutor async, IQueryable<TItem> source)
        {
            var token = BeginLoad();
            var filtered = ApplyFilters(source);
            var ordered = SortColumn?.ApplySort(filtered, SortDescending) ?? filtered;
            var paged = AllowPaging ? ordered.Skip(skip).Take(pageSize) : ordered;

            IsLoading = true;
            StateHasChanged();

            try
            {
                var items = await async.ToListAsync(paged, token);

                // A page is a subset, so its length says nothing about the total and the total costs a
                // second round trip. An unpaged query is the whole set, so the list already is the count.
                // The count is of the filtered set, not the source: the pager counts what is on screen.
                var count = AllowPaging ? await async.CountAsync(filtered, token) : items.Count;

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

        async Task InvokeLoadDataAsync()
        {
            var args = new LoadDataArgs
            {
                Skip = AllowPaging ? skip : null,
                Top = AllowPaging ? pageSize : null,
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

            var data = Data ?? Enumerable.Empty<TItem>();

            if (LoadData.HasDelegate)
            {
                // The handler sorted and paged already; sorting or paging it again would be wrong.
                return data;
            }

            data = Composed(data);

            return AllowPaging ? data.Skip(skip).Take(pageSize) : data;
        }

        /// <summary>
        /// Filters and sorts, without paging. Nothing is wrapped in a queryable unless something is
        /// actually filtered or sorted, so an unfiltered, unsorted grid enumerates its source directly.
        /// </summary>
        IEnumerable<TItem> Composed(IEnumerable<TItem> data)
        {
            var filters = AllowFiltering ? BuildFilters() : null;

            if (filters is not null)
            {
                data = (data as IQueryable<TItem> ?? data.AsQueryable())
                    .Where(filters, LogicalFilterOperator, FilterCaseSensitivity);
            }

            if (SortColumn is not null)
            {
                // The column applies its own ordering, so it stays a typed expression the provider can
                // translate rather than a parsed string.
                data = data is IQueryable<TItem> queryable
                    ? SortColumn.ApplySort(queryable, SortDescending) ?? data
                    : SortColumn.ApplySort(data.AsQueryable(), SortDescending) ?? data;
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

            // A filtered grid must count what the filter left, which means composing and walking it.
            // Only pay that when something is actually filtered.
            if (AllowFiltering && BuildFilters() is not null)
            {
                return Composed(Data ?? Enumerable.Empty<TItem>()).Count();
            }

            // Count without enumerating where the source can say. Enumerable.Count() already does this
            // for ICollection<T>, but not for the non-generic ICollection an untyped source may be.
            return Data switch
            {
                null => 0,
                ICollection<TItem> collection => collection.Count,
                ICollection collection => collection.Count,
                _ => Data.Count(),
            };
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
            loadCts?.Dispose();
            loadCts = null;

            GC.SuppressFinalize(this);
        }
    }
}
