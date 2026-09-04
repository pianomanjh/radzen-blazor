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
    /// A column that displays a name and carries an id. The row holds integers and the names are held
    /// once for the grid, so a thousand rows with a category each are a thousand ints and one lookup.
    /// </summary>
    public class LookupColumnTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            IEnumerable<Person> data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });
        }

        static string[] Cells(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        [Fact]
        public void ACellShowsTheNameTheRowsIdStandsFor()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))));

            Assert.Equal(new[] { "Toys", "Games", "Toys", "Puzzles" }, Cells(cut, 0));
        }

        [Fact]
        public void NamesCanComeFromASequenceAlreadyInMemory()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name))));

            Assert.Equal(new[] { "Toys", "Games", "Toys", "Puzzles" }, Cells(cut, 0));
        }

        // Two different failures, and the cheapest possible place to stop them looking the same.
        [Fact]
        public void ANullKeyRendersAnEmptyCell()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int?>(x => x.RegionId,
                FastGridLookup.Map(Lookups.Regions()))));

            Assert.Equal(new[] { "North", "", "South", "North" }, Cells(cut, 0));
        }

        [Fact]
        public void AnIdWithNoEntryRendersTheIdItself()
        {
            // A deleted row, a lookup narrowed by a Where, or a stale cache. It is a fault, and the id
            // is the only thing that lets anyone diagnose it.
            using var ctx = new TestContext();
            var data = People.Sample();

            data[0].CategoryId = 99;

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))), data: data);

            Assert.Equal(new[] { "99", "Games", "Toys", "Puzzles" }, Cells(cut, 0));
        }

        // The one case with a gap between the first paint and the names, and the only one a test can
        // see it in: a synchronous render is already settled by the time it returns.
        [Fact]
        public void AQueryLookupLeavesTheCellsBlankUntilItArrives()
        {
            using var ctx = new TestContext();
            var executor = new GatedExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            // Rows in memory, names over a queryable: what decides is the lookup's own source, so this
            // grid must not touch the database from the render thread either.
            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            Assert.Equal(new[] { "", "", "", "" }, Cells(cut, 0));

            executor.Pending.Release();

            cut.WaitForAssertion(() =>
                Assert.Equal(new[] { "Toys", "Games", "Toys", "Puzzles" }, Cells(cut, 0)));
        }

        [Fact]
        public void AQueryLookupFetchesIdsAndNamesRatherThanWholeRows()
        {
            // The reason Query takes expressions and Items takes delegates: only this one composes into
            // the provider's own query, and a lookup that materialized entities would not need them.
            using var ctx = new TestContext();
            var executor = new GatedExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            // NotNull first: without it this passes for a lookup that was never fetched at all, which
            // is the false positive §9 keeps finding.
            Assert.NotNull(executor.LastElementType);
            Assert.NotEqual(typeof(Category), executor.LastElementType);
        }

        /// <summary>Holds each materialization open until the test releases it.</summary>
        sealed class GatedExecutor : IFastGridQueryExecutor
        {
            public Gate Pending { get; private set; }

            public Type LastElementType { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
            {
                var gate = new Gate();

                Pending = gate;
                LastElementType = queryable.ElementType;

                return gate.Source.Task.ContinueWith(_ => queryable.ToList(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            public sealed class Gate
            {
                public TaskCompletionSource<bool> Source { get; } =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);

                public void Release() => Source.TrySetResult(true);
            }
        }
    }
}
