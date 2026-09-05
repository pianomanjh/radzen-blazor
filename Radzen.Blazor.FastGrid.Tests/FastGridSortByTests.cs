using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Sorting by a key the column is not typed at, through <see cref="FastGridSort{TItem}" />.
    /// </summary>
    /// <remarks>
    /// A template column and a collection column both have somewhere to sort from that is not a type
    /// parameter. A template column used to say it as a property path, which sorted nothing at all
    /// locally - the header was clickable, the sort was recorded, the indicator was drawn, and the rows
    /// did not move - and a collection column said it as an expression returning object, which worked
    /// only by reflecting the key's type back out of the tree.
    /// </remarks>
    public class FastGridSortByTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            IEnumerable<Person> data, RenderFragment columns)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowSorting, true);
            });
        }

        static string[] Column(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[index].TextContent).ToArray();

        static void ClickHeader(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("thead th")[index].QuerySelector("div").Click();

        static RenderFragment TemplateSortedBy(FastGridSort<Person> sort, string path = null) =>
            Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Template<Person>(person => builder => builder.AddContent(0, person.Id),
                    title: "Id", sortProperty: path, sortBy: sort));

        // A sort a column was *handed* can be the second column of a multi-column sort as well as the
        // first, and until this was written nothing in the suite exercised that: mutating the base's
        // ApplyThenBy and ApplyThenByInMemory to answer null left all 798 tests green, on the four
        // then-by forwards that three columns each used to carry a copy of.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASortByColumnCanBeTheSecondColumnOfTheSort(bool queryable)
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, queryable ? people.AsQueryable() : people);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.AllowMultiColumnSorting, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, Grade>(x => x.Grade, title: "Grade"),
                    Columns.Template<Person>(person => builder => builder.AddContent(0, person.First),
                        title: "First", sortBy: FastGridSort<Person>.By(x => x.First))));
            });

            ClickHeader(cut, 0);
            ClickHeader(cut, 1);

            // Junior before Senior, and within each grade by first name. Neither column on its own puts
            // the rows in this order, so it can only come from the second sort being added to the first.
            Assert.Equal(new[] { "Alice", "Dave", "Bob", "Carol" }, Column(cut, 1));
        }

        // The bug. Before SortBy existed there was no way to make this happen at all.
        [Fact]
        public void ATemplateColumnSortsTheRows()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.Id)));

            ClickHeader(cut, 1);

            var ids = Column(cut, 1).Select(int.Parse).ToArray();

            Assert.Equal(ids.OrderBy(id => id), ids);
            Assert.NotEqual(ids.OrderByDescending(id => id), ids);
        }

        [Fact]
        public void ATemplateColumnSortsDescendingOnTheSecondClick()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.Id)));

            ClickHeader(cut, 1);
            ClickHeader(cut, 1);

            var ids = Column(cut, 1).Select(int.Parse).ToArray();

            Assert.Equal(ids.OrderByDescending(id => id), ids);
        }

        // A path alone still marks the column sortable, because a LoadData grid sorts by it - but it is
        // the grid's own sorting that it could never do.
        [Fact]
        public void APathAloneStillReachesLoadDataAsTheOrderBy()
        {
            using var ctx = new TestContext();
            LoadDataArgs seen = null;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TemplateSortedBy(null, nameof(Person.Id)));
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, a => seen = a));
            });

            ClickHeader(cut, 1);

            Assert.Equal("Id asc", seen?.OrderBy);
        }

        // Both together: SortBy sorts the rows, and its own path is what a server would be told.
        [Fact]
        public void SortBySuppliesThePathWhenBothAreSet()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.Salary), nameof(Person.Id)));

            var column = cut.FindComponent<TemplateColumn<Person>>().Instance;

            Assert.Equal(nameof(Person.Salary), column.SortPath);
        }

        // A computed key can order rows but has no path to send anywhere, which is the same rule every
        // other computed sort key follows.
        [Fact]
        public void AComputedKeySortsButHasNoPath()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.First.Length + p.Id)));

            Assert.Null(cut.FindComponent<TemplateColumn<Person>>().Instance.SortPath);

            ClickHeader(cut, 1);

            var keys = cut.FindAll("tbody tr")
                .Select(r => r.QuerySelectorAll("td"))
                .Select(tds => tds[0].TextContent.Length + int.Parse(tds[1].TextContent))
                .ToArray();

            Assert.Equal(keys.OrderBy(k => k), keys);
        }

        // A collection column sorts by a member of the row, not of the element - which is why its key
        // was never a type parameter of the column.
        [Fact]
        public void ACollectionColumnSortsByItsKey()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, int>(x => x.Id, title: "Id"),
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name,
                    sortBy: FastGridSort<Person>.By(p => p.Salary))));

            ClickHeader(cut, 1);

            var ids = Column(cut, 0).Select(int.Parse).ToArray();
            var bySalary = People.Sample().OrderBy(p => p.Salary).Select(p => p.Id).ToArray();

            Assert.Equal(bySalary, ids);
        }

        [Fact]
        public void ACollectionColumnWithNoSortByIsNotSortable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, int>(x => x.Id, title: "Id"),
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name)));

            Assert.False(cut.FindComponent<CollectionColumn<Person, Company>>().Instance.CanSort);
            Assert.Null(cut.FindAll("thead th")[1].QuerySelector("div").GetAttribute("onclick"));
        }

        // The sort is read live rather than cached alongside the compiled expressions, because it
        // compiles nothing. A cache would have to be invalidated, and a FastGridSort written inline in
        // markup is a new instance every render - so the invalidation would fire every render and take
        // the compiles with it.
        [Fact]
        public void ChangingTheSortChangesThePathItReports()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, int>(x => x.Id, title: "Id"),
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name,
                    sortBy: FastGridSort<Person>.By(p => p.Salary))));

            var column = cut.FindComponent<CollectionColumn<Person, Company>>().Instance;

            Assert.Equal(nameof(Person.Salary), column.SortPath);

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, int>(x => x.Id, title: "Id"),
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name,
                    sortBy: FastGridSort<Person>.By(p => p.Hired)))));

            Assert.Equal(nameof(Person.Hired),
                cut.FindComponent<CollectionColumn<Person, Company>>().Instance.SortPath);
        }

        // The two routes have to agree, as everywhere else: a list is ordered by a delegate and a
        // queryable by an expression, and the same sort has to mean the same thing to both.
        [Fact]
        public void TheListAndTheQueryableRouteAgree()
        {
            var people = People.Many(12);

            using var listContext = new TestContext();
            using var queryableContext = new TestContext();

            RenderFragment columns() => TemplateSortedBy(FastGridSort<Person>.By(p => p.Bonus));

            var overList = Render(listContext, people, columns());
            var overQueryable = Render(queryableContext, people.AsQueryable(), columns());

            ClickHeader(overList, 1);
            ClickHeader(overQueryable, 1);

            Assert.Equal(Column(overQueryable, 1), Column(overList, 1));
            Assert.NotEmpty(Column(overList, 1));

            // Descending too: the two routes build the ordering separately, so agreeing one way round
            // says nothing about the other.
            ClickHeader(overList, 1);
            ClickHeader(overQueryable, 1);

            Assert.Equal(Column(overQueryable, 1), Column(overList, 1));
            Assert.NotEqual(Column(overList, 1), Column(overList, 1).OrderBy(x => x).ToArray());
        }

        // The in-memory override earns its place by keeping the grid off the queryable route, and that
        // is invisible in the rows - a column that declines still sorts, through the slower path. So the
        // route itself is what this asserts.
        [Fact]
        public void SortingByATemplateColumnStaysOnTheInMemoryRoute()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(8),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.Id)));

            ClickHeader(cut, 1);

            Assert.True(cut.Instance.ComposedInMemory);
        }

        [Fact]
        public void SortingByACollectionColumnStaysOnTheInMemoryRouteToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(8), Columns.Of(
                Columns.Property<Person, int>(x => x.Id, title: "Id"),
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name,
                    sortBy: FastGridSort<Person>.By(p => p.Salary))));

            ClickHeader(cut, 1);

            Assert.True(cut.Instance.ComposedInMemory);
        }

        // The other half of the same claim: a queryable is not composed in memory, whatever the columns
        // can do, because there the expression is the point.
        [Fact]
        public void AQueryableIsNotComposedInMemory()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(8).AsQueryable(),
                TemplateSortedBy(FastGridSort<Person>.By(p => p.Id)));

            ClickHeader(cut, 1);

            Assert.False(cut.Instance.ComposedInMemory);
        }
    }
}
