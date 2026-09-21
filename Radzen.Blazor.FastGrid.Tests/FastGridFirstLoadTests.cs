using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What a column declares reaches the first load, on every route. §23: a load composes from the
    /// column list, and the first one used to be started from the grid's parameter set - before any
    /// column had registered - so the first query a grid sent carried nothing its markup declared.
    /// </summary>
    /// <remarks>
    /// Four faces of one cause, and two controls. The two routes that compose after the render - in
    /// memory as the table draws, virtualized when the window is asked for - were never wrong, and are
    /// here so that a fix which moves the fault rather than removing it is visible.
    /// </remarks>
    public class FastGridFirstLoadTests
    {
        /// <summary>Records the expression of every query it is asked to materialize.</summary>
        sealed class RecordingExecutor : IFastGridQueryExecutor
        {
            public List<string> Materialized { get; } = new();

            /// <summary>Raised as a query is materialized, for tests that assert on ordering.</summary>
            public Action? OnQuery { get; set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                Materialized.Add(queryable.Expression.ToString());
                OnQuery?.Invoke();

                return Task.FromResult(queryable.ToList());
            }
        }

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr")
                .Where(row => row.QuerySelectorAll("td").Length > 0)
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        /// <summary>
        /// A grid whose first column declares a filter, a sort, or both - which is the whole of what
        /// this section is about, so every test here declares them in markup and never clicks anything.
        /// </summary>
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            IEnumerable<Person> data, object? filterValue = null, SortOrder? sortOrder = null,
            bool virtualized = false)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.AllowVirtualization, virtualized);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, filterValue: filterValue,
                        sortOrder: sortOrder),
                    Columns.Property<Person, int>(x => x.Id)));
            });
        }

        static (IRenderedComponent<RadzenFastGrid<Person>> Grid, RecordingExecutor Executor) Executed(
            TestContext ctx, object? filterValue = null, SortOrder? sortOrder = null)
        {
            var executor = new RecordingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            return (Render(ctx, People.Sample().AsQueryable(), filterValue, sortOrder), executor);
        }

        [Fact]
        public void ADeclaredFilterReachesTheFirstQueryTheExecutorRuns()
        {
            using var ctx = new TestContext();

            var (cut, executor) = Executed(ctx, filterValue: "Alice");

            // The query itself, not merely the rows: composing in the grid and filtering afterwards
            // would draw the same table and still send a whole unfiltered set across the wire.
            Assert.Contains("Where(", Assert.Single(executor.Materialized), StringComparison.Ordinal);
            Assert.Equal(new[] { "Alice" }, FirstNames(cut));
        }

        [Fact]
        public void ADeclaredSortReachesTheFirstQueryTheExecutorRuns()
        {
            using var ctx = new TestContext();

            var (cut, executor) = Executed(ctx, sortOrder: SortOrder.Ascending);

            Assert.Contains("OrderBy(", Assert.Single(executor.Materialized), StringComparison.Ordinal);
            Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void TheHeaderNoLongerClaimsASortTheRowsAreNotIn()
        {
            // The half of this that a user sees. Unsorted rows under aria-sort="ascending" is the grid
            // telling them it has sorted, which is worse than not having sorted.
            using var ctx = new TestContext();

            var (cut, _) = Executed(ctx, sortOrder: SortOrder.Ascending);

            Assert.Equal("ascending", cut.FindAll("thead th")[0].GetAttribute("aria-sort"));
            Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void TheDeclaredStateCostsNoSecondQuery()
        {
            // §23 chose deferring the load over reloading after registration, and this is the
            // difference between them: a reload would send one query composed from nothing first.
            using var ctx = new TestContext();

            var (_, executor) = Executed(ctx, filterValue: "Alice", sortOrder: SortOrder.Ascending);

            Assert.Single(executor.Materialized);
        }

        [Fact]
        public void TheGridIsLoadingOnTheRenderItDefersPast()
        {
            // §23's one concession to the deferral: a load owed is not a load running, and IsLoading is
            // what draws the scrim. Marked when the load is deferred rather than when it starts, the
            // first render is covered; marked only by the load, it is a bare empty grid for one frame.
            //
            // Order rather than presence, because both orderings end with the scrim drawn. What
            // separates them is whether it was drawn before the executor was asked for anything:
            // LoadPageAsync sets IsLoading and calls StateHasChanged, which queues a render rather than
            // running one, so the query goes out first when nothing marked it earlier.
            using var ctx = new TestContext();
            var order = new List<string>();
            var executor = new RecordingExecutor { OnQuery = () => order.Add("query") };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.LoadingTemplate, (RenderFragment)(b => order.Add("scrim")));
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
            });

            Assert.Equal("scrim", order.FirstOrDefault());
        }

        [Fact]
        public void ARestoredSettingsSortCostsOneQuery()
        {
            // Two flags can want a load on the same render: ApplySettings runs during it and asks for a
            // reload, and the first load is owed from before it. The settings branch subsumes the owed
            // one - and nothing in the suite covered a settings restore over a source that loads at all,
            // so both firing would have gone out as two queries with every test still green.
            using var ctx = new TestContext();
            var executor = new RecordingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.Settings, new FastGridSettings
                {
                    Columns = new List<FastGridColumnSettings>
                    {
                        new() { UniqueID = "First", SortOrder = SortOrder.Descending },
                    },
                });
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
            });

            // The restore reached the query, and it was the only query.
            Assert.Contains("OrderByDescending(", Assert.Single(executor.Materialized),
                StringComparison.Ordinal);
            Assert.Equal(new[] { "Dave", "Carol", "Bob", "Alice" }, FirstNames(cut));
        }

        [Fact]
        public void ADeclaredFilterReachesTheFirstLoadDataCall()
        {
            using var ctx = new TestContext();
            var calls = new List<LoadDataArgs>();

            RenderLoadData(ctx, calls, filterValue: "Alice");

            var filter = Assert.Single(Assert.Single(calls).Filters!);

            Assert.Equal("First", filter.Property);
            Assert.Equal("Alice", filter.FilterValue);
        }

        [Fact]
        public void ADeclaredSortReachesTheFirstLoadDataCall()
        {
            using var ctx = new TestContext();
            var calls = new List<LoadDataArgs>();

            RenderLoadData(ctx, calls, sortOrder: SortOrder.Ascending);

            Assert.Equal("First asc", Assert.Single(calls).OrderBy);
        }

        static void RenderLoadData(TestContext ctx, List<LoadDataArgs> calls,
            object? filterValue = null, SortOrder? sortOrder = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var all = People.Sample();

            ctx.RenderComponent<LoadDataHost>(p =>
            {
                p.Add(h => h.AllowFiltering, true);
                p.Add(h => h.AllowSorting, true);
                p.Add(h => h.Columns, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, filterValue: filterValue,
                        sortOrder: sortOrder),
                    Columns.Property<Person, int>(x => x.Id)));
                p.Add(h => h.OnLoad, (args, host) =>
                {
                    calls.Add(args);
                    host.Serve(all, all.Count);
                });
            });
        }


        /// <summary>A parent that reloads the grid from its own first OnAfterRenderAsync.</summary>
        sealed class EagerReloadHost : ComponentBase
        {
            [Parameter] public IEnumerable<Person> Data { get; set; } = default!;

            public RadzenFastGrid<Person>? Grid { get; private set; }

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<RadzenFastGrid<Person>>(0);
                builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), Data);
                builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.ChildContent), Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                builder.AddComponentReferenceCapture(3, o => Grid = (RadzenFastGrid<Person>)o);
                builder.CloseComponent();
            }

            protected override async Task OnAfterRenderAsync(bool firstRender)
            {
                if (firstRender)
                {
                    await Grid!.Reload();
                }
            }
        }

        [Fact]
        public void APublicReloadBeforeTheGridHasDrawnDoesNotAddASecondLoad()
        {
            using var ctx = new TestContext();
            var executor = new RecordingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            ctx.RenderComponent<EagerReloadHost>(p => p.Add(h => h.Data, People.Sample().AsQueryable()));

            Assert.Single(executor.Materialized);
        }

        // The two routes that compose after the render, which were never wrong. They are the control:
        // a fix that moved the composition point rather than deferring the load would break these.
        [Fact]
        public void TheInMemoryRouteAppliesBothOnItsFirstDraw()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), filterValue: "a", sortOrder: SortOrder.Descending);

            Assert.Equal(new[] { "Dave", "Carol" }, FirstNames(cut));
        }

        [Fact]
        public void TheVirtualizedRouteAppliesBothOnItsFirstFetch()
        {
            using var ctx = new TestContext();
            var executor = new RecordingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, People.Sample().AsQueryable(), filterValue: "a",
                sortOrder: SortOrder.Descending, virtualized: true);

            var query = Assert.Single(executor.Materialized);

            Assert.Contains("Where(", query, StringComparison.Ordinal);
            Assert.Contains("OrderByDescending(", query, StringComparison.Ordinal);
            Assert.Equal(new[] { "Dave", "Carol" }, FirstNames(cut));
        }
    }
}
