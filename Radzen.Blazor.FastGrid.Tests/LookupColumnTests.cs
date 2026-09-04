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
            var executor = new GatedLookupExecutor();

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
            var executor = new GatedLookupExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            // NotNull first: without it this passes for a lookup that was never fetched at all, which
            // is the false positive §9 keeps finding.
            Assert.NotNull(executor.LastElementType);
            Assert.NotEqual(typeof(Category), executor.LastElementType);
        }

        [Fact]
        public void ALookupThatFailsToFetchDrawsItsIdsRatherThanTakingTheGridDown()
        {
            // The rows are drawn and correct and only the names are missing, so the grid stays up - and
            // resolves to no names at all, which draws every id. That is the same thing a missing entry
            // draws, for the same reason: a blank column would be a fault nobody can see.
            using var ctx = new TestContext();
            var executor = new GatedLookupExecutor { Fails = new InvalidOperationException("no") };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            executor.Pending.Release();

            cut.WaitForAssertion(() => Assert.Equal(new[] { "10", "20", "10", "30" }, Cells(cut, 0)));
        }

        [Fact]
        public void FetchingNamesCostsOneExtraRenderAndNoMore()
        {
            // A column that comes back with no names asks to be redrawn so that it can queue itself
            // again, which is a render feeding a fetch feeding a render. The bound is that an answer -
            // including an empty one - is an answer: this grid's own history has a render loop that
            // ran 880,000 times in two and a half seconds and logged nothing.
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new GatedLookupExecutor { Holds = 0 });

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            Assert.Equal(new[] { "Toys", "Games", "Toys", "Puzzles" }, Cells(cut, 0));
            Assert.InRange(cut.RenderCount, 1, 2);
        }

        [Fact]
        public void ASortLandingWhileTheNamesAreInFlightDoesNotThrowThemAway()
        {
            // The fetch is cancelled by the grid going away and by nothing else. A page load being
            // superseded says nothing about a lookup - only Reload drops those - and the render that
            // superseded it has already happened, so an answer thrown away here is one nobody asks for
            // again.
            using var ctx = new TestContext();
            var executor = new GatedLookupExecutor { PassThrough = typeof(Person) };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))),
                extra: p => p.Add(g => g.AllowSorting, true),
                data: People.Sample().AsQueryable());

            cut.FindAll("thead th")[0].QuerySelector("div").Click();

            executor.Pending.Release();

            cut.WaitForAssertion(() => Assert.DoesNotContain("", Cells(cut, 1)));

            Assert.Equal(1, executor.Materializations);
        }

        [Fact]
        public void NamesDroppedWhileTheyWereInFlightAreFetchedAgain()
        {
            // Reload is the one thing that drops them, and an answer that arrives afterwards is about
            // a lookup nobody is showing. Written back, it would also stop the column ever asking
            // again - so the count is what this turns on, not the cells.
            using var ctx = new TestContext();
            var executor = new GatedLookupExecutor { Holds = 1 };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name))));

            var stale = executor.Pending;

            cut.InvokeAsync(() => cut.Instance.Reload()).Wait();

            stale.Release();

            cut.WaitForAssertion(() =>
                Assert.Equal(new[] { "Toys", "Games", "Toys", "Puzzles" }, Cells(cut, 0)));

            Assert.Equal(2, executor.Materializations);
        }
    }
}
