using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
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
    public partial class RadzenFastGrid<TItem> : IDisposable, IAsyncDisposable
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

        /// <summary>
        /// State to restore - the sort, the filters and the page. Applied when the reference changes, as
        /// the grid draws, which is the first moment its columns are known.
        /// </summary>
        [Parameter] public FastGridSettings? Settings { get; set; }

        /// <summary>
        /// Raised with the current state whenever the grid reloads, which is every sort, filter and page
        /// change - and also a <see cref="Reload" /> called from application code, since a reload is a
        /// reload. Built only when something is listening.
        /// </summary>
        [Parameter] public EventCallback<FastGridSettings> SettingsChanged { get; set; }

        FastGridSettings? appliedSettings;

        // The last settings object the grid handed to SettingsChanged.
        FastGridSettings? raisedSettings;
        bool settingsPending;
        bool settingsNeedReload;

        /// <summary>How string comparisons treat case. The provider decides by default.</summary>
        [Parameter] public FilterCaseSensitivity FilterCaseSensitivity { get; set; }

        /// <summary>
        /// Whether a filter applies as the user types rather than when the box loses focus. On by
        /// default, as in RadzenDataGrid.
        /// </summary>
        [Parameter] public bool FilterAsYouType { get; set; } = true;

        /// <summary>
        /// How long typing must pause before the filter applies, in milliseconds. Zero applies on every
        /// keystroke, which over a queryable is a query per keystroke.
        /// </summary>
        [Parameter] public int FilterDelay { get; set; } = 500;

        // Set by Dispose, read by the filter delay - the one thing here that can still be running after
        // the component is gone.
        volatile bool disposed;

        // A generation counter rather than a CancellationTokenSource per keystroke: the superseded delay
        // still runs, but it finds itself out of date and does nothing, and there is no token source to
        // own, cancel or dispose. A timer that fires and returns costs less than the lifetime rules.
        int filterGeneration;

        /// <summary>
        /// Applies a filter after the typing pause, unless another keystroke arrives first.
        /// </summary>
        async Task OnFilterTyped(ColumnBase<TItem> column, string? text)
        {
            var generation = Interlocked.Increment(ref filterGeneration);

            if (FilterDelay > 0)
            {
                await Task.Delay(FilterDelay).ConfigureAwait(false);

                // Read after the wait, not captured before it: what matters is whether anything was
                // typed while this one was waiting. Disposal is checked here too: a delay outlives the
                // component that started it whenever the user types and navigates away inside it, and
                // reloading a grid that is gone is the one way this can touch a torn-down renderer.
                if (generation != Volatile.Read(ref filterGeneration) || disposed)
                {
                    return;
                }
            }

            await InvokeAsync(() => ApplyTypedFilter(column, text)).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies what the box holds now. Raised by a blur or an Enter, so it also stands in for the
        /// pause that never came - a box abandoned mid-delay still filters on the way out.
        /// </summary>
        Task OnFilterCommitted(ColumnBase<TItem> column, string? text)
        {
            // Supersede any waiting delay: whatever it was going to apply, this is applying now.
            Interlocked.Increment(ref filterGeneration);

            return ApplyTypedFilter(column, text);
        }

        /// <summary>
        /// The one place the two filter events meet, and the only one that reloads. Both fire for the
        /// same keystrokes - typing raises input, leaving the box raises change - so without this the
        /// blur after a pause would run the query the pause already ran.
        /// </summary>
        Task ApplyTypedFilter(ColumnBase<TItem> column, string? text)
        {
            if (string.Equals(column.AppliedFilterText, text, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            return OnFilterInput(column, text);
        }

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
        IFastGridQueryExecutor? executor;
        CancellationTokenSource? loadCts;

        object? isODataFor;
        bool isOData;

        /// <summary>
        /// What will execute a bound queryable asynchronously: whatever the service provider offers, and
        /// otherwise the built-in <see cref="IAsyncEnumerable{T}" /> executor, which needs no registration.
        /// Null only when asynchronous execution has been switched off.
        /// </summary>
        IFastGridQueryExecutor? Executor
        {
            get
            {
                if (!executorResolved)
                {
                    executorResolved = true;
                    executor = Services?.GetService(typeof(IFastGridQueryExecutor)) as IFastGridQueryExecutor
                        ?? (AsyncQueryExecutionDisabled ? null : AsyncEnumerableQueryExecutor.Instance);
                }

                return executor;
            }
        }

        /// <summary>
        /// The switch <c>Radzen.Blazor</c> reads to turn asynchronous execution off, honoured here too so
        /// one setting covers both grids.
        /// </summary>
        static bool AsyncQueryExecutionDisabled =>
            AppContext.TryGetSwitch("Radzen.Blazor.DisableAsyncQueryExecution", out var disabled) && disabled;

        /// <inheritdoc />
        protected override Task OnParametersSetAsync()
        {
            // Noted here and applied as the table draws: sorts and filters name columns, and no column
            // has registered yet on the parameter set that precedes the first render.
            // Not the settings this grid just produced: that is its own state coming back, and applying
            // it would be a loop rather than a restore.
            if (!ReferenceEquals(appliedSettings, Settings) && !ReferenceEquals(raisedSettings, Settings))
            {
                appliedSettings = Settings;
                settingsPending = Settings is not null;
            }

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

            // Read once. A source that answers with a new object every time it is read - a DbSet put
            // through AsNoTracking, a Where written in markup - would otherwise be compared against one
            // instance and remembered as another.
            var data = Data;
            var dataChanged = !ReferenceEquals(lastData, data);

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

                // A queryable load may still be in flight from before the handler was attached, and
                // nothing below it will supersede one.
                CancelLoad();

                // While virtualizing the provider owns fetching, and it asks for a window. Loading here
                // as well would call the handler once with no window at all and throw the answer away.
                return AllowVirtualization ? Task.CompletedTask : InvokeLoadDataAsync();
            }

            if (!dataChanged && !pagingChanged)
            {
                return Task.CompletedTask;
            }

            lastData = data;

            // Drop what any previous load produced. Deliberately not RefreshAsync on the ordinary path:
            // that renders, and the render ComponentBase queues after this returns would then be the
            // second of two - a whole extra pass over every row, measured at +94% allocation.
            loaded = null;
            loadedCount = null;

            // Virtualizing is the exception, and has to be: Virtualize is still holding the window it
            // fetched from the old source, and nothing else will ask it for another.
            //
            // Silently, though. Being handed a new source is not a setting the user chose, so there is
            // nothing here to persist - and announcing it is a loop rather than a courtesy. A queryable
            // read from a property is a new object every time it is read, which is what ordinary
            // application code produces; the parent stores the settings this raises, re-renders, hands
            // back another new queryable, and the grid refreshes again. That ran to 880,000 renders in
            // two and a half seconds with no exception and nothing in the log. The paged branch below
            // never had the fault because BeginAsyncLoad announces nothing.
            if (AllowVirtualization)
            {
                // A paged load in flight is answering for a grid that no longer pages.
                CancelLoad();

                return RefreshAsync(announce: false);
            }

            if (BeginAsyncLoad() is { } load)
            {
                return load;
            }

            // Nothing started, so nothing will supersede a load already running - and the source it was
            // reading has just been replaced.
            CancelLoad();

            return Task.CompletedTask;
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

                // The handler answers by assigning Data and Count, which are the component's own
                // fields rather than anything this call owns. Two overlapping scrolls therefore share
                // them: whichever handler finishes last leaves its rows there, and the other request
                // would pair those rows with its own StartIndex - a window that says it begins
                // somewhere it does not, which is exactly what the keyboard cursor indexes by.
                if (request.CancellationToken.IsCancellationRequested)
                {
                    return new ItemsProviderResult<TItem>(Array.Empty<TItem>(), 0);
                }

                return Window(request.StartIndex,
                    new ItemsProviderResult<TItem>(Data ?? Enumerable.Empty<TItem>(), Count));
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

                    // Awaiting a cancelled token throws, but a query that had already finished does
                    // not - it returns normally into a grid that has since scrolled somewhere else.
                    // Virtualize discards the result either way; Window would keep it, and the window
                    // it recorded would disagree with the rows on screen.
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        return new ItemsProviderResult<TItem>(Array.Empty<TItem>(), 0);
                    }

                    return Window(request.StartIndex,
                        new ItemsProviderResult<TItem>(window, virtualTotal.Value));
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
            return Window(request.StartIndex,
                new ItemsProviderResult<TItem>(Page(rows, request.StartIndex, top).ToList(),
                    virtualTotal.Value));
        }

        /// <summary>
        /// The rows Virtualize is currently showing, and where in the data they start. Only the keyboard
        /// cursor reads it: Virtualize hands its ChildContent an item and no position, so this is where
        /// a rendered row's index in the whole data set comes from.
        /// </summary>
        /// <remarks>
        /// Kept whether or not navigation is on, which is the one place this component does not follow
        /// its own "nothing is paid for when switched off" rule, and the reason is that switching it on
        /// is a runtime parameter change: a grid whose navigation arrives after the window did would
        /// have no window to index against until the next scroll, and would spend that time addressing
        /// rows by a position that means something else. What it costs is a reference assignment per
        /// scroll batch - tens of rows, once - rather than anything per row or per render.
        /// </remarks>
        IList<TItem>? virtualWindow;
        int virtualWindowStart;

        ItemsProviderResult<TItem> Window(int start, ItemsProviderResult<TItem> result)
        {
            virtualWindowStart = start;

            // Already a list on every path that materializes one, which is all but the LoadData handler's.
            virtualWindow = result.Items as IList<TItem> ?? result.Items.ToList();

            return result;
        }

        /// <summary>
        /// A rendered row's index in the whole data set, under virtualization. Identity against the
        /// window rather than a counter: Virtualize re-renders on its own as the viewport scrolls, so a
        /// cursor reset by the grid's render would drift the moment it did. The window is tens of rows,
        /// and this is only walked for the two features that need the position - the keyboard cursor,
        /// and the row number a screen reader is told.
        /// </summary>
        int VirtualRowIndex(TItem item)
        {
            if ((!AllowKeyboardNavigation && !RowsAreCounted) || virtualWindow is null)
            {
                return -1;
            }

            var index = virtualWindow.IndexOf(item);

            return index < 0 ? -1 : virtualWindowStart + index;
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
            var task = Filter(column, FilterValueFrom(column, text));

            // Filter routes through SetFilter, which clears this; recording the text afterwards is what
            // says the box's contents are what is applied. Anything that filters by another route -
            // descriptors, the clear button, a declared value - leaves it cleared, so the next thing
            // typed applies even if it repeats what was typed before.
            column.AppliedFilterText = text;

            return task;
        }

        /// <summary>The value a column filters by for the given text, or null if the text is not one.</summary>
        static object? FilterValueFrom(ColumnBase<TItem> column, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // The element type, not the property type: a filter on a list of dates is compared against a
            // date, and a conversion would have no idea what to do with the list.
            var declared = column.EffectiveFilterType;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;

            if (type == typeof(string) || type == typeof(object))
            {
                return text;
            }

            try
            {
                // ConvertType rather than Convert.ChangeType, and Enum.Parse rather than either: neither
                // an enum nor a Guid converts from a string through IConvertible, so the framework call
                // throws for both and what was typed silently cleared the filter instead of applying it.
                return type.IsEnum
                    ? Enum.Parse(type, text, ignoreCase: true)
                    : ConvertType.ChangeType(text, declared, CultureInfo.CurrentCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException
                or ArgumentException)
            {
                return null;
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
                    var distinct = await ToObjectListAsync(async, values, token);

                    // Awaiting a cancelled token throws; a query that had already finished does not, and
                    // the cache it is about to write into may have been emptied while it ran. Writing
                    // then would leave a check-box list offering the previous source's values with
                    // nothing to clear it until the next Reload.
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    lookups[column] = Ordered(distinct);
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
        static Task<List<object>> ToObjectListAsync(IFastGridQueryExecutor async, IQueryable values,
            CancellationToken token)
        {
            // Unreachable with the switch off: the only caller asks a column for its distinct values,
            // and a column that would need this returns null there instead. Stated rather than assumed,
            // because a future caller that did not know that would otherwise fail obscurely.
            if (!DynamicCode.Supported)
            {
                throw DynamicCode.Unavailable("Awaiting a distinct query over a run-time element type");
            }

            return (Task<List<object>>)ToObjectListMethod
                .MakeGenericMethod(values.ElementType)
                .Invoke(null, new object[] { async, values, token })!;
        }

        static async Task<List<object>> ToObjectListOfAsync<TValue>(IFastGridQueryExecutor async,
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
        /// Whether the pager's page is being driven from the grid, so the PageChanged it raises back is
        /// an echo of what the grid already applied rather than a request to move.
        /// </summary>
        bool syncingPagers;

        /// <summary>
        /// Puts the pager back on the page the grid is actually showing. RadzenPager keeps its own
        /// offset and has no CurrentPage parameter to be told through, so every path that sends the grid
        /// back to page one - a sort, a filter, ClearFilters, ApplyFilters, GoToPage - left the pager
        /// highlighting the old page and paging onward from it.
        /// </summary>
        async Task SyncPagersAsync()
        {
            var page = pageSize > 0 ? skip / pageSize : 0;

            if (NeedsSync(topPager, page) || NeedsSync(bottomPager, page))
            {
                syncingPagers = true;

                try
                {
                    await SyncPager(topPager, page);
                    await SyncPager(bottomPager, page);
                }
                finally
                {
                    syncingPagers = false;
                }
            }

            static bool NeedsSync(RadzenPager? pager, int page) => pager is not null && pager.CurrentPage != page;

            static Task SyncPager(RadzenPager? pager, int page) =>
                NeedsSync(pager, page) ? pager!.GoToPage(page) : Task.CompletedTask;
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
            // A grid composing over a queryable in memory has already drawn the restored state - the
            // render that applied it composed from it. One that loads its data has not: the load that
            // produced what is on screen ran before the settings existed.
            if (settingsNeedReload)
            {
                settingsNeedReload = false;

                await RefreshAsync();
            }

            if (ClampPage())
            {
                await RefreshAsync();
            }

            await SyncPagersAsync();

            // After the pagers, so the rows the listener will resolve are the ones now on screen.
            await AttachClicksAsync();

            await AttachNavigationAsync();

            // Last, and after every path above that can reload: this is the render the cursor has to be
            // put back on, and a reload started here would move the rows out from under it.
            await ReassertFocusAsync();

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

            // Typed as the column's element type, not List<object>: the reflective builder puts this
            // list straight into Contains<TElement>(selected, x), and a List<object> there is not an
            // IEnumerable<TElement> - so a provider cannot translate it and the comparison never binds.
            //
            // A column that composes its own predicate does not care, because it retypes the values
            // against the type parameter it already has. So with the switch off - where closing List<>
            // over a run-time type is exactly what is unavailable - the untyped list is enough, and the
            // only columns that would have needed the typed one have already declined to filter.
            var type = Nullable.GetUnderlyingType(column.EffectiveFilterType) ?? column.EffectiveFilterType;
            var selected = DynamicCode.Supported
                ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!
                : new List<object>();

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

                (filters ??= new List<FilterDescriptor>()).Add(DescriptorFor(column));
            }

            return filters;
        }

        /// <summary>Composes the columns' filters onto a queryable. Untouched when nothing is filtered.</summary>
        /// <remarks>
        /// Each column is asked for its own predicate first. A column that knows the filtered property's
        /// type as a type parameter composes one directly, which is both what a provider translates and
        /// what an ahead-of-time compiler can see through; only the columns that decline - a template
        /// column filtering by a path, a collection column, a column declared as <c>object</c> - are
        /// handed to <c>QueryableExtension</c>, which finds their members by reflection.
        /// </remarks>
        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "ApplyFilter is virtual; the analyzer resolves it to the base implementation, which is the one that always returns null.")]
        IQueryable<TItem> ApplyFilters(IQueryable<TItem> source)
        {
            if (!AllowFiltering && !drawing)
            {
                return source;
            }

            // What QueryableExtension itself checks to decide whether OrdinalIgnoreCase comparisons are
            // available, so the two builders agree about a given source.
            var inMemory = source is EnumerableQuery;

            // Or is the case where the two groups cannot be applied separately, so it is the only case
            // that needs every descriptor kept in case they have to be applied together. And - the
            // default, and what a filter row produces - never does.
            var either = LogicalFilterOperator == LogicalFilterOperator.Or;

            Expression<Func<TItem, bool>>? predicate = null;
            List<FilterDescriptor>? declined = null;
            List<FilterDescriptor>? all = null;

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.HasFilter)
                {
                    continue;
                }

                var descriptor = either ? DescriptorFor(column) : null;

                if (either)
                {
                    (all ??= new List<FilterDescriptor>()).Add(descriptor!);
                }

                if (column.ApplyFilter(FilterCaseSensitivity, inMemory) is { } composed)
                {
                    predicate = predicate is null
                        ? composed
                        : FilterPredicate.Join(predicate, composed, LogicalFilterOperator);
                }
                else
                {
                    (declined ??= new List<FilterDescriptor>()).Add(descriptor ?? DescriptorFor(column));
                }
            }

            if (declined is null)
            {
                return predicate is null ? source : source.Where(predicate);
            }

            if (predicate is null)
            {
                return Reflective(source, declined);
            }

            // Two Wheres are an And between the groups, which is right for And and wrong for Or: a row
            // that matched only a declining column would be dropped by the second Where. So a mixed Or
            // goes through the reflective builder whole rather than being composed wrongly - one
            // builder, one answer. It costs that grid its AOT-cleanliness, which it had already lost to
            // the column that declined.
            return either
                ? Reflective(source, all!)
                : Reflective(source.Where(predicate), declined);
        }

        /// <summary>
        /// The one call in this component that reaches a property by name. Reserved for the columns that
        /// cannot compose their own predicate, and reachable only while dynamic filtering is enabled.
        /// </summary>
        IQueryable<TItem> Reflective(IQueryable<TItem> source, List<FilterDescriptor> filters)
        {
            if (!DynamicCode.Supported)
            {
                throw DynamicCode.Unavailable(
                    $"Filtering '{filters[0].Property}' through the column's property path");
            }

            return source.Where(filters, LogicalFilterOperator, FilterCaseSensitivity);
        }

        static FilterDescriptor DescriptorFor(ColumnBase<TItem> column) => new()
        {
            Property = column.FilterPropertyPath,

            // Names a member of the collection's element, so the predicate becomes
            // Customers.Any(c => c.Name ...) rather than a comparison against the collection.
            FilterProperty = column.FilterMemberPath,
            FilterValue = column.CurrentFilterValue,
            FilterOperator = column.CurrentFilterOperator,
            Type = column.FilterPropertyType,
        };

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

        int? drawingTotal;

        void BeginDrawing()
        {
            drawingFilters = AllowFiltering ? BuildFilters() : null;
            drawingComposed = null;
            drawingComposedOf = null;
            drawingTotal = null;
            drawing = true;
        }

        void EndDrawing()
        {
            drawing = false;
            drawingFilters = null;
            drawingComposed = null;
            drawingComposedOf = null;
            drawingTotal = null;
        }

        /// <summary>Moves to a zero-based page and reloads.</summary>
        public Task GoToPage(int page)
        {
            skip = Math.Max(0, page) * pageSize;

            return RefreshAsync();
        }

        /// <summary>
        /// Restores stored state. Called as the table draws, so every column has registered and the view
        /// has not composed yet - the same moment a column's own declared filter and sort take effect.
        /// </summary>
        void ApplySettings(FastGridSettings settings)
        {
            if (settings.PageSize is { } size and > 0)
            {
                // Not declaredPageSize, which is what the *markup* asked for and exists only to notice
                // that changing. Writing a restored size there makes the next parameter set read it as
                // an outside change, throw the restored size away and go back to page one - and the
                // settings raised after that persist the wrong size, so it never comes back.
                pageSize = size;
            }

            if (settings.CurrentPage is { } page and >= 0)
            {
                skip = page * pageSize;
            }

            if (settings.Columns is null)
            {
                return;
            }

            sorts.Clear();

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].SetFilter(null, null);
            }

            // Walked in the stored order, not the columns' - it is what records the sort's precedence.
            foreach (var stored in settings.Columns)
            {
                if (stored?.Property is not { Length: > 0 } path)
                {
                    continue;
                }

                var column = ColumnForPath(path);

                if (column is null)
                {
                    continue;
                }

                if (stored.SortOrder is { } order && column.CanSort)
                {
                    sorts.Add((column, order == SortOrder.Descending));
                }

                if (stored.FilterValue is not null)
                {
                    column.SetFilter(stored.FilterValue, stored.FilterOperator);
                }

                // Only when something recorded a choice. A null leaves the markup's Visible standing,
                // which is what a grid with no picker stores for every column.
                if (stored.Visible is { } visible)
                {
                    column.SetPicked(visible);
                }

                // Same: a null leaves the declared Width standing rather than clearing it.
                if (stored.Width is { Length: > 0 } width)
                {
                    column.SetResizedWidth(width);
                }

                // And the same again for where the column sits.
                if (stored.OrderIndex is { } orderIndex)
                {
                    column.SetReorderedIndex(orderIndex);
                }
            }

            // A grid composing in memory has drawn this state already - the render applying it composed
            // from it. One that loads has to ask again, which is LoadData or a source the executor will
            // actually run: AsyncOwnsData, not merely that an executor exists. Since the executor is
            // built in it always exists, and reading it as "does this grid load" made every settings
            // apply schedule a reload it did not need - which raised SettingsChanged, which handed the
            // grid new settings, which applied them and scheduled another. One sort spun the circuit at
            // several thousand renders a second and never stopped.
            settingsNeedReload = LoadData.HasDelegate || AsyncOwnsData;
        }

        // Null unless the grid actually has a picker and this column is in it. Recording visibility for
        // a column nothing can change would store the markup back to itself, and would then override a
        // later edit to that markup on the next load.
        bool? RecordedVisibility(ColumnBase<TItem> column) =>
            AllowColumnPicking && column.Pickable ? column.IsVisible : null;

        // Same rule for width: only a width a drag produced is a choice worth storing. The declared one
        // is already in the markup, and recording it back would override a later edit to that markup.
        static string? RecordedWidth(ColumnBase<TItem> column) => column.ResizedWidth;

        static int? RecordedOrderIndex(ColumnBase<TItem> column) => column.ReorderedIndex;

        ColumnBase<TItem>? ColumnForPath(string path)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].PropertyPath, path, StringComparison.Ordinal))
                {
                    return columns[i];
                }
            }

            return null;
        }

        /// <summary>The grid's current state, in the form <see cref="Settings" /> takes.</summary>
        public FastGridSettings CaptureSettings()
        {
            var stored = new List<FastGridColumnSettings>();

            // Sorted columns first and in order, since the list is what carries the precedence.
            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                if (column.PropertyPath is { Length: > 0 } path)
                {
                    stored.Add(new FastGridColumnSettings
                    {
                        Property = path,
                        SortOrder = descending ? SortOrder.Descending : SortOrder.Ascending,
                        FilterValue = column.HasFilter ? column.CurrentFilterValue : null,
                        FilterOperator = column.HasFilter ? column.CurrentFilterOperator : null,
                        Visible = RecordedVisibility(column),
                        Width = RecordedWidth(column),
                        OrderIndex = RecordedOrderIndex(column),
                    });
                }
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                var visibility = RecordedVisibility(column);
                var width = RecordedWidth(column);
                var orderIndex = RecordedOrderIndex(column);

                if ((!column.HasFilter && visibility is null && width is null && orderIndex is null)
                    || SortIndexOf(column) >= 0
                    || column.PropertyPath is not { Length: > 0 } path)
                {
                    continue;
                }

                stored.Add(new FastGridColumnSettings
                {
                    Property = path,
                    FilterValue = column.HasFilter ? column.CurrentFilterValue : null,
                    FilterOperator = column.HasFilter ? column.CurrentFilterOperator : null,
                    Visible = visibility,
                    Width = width,
                    OrderIndex = orderIndex,
                });
            }

            return new FastGridSettings
            {
                Columns = stored,
                CurrentPage = CurrentPage,
                PageSize = pageSize,
            };
        }

        /// <summary>
        /// Re-reads the data for whatever the grid is currently showing.
        /// </summary>
        /// <param name="announce">
        /// Whether to raise <see cref="SettingsChanged" />. True for everything a user did - a sort, a
        /// filter, a page, a resize - and false when the only thing that changed is the source the grid
        /// was handed, because no setting changed and saying otherwise starts a loop. See the caller.
        /// </param>
        Task RefreshAsync(bool announce = true)
        {
            // A Shift run and its anchor are both positions in the view, and this is where the view
            // stops being the one they were taken in. Dropping them here rather than at each caller
            // covers the sort, the filter and the page together, which is all three ways a row can
            // arrive at an index that used to belong to another one.
            ForgetRange();

            // Every state change a user can make funnels through here, so this is the one place the
            // grid has to say so - and it is not the render path, which is what keeps a grid nobody is
            // persisting from ever building the object.
            if (announce && SettingsChanged.HasDelegate)
            {
                // Remembered so the settings the grid hands out are not then read back as an instruction.
                // An application that stores what it is given and passes it back - which is the whole
                // point of the parameter - would otherwise return this object as a parameter change, and
                // a grid that reloads on a settings change would reload, raise, and be handed it again.
                raisedSettings = CaptureSettings();

                _ = SettingsChanged.InvokeAsync(raisedSettings);
            }

            // Every branch below either starts a load that supersedes the one in flight or starts none
            // at all, and the second kind would leave it to land. Cancelling here covers all of them;
            // BeginAsyncLoad installs a fresh token immediately after when there is a load to run.
            CancelLoad();

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
        bool TryGetAsyncSource([NotNullWhen(true)] out IFastGridQueryExecutor? executor,
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

        async Task LoadPageAsync(IFastGridQueryExecutor async, IQueryable<TItem> source)
        {
            var token = BeginLoad();
            var filtered = ApplyFilters(source);
            var ordered = ApplySorts(filtered);
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

        /// <summary>
        /// Cancels whatever load is in flight, for a path that is not going to start another.
        /// </summary>
        /// <remarks>
        /// Starting a load is what supersedes one, so a grid that stops being loadable at all - handed
        /// an ordinary list, switched to virtualizing, given a <see cref="LoadData" /> handler - leaves
        /// the old query running with nothing to displace it. It then writes its rows into
        /// <c>loaded</c> over the source that replaced it, and the grid renders a table that belongs to
        /// data it no longer has, with no exception anywhere. Cancelling is the whole fix: the load
        /// already checks its token before it writes.
        /// <para>
        /// The source is left in place rather than cleared. Cancelling runs callbacks synchronously, so
        /// clearing after the cancel would clobber a load one of them started - and clearing before it
        /// would hand the lookup fetch <see cref="CancellationToken.None" /> at exactly the moment
        /// everything in flight is meant to stop. A cancelled source answers both correctly, and the
        /// next <c>BeginLoad</c> replaces it.
        /// </para>
        /// </remarks>
        void CancelLoad() => loadCts?.Cancel();

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
            if (sorts.Count == 0)
            {
                return null;
            }

            var odata = IsOData();
            StringBuilder? builder = null;
            string? single = null;

            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                if (column.PropertyPath is not { Length: > 0 } path)
                {
                    continue;
                }

                var property = odata ? path.Replace('.', '/') : path;
                var term = descending ? property + " desc" : property + " asc";

                // One sorted column is the ordinary case and costs no builder; the rest join with the
                // comma both dynamic LINQ and OData $orderby read.
                if (single is null)
                {
                    single = term;
                }
                else
                {
                    (builder ??= new StringBuilder(single)).Append(',').Append(term);
                }
            }

            return builder is not null ? builder.ToString() : single;
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

            // The string form a LoadData handler receives, which is built by walking the descriptors'
            // property paths. There is no typed equivalent: the point of it is to be a string.
            if (!DynamicCode.Supported)
            {
                return null;
            }

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

            // Before the executor check, and in the same order TotalCount and ProvideRows read the two.
            // A handler is free to assign a queryable rather than a list - it has already sorted and
            // paged, so what it leaves behind is one page - and taking the executor branch first
            // rendered nothing while the pager, which checks the handler first, went on counting the
            // handler's rows. A grid reading "1-10 of 500" above an empty table, with no reload able to
            // fix it.
            if (LoadData.HasDelegate)
            {
                // The handler sorted and paged already; sorting or paging it again would be wrong.
                return data;
            }

            // Nothing has loaded yet and the query belongs to the executor. Composing over it here
            // enumerates it on the render thread - a whole unpaged table pulled synchronously, for rows
            // the awaited load is about to replace.
            if (AsyncOwnsData)
            {
                return Array.Empty<TItem>();
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
            var filtering = AllowFiltering && ActiveFilters() is not null;
            var sorting = SortColumn is not null;

            ComposedInMemory = false;

            if (!filtering && !sorting)
            {
                return data;
            }

            // A source that is already in memory is composed with delegates rather than expressions.
            // Wrapping a list in an EnumerableQuery to hand it an expression tree makes it rewrite and
            // recompile that tree every time the result is enumerated: measured at 1000 rows, 1,117 us
            // and 11.8 KB to filter that way against 38 us and 0.07 KB through a delegate, on a render
            // that costs 1,800 us in total. Composing over a real queryable still uses expressions,
            // because there the point is for the provider to translate them.
            if (data is not IQueryable<TItem> queryable)
            {
                if (ComposeInMemory(data, filtering, sorting) is { } composed)
                {
                    ComposedInMemory = true;

                    return composed;
                }

                // A column that cannot compose in memory - a template column filtering by a path -
                // sends the whole composition back to the expression route rather than half of it.
                queryable = data.AsQueryable();
            }

            if (filtering)
            {
                queryable = ApplyFilters(queryable);
            }

            // The column applies its own ordering, so it stays a typed expression the provider can
            // translate rather than a parsed string.
            return sorting ? ApplySorts(queryable) : queryable;
        }

        /// <summary>
        /// Whether the last composition took the delegate route rather than wrapping the source in a
        /// queryable.
        /// </summary>
        /// <remarks>
        /// Exposed for the tests, and only to them, because the fast path is invisible in the rows: a
        /// column that declines to compose in memory sends the whole thing to the expression route,
        /// which produces the same answer and costs about 1.1 ms per render at 1000 rows. Without this
        /// a column could quietly stop overriding <c>ApplySortInMemory</c> and every test would still
        /// pass.
        /// </remarks>
        internal bool ComposedInMemory { get; private set; }

        /// <summary>
        /// Filters and sorts an in-memory sequence without wrapping it in a queryable, or returns null
        /// when some column cannot be composed that way and the caller should take the other route.
        /// </summary>
        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "ApplyFilterInMemory is virtual; the analyzer resolves it to the base implementation, which is the one that always returns null.")]
        IEnumerable<TItem>? ComposeInMemory(IEnumerable<TItem> data, bool filtering, bool sorting)
        {
            if (filtering)
            {
                Func<TItem, bool>? predicate = null;
                var either = LogicalFilterOperator == LogicalFilterOperator.Or;

                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];

                    if (!column.HasFilter)
                    {
                        continue;
                    }

                    if (column.ApplyFilterInMemory(FilterCaseSensitivity) is not { } composed)
                    {
                        return null;
                    }

                    var previous = predicate;

                    predicate = previous is null ? composed
                        : either ? item => previous(item) || composed(item)
                        : item => previous(item) && composed(item);
                }

                if (predicate is not null)
                {
                    data = data.Where(predicate);
                }
            }

            if (!sorting)
            {
                return data;
            }

            IOrderedEnumerable<TItem>? ordered = null;

            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                var next = ordered is null
                    ? column.ApplySortInMemory(data, descending)
                    : column.ApplyThenByInMemory(ordered, descending);

                // Null here means the column declined, which the queryable route treats as "skip this
                // column". Taking the other route instead would be a different answer, not a slower
                // one, so only a first column that declines sends it back - and only when no ordering
                // has begun, since a half-applied one cannot be handed over.
                if (next is null && ordered is null && i == 0)
                {
                    return null;
                }

                ordered = next ?? ordered;
            }

            return ordered ?? data;
        }

        /// <summary>
        /// Composes every sort onto the query, in order of precedence. A column that cannot order -
        /// which is what ApplySort returning null means - is skipped rather than allowed to break the
        /// chain, so one uncomparable column does not cost the sort the caller asked for.
        /// </summary>
        IQueryable<TItem> ApplySorts(IQueryable<TItem> source)
        {
            IOrderedQueryable<TItem>? ordered = null;

            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                ordered = ordered is null
                    ? column.ApplySort(source, descending) ?? ordered
                    : column.ApplyThenBy(ordered, descending) ?? ordered;
            }

            return ordered ?? source;
        }

        /// <summary>
        /// How many rows there are behind whatever is on screen, memoized for the render pass.
        /// </summary>
        /// <remarks>
        /// The pager asks, the page clamp asks, and <c>aria-rowcount</c> asks - and over a plain
        /// sequence each of those is a walk of the source. It is the same memo <c>Composed</c> keeps
        /// and for the same reason: within one pass the answer cannot change, and the count is
        /// independent of which page is being drawn.
        /// </remarks>
        int TotalCount()
        {
            if (drawing && drawingTotal is { } counted)
            {
                return counted;
            }

            var total = CountAll();

            if (drawing)
            {
                drawingTotal = total;
            }

            return total;
        }

        int CountAll()
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
            // GoToPage raises this on the way back from SyncPagersAsync. The grid is already on that
            // page - refreshing for it would reload the same rows and re-enter the sync.
            if (syncingPagers)
            {
                return;
            }

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
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        /// <summary>Releases the grid's in-flight load.</summary>
        /// <param name="disposing">Whether managed state should be released.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            // Before cancelling, so a filter delay that wakes up during teardown sees it.
            disposed = true;

            // Cancel first: disposing alone leaves an in-flight query running against a component that
            // is gone, holding its context open until it finishes.
            loadCts?.Cancel();
            loadCts?.Dispose();
            loadCts = null;

            // The references handed to the browser. The listener itself is released in DisposeAsync,
            // which is the path Blazor takes for a component that offers one.
            clickReference?.Dispose();
            clickReference = null;

            selfReference?.Dispose();
            selfReference = null;
        }

        /// <summary>Releases the grid, and the listener it attached in the browser.</summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeScriptAsync().ConfigureAwait(false);

            Dispose(true);

            GC.SuppressFinalize(this);
        }
    }
}
