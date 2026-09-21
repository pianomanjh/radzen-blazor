using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The three things a caller outside the assembly can read off a drawn grid: which columns it is
    /// drawing, the rows on screen, and every row the filter matched. §40's export is the consumer
    /// that needs all three, and it lives in another package - so anything it cannot reach is not a
    /// seam.
    /// </summary>
    public class FastGridPublicViewTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static RenderFragment ThreeColumns() => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First", orderIndex: 2),
            Columns.Property<Person, string>(p => p.Last, title: "Last", orderIndex: 0),
            Columns.Property<Person, int>(p => p.Id, title: "Id", orderIndex: 1));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            System.Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                extra?.Invoke(p);
            });

        // --- columns ---------------------------------------------------------------------------

        [Fact]
        public void VisibleColumnsAreInTheOrderTheyAreDrawn()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            // Declared First, Last, Id; ordered by OrderIndex to Last, Id, First. Reading declaration
            // order here would pass on a grid that ignored OrderIndex entirely.
            Assert.Equal(new[] { "Last", "Id", "First" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());
        }

        [Fact]
        public void VisibleColumnsLeavesOutAColumnThatIsNotDrawn()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p2 => p2.First, title: "First"),
                    Columns.Property<Person, string>(p2 => p2.Last, title: "Last", visible: false)));
            });

            Assert.Equal(new[] { "First" }, cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());
        }

        // --- rows ------------------------------------------------------------------------------

        [Fact]
        public void DrawnRowsIsThePageAndFilteredRowsIsAllOfThem()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
            });

            var drawn = cut.Instance.DrawnRows.ToList();
            var all = cut.Instance.FilteredRows.ToList();

            Assert.Equal(2, drawn.Count);
            Assert.Equal(4, all.Count);

            // The page is the head of the whole, not a differently ordered slice of it.
            Assert.Equal(all.Take(2).Select(r => r.Id), drawn.Select(r => r.Id));
        }

        [Fact]
        public void FilteredRowsCarriesTheFilterAndTheSort()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p2 => p2.First, title: "First",
                        sortOrder: SortOrder.Ascending),
                    Columns.Property<Person, decimal>(p2 => p2.Salary, title: "Salary",
                        filterValue: 2500m, filterOperator: FilterOperator.GreaterThan)));
            });

            var all = cut.Instance.FilteredRows.ToList();

            // Sample() holds four rows and two earn more than 2500 - Carol at 4000 and Bob at 3000.
            // Ascending by first name puts Bob in front; the data's own order puts Carol there, so
            // dropping either the filter or the sort fails this.
            Assert.Equal(2, all.Count);
            Assert.Equal("Bob", all[0].First);
            Assert.All(all, row => Assert.True(row.Salary > 2500m));
        }

        [Fact]
        public void FilteredRowsIsThePageWhenAHandlerOwnsTheLoad()
        {
            using var ctx = Context();

            var page = People.Sample().Take(2).ToList();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, page);
                p.Add(g => g.Count, 40);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
                p.Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(ctx.Renderer, _ => { }));
                p.Add(g => g.ChildContent, ThreeColumns());
            });

            // The handler sorted and paged; the grid holds one page and there is no more to have.
            // Answering anything else here would mean running a query the handler never ran.
            Assert.Equal(2, cut.Instance.FilteredRows.Count());
            Assert.Equal(cut.Instance.DrawnRows.Select(r => r.Id), cut.Instance.FilteredRows.Select(r => r.Id));
        }
    }
}
