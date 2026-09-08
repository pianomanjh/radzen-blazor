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

        // Whether the grid has drawn at least once, and whether a load is waiting for that. §23: a load
        // composes from the column list, and a column registers during the render - so the parameter set
        // that precedes the first render is the one moment the grid cannot compose a query from its own
        // state. What it composed there carried no declared filter and no declared sort.
        bool drawn;
        bool loadOwed;

        /// <summary>How string comparisons treat case. The provider decides by default.</summary>
        [Parameter] public FilterCaseSensitivity FilterCaseSensitivity { get; set; }

        /// <summary>
        /// What "today" means, for the relative dates a filter can be written in - §34. Null, which is
        /// the default, means <see cref="System.TimeProvider.System" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read through <c>GetLocalNow</c>, because a user filtering <em>hired today</em> means their
        /// today. This parameter is the only answer available to a Blazor Server application whose
        /// server is in a different zone from its user: it hands over a provider carrying that zone and
        /// the grid asks no further questions.
        /// </para>
        /// <para>
        /// It is also what makes a relative date testable at all. §9's protocol has nowhere to put a
        /// test of <em>yesterday</em> against a real clock; against a fixed provider it is an ordinary
        /// test of a pure rule - and §33's review is why that matters: a test one layer above a rule is
        /// not a test of the rule.
        /// </para>
        /// <para>
        /// <see cref="System.TimeProvider" /> rather than a clock interface of this grid's own, because
        /// it is the framework's since .NET 8 and a second one would be a seam with exactly one
        /// implementation.
        /// </para>
        /// </remarks>
        [Parameter] public TimeProvider? Clock { get; set; }

        DateTimeOffset filterNow;

        /// <summary>
        /// Reads the clock once, for the composition about to be built.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called at the head of each operation that composes: the reload funnel, the virtualized
        /// provider, and the start of a draw pass. Not at every place a filter is read - two reads
        /// within one composition would then land on two instants, and a range whose bounds straddle
        /// midnight excludes its own start. The one composition that deliberately reads a *previous*
        /// stamp is the public <see cref="Filters" />, which reports what the grid is filtering by now
        /// rather than what it would filter by if asked again.
        /// </para>
        /// <para>
        /// Nothing has to guard against an unset stamp. A fourth call stood in <c>OnParametersSetAsync</c>
        /// and then a lazy one in <see cref="Options" />, and the mutation loop could not fail either -
        /// they were redundant with these three. They also could not matter: a composition reads the
        /// columns, and no column has registered until a render has run, which is a draw pass.
        /// </para>
        /// </remarks>
        void StampFilterClock() => filterNow = (Clock ?? TimeProvider.System).GetLocalNow();

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

        // Which view the grid is currently showing, counted rather than described. Anything measured
        // against the rows on screen - so far, an auto-fit - stamps itself with this and throws its
        // answer away if it comes back into a different one. Incremented in RefreshAsync because that
        // is already the single funnel every sort, filter, page and data change goes through, which is
        // what keeps this from being a second opinion about what is current.
        int viewGeneration;

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
            // Against what the box is showing, not only against what a box last put there. The two are
            // the same for a filter that came from typing; for every other filter AppliedFilterText is
            // null, so a blur over an untouched box used to re-apply the box's own text as a new filter
            // and narrow a Between to an Equals on its lower bound. §34 turned that into a filter
            // *cleared*, since a relative token does not parse as a date - which is how it was found.
            if (string.Equals(column.AppliedFilterText ?? column.FilterBoxText, text,
                StringComparison.Ordinal))
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

        // The day virtualTotal was counted on, so DropStaleTotal can tell whether it still answers.
        DateTime countedOn;

        /// <summary>
        /// Whether the grid is actually paging. One rule, in one place: virtualization and paging solve
        /// the same problem, and reading AllowPaging directly anywhere else lets the two disagree - a
        /// pager under a virtualized body, or a window taken from within a page.
        /// </summary>
        internal bool Paging => AllowPaging && !AllowVirtualization;

        /// <summary>The underlying Virtualize component, or null when virtualization is off.</summary>
        public Virtualize<TItem>? Virtualize => AllowVirtualization ? virtualize : null;

        /// <summary>
        /// Whether the application says it is fetching rows of its own, which raises the same scrim the
        /// grid raises for its own loads.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A parameter beside the grid's own state rather than instead of it</strong>, for
        /// <see cref="ColumnBase{TItem}.Visible" />'s reason: a component must not assign to its own
        /// parameter, so the runtime's answer lives beside the markup's word. The two are combined
        /// differently, though - a picked visibility <em>overrides</em> the declared one, and these two
        /// are an <c>or</c>, because either party being busy is the grid being busy.
        /// </para>
        /// <para>
        /// <strong>Nothing here has to be passed and this is the exception.</strong> A grid handed an
        /// <c>IQueryable</c> or a <c>LoadData</c> raises and lowers the scrim itself, with nothing to
        /// forget to reset on the failure path - which is why <see cref="IsLoading" /> is read-only. A
        /// page that awaits its own fetch and then assigns <c>Data</c> does the loading outside the grid
        /// entirely, and had no way to say so. §38's application says it on 39 of its 91 grids.
        /// </para>
        /// </remarks>
        [Parameter] public bool Loading { get; set; }

        // Whether one of the grid's own asynchronous loads is in flight. IsLoading is this or the
        // application's word, and everything that asks whether the grid is busy asks that one.
        bool loadingRows;

        /// <summary>
        /// Whether a load is in flight - the grid's own, or one <see cref="Loading" /> declares.
        /// </summary>
        public bool IsLoading => loadingRows || Loading;

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

        /// <summary>
        /// Drops a filter that has just had its editor taken away, and says whether it dropped one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §35, and §32's policy applied to a second way in: a filter that outlives a change of
        /// <c>FilterMode</c> is a filter arriving somewhere it cannot be read, and being unreadable is
        /// what §32 said a filter must not silently be. Guarding
        /// <see cref="ColumnBase{TItem}.FilterSelection" /> stops the crash and leaves the old scalar
        /// filtering rows that no control on the screen explains, with the box that could have cleared
        /// it gone.
        /// </para>
        /// <para>
        /// <strong>Only a transition clears.</strong> §35 said "cleared when the mode changes" and the
        /// build found that the broad reading of it - clearing whenever a filter and a set editor
        /// disagree - is a different rule that breaks a contract this grid already has: a caller asking
        /// <c>ApplyFilters</c> or <c>Filter(column, value)</c> for <c>Equals 3</c> on a column that was
        /// already a check-box list has said what they want, and §14's lookup columns have answered by
        /// applying it and ticking nothing since they were built. Dropping that would be the silent
        /// disappearance §32 exists to refuse.
        /// </para>
        /// <para>
        /// One pass with one reload after it, rather than a clear per column - §31's rule about
        /// <em>Clear all</em>, which is the same shape and the same trap.
        /// </para>
        /// </remarks>
        bool ScreenFilterEditors()
        {
            var dropped = false;

            // Nothing to screen on a grid that does not filter, and nothing to record either - a column
            // that has never drawn an editor cannot have had one taken away.
            if (!AllowFiltering)
            {
                return false;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var mode = FilterModeOf(column);

                // Against what was last drawn, not against what was last asked: the editor the filter
                // has to survive is the one the user was actually looking at. Recorded by RecordEditors
                // as the table draws, and null until a column has been drawn once - so markup that
                // declares both a FilterValue and CheckBoxList keeps the filter it declared.
                if (column.LastEditorMode is not { } previous || previous == mode)
                {
                    continue;
                }

                // The set editor is the only one that is picky: a box renders any single value, and a
                // column with no filter has nothing to screen.
                if (!column.EditedAsSet || column.CurrentFilter is not { } filter
                    || filter.First.Operator.Arity() == FastGridFilterArity.Many)
                {
                    continue;
                }

                column.SetFilter(null, null, null);
                dropped = true;
            }

            return dropped;
        }

        /// <inheritdoc />
        protected override Task OnParametersSetAsync()
        {
            // Before every branch below, because most of them return early and a filter the editor
            // cannot hold has to go whichever one is taken. See ScreenFilterEditors.
            var screened = ScreenFilterEditors();

            // Noted here and applied as the table draws: sorts and filters name columns, and no column
            // has registered yet on the parameter set that precedes the first render.
            // Not the settings this grid just produced: that is its own state coming back, and applying
            // it would be a loop rather than a restore.
            if (!ReferenceEquals(appliedSettings, Settings) && !ReferenceEquals(raisedSettings, Settings))
            {
                appliedSettings = Settings;
                settingsPending = Settings is not null;
            }

            // A dropped filter is a narrower grid, whatever else this parameter set did, so it joins the
            // one thing here that already means "recompose and refetch".
            var pagingChanged = screened;

            if (screened)
            {
                skip = 0;
            }

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

            // A source the grid composes over is a query; anything else is rows. The distinction is what
            // decides whether a new instance means new values.
            var composed = !LoadData.HasDelegate && data is IQueryable<TItem>;

            // Before the LoadData branch, not after: a LoadData grid replaces Data on every load, and a
            // check-box list built from page one is wrong for every page after it.
            //
            // A composed source is the case that must not clear here. Application code answers with a
            // new queryable every time it is read - a DbSet put through AsNoTracking, a Where written in
            // a property - so this identity is not a data change there, and treating it as one puts a
            // SELECT DISTINCT per check-box-list column behind every render the parent does, for values
            // that did not move. Measured at one scan per parameter set before the two were told apart.
            //
            // The consequence, and it is the rule §14's lookups follow deliberately: markup that swaps
            // one query for a genuinely different one goes on offering the first one's values until
            // Reload(). That is what Reload() is for, and its own comment has always said so.
            if (dataChanged && !composed)
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
                if (AllowVirtualization)
                {
                    return Task.CompletedTask;
                }

                // The handler is told the sorts and filters, which are read off the columns - so before
                // the first render it would be told there are none, and a grid whose markup declares a
                // filter would have its handler asked for an unfiltered page and never asked again.
                if (!drawn)
                {
                    OweLoad();

                    return Task.CompletedTask;
                }

                return InvokeLoadDataAsync();
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

            // The same before the first render, and for the same reason: LoadPageAsync composes the
            // filter and the sort onto the query before it hands it to the executor, so composing here
            // would send one query that carried neither and nothing would follow to correct it.
            if (!drawn && AsyncOwnsData)
            {
                OweLoad();

                return Task.CompletedTask;
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

            // And the same for a lookup column's names, which nothing else invalidates: a category
            // added while the grid was open would otherwise never appear, and a cache with no
            // invalidation at all is what produces that report.
            //
            // Before the columns, not after: dropping one resolves it again on the spot, and it would
            // find what it just dropped still sitting here.
            sharedNames.Clear();

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].DropNames();
            }

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

            // A window is fetched without going through the reload funnel, and this method reads the
            // composition twice - once for the rows and once for the total. One stamp for both, and the
            // options are taken into a local rather than read twice off the property: a virtualized grid
            // re-renders freely while its provider is in flight, and a draw pass or a parameter set
            // landing inside the await below restamps the field. The count would then have been taken at
            // a newer instant than the rows it is counting - the window is last week's and the scrollbar
            // is this week's, once a day, with no reproduction.
            StampFilterClock();
            DropStaleTotal();

            var options = Options;

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
                var source = (IQueryable<TItem>)Compose(queryable, options);

                try
                {
                    // request.CancellationToken already covers a superseded scroll, so a cancelled
                    // window propagates out to Virtualize rather than being swallowed here.
                    var window = await async.ToListAsync(source.Skip(request.StartIndex).Take(top),
                        request.CancellationToken);

                    if (virtualTotal is null)
                    {
                        // Counted without the ordering on it. A count wraps the query in
                        // GroupBy(_ => 1).Select(g => g.Count()), and a trailing ORDER BY inside that
                        // aggregate is what a provider is entitled to refuse to translate - SQL Server
                        // rejects it outright. LoadPageAsync counts the filtered query for this reason
                        // and this path was counting the sorted one.
                        virtualTotal = await async.CountAsync(
                            Composition.Filter(columns, queryable, options), request.CancellationToken);

                        countedOn = options.Now.Date;
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

            var rows = Compose(Data ?? Enumerable.Empty<TItem>(), options);

            // Same reason as the query above: counting a filtered in-memory sequence walks it, and
            // scrolling must not walk the whole source once per window.
            if (virtualTotal is null)
            {
                virtualTotal = TotalCount();
                countedOn = options.Now.Date;
            }

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

            var index = IndexInWindow(item);

            return index < 0 ? -1 : virtualWindowStart + index;
        }

        /// <summary>Where an item sits in the rendered window, or -1.</summary>
        /// <remarks>
        /// By identity for a reference type rather than by <c>IndexOf</c>, which compares with
        /// <see cref="EqualityComparer{T}.Default" />: a record - or anything else overriding Equals -
        /// makes two distinct rows of one window answer to the same index. The second then takes the
        /// first's number and the index it should have had is never written at all, so the cursor
        /// lands on the wrong row, or on a row that cannot be found and none at all. A value type has
        /// nothing but its value to be told apart by, so there this is IndexOf and the ambiguity
        /// belongs to the data.
        /// </remarks>
        int IndexInWindow(TItem item)
        {
            // typeof rather than `default(TItem) is not null`, which answers null for a Nullable<T>
            // and would send every int? row down the identity path to be compared against freshly
            // boxed values it can never be the same object as. Those grids resolved no row at all.
            if (typeof(TItem).IsValueType)
            {
                return virtualWindow!.IndexOf(item);
            }

            for (var i = 0; i < virtualWindow!.Count; i++)
            {
                if (ReferenceEquals(virtualWindow[i], item))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Filters a column by a value, and reloads. Passing null for the operator restores the column's
        /// default - Contains for a string column, Equals otherwise.
        /// </summary>
        public Task Filter(ColumnBase<TItem> column, object? value, FilterOperator? filterOperator = null)
            => Filter(column, value, filterOperator?.Owned(), text: null);

        /// <summary>
        /// The same, in this grid's own vocabulary - which is the one that can say <c>Between</c>.
        /// </summary>
        public Task Filter(ColumnBase<TItem> column, object? value, FastGridFilterOperator? filterOperator)
            => Filter(column, value, filterOperator, text: null);

        /// <summary>Filters a column by a whole filter - one condition or two - and reloads.</summary>
        public Task Filter(ColumnBase<TItem> column, FastGridFilter? filter)
        {
            if (column is null || !column.CanFilter)
            {
                return Task.CompletedTask;
            }

            column.SetFilter(filter, text: null);

            skip = 0;

            return RefreshAsync();
        }

        /// <summary>
        /// The same, recording what was typed to produce the value - or null for a filter that came from
        /// anywhere else, which is what leaves the next thing typed applying even when it repeats.
        /// </summary>
        /// <remarks>
        /// The text is recorded here rather than by the caller afterwards, because the reload below
        /// announces the settings and the text is part of them: a lookup column's box filtering by a
        /// name nothing answers to is an <c>In</c> over no ids, and so is a check-box list with nothing
        /// ticked. Recorded after the announcement, the two are stored identically and the grid comes
        /// back showing every row.
        /// </remarks>
        Task Filter(ColumnBase<TItem> column, object? value, FastGridFilterOperator? filterOperator,
            string? text)
        {
            if (column is null || !column.CanFilter)
            {
                return Task.CompletedTask;
            }

            column.SetFilter(value, filterOperator, text);

            // A narrower set has different pages; the row that was on page 3 may not exist any more.
            skip = 0;

            return RefreshAsync();
        }

        /// <summary>
        /// Applies what was typed into a column's filter box. The text is converted to the column's
        /// property type; text that is not a value of that type filters nothing rather than throwing,
        /// which is what a half-typed date or number looks like.
        /// </summary>
        Task OnFilterInput(ColumnBase<TItem> column, string? text) =>
            Filter(column, column.FilterValueFromText(text), filterOperator: null, text);

        /// <summary>The filter presentation this column actually uses.</summary>
        internal FilterMode FilterModeOf(ColumnBase<TItem> column) => column.FilterMode ?? FilterMode;

        readonly Dictionary<ColumnBase<TItem>, IEnumerable> lookups = new();

        /// <summary>
        /// The values a column's check-box-list offers. Cached per column and dropped whenever the data
        /// changes, so the distinct query runs once rather than on every render of the filter row.
        /// </summary>
        internal IEnumerable FilterLookup(ColumnBase<TItem> column)
        {
            // Before FilterLookupData, which a lookup column rejects outright rather than silently
            // losing - a column that has its own values has nothing to scan for and nothing to be
            // supplied with.
            if (column.FilterValues is { } own)
            {
                return own;
            }

            // Through FilterEntries from here down, which is where §36's blank joins the list. Not on
            // the branch above: a column answering with its own values has already put one there if it
            // offers one, which is what LookupColumnBase has done since §14 and what the enum arm on
            // ColumnBase.FilterValues now does too.
            // FilterEntries caches on the reference it is handed, so a FilterLookupData written as a
            // per-render expression rebuilds the entry list every render - and hands the list control a
            // new Data identity with it. The same lifetime rule §14 gives a Lookup and §10 gives Data:
            // hold the instance. Documented on the parameter.
            if (column.FilterLookupData is { } supplied)
            {
                return column.FilterEntries(supplied);
            }

            if (lookups.TryGetValue(column, out var cached))
            {
                return column.FilterEntries(cached);
            }

            // The same rule View() and TotalCount() follow: a source the executor owns is not touched
            // from the render thread. Running the distinct query here is a blocking round trip inside
            // BuildRenderTree, and on Entity Framework a second operation on a context that the awaited
            // page load is still using. The values are fetched after the render instead, and the column
            // offers nothing until they arrive.
            if (AsyncOwnsData)
            {
                pendingLookups.Add(column);

                // Not through FilterEntries: there is nothing to add a blank to yet, so a nullable
                // column shows no entry at all until the scan lands and then gains one. The alternative
                // is a list holding only a blank, which reads as "the only value here is nothing"
                // rather than as "still loading".
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

            return column.FilterEntries(materialized);
        }

        readonly Dictionary<object, object> sharedNames = new();

        /// <summary>
        /// The names a lookup has already been resolved to on this grid, or null for one nobody has.
        /// </summary>
        /// <remarks>
        /// Two columns over the same table is the ordinary case rather than the exotic one -
        /// CreatedByUserId and ApprovedByUserId both resolve against users - and per-column ownership
        /// would build the map twice and hold it twice. A grid-level registry would share it at the
        /// cost of a second thing to declare and a name to get wrong, and would make a column's meaning
        /// non-local; keying on the lookup value itself needs neither.
        /// <para>
        /// Only what is resolved without a fetch ever lands here. A query lookup's members include
        /// <c>Expression</c>s, which are a fresh object graph on every evaluation and do not override
        /// <c>Equals</c>, so two of them never match whatever the call site does - which costs a lookup
        /// shared by two columns one extra fetch at startup and nothing afterwards, because a column
        /// resolves once.
        /// </para>
        /// </remarks>
        internal object? SharedNames(object lookup) =>
            sharedNames.TryGetValue(lookup, out var names) ? names : null;

        /// <summary>Records what a lookup resolved to, for the next column declaring the same one.</summary>
        internal void ShareNames(object lookup, object names) => sharedNames[lookup] = names;

        readonly HashSet<ColumnBase<TItem>> pendingNameColumns = new();

        /// <summary>
        /// Records that a lookup column has no names yet. Called from the render; the fetch happens
        /// after it, for the same reason the check-box list's scan does.
        /// </summary>
        internal void QueueNames(ColumnBase<TItem> column) => pendingNameColumns.Add(column);

        /// <summary>Whether any column on the page is still waiting for the names it draws.</summary>
        internal bool NamesOutstanding
        {
            get
            {
                for (var i = 0; i < visibleColumns.Count; i++)
                {
                    if (visibleColumns[i].NamesOutstanding)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        CancellationTokenSource? lifetime;

        /// <summary>
        /// Cancelled when the grid goes away, and by nothing else.
        /// </summary>
        /// <remarks>
        /// Deliberately not the page load's token, which the check-box list's scan uses. That scan is
        /// about the data and is stale the moment a newer load replaces it; a lookup column's names
        /// are not - only <see cref="Reload" /> drops them - so a sort landing mid-fetch would throw
        /// away an answer that was still correct, and the render that superseded it has already
        /// happened, so nothing would ask again.
        /// </remarks>
        CancellationToken Lifetime => (lifetime ??= new CancellationTokenSource()).Token;

        /// <summary>
        /// Fetches the names of any lookup column that asked for them during the render. Distinct from
        /// <see cref="LoadLookupsAsync" /> beside it, which scans the data for a check-box list's
        /// values: a lookup column runs no such scan and these are the names its cells draw.
        /// </summary>
        async Task LoadColumnNamesAsync()
        {
            if (pendingNameColumns.Count == 0)
            {
                return;
            }

            var wanted = pendingNameColumns.ToList();

            pendingNameColumns.Clear();

            var redraw = false;

            foreach (var column in wanted)
            {
                if (await column.FetchNamesAsync(Executor, Lifetime))
                {
                    redraw = true;
                }
            }

            // A column whose answer was dropped while it ran comes back with no names and asks to be
            // redrawn anyway: the render is what puts it back on the queue, and without one it would
            // wait for a fetch nobody is going to start.
            if (redraw && !disposed)
            {
                StateHasChanged();
            }
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
            // Before anything below reads the rows, and before the flag is cleared: every path here is
            // allowed to compose from the column list because this one has run.
            drawn = true;

            // §39, and before the two branches below rather than after them: what it reads sets
            // settingsPending, and the render that answers StateHasChanged is what applies it. Landing
            // after the reload branches would compose the owed load off the pre-restore state and then
            // need a second one to correct it.
            //
            // The first render only. A key that arrives later is a case this does not serve - see
            // §39's *Where this could still be wrong*, which records what the playground found when
            // it tried: the read happens, and the restored state does not reach the composed view.
            if (firstRender)
            {
                await RestoreStoredSettingsAsync();
            }

            // A grid composing over a queryable in memory has already drawn the restored state - the
            // render that applied it composed from it. One that loads its data has not: the load that
            // produced what is on screen ran before the settings existed.
            if (settingsNeedReload)
            {
                settingsNeedReload = false;

                // A restore composes the same query the owed load would have, off the same columns. One
                // reload serves both, and running them in turn would send the second for nothing.
                loadOwed = false;

                // Not announced. Restored state is state the grid was handed, not a change its user
                // made - the same argument ClearSettings makes, and §39 gave it teeth: raising here
                // also *stores*, so a grid on LoadData read its key and then immediately wrote it back
                // having done nothing. The loadOwed branch below has always said this for its own case.
                await RefreshAsync(announce: false);
            }
            else if (loadOwed)
            {
                loadOwed = false;

                // The funnel, not a second copy of its dispatch: it picks the route that owns this
                // source, re-reading a world the deferral may have changed - virtualization switched on,
                // or a source that has stopped being one the executor owns - and each of its branches
                // either starts a load or lowers the scrim. Announcing is off because being handed a
                // first page is not a setting anyone chose. Reached only with `drawn` already true, so
                // the guard above cannot send it straight back here.
                await RefreshAsync(announce: false);
            }

            if (ClampPage())
            {
                await RefreshAsync();
            }

            await SyncPagersAsync();

            // After the pagers, so the rows the listener will resolve are the ones now on screen.
            await SyncClicksAsync();

            await SyncNavigationAsync();

            // §29's order, arrived at from the other direction: the click set the target column and
            // rendered, and only now does the panel have a body for the browser to measure. Opening
            // from the click itself hands Radzen.openPopup an empty div and positions it for a size it
            // does not have.
            if (menuPending >= 0)
            {
                await OpenPendingFilterMenuAsync();
            }

            // §39's band menu, on the same terms: the click set the flag and rendered, and only now is
            // there a list for the browser to measure.
            if (gridMenuPending)
            {
                await OpenPendingGridMenuAsync();
            }

            // Before the focus is put back, because a fit changes how wide every column is and
            // bringing the cursor's cell into view is measured against exactly that.
            //
            // And before the names below it, which is the ordering the lookup deferral rests on: the
            // fit measures what is painted, so it has to run on a render that already has the names
            // rather than on the one they arrive during. Swapped, it would measure the blank cells it
            // exists to avoid and every test would still pass.
            await AutoFitOnFirstRenderAsync();

            // Last, and after every path above that can reload: this is the render the cursor has to be
            // put back on, and a reload started here would move the rows out from under it.
            await ReassertFocusAsync();

            await LoadColumnNamesAsync();

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

            // What was ticked is not always what the column filters by: a lookup column offers names
            // carrying ids, and the ids are what the predicate and the descriptor compare.
            //
            // An empty list is passed through rather than turned into null here: HasFilter is the single
            // rule for what counts as a filter, and nothing ticked is none. A column that reads an empty
            // selection differently - a lookup column's box, where no name answered is an answer -
            // overrides that rule rather than being special-cased here.
            return Filter(column, column.FilterValueFromSelection(sequence), Radzen.FilterOperator.In);
        }

        /// <summary>Clears every column's filter, and reloads.</summary>
        public Task ClearFilters()
        {
            var cleared = false;

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].HasFilter)
                {
                    columns[i].SetFilter(null, null, null);
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
        public IReadOnlyList<CompositeFilterDescriptor> Filters =>
            Composition.Filters(columns, Options.Now)
                ?? (IReadOnlyList<CompositeFilterDescriptor>)Array.Empty<CompositeFilterDescriptor>();

        /// <summary>
        /// Applies a set of descriptors to the columns they name, so a `RadzenDataFilter` or restored
        /// settings can drive the grid. Descriptors naming no column are ignored.
        /// </summary>
        public Task ApplyFilters(IEnumerable<CompositeFilterDescriptor> filters)
        {
            ArgumentNullException.ThrowIfNull(filters);

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].SetFilter(null, null, null);
            }

            foreach (var filter in filters)
            {
                var column = ColumnByFilterPath(filter.Property);

                if (column is null)
                {
                    continue;
                }

                // Through the same reconstruction the settings restore uses, and with null for the text
                // because this path has never had any - a descriptor carries a value and an operator.
                // That is most of why the reconstruction cannot be keyed on the text: the crash §32
                // measured needs no text, and here there is none to be had.
                RestoreFilter(column, filter);
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
        /// The settings a composition depends on, as against the data it composes. A value rather than
        /// three arguments repeated at five call sites, and free to build: three fields on the stack.
        /// </summary>
        CompositionOptions Options =>
            new CompositionOptions(AllowFiltering, FilterCaseSensitivity, LogicalFilterOperator,
                filterNow);

        /// <summary>
        /// What has already been worked out for the render in progress. See <see cref="DrawPass{TItem}" />
        /// for why this is one value rather than the flag and four fields it replaced.
        /// </summary>
        DrawPass<TItem> pass;

        /// <summary>
        /// The filters in force: the pass's own while the table is being drawn, worked out on the spot
        /// outside one. Null when nothing is filtered and null when filtering is switched off, so a
        /// caller holding this does not ask about <see cref="AllowFiltering" /> a second time.
        /// </summary>
        List<CompositeFilterDescriptor>? ActiveFilters() => Composition.ActiveFilters(columns, Options, in pass);

        /// <summary>
        /// Notes the editor each column is being drawn with, for <see cref="ScreenFilterEditors" />.
        /// </summary>
        /// <remarks>
        /// Here rather than in the screening pass itself, because the pass runs before the first render
        /// - when no column has registered - and would therefore never see the editor a filter was
        /// authored in. One enum write per column per draw, which is the cost of the field it writes.
        /// </remarks>
        void RecordEditors()
        {
            if (!AllowFiltering)
            {
                return;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].LastEditorMode = FilterModeOf(columns[i]);
            }
        }

        void BeginDrawing()
        {
            RecordEditors();

            // The draw pass is a composition of its own - the descriptors it opens with are what the
            // in-memory route then filters by - so it gets its own stamp. See StampFilterClock.
            StampFilterClock();

            pass = DrawPass<TItem>.Begin(AllowFiltering ? Composition.Filters(columns, Options.Now) : null);
        }

        void EndDrawing() => pass = default;

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

            // A reset must not reach further than the restore below it. Both are keyed on the column's
            // identity, and a column without one is never stored - so clearing its filter or its sort
            // discards state that nothing below can put back, and the column loses what its markup
            // declared.
            //
            // Which columns those are has changed with §27 and the set has shrunk: every column bound to
            // a member now has an identity, where before a CollectionColumn or a lookup with no SortBy
            // had none. What is left is a template column declaring neither a UniqueID nor a sort, and a
            // column over a computed expression declaring no UniqueID.
            sorts.RemoveAll(entry => entry.Column.Identity.HasName);

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Identity.HasName)
                {
                    columns[i].SetFilter(null, null, null);
                }
            }

            // Walked in the stored order, not the columns' - it is what records the sort's precedence.
            foreach (var stored in settings.Columns)
            {
                if (stored?.UniqueID is not { Length: > 0 } name)
                {
                    continue;
                }

                var column = ColumnForIdentity(name);

                if (column is null)
                {
                    continue;
                }

                if (stored.SortOrder is { } order && column.CanSort)
                {
                    sorts.Add((column, order == SortOrder.Descending));
                }

                // The text goes with the value: it is what says the value came from the box, and on a
                // lookup column it is the only thing that tells a name nothing answered to from a list
                // with nothing ticked - both are In over no ids.
                RestoreFilter(column, stored);

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

        /// <summary>
        /// Puts one stored filter back on its column, or leaves the column unfiltered when the stored
        /// filter cannot be rebuilt.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both restore paths come through here so that the rule is written once. Neither used to have
        /// one: they handed the stored value straight to <c>SetFilter</c>, and a value that had been
        /// through a serializer made the typed builders decline, which routed it to the reflective one
        /// and threw inside the render. §32 has the matrix.
        /// </para>
        /// <para>
        /// The operator is read before the value, because for four of them the value is not the filter.
        /// An <c>IsNull</c> stores a null and means it, and the guard that used to stand here -
        /// <c>stored.FilterValue is not null</c> - read that null as "no filter stored" and dropped it.
        /// So an IsNull filter was written on every capture and restored on none.
        /// </para>
        /// <para>
        /// Dropping is deliberate rather than defensive: this is a restore, and one that cannot read a
        /// column has no business taking the page down with it. The cost is that the next capture writes
        /// the unreadable filter away for good, which §32 argues is better than a settings blob that
        /// stops describing the grid.
        /// </para>
        /// </remarks>
        static void RestoreFilter(ColumnBase<TItem> column, object? value,
            FastGridFilterOperator? filterOperator, string? text)
        {
            var only = filterOperator ?? column.DefaultFilterOperator;

            if (only.Arity() == FastGridFilterArity.None)
            {
                column.SetFilter(new FastGridFilter(new FastGridFilterCondition(only)), text: null);

                return;
            }

            if (column.RestoredFilterValue(value, filterOperator, text) is { } restored)
            {
                column.SetFilter(restored, filterOperator, text);
            }
        }

        /// <summary>Writes a column's filter into the settings it is stored as.</summary>
        /// <remarks>
        /// Canonical text rather than the values themselves - §33. Nothing is written for a column that
        /// is not filtered, which is what keeps a settings blob describing choices rather than defaults.
        /// </remarks>
        static void StoreFilter(ColumnBase<TItem> column, FastGridColumnSettings settings)
        {
            if (!column.HasFilter || column.CurrentFilter is not { } filter)
            {
                return;
            }

            settings.FilterValues = FilterValueText.From(filter.First);
            settings.FilterOperator = filter.First.Operator;
            settings.FilterText = column.AppliedFilterText;
            settings.SecondFilterText = column.AppliedSecondFilterText;

            if (filter.EffectiveSecond is { } second)
            {
                settings.SecondFilterValues = FilterValueText.From(second);
                settings.SecondFilterOperator = second.Operator;
                settings.LogicalFilterOperator = filter.LogicalFilterOperator;
            }
        }

        /// <summary>
        /// Puts one stored filter back on its column, or leaves the column unfiltered when any part of
        /// it cannot be rebuilt.
        /// </summary>
        /// <remarks>
        /// <strong>Any condition that fails takes the whole filter with it</strong>, which §33 argues
        /// against the kinder-looking alternative: <c>In [...] OR IsNull</c> that loses its list would
        /// degrade to <c>IsNull</c> alone, so the grid would show only the blank rows - a narrower and
        /// entirely different answer, presented as the user's. An <c>AND</c> pair losing a half widens
        /// just as silently. §32 chose showing everything over hiding some for a reason nothing explains,
        /// and this is that rule applied to a compound.
        /// </remarks>
        static void RestoreFilter(ColumnBase<TItem> column, FastGridColumnSettings stored)
        {
            if (stored.FilterOperator is not { } filterOperator)
            {
                return;
            }

            if (RestoredCondition(column, filterOperator, stored.FilterValues, stored.FilterText)
                is not { } first)
            {
                return;
            }

            // A second condition that stored no values at all is *absent*, not failed - the two look
            // alike at the end of the rebuild and mean opposite things. Read as a failure it took the
            // whole filter down, so a column carrying an empty second slot came back unfiltered.
            var hasSecond = stored.SecondFilterOperator is { } declared
                && (declared.Arity() == FastGridFilterArity.None
                    || stored.SecondFilterValues is { Count: > 0 }
                    || stored.SecondFilterText is { Length: > 0 });

            if (!hasSecond || stored.SecondFilterOperator is not { } secondOperator)
            {
                column.SetFilter(new FastGridFilter(first), stored.FilterText, stored.SecondFilterText);

                return;
            }

            if (RestoredCondition(column, secondOperator, stored.SecondFilterValues,
                    stored.SecondFilterText) is not { } second)
            {
                return;
            }

            column.SetFilter(
                new FastGridFilter(first, second,
                    stored.LogicalFilterOperator ?? Radzen.LogicalFilterOperator.And),
                stored.FilterText, stored.SecondFilterText);
        }

        /// <summary>One stored condition rebuilt against the column's type, or null when it cannot be.</summary>
        /// <remarks>
        /// The text is the fallback §32 argued for and it is still the last resort, not the first: the
        /// stored values are canonical and culture-free, and the text is one person's typing. It reaches
        /// past what a parse can do on exactly one kind of column - a lookup, where the values are ids
        /// and the text is the name they were picked by.
        /// </remarks>
        static FastGridFilterCondition? RestoredCondition(ColumnBase<TItem> column,
            FastGridFilterOperator filterOperator, IList<string?>? values, string? text)
        {
            // A filter the column's type cannot be compared with is refused rather than declined: see
            // SupportsOperator, where declining is shown to move the crash rather than remove it.
            if (!column.SupportsOperator(filterOperator))
            {
                return null;
            }

            var arity = filterOperator.Arity();

            if (arity == FastGridFilterArity.None)
            {
                return new FastGridFilterCondition(filterOperator);
            }

            if (column.RestoredValues(filterOperator, values) is { } rebuilt)
            {
                return new FastGridFilterCondition(filterOperator, rebuilt);
            }

            return column.FilterValueFromText(text) is { } fromText
                && (arity == FastGridFilterArity.Many) == (fromText is IEnumerable and not string)
                    ? ColumnBase<TItem>.Condition(filterOperator, fromText)
                    : null;
        }

        /// <summary>A pair of bounds as the range they were emitted from, or null if either will not read.</summary>
        static FastGridFilterCondition? RangeFrom(ColumnBase<TItem> column, object? from, object? to)
        {
            var lower = column.RestoredFilterValue(from, FastGridFilterOperator.GreaterThanOrEquals,
                text: null);
            var upper = column.RestoredFilterValue(to, FastGridFilterOperator.LessThanOrEquals, text: null);

            return lower is null || upper is null
                ? null
                : new FastGridFilterCondition(FastGridFilterOperator.Between, new[] { lower, upper });
        }

        /// <summary>An incoming descriptor put back on its column, for the <c>Filters</c> path.</summary>
        /// <remarks>
        /// A descriptor with children is a compound this grid emitted, or one a <c>RadzenDataFilter</c>
        /// built; either way the children are the conditions. A descriptor without them is one condition,
        /// which is every descriptor written before §33.
        /// </remarks>
        static void RestoreFilter(ColumnBase<TItem> column, CompositeFilterDescriptor descriptor)
        {
            var children = descriptor.Filters?.ToList();

            if (children is { Count: > 0 })
            {
                var conditions = children
                    .Select(child => ConditionFrom(column, child))
                    .Where(condition => condition is not null)
                    .Take(2)
                    .ToList();

                if (conditions.Count > 0)
                {
                    column.SetFilter(
                        new FastGridFilter(conditions[0]!,
                            conditions.Count > 1 ? conditions[1] : null,
                            descriptor.LogicalFilterOperator),
                        text: null);
                }

                return;
            }

            if (ConditionFrom(column, descriptor) is { } single)
            {
                column.SetFilter(new FastGridFilter(single), text: null);
            }
        }

        static FastGridFilterCondition? ConditionFrom(ColumnBase<TItem> column,
            CompositeFilterDescriptor descriptor)
        {
            // A child with children of its own is a range: the pair of bounds this grid emits for a
            // Between, joined by And. Read back as two conditions it consumed the compound's second slot
            // and pushed the real second condition out, so Between OR IsNull came back as a bare range -
            // and a capture after that stored GreaterThanOrEquals and LessThanOrEquals, which is the
            // shape degrading a little on every public round trip.
            if (descriptor.Filters?.ToList() is { Count: 2 } bounds
                && descriptor.LogicalFilterOperator == Radzen.LogicalFilterOperator.And
                && bounds[0].FilterOperator == Radzen.FilterOperator.GreaterThanOrEquals
                && bounds[1].FilterOperator == Radzen.FilterOperator.LessThanOrEquals)
            {
                return RangeFrom(column, bounds[0].FilterValue, bounds[1].FilterValue);
            }

            var filterOperator = descriptor.FilterOperator?.Owned() ?? column.DefaultFilterOperator;

            if (filterOperator.Arity() == FastGridFilterArity.None)
            {
                return new FastGridFilterCondition(filterOperator);
            }

            // Through §32's reconstruction, because a descriptor's value is object? and arrives from
            // outside - a RadzenDataFilter, a remote store - so it is no more trustworthy than a blob.
            return column.RestoredFilterValue(descriptor.FilterValue, filterOperator, text: null) is { } value
                ? ColumnBase<TItem>.Condition(filterOperator, value)
                : null;
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

        /// <summary>
        /// The column a stored row names, or null when no column answers to it - a column that has left
        /// the markup since the settings were written, or one whose UniqueID has changed.
        /// </summary>
        /// <remarks>
        /// First match, and that is safe here rather than the fault it used to be: CheckColumnIdentities
        /// has already run for this render, so there is at most one.
        /// </remarks>
        ColumnBase<TItem>? ColumnForIdentity(string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Identity.Name, name, StringComparison.Ordinal))
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

                if (column.Identity.Name is { Length: > 0 } name)
                {
                    var entry = new FastGridColumnSettings
                    {
                        UniqueID = name,
                        SortOrder = descending ? SortOrder.Descending : SortOrder.Ascending,
                        Visible = RecordedVisibility(column),
                        Width = RecordedWidth(column),
                        OrderIndex = RecordedOrderIndex(column),
                    };

                    StoreFilter(column, entry);
                    stored.Add(entry);
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
                    || column.Identity.Name is not { Length: > 0 } name)
                {
                    continue;
                }

                var entry = new FastGridColumnSettings
                {
                    UniqueID = name,
                    Visible = visibility,
                    Width = width,
                    OrderIndex = orderIndex,
                };

                StoreFilter(column, entry);
                stored.Add(entry);
            }

            return new FastGridSettings
            {
                Columns = stored,
                CurrentPage = CurrentPage,
                PageSize = pageSize,
            };
        }

        /// <summary>
        /// Says that something a user chose has changed: to the application, and to the store.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>One method because there are three callers, and the browser found the two that had
        /// drifted.</strong> §39 recorded <em>storing must not be gated on <c>SettingsChanged</c></em> -
        /// a grid with a <see cref="StorageKey" /> and no handler is the ordinary way to use the
        /// storage - and fixed it where <c>RefreshAsync</c> announces. <c>RaiseColumnResized</c> and the
        /// reorder drop announce too, do not reload, and so were not on that path: both still read
        /// <c>if (SettingsChanged.HasDelegate)</c>, and on a grid using the storage the way §39 says it
        /// is meant to be used, <strong>a dragged width and a moved column were never stored</strong>.
        /// Measured in the playground: a resize from 260px to 540px left <c>Columns</c> at <c>[]</c>
        /// while the paging beside it stored <c>CurrentPage</c> the same second. Nothing threw; the
        /// layout simply did not come back.
        /// </para>
        /// <para>
        /// The second half of the drift is quieter. Neither site set <see cref="raisedSettings" />, so
        /// an application that stores what it is handed and passes it back - which is the whole point of
        /// the parameter - had a resize's echo read as an instruction where a sort's was not.
        /// </para>
        /// </remarks>
        Task AnnounceSettings()
        {
            // Either reason to build the object, and §39 added the second: a grid with a StorageKey and
            // no SettingsChanged handler is the ordinary way to use the storage, and gating the capture
            // on the callback alone would store nothing for it.
            if (!SettingsChanged.HasDelegate && StorageKey is not { Length: > 0 })
            {
                return Task.CompletedTask;
            }

            // Remembered so the settings the grid hands out are not then read back as an instruction.
            // An application that stores what it is given and passes it back would otherwise return this
            // object as a parameter change, and a grid that reloads on a settings change would reload,
            // raise, and be handed it again.
            raisedSettings = CaptureSettings();

            // The same object, so what is stored and what the application was handed cannot disagree.
            // Never awaited: this sits on the path of every sort, filter, page, resize and reorder, and
            // a store that goes to a server would otherwise make each of them wait for a round trip.
            _ = StoreSettingsAsync(raisedSettings);

            // The callback is the caller's to await or not, and the two callers disagree for reasons
            // that predate this method - which is why it is returned rather than decided here. The
            // review caught the first draft deciding it: RefreshAsync does not await, on the argument
            // in its own comment, and the extraction quietly applied that to the resize and the reorder
            // as well - both of which awaited, and on which a consumer's handler throwing was observed.
            return SettingsChanged.HasDelegate
                ? SettingsChanged.InvokeAsync(raisedSettings)
                : Task.CompletedTask;
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

            viewGeneration++;

            // Every state change a user can make funnels through here, so a load's relative dates are
            // read once, here, and every route below - the page query, its count, and a LoadData
            // handler's descriptors - agrees about when now was.
            StampFilterClock();

            // Every state change a user can make funnels through here, so this is the one place the
            // grid has to say so - and it is not the render path, which is what keeps a grid nobody is
            // persisting from ever building the object.
            // Not awaited, and that is this method's own long-standing choice rather than the shared
            // one: it is on the path of every sort, filter and page.
            if (announce)
            {
                _ = AnnounceSettings();
            }

            // Every branch below either starts a load that supersedes the one in flight or starts none
            // at all, and the second kind would leave it to land. Cancelling here covers all of them;
            // BeginAsyncLoad installs a fresh token immediately after when there is a load to run.
            CancelLoad();

            // §23's rule, and this is the place that makes it hold rather than the two call sites that
            // first noticed it. Every public way to reload - Reload, ClearFilters, ApplyFilters,
            // GoToPage - funnels through here, and a parent may call any of them from its own
            // OnAfterRenderAsync, which the renderer runs before the grid's. The load would then be
            // composed correctly and still be the first of two, because the owed one is behind it: the
            // application's handler invoked twice, concurrently, which is worse than the reload design
            // §23 rejected for costing exactly that. Owed instead, so there is one load either way.
            // Virtualizing is exempt, as it is everywhere else in §23: the provider composes when it is
            // asked for a window, which is already after the render, so there is nothing here to wait
            // for and owing one would only put a fetch behind the fetch.
            if (!drawn && !AllowVirtualization && (LoadData.HasDelegate || AsyncOwnsData))
            {
                OweLoad();

                return Task.CompletedTask;
            }

            if (AllowVirtualization)
            {
                // A new filter changes how many rows there are, so the cached total goes with it.
                virtualTotal = null;

                // Nothing below this branch lowers the scrim, and a deferral may have raised one: the
                // provider owns fetching from here and never touches loadingRows.
                loadingRows = false;

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
            // asynchronous load produced is all that is needed - and lowering the scrim, because this is
            // the branch that knows nothing will run, and a deferral may have raised one for a source
            // that has since stopped being loadable.
            loaded = null;
            loadedCount = null;
            loadingRows = false;

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

        /// <summary>
        /// Notes that a load must wait for the first render, and marks the grid loading now rather than
        /// when the load starts.
        /// </summary>
        /// <remarks>
        /// The scrim is drawn from <see cref="IsLoading" />, which a load sets as it begins - so a
        /// deferred load would leave the render it defers past showing a grid that is empty and not
        /// loading. That is the render on which <c>RenderEmpty</c> draws <c>EmptyTemplate</c>, which
        /// makes the flash read as "no records" rather than as "not yet". Marked only where a load is
        /// known to follow: a flag raised for a load that never runs is a scrim that never lifts.
        /// </remarks>
        /// <summary>
        /// Forgets a cached row total that a relative filter has outlived.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Before §34 a filter's answer could not change without going through the reload funnel, which
        /// nulls <see cref="virtualTotal" />. A relative one can: a virtualized grid left open across
        /// midnight serves its next window at the new instant while the cached count still holds the
        /// previous day's - a scrollbar and an <c>aria-rowcount</c> that stay wrong until something
        /// reloads. §34 named this failure and located it in <c>ActiveFilter</c>; the carrier is a
        /// cached integer.
        /// </para>
        /// <para>
        /// By the <em>day</em>, not by the stamp. Every anchor is day-granular and <c>@end</c> stays
        /// inside its day, so a token's answer can only change when the local date does - and comparing
        /// stamps instead would recount on every window, which is a query per scroll.
        /// </para>
        /// </remarks>
        void DropStaleTotal()
        {
            if (virtualTotal is not null && Composition.OutlivedTheDay(columns, countedOn, filterNow))
            {
                virtualTotal = null;
            }
        }

        void OweLoad()
        {
            // Whatever is in flight was composed from a column list this grid is about to compose again
            // from a complete one, so it is superseded rather than left to land on top of the answer.
            CancelLoad();

            loadOwed = true;
            loadingRows = true;
        }

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
            var filtered = Composition.Filter(columns, source, Options);
            var ordered = Composition.Sort(sorts, filtered);
            var paged = Paging ? ordered.Skip(skip).Take(pageSize) : ordered;

            loadingRows = true;
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
                    loadingRows = false;
                }
            }

            StateHasChanged();
        }

        Task InvokeLoadDataAsync() => Paging
            ? InvokeLoadDataAsync(skip, pageSize)
            : InvokeLoadDataAsync(null, null);

        async Task InvokeLoadDataAsync(int? start, int? count)
        {
            var filters = Composition.Filters(columns, Options.Now);

            var args = new LoadDataArgs
            {
                Skip = start,
                Top = count,
                OrderBy = OrderBy(),

                // Projected back, because this property is upstream's and typed to FilterDescriptor.
                // The string below is built from the composites themselves. See Composition.Descriptors.
                Filters = Composition.Descriptors(filters),
            };

            args.Filter = FilterString(filters);

            loadingRows = true;

            try
            {
                await LoadData.InvokeAsync(args);
            }
            finally
            {
                loadingRows = false;
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

                if (column.SortPath is not { Length: > 0 } path)
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
        string? FilterString(IEnumerable<CompositeFilterDescriptor>? filters)
        {
            if (filters is null)
            {
                return null;
            }

            // The string form a LoadData handler receives, which is built by walking the descriptors'
            // property paths. There is no typed equivalent: the point of it is to be a string.
            if (!DynamicCode.Supported)
            {
                return null;
            }

            // Handed on as they are. This used to copy each descriptor into a composite field by field
            // and the copy dropped FilterProperty, which is the member of a collection's element that a
            // CollectionColumn filters by - so the string a LoadData handler received compared against
            // the collection itself. §33 made composites the one currency and the copy went with it.
            var text = IsOData()
                ? filters.ToODataFilterString<TItem>(LogicalFilterOperator, FilterCaseSensitivity)
                : filters.ToFilterString<TItem>(LogicalFilterOperator, FilterCaseSensitivity);

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

        /// <summary>
        /// Every row the filters and the sort produce, unpaged - what a reader means by "all of it"
        /// rather than "this page". <see cref="FilteredRows" /> is this; <see cref="View" /> is this
        /// paged.
        /// </summary>
        /// <param name="composed">
        /// Whether the grid is the one that composed them, which is the same question as whether they
        /// are unpaged. Where it is false the rows are one page and there is no more to have.
        /// </param>
        /// <remarks>
        /// The three cases where the grid does not compose are answered here rather than twice: two
        /// callers reading the same three branches is two chances for them to drift, and the ordering
        /// between the first two is load-bearing (below).
        /// </remarks>
        IEnumerable<TItem> Composed(out bool composed)
        {
            composed = false;

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

            composed = true;

            return Compose(data);
        }

        IEnumerable<TItem> View()
        {
            var data = Composed(out var composed);

            return composed && Paging ? Page(data, skip, pageSize) : data;
        }

        /// <summary>
        /// The rows the grid is drawing - one page of them when it is paging, and every row it holds
        /// when it is not.
        /// </summary>
        /// <remarks>
        /// Valid while the grid is between renders and no longer: it composes onto the bound source
        /// rather than holding a copy, so enumerating it after a filter, a sort or a page has moved
        /// answers the new question. Enumerate it, do not keep it.
        /// </remarks>
        public IEnumerable<TItem> DrawnRows => View();

        /// <summary>
        /// Every row the filters and the sort produce, unpaged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same rows as <see cref="DrawnRows" /> wherever the grid did not compose them: a
        /// <c>LoadData</c> handler has already sorted and paged, and the asynchronous executor owns
        /// its query, so in both cases one page is all the grid holds. Asking for more would mean
        /// running a query neither of them ran, and §40 records taking the page as the answer rather
        /// than as a limitation to work around.
        /// </para>
        /// <para>
        /// The same lifetime rule as <see cref="DrawnRows" />, and it matters more here: over a
        /// queryable this is an unpaged database query that runs when something enumerates it.
        /// </para>
        /// </remarks>
        public IEnumerable<TItem> FilteredRows => Composed(out _);

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
        /// Filters and sorts the source, without paging, through <see cref="Composition" /> - and
        /// records which of its two routes ran.
        /// </summary>
        IEnumerable<TItem> Compose(IEnumerable<TItem> data) => Compose(data, Options);

        /// <summary>
        /// The same, under options a caller has already taken - so that a caller composing twice does it
        /// under one stamp. See <see cref="ProvideRows" />, which is the only one that does.
        /// </summary>
        IEnumerable<TItem> Compose(IEnumerable<TItem> data, CompositionOptions options)
        {
            var composed = Composition.Compose(columns, sorts, data, options, ref pass);

            ComposedInMemory = composed.InMemory;

            return composed.Rows;
        }

        /// <summary>
        /// Whether the last composition took the delegate route rather than wrapping the source in a
        /// queryable.
        /// </summary>
        /// <remarks>
        /// The module answers this; the grid only writes down what it was told. That is the whole of
        /// what this property is now, and it is why it still exists after the composition moved out: it
        /// is the one place a test standing outside the grid can see that the grid asked the module and
        /// used the answer. A module can be correct and unused, and this branch's recorded failure mode
        /// is a silent wrong answer, which is exactly that shape.
        /// </remarks>
        internal bool ComposedInMemory { get; private set; }

        /// <summary>
        /// How many rows there are behind whatever is on screen, memoized for the render pass.
        /// </summary>
        /// <remarks>
        /// The pager asks, the page clamp asks, and <c>aria-rowcount</c> asks - and over a plain
        /// sequence each of those is a walk of the source. It is the same memo <c>Composed</c> keeps
        /// and for the same reason: within one pass the answer cannot change, and the count is
        /// independent of which page is being drawn.
        /// </remarks>
        int TotalCount() => pass.Counted(out var counted) ? counted : pass.Keep(CountAll());

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
                return Total(Compose(Data ?? Enumerable.Empty<TItem>()));
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

            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;

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
