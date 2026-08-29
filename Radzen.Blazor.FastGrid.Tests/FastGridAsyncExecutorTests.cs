using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The grid asks an <see cref="IAsyncQueryExecutor" /> to count and materialize a queryable whose
    /// provider supports it, so an Entity Framework page is awaited rather than blocking the thread on
    /// Count() / ToList(). With no executor registered, or one that does not support the queryable,
    /// nothing changes.
    /// </summary>
    public class FastGridAsyncExecutorTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                extra?.Invoke(p);
            });
        }

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        [Fact]
        public void UntouchedWhenNoneIsRegistered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(6).AsQueryable());

            Assert.Equal(6, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void UntouchedWhenTheSourceIsNotAQueryable()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(6));

            Assert.Equal(6, cut.FindAll("tbody tr").Count);
            Assert.Equal(0, executor.ToListCalls);
            Assert.Equal(0, executor.CountCalls);
        }

        [Fact]
        public void UntouchedWhenItDoesNotSupportTheQueryable()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor { Supported = false };
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(6).AsQueryable());

            Assert.Equal(6, cut.FindAll("tbody tr").Count);
            Assert.Equal(0, executor.ToListCalls);
        }

        [Fact]
        public void MaterializesTheQueryableThroughIt()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(6).AsQueryable());

            Assert.Equal(1, executor.ToListCalls);
            Assert.Equal(6, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void DoesNotCountAnUnpagedQueryable()
        {
            // The whole set was materialized, so the list is the count. A second round trip to the
            // database would buy nothing.
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            Render(ctx, People.Many(6).AsQueryable());

            Assert.Equal(0, executor.CountCalls);
        }

        [Fact]
        public void CountsThroughItWhenPaging()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.ShowPagingSummary, true);
            });

            Assert.Equal(1, executor.CountCalls);
            Assert.Equal(new[] { "First1", "First2", "First3", "First4" }, FirstNames(cut));
            Assert.Contains("30", cut.Find(".rz-pager-summary").TextContent, StringComparison.Ordinal);
        }

        [Fact]
        public void CountsTheSourceRatherThanThePage()
        {
            // Counting the paged query would report the page size as the total, and the pager would show
            // one page however much data there is.
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(30, executor.LastCount);
        }

        [Fact]
        public void SortsThroughTheProviderBeforeMaterializing()
        {
            // The ordering must reach the executor as part of the query, not be applied to the page after
            // it comes back - otherwise the database returns an arbitrary page and the grid sorts that.
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.AllowSorting, true);
            });

            cut.FindAll("thead th")[0].QuerySelector("div")!.Click();

            Assert.Contains("OrderBy", executor.LastExpression, StringComparison.Ordinal);
            Assert.Equal(new[] { "First1", "First10", "First11", "First12" }, FirstNames(cut));
        }

        [Fact]
        public void PagingReloadsThroughIt()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.Find(".rz-pager-next").Click();

            Assert.Equal(2, executor.ToListCalls);
            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));
        }

        [Fact]
        public void FiltersThroughTheProviderBeforeMaterializing()
        {
            // The filter must reach the executor as part of the query. Applying it to the page after it
            // comes back would fetch an unfiltered page and then hide most of it.
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            Assert.Contains("Where", executor.LastExpression, StringComparison.Ordinal);
            Assert.Equal(new[] { "First1", "First10", "First11", "First12" }, FirstNames(cut));
        }

        [Fact]
        public void CountsWhatTheFilterLeftRatherThanTheWholeSource()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            // First1 and First10..First19.
            Assert.Equal(11, executor.LastCount);
        }

        [Fact]
        public void CarriesTheCaseSensitivitySettingIntoTheQuery()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Sample().AsQueryable(), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterCaseSensitivity, FilterCaseSensitivity.CaseInsensitive);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");

            Assert.Equal(new[] { "Carol", "Alice", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void CarriesTheLogicalOperatorIntoTheQuery()
        {
            using var ctx = new TestContext();
            var executor = new FakeExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Sample().AsQueryable(), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Carol");
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("4");

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public async Task ASupersededLoadDoesNotOverwriteTheNewerOne()
        {
            // Page 2 is asked for while page 1 is still in flight. If the slow first answer were allowed
            // to land it would replace the newer page with a stale one.
            using var ctx = new TestContext();
            var executor = new GatedExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(30).AsQueryable(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            var first = executor.Pending;
            var page = cut.InvokeAsync(() => cut.Instance.GoToPage(1));
            var second = executor.Pending;

            Assert.NotSame(first, second);

            second!.Release();
            await page;

            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));

            first!.Release();

            // Let the stale load actually resume before checking that it changed nothing: its answer is
            // already computed, so the only thing standing between it and the screen is the grid.
            await first.Completed;
            await cut.InvokeAsync(() => Task.CompletedTask);
            await cut.InvokeAsync(() => Task.CompletedTask);

            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));
        }

        [Fact]
        public void ReportsWhetherALoadIsInFlight()
        {
            using var ctx = new TestContext();
            var executor = new GatedExecutor();
            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            var cut = Render(ctx, People.Many(6).AsQueryable());

            Assert.True(cut.Instance.IsLoading);

            executor.Pending!.Release();

            cut.WaitForAssertion(() => Assert.False(cut.Instance.IsLoading));
        }

        /// <summary>Executes against the in-memory queryable, recording what it was asked to run.</summary>
        class FakeExecutor : IAsyncQueryExecutor
        {
            public bool Supported { get; set; } = true;

            public int ToListCalls { get; private set; }

            public int CountCalls { get; private set; }

            public int LastCount { get; private set; }

            public string LastExpression { get; private set; } = "";

            public bool IsSupported<T>(IQueryable<T> queryable) => Supported;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                CountCalls++;
                LastCount = queryable.Count();

                return Task.FromResult(LastCount);
            }

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                ToListCalls++;
                LastExpression = queryable.Expression.ToString();

                return Task.FromResult(queryable.ToList());
            }
        }

        /// <summary>
        /// Holds each materialization open until the test releases it, and deliberately ignores the
        /// cancellation token: a real executor may finish its query before it observes cancellation, so
        /// the grid cannot rely on the await throwing to discard a superseded answer.
        /// </summary>
        sealed class GatedExecutor : IAsyncQueryExecutor
        {
            public Gate? Pending { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                var gate = new Gate();

                Pending = gate;

                var task = gate.Source.Task.ContinueWith(_ => queryable.ToList(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                gate.Completed = task;

                return task;
            }

            public sealed class Gate
            {
                public TaskCompletionSource<bool> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

                /// <summary>Completes when this materialization has produced its answer.</summary>
                public Task Completed { get; set; } = Task.CompletedTask;

                public void Release() => Source.TrySetResult(true);
            }
        }
    }
}
