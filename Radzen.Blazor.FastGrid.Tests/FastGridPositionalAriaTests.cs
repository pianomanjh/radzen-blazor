using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Where a cell sits in the whole table, and - just as much - when the grid says nothing at all.
    /// </summary>
    /// <remarks>
    /// The emission rule is the ARIA specification's own read literally: a grid holding every row and
    /// every column needs none of this, because the browser can count what it has. Half the tests here
    /// assert the silence, because the silence is what keeps the 153 KB baseline where it is.
    /// </remarks>
    public class FastGridPositionalAriaTests
    {
        static RenderFragment ThreeColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"),
            Columns.Property<Person, decimal>(x => x.Salary, title: "Salary"));

        static IRenderedComponent<RadzenFastGrid<Person>> Grid(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IList<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                extra?.Invoke(p);
            });
        }

        static IElement View(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.Find(".rz-data-grid-data");

        static string?[] RowIndexes(IRenderedComponent<RadzenFastGrid<Person>> cut, string selector) =>
            cut.FindAll(selector).Select(r => r.GetAttribute("aria-rowindex")).ToArray();

        static string?[] ColIndexes(IElement row) =>
            row.Children.Select(c => c.GetAttribute("aria-colindex")).ToArray();

        // ---- the silence ----

        [Fact]
        public void AGridHoldingEveryRowAndColumnNumbersNothing()
        {
            // The browser can count what it has, and the specification says as much. This is also the
            // configuration the baseline measurement renders, so anything emitted here is on the bill
            // of every grid that uses the component.
            using var ctx = new TestContext();

            var cut = Grid(ctx);

            Assert.False(View(cut).HasAttribute("aria-rowcount"));
            Assert.False(View(cut).HasAttribute("aria-colcount"));

            Assert.Empty(cut.FindAll("[aria-rowindex]"));
            Assert.Empty(cut.FindAll("[aria-colindex]"));
        }

        [Fact]
        public void APagedGridStillNumbersNoColumns()
        {
            // The two halves are paid for separately: paging windows the rows and leaves the columns
            // alone, so the per-cell attribute - the expensive one - is not written.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
            });

            Assert.True(View(cut).HasAttribute("aria-rowcount"));
            Assert.False(View(cut).HasAttribute("aria-colcount"));
            Assert.Empty(cut.FindAll("[aria-colindex]"));
        }

        [Fact]
        public void AColumnPickerThatHasHiddenNothingNumbersNoColumns()
        {
            // Offering the picker is not the condition; using it is. A grid showing all five of five
            // columns has them contiguous from one, which is the case the browser can work out.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowColumnPicking, true));

            Assert.False(View(cut).HasAttribute("aria-colcount"));
            Assert.Empty(cut.FindAll("[aria-colindex]"));
        }

        // ---- rows ----

        [Fact]
        public void APagedGridSaysHowManyRowsThereAreAndWhichOnesTheseAre()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
            }, People.Many(7));

            // Seven rows and the header row above them.
            Assert.Equal("8", View(cut).GetAttribute("aria-rowcount"));

            Assert.Equal(new[] { "1" }, RowIndexes(cut, "thead tr"));
            Assert.Equal(new[] { "2", "3" }, RowIndexes(cut, "tbody tr"));

            cut.InvokeAsync(() => cut.Instance.GoToPage(2)).Wait();

            // Page three is rows five and six, which are rows six and seven of the grid.
            Assert.Equal(new[] { "6", "7" }, RowIndexes(cut, "tbody tr"));
        }

        [Fact]
        public void TheFilterRowIsTheSecondRowOfTheGrid()
        {
            // It is a second row of the same header rather than a thing of its own, so the first data
            // row is row three rather than row two.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
                p.Add(g => g.AllowFiltering, true);
            }, People.Many(7));

            Assert.Equal("9", View(cut).GetAttribute("aria-rowcount"));
            Assert.Equal(new[] { "1", "2" }, RowIndexes(cut, "thead tr"));
            Assert.Equal(new[] { "3", "4" }, RowIndexes(cut, "tbody tr"));
        }

        [Fact]
        public void AGridWithNoHeaderStartsCountingAtTheFirstRow()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
                p.Add(g => g.ShowHeader, false);
            }, People.Many(7));

            Assert.Equal("7", View(cut).GetAttribute("aria-rowcount"));
            Assert.Equal(new[] { "1", "2" }, RowIndexes(cut, "tbody tr"));
        }

        [Fact]
        public void AVirtualizedGridNumbersFromTheDataSetRatherThanTheWindow()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowVirtualization, true), People.Many(6));

            Assert.Equal(new[] { "2", "3", "4", "5", "6", "7" }, RowIndexes(cut, "tbody tr.rz-data-row"));
        }

        [Fact]
        public void ADetailRowCarriesItsParentsNumber()
        {
            // Numbering it separately would push every row below it out of step with the data set,
            // which is the one thing the attribute exists to keep true.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 3);
                p.Add(g => g.Template,
                    (RenderFragment<Person>)(person => b => b.AddContent(0, person.First)));
            }, People.Many(7));

            cut.FindAll("tbody tr.rz-data-row")[1].QuerySelector("button")!.Click();

            Assert.Equal(new[] { "2", "3", "3", "4" }, RowIndexes(cut, "tbody tr"));
        }

        // ---- columns ----

        [Fact]
        public void HidingAColumnNumbersWhatIsLeftAgainstTheWholeSet()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowColumnPicking, true));

            var middle = cut.FindComponents<PropertyColumn<Person, string>>()[1].Instance;

            cut.InvokeAsync(() => middle.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal("3", View(cut).GetAttribute("aria-colcount"));

            // The gap is where Last was: the two that are left are still columns one and three.
            Assert.Equal(new[] { "1", "3" }, ColIndexes(cut.Find("thead tr")));
            Assert.Equal(new[] { "1", "3" }, ColIndexes(cut.FindAll("tbody tr")[0]));
        }

        [Fact]
        public void TheFilterRowIsNumberedWithTheColumnsToo()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowColumnPicking, true);
                p.Add(g => g.AllowFiltering, true);
            });

            var middle = cut.FindComponents<PropertyColumn<Person, string>>()[1].Instance;

            cut.InvokeAsync(() => middle.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal(new[] { "1", "3" }, ColIndexes(cut.FindAll("thead tr")[1]));
        }

        [Fact]
        public void TheRowDetailToggleIsColumnOneAndTheRestShiftPastIt()
        {
            // It is a rendered gridcell, so it occupies a column and the data columns start at two.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowColumnPicking, true);
                p.Add(g => g.Template,
                    (RenderFragment<Person>)(person => b => b.AddContent(0, person.First)));
            });

            var middle = cut.FindComponents<PropertyColumn<Person, string>>()[1].Instance;

            cut.InvokeAsync(() => middle.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal("4", View(cut).GetAttribute("aria-colcount"));
            Assert.Equal(new[] { "1", "2", "4" }, ColIndexes(cut.FindAll("tbody tr")[0]));
        }
        [Fact]
        public void HidingTheLastColumnNeedsNoNumbersAtAll()
        {
            // What is left is columns one and two of three, which is what a browser counting the
            // rendered cells would work out for itself. The count still has to be given: that there
            // are three is the part the DOM cannot say.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowColumnPicking, true));

            var last = cut.FindComponents<PropertyColumn<Person, decimal>>()[0].Instance;

            cut.InvokeAsync(() => last.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal("3", View(cut).GetAttribute("aria-colcount"));
            Assert.Empty(cut.FindAll("[aria-colindex]"));
        }

        [Fact]
        public void HidingTheFirstColumnNeedsOneNumberPerRow()
        {
            // An unbroken run that starts late: saying where it starts is enough, and a browser
            // counts on from there. One frame per row instead of one per cell.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowColumnPicking, true));

            var first = cut.FindComponents<PropertyColumn<Person, string>>()[0].Instance;

            cut.InvokeAsync(() => first.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal(new[] { "2", null }, ColIndexes(cut.Find("thead tr")));
            Assert.Equal(new[] { "2", null }, ColIndexes(cut.FindAll("tbody tr")[0]));
        }

        [Fact]
        public void ARowDetailToggleForcesEveryCellToBeNumbered()
        {
            // The toggle pins the first cell to column one, so a run that starts anywhere else has a
            // hole between the two and there is no unbroken case left for it to be.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p =>
            {
                p.Add(g => g.AllowColumnPicking, true);
                p.Add(g => g.Template,
                    (RenderFragment<Person>)(person => b => b.AddContent(0, person.First)));
            });

            var first = cut.FindComponents<PropertyColumn<Person, string>>()[0].Instance;

            cut.InvokeAsync(() => first.SetPicked(false)).Wait();
            cut.Render();

            Assert.Equal(new[] { "1", "3", "4" }, ColIndexes(cut.FindAll("tbody tr")[0]));
        }
    }
}
