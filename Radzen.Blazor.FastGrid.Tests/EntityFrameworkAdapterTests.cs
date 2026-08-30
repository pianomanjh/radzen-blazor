using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// End to end against the real adapter package and a real Entity Framework provider, rather than a
    /// fake executor: <c>AddRadzenQueryableEntityFrameworkAdapter()</c> and a DbSet. What the fake can
    /// never show is whether the queries the grid composes are ones a provider will actually translate -
    /// an untranslatable one throws here.
    /// </summary>
    public class EntityFrameworkAdapterTests : IDisposable
    {
        readonly SqliteConnection connection = new("DataSource=:memory:");
        readonly Ctx context;

        public EntityFrameworkAdapterTests()
        {
            connection.Open();

            context = new Ctx(new DbContextOptionsBuilder<Ctx>()
                .UseSqlite(connection).Options);

            context.Database.EnsureCreated();

            context.People.AddRange(Enumerable.Range(1, 40).Select(i => new Employee
            {
                Id = i,
                Name = "Name" + i,
                Department = i % 4 == 0 ? "Ops" : i % 3 == 0 ? "Sales" : "Engineering",
                Salary = 1000 + i,
            }));

            context.SaveChanges();
        }

        public void Dispose()
        {
            context.Dispose();
            connection.Dispose();
            GC.SuppressFinalize(this);
        }

        IRenderedComponent<RadzenFastGrid<Employee>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Employee>>>? extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddRadzenQueryableEntityFrameworkAdapter();

            return ctx.RenderComponent<RadzenFastGrid<Employee>>(p =>
            {
                p.Add(g => g.Data, context.People);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Employee, string>(x => x.Name),
                    Columns.Property<Employee, string>(x => x.Department),
                    Columns.Property<Employee, decimal>(x => x.Salary)));
                extra?.Invoke(p);
            });
        }

        // role=row rather than plain tr: virtualization's spacers are rows with no cells in them.
        static string[] Names(IRenderedComponent<RadzenFastGrid<Employee>> cut) =>
            cut.FindAll("tbody tr[role=row]").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        [Fact]
        public void TheAdapterRegistrationIsAllThatIsNeeded()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
            });

            cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tbody tr[role=row]").Count));
            Assert.Equal(new[] { "Name1", "Name2", "Name3", "Name4", "Name5" }, Names(cut));
        }

        [Fact]
        public void ThePagerCountsWhatTheDatabaseSays()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ShowPagingSummary, true);
            });

            cut.WaitForAssertion(() =>
                Assert.Contains("40", cut.Find(".rz-pager-summary").TextContent, StringComparison.Ordinal));
        }

        [Fact]
        public void SortingIsTranslatedRatherThanAppliedToThePage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.AllowSorting, true);
            });

            cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tbody tr[role=row]").Count));

            cut.FindAll("thead th")[2].QuerySelector("div")!.Click();

            // Descending by salary is the last five rows of forty; sorting the page after fetching it
            // would give the first five in some order instead.
            cut.WaitForAssertion(() => Assert.Equal(
                new[] { "Name1", "Name2", "Name3", "Name4", "Name5" }, Names(cut)));

            cut.FindAll("thead th")[2].QuerySelector("div")!.Click();

            cut.WaitForAssertion(() => Assert.Equal(
                new[] { "Name40", "Name39", "Name38", "Name37", "Name36" }, Names(cut)));
        }

        [Fact]
        public void FilteringIsTranslatedToWhereRatherThanAppliedInMemory()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ShowPagingSummary, true);
            });

            cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tbody tr[role=row]").Count));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("Ops");

            // Ten of forty are in Ops, so the pager must say ten - the count has to carry the filter.
            cut.WaitForAssertion(() =>
                Assert.Contains("10", cut.Find(".rz-pager-summary").TextContent, StringComparison.Ordinal));
        }

        [Fact]
        public void TheCheckBoxListLookupIsADistinctQuery()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
            });

            var offered = cut.FindComponents<RadzenDropDown<IEnumerable>>()[1]
                .Instance.Data.Cast<object>().ToArray();

            Assert.Equal(new object[] { "Engineering", "Ops", "Sales" }, offered);
        }

        [Fact]
        public void VirtualizationFetchesItsRowsThroughTheAdapter()
        {
            // How large the window is depends on the viewport, which no renderer without a browser has,
            // so bUnit asks the provider for everything. What is checked here is that the rows arrive
            // through the awaited Skip/Take query at all, and that the spacers are table rows - a div
            // there would be dropped by the parser and the rows would lay out as if the table had none.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowVirtualization, true));

            cut.WaitForAssertion(() => Assert.Equal(40, cut.FindAll("tbody tr[role=row]").Count));

            Assert.Equal("Name1", Names(cut)[0]);
            Assert.Equal(2, cut.FindAll("tbody tr[aria-hidden=true]").Count);
        }

        [Fact]
        public void VirtualizationComposesTheFilterIntoTheWindowQuery()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowVirtualization, true);
                p.Add(g => g.AllowFiltering, true);
            });

            cut.WaitForAssertion(() => Assert.Equal(40, cut.FindAll("tbody tr[role=row]").Count));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("Ops");

            // Virtualize holds its own copy of the window, so a filter that only re-renders shows the
            // same forty rows back. The refetch is what makes the new query run.
            cut.WaitForAssertion(() => Assert.Equal(10, cut.FindAll("tbody tr[role=row]").Count));

            Assert.All(cut.FindAll("tbody tr[role=row]"),
                row => Assert.Equal("Ops", row.QuerySelectorAll("td")[1].TextContent));
        }

        public class Employee
        {
            public int Id { get; set; }

            public string Name { get; set; } = "";

            public string Department { get; set; } = "";

            public decimal Salary { get; set; }
        }

        public class Ctx : DbContext
        {
            public Ctx(DbContextOptions<Ctx> options) : base(options)
            {
            }

            public DbSet<Employee> People => Set<Employee>();
        }
    }
}
