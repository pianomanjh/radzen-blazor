using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
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

        static IRenderedComponent<RadzenFastGrid<Person>> Filtered(TestContext ctx, RenderFragment columns,
            IEnumerable<Person> data = null) =>
            Render(ctx, columns, p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
            }, data);

        static string[] Cells(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static RadzenDropDown<System.Collections.IEnumerable> Picker(
            IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindComponents<RadzenDropDown<System.Collections.IEnumerable>>()[index].Instance;

        static object[] Offered(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            Picker(cut, index).Data.Cast<object>().ToArray();

        static string[] OfferedNames(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            Offered(cut, index).Select(entry => entry.ToString()).ToArray();

        static object Named(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, string name) =>
            Offered(cut, index).Single(entry => entry.ToString() == name);

        static void Pick(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, params object[] entries) =>
            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<System.Collections.IEnumerable>>()[index]
                .Instance.Change.InvokeAsync(entries.ToList())).Wait();

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
        // --- The filter -------------------------------------------------------------------------

        [Fact]
        public void TheCheckBoxListIsTheLookupItselfRatherThanAScanOfTheData()
        {
            // No SELECT DISTINCT runs: the names are already held and the ids come with them. "Books"
            // is in the list and in no row, which is the point - a stable list is worth more than a
            // shorter one, and one that moves as the data does moves a filter control under the reader.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))));

            Assert.Equal(new[] { "Books", "Games", "Puzzles", "Toys" }, OfferedNames(cut, 0));
        }

        [Fact]
        public void PickingANameFiltersByItsId()
        {
            // Ids everywhere - the predicate, the descriptor and the persisted settings - so no join is
            // needed and a filter survives someone renaming the lookup row.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))));

            Pick(cut, 0, Named(cut, 0, "Toys"));

            Assert.Equal(new[] { "Toys", "Toys" }, Cells(cut, 0));

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal("CategoryId", filter.Property);
            Assert.Equal(FilterOperator.In, filter.FilterOperator);
            Assert.Equal(new[] { 10 }, ((System.Collections.IEnumerable)filter.FilterValue).Cast<int>());
        }

        [Fact]
        public void ANullableKeyOffersTheRowsWithNoIdAsAChoiceOfTheirOwn()
        {
            // "Which products have no category" is a reasonable question, and In over a nullable key
            // answers it. Note this is the check-box list's own rule reversed: that one drops nulls.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int?>(
                x => x.RegionId, FastGridLookup.Map(Lookups.Regions()))));

            Assert.Equal(new[] { "(Blank)", "North", "South" }, OfferedNames(cut, 0));

            Pick(cut, 0, Named(cut, 0, "(Blank)"));

            Assert.Equal(new[] { "" }, Cells(cut, 0));
        }

        [Fact]
        public void TheOfferedListIsTheSameOneOnEveryRender()
        {
            // Rebuilding it per render would replace the drop-down's Data on every parent render, which
            // is a new list for values that did not move.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))));

            var before = Picker(cut, 0).Data;

            cut.Render();

            Assert.Same(before, Picker(cut, 0).Data);
        }

        [Fact]
        public void TheTicksSurviveARenderThatChangedNothing()
        {
            // The drop-down is bound to entries and the column filters by ids, so what it is handed has
            // to be found again from the ids on every render. A multiple selection losing a tick on an
            // unrelated render is a fault this drop-down has had before.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))));

            Pick(cut, 0, Named(cut, 0, "Toys"), Named(cut, 0, "Games"));

            cut.Render();

            Assert.Equal(new[] { "Games", "Toys" },
                ((System.Collections.IEnumerable)Picker(cut, 0).Value).Cast<object>()
                    .Select(entry => entry.ToString()).OrderBy(text => text).ToArray());
        }

        [Fact]
        public void NoDistinctQueryRunsForALookupColumn()
        {
            // The names are already held and the ids come with them, so the one query behind an
            // ordinary check-box list is a query this column does not have.
            using var ctx = new TestContext();
            var executor = new GatedLookupExecutor { Holds = 0, PassThrough = typeof(Person) };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))),
                data: People.Sample().AsQueryable());

            Assert.Equal(0, executor.Materializations);
        }

        [Fact]
        public void SimpleModeMatchesTheTypedTextAgainstTheNames()
        {
            // Matching the ids as text would be useless - nobody types 30 looking for Puzzles - and
            // refusing simple mode outright would leave FilterMode with a value that does nothing on
            // one kind of column.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))),
                extra: p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("puz");

            Assert.Equal(new[] { "Puzzles" }, Cells(cut, 0));

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal(FilterOperator.In, filter.FilterOperator);
            Assert.Equal(new[] { 30 }, ((System.Collections.IEnumerable)filter.FilterValue).Cast<int>());
        }

        [Fact]
        public void TypingMatchesTheNamesAndNotTheLabelOnTheBlankEntry()
        {
            // "(Blank)" is a label the grid made up, not a name the lookup holds, and it is a different
            // one in every culture Radzen ships. Matching it would make what a typed filter finds
            // depend on the language the page is in.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int?>(
                x => x.RegionId, FastGridLookup.Map(Lookups.Regions()))),
                extra: p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("bl");

            Assert.Empty(cut.FindAll("tbody tr td"));
        }

        [Fact]
        public void ANullAmongTheFilterValuesTicksNothingWhereAKeyCannotBeNull()
        {
            // ApplyFilters takes descriptors from outside - a RadzenDataFilter, or settings stored
            // against some other column - so the values are not always ones this column produced. A null
            // there means the entry with no id, and a column whose key cannot be null has none: reading
            // it as default(TKey) would tick the entry whose id happens to be zero.
            using var ctx = new TestContext();

            var categories = new Dictionary<int, string> { [0] = "Unfiled", [10] = "Toys" };

            var cut = Filtered(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(categories))));

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor
                {
                    Property = "CategoryId",
                    FilterOperator = FilterOperator.In,
                    FilterValue = new List<int?> { null },
                },
            })).Wait();

            Assert.Empty(((System.Collections.IEnumerable)Picker(cut, 0).Value).Cast<object>());
        }

        [Fact]
        public void SettingFilterLookupDataBesideALookupNamesBothParameters()
        {
            // Silently ignoring a parameter somebody deliberately set is a failure mode this grid has
            // paid for more than once. FilterLookupData is inherited and therefore still settable, and
            // a column drawing its list from Lookup has nothing for it to supply.
            using var ctx = new TestContext();

            var thrown = Assert.ThrowsAny<Exception>(() => Filtered(ctx, Columns.Of(
                Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Map(Lookups.Categories()),
                    filterLookupData: new[] { "Toys" }))));

            Assert.Contains("FilterLookupData", Deepest(thrown).Message, StringComparison.Ordinal);
            Assert.Contains("Lookup", Deepest(thrown).Message, StringComparison.Ordinal);
        }

        static Exception Deepest(Exception exception) =>
            exception.InnerException is null ? exception : Deepest(exception.InnerException);

        [Fact]
        public void ALookupColumnIsNotSortableWithoutASortBy()
        {
            // Sorting by the id puts the categories in insertion order under a column showing names
            // alphabetically - a wrong answer that looks like a working feature.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Lookup<Person, int>(x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))),
                extra: p => p.Add(g => g.AllowSorting, true));

            // Against a sortable neighbour, so an assertion that could only ever see an empty list is
            // not what is passing here.
            var headers = cut.FindAll("thead th");

            Assert.Contains("rz-sortable-column", headers[0].ClassName, StringComparison.Ordinal);
            Assert.DoesNotContain("rz-sortable-column", headers[1].ClassName, StringComparison.Ordinal);
        }

        [Fact]
        public void ASortByIsWhatMakesItSortable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()),
                sortBy: FastGridSort<Person>.By(p => p.First))),
                extra: p => p.Add(g => g.AllowSorting, true));

            cut.Find("thead th div").Click();

            Assert.Equal(new[] { "Games", "Puzzles", "Toys", "Toys" }, Cells(cut, 0));
        }

        // --- One lookup, however many columns declare it -------------------------------------------

        [Fact]
        public void TwoColumnsOverOneLookupResolveItOnce()
        {
            // Two columns over the same table is the ordinary case rather than the exotic one -
            // CreatedByUserId and ApprovedByUserId both resolve against users - and per-column
            // ownership would build it twice and hold it twice.
            using var ctx = new TestContext();

            var rows = Lookups.CategoryRows();
            var reads = 0;
            var lookup = FastGridLookup.Items(rows, category => { reads++; return category.Id; },
                category => category.Name);

            Render(ctx, Columns.Of(
                Columns.Lookup<Person, int>(x => x.CategoryId, lookup, title: "Category"),
                Columns.Lookup<Person, int>(x => x.CategoryId, lookup, title: "Also category")));

            Assert.Equal(rows.Count, reads);
        }

        [Fact]
        public void ReloadIsWhatPicksUpANameThatChanged()
        {
            // Nothing invalidates a lookup automatically, and one with no way to refresh at all is a
            // cache with no invalidation - which produces an "I renamed a category and it never
            // changed" report that has no answer. This is the only escape hatch, and there is
            // deliberately no second refresh verb with a subtly different scope beside it.
            using var ctx = new TestContext();

            var rows = Lookups.CategoryRows();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(rows, c => c.Id, c => c.Name))));

            Assert.Equal("Toys", Cells(cut, 0)[0]);

            rows.Single(category => category.Id == 10).Name = "Playthings";

            cut.InvokeAsync(() => cut.Instance.Reload()).Wait();

            Assert.Equal("Playthings", Cells(cut, 0)[0]);
        }

        [Fact]
        public void AStoredFilterIsIdsAndSurvivesTheLookupBeingRenamed()
        {
            // The reason ids are what the filter carries everywhere. Stored as a name it would break the
            // day someone edited the lookup row; stored as an id it does not. Note the column needs a
            // SortBy to be stored at all - settings are keyed on PropertyPath, which is the sort path,
            // so a lookup column without one shares CollectionColumn's gap. §10b has that open.
            using var ctx = new TestContext();

            var rows = Lookups.CategoryRows();

            RenderFragment Columns(FastGridSort<Person> sortBy) => Tests.Columns.Of(
                Tests.Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Items(rows, c => c.Id, c => c.Name), sortBy: sortBy));

            FastGridSettings captured = null;

            var cut = Render(ctx, Columns(FastGridSort<Person>.By(p => p.CategoryId)), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                p.Add(g => g.SettingsChanged,
                    EventCallback.Factory.Create<FastGridSettings>(this, s => captured = s));
            });

            Pick(cut, 0, Named(cut, 0, "Toys"));

            Assert.Equal(new[] { 10 },
                ((System.Collections.IEnumerable)captured.Columns.Single().FilterValue).Cast<int>());

            rows.Single(category => category.Id == 10).Name = "Playthings";

            using var second = new TestContext();

            var restored = Render(second, Columns(FastGridSort<Person>.By(p => p.CategoryId)), p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                p.Add(g => g.Settings, captured);
            });

            Assert.Equal(new[] { "Playthings", "Playthings" }, Cells(restored, 0));
        }

        [Fact]
        public async Task AFetchTheGridWentAwayDuringLeavesTheNamesUnresolved()
        {
            // The one exit from the fetch that is not an answer, and the reason it needs a catch of its
            // own: the wide one below it would read cancellation as a failure and resolve the column to
            // no names, which draws every id for good. Driven against the column rather than through a
            // grid, because a disposed grid renders nothing and would make either answer look alike.
            var column = new LookupColumn<Person, int>
            {
                Lookup = FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(),
                    c => c.Id, c => c.Name),
            };

            using var gone = new CancellationTokenSource();

            await gone.CancelAsync();

            var redraw = await column.FetchNamesAsync(new GatedLookupExecutor { Holds = 0 }, gone.Token);

            Assert.False(redraw);
            Assert.True(column.NamesOutstanding);
        }

        [Fact]
        public void AnEmptyAnswerIsAnAnswer()
        {
            // A lookup that resolves to no names at all leaves every cell drawing its id, and must not
            // put the column back on the queue for an answer it has already given. The bound this pins
            // is a render loop: leaving the column outstanding after a successful fetch does not fail a
            // test, it aborts the run with a stack overflow.
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new GatedLookupExecutor { Holds = 0 });

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Query(new List<Category>().AsQueryable(), c => c.Id, c => c.Name))));

            Assert.Equal(new[] { "10", "20", "10", "30" }, Cells(cut, 0));
            Assert.InRange(cut.RenderCount, 1, 2);
        }

        [Fact]
        public void TwoLookupsBuiltTheSameWayAreOneLookupAndTwoQueriesNeverAre()
        {
            // What the sharing rests on, and the reason it is a bonus rather than a mechanism: a query
            // lookup's members include Expressions, which are a fresh object graph on every evaluation
            // and do not override Equals, so no call site can hold one still by being careful.
            var rows = Lookups.CategoryRows();
            var map = Lookups.Categories();
            var query = rows.AsQueryable();

            // Built from one call site evaluated twice, which is what markup does with an expression on
            // every render. Two separate call sites are two cached delegates and would say nothing.
            FastGridLookup<int> Items() => FastGridLookup.Items(rows, c => c.Id, c => c.Name);
            FastGridLookup<int> Query() => FastGridLookup.Query(query, c => c.Id, c => c.Name);

            Assert.Equal(FastGridLookup.Map(map), FastGridLookup.Map(map));
            Assert.Equal(Items(), Items());
            Assert.NotEqual(Query(), Query());
        }

        [Fact]
        public void AScalarCellAllocatesNothing()
        {
            // §14 predicted this and a prediction is not a measurement. What a cell renders is a string
            // the lookup already holds, so there is nothing left to build - cheaper than a PropertyColumn
            // carrying a FormatString, which builds one.
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { CategoryId = 10 };

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(
                x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()))), data: new[] { item });

            Assert.Equal("Toys", cut.Find("tbody td span").TextContent);

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.True(Allocation.PerCell(column, item, iterations) < 1,
                "rendering a resolved lookup cell should allocate nothing");
        }
    }
}
