using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Column geometry, visibility and order - everything that decides which columns are drawn, where,
    /// and how wide. All of it is per column or per grid, never per row.
    /// </summary>
    public class FastGridColumnLayoutTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });

        // --- width -----------------------------------------------------------------------------

        // A width on every cell is a frame per cell; one col per column is a frame per column, and the
        // browser applies it to the whole column either way.
        [Fact]
        public void WidthIsWrittenOnceOntoTheColumnGroup()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "120px"),
                Columns.Property<Person, string>(p => p.Last, title: "Last")));

            var cols = cut.FindAll("colgroup col");

            Assert.Equal(2, cols.Count);
            Assert.Equal("width:120px", cols[0].GetAttribute("style"));
            Assert.Null(cols[1].GetAttribute("style"));

            // And nowhere else: no cell of the sized column carries a width.
            Assert.All(cut.FindAll("tbody tr td:first-child"),
                td => Assert.DoesNotContain("width", td.GetAttribute("style") ?? string.Empty));
        }

        [Fact]
        public void NoColumnGroupIsWrittenWhenNothingHasAWidth()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")));

            Assert.Empty(cut.FindAll("colgroup"));
        }

        [Fact]
        public void TheGridWidthAppliesToColumnsThatSetNone()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "200px"),
                Columns.Property<Person, string>(p => p.Last, title: "Last")),
                p => p.Add(g => g.ColumnWidth, "80px"));

            var cols = cut.FindAll("colgroup col");

            Assert.Equal("width:200px", cols[0].GetAttribute("style"));
            Assert.Equal("width:80px", cols[1].GetAttribute("style"));
        }

        // --- alignment and bounds --------------------------------------------------------------

        [Fact]
        public void AlignmentAndWidthBoundsGoInTheCellStyle()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary",
                    textAlign: TextAlign.Right, minWidth: "80px", maxWidth: "160px")));

            Assert.Equal("text-align:right;min-width:80px;max-width:160px",
                cut.Find("tbody tr td").GetAttribute("style"));

            // The header carries the same, or a right-aligned column's title sits over left-aligned cells.
            Assert.Equal("text-align:right;min-width:80px;max-width:160px",
                cut.Find("thead th").GetAttribute("style"));
        }

        // The common column contributes no style at all, which is the case that has to stay free.
        [Fact]
        public void ADefaultColumnWritesNoCellStyle()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")));

            Assert.Null(cut.Find("tbody tr td").GetAttribute("style"));
        }

        [Fact]
        public void TheCellStyleIsTheSameInstanceOnEveryRow()
        {
            using var ctx = Context();

            var column = new PropertyColumn<Person, decimal> { TextAlign = TextAlign.Right };

            // Memoized, so a thousand rows share one string rather than composing one each.
            Assert.Same(column.CellStyle, column.CellStyle);
            Assert.Equal("text-align:right", column.CellStyle);
        }

        [Theory]
        [InlineData(null, "rz-cell-data rz-text-truncate")]
        [InlineData(Radzen.Blazor.WhiteSpace.Wrap, "rz-cell-data rz-text-wrap")]
        [InlineData(Radzen.Blazor.WhiteSpace.Nowrap, "rz-cell-data rz-text-nowrap")]
        public void WrappingModeIsTheCellSpanClass(Radzen.Blazor.WhiteSpace? whiteSpace, string expected)
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", whiteSpace: whiteSpace)));

            Assert.Equal(expected, cut.Find("tbody tr td span").GetAttribute("class"));
        }

        // --- visibility and order --------------------------------------------------------------

        [Fact]
        public void AHiddenColumnIsNotDrawn()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last", visible: false),
                Columns.Property<Person, int>(p => p.Id, title: "Id")));

            Assert.Equal(new[] { "First", "Id" },
                cut.FindAll("thead th .rz-column-title-content").Select(h => h.TextContent).ToArray());
            Assert.Equal(2, cut.FindAll("tbody tr")[0].QuerySelectorAll("td").Length);
        }

        // A hidden column is out of the layout, not out of the query: this is how a grid filters by a
        // column it does not show.
        [Fact]
        public void AHiddenColumnStillFilters()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last", visible: false,
                    filterValue: "Adams", filterOperator: FilterOperator.Equals)),
                p => p.Add(g => g.AllowFiltering, true));

            // The filter row has a cell for the visible column and none for the hidden one ...
            Assert.Single(cut.FindAll("thead tr")[1].QuerySelectorAll("th"));

            Assert.Single(cut.FindAll("tbody tr"));
            Assert.Contains("Carol", cut.Markup);
        }

        [Fact]
        public void OrderIndexRepositionsAColumn()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last"),
                Columns.Property<Person, int>(p => p.Id, title: "Id", orderIndex: 0)));

            Assert.Equal(new[] { "Id", "First", "Last" },
                cut.FindAll("thead th .rz-column-title-content").Select(h => h.TextContent).ToArray());
        }

        // Two columns sharing an index keep the order they were declared in - the ordering pass is
        // stable, so an index is a nudge rather than a coin toss.
        [Fact]
        public void ColumnsSharingAnOrderIndexKeepTheirDeclaredOrder()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", orderIndex: 1),
                Columns.Property<Person, string>(p => p.Last, title: "Last", orderIndex: 1),
                Columns.Property<Person, int>(p => p.Id, title: "Id", orderIndex: 0)));

            Assert.Equal(new[] { "Id", "First", "Last" },
                cut.FindAll("thead th .rz-column-title-content").Select(h => h.TextContent).ToArray());
        }

        // --- declared sort ---------------------------------------------------------------------

        [Fact]
        public void AColumnCanDeclareTheInitialSort()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, int>(p => p.Id, title: "Id", sortOrder: SortOrder.Descending)),
                p => p.Add(g => g.AllowSorting, true));

            var first = cut.FindAll("tbody tr")[0].QuerySelectorAll("td");

            Assert.Equal("Dave", first[0].TextContent);
            Assert.Equal("descending", cut.FindAll("thead th")[1].GetAttribute("aria-sort"));
        }
    }
}
