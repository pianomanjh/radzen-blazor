using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class PropertyColumnTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, columns);
            });
        }

        static string[] CellsOfColumn(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static string[] HeaderTitles(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("thead th .rz-column-title-content").Select(e => e.TextContent).ToArray();

        [Fact]
        public void RendersTheValueOfEveryRow()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(
                Columns.Property<Person, string>(p => p.First),
                Columns.Property<Person, int>(p => p.Id)));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, CellsOfColumn(cut, 0));
            Assert.Equal(new[] { "3", "1", "4", "2" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void RendersANestedProperty()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Customer.Name)));

            Assert.Equal(new[] { "Zeta", "Yankee", "Xray", "Whisky" }, CellsOfColumn(cut, 0));
        }

        [Fact]
        public void RendersAComputedExpression()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last)));

            Assert.Equal(
                new[] { "Carol Adams", "Alice Draper", "Dave Bell", "Bob Cook" },
                CellsOfColumn(cut, 0));
        }

        [Fact]
        public void RendersAnEmptyCellForANullValue()
        {
            using var ctx = new TestContext();
            var data = new List<Person> { new Person { First = null, Customer = null } };

            var cut = Render(ctx, data, Columns.Of(
                Columns.Property<Person, string>(p => p.First)));

            Assert.Equal(string.Empty, CellsOfColumn(cut, 0).Single());
        }

        [Fact]
        public void FormatAppliesToADecimal()
        {
            using var ctx = new TestContext();
            var data = new List<Person> { new Person { Salary = 1234.5m } };

            using (new CultureScope("en-US"))
            {
                var cut = Render(ctx, data, Columns.Of(
                    Columns.Property<Person, decimal>(p => p.Salary, format: "C")));

                var text = CellsOfColumn(cut, 0).Single();

                Assert.Equal(1234.5m.ToString("C", CultureInfo.CurrentCulture), text);

                // The half that discriminates: an ignored Format would render the plain ToString.
                Assert.NotEqual(1234.5m.ToString(CultureInfo.CurrentCulture), text);
            }
        }

        [Fact]
        public void FormatAppliesToADateTime()
        {
            using var ctx = new TestContext();
            var hired = new DateTime(2021, 1, 2);
            var data = new List<Person> { new Person { Hired = hired } };

            using (new CultureScope("en-US"))
            {
                var cut = Render(ctx, data, Columns.Of(
                    Columns.Property<Person, DateTime>(p => p.Hired, format: "d")));

                var text = CellsOfColumn(cut, 0).Single();

                Assert.Equal(hired.ToString("d", CultureInfo.CurrentCulture), text);
                Assert.NotEqual(hired.ToString(CultureInfo.CurrentCulture), text);
            }
        }

        [Fact]
        public void NoFormat_RendersThePlainValue()
        {
            using var ctx = new TestContext();
            var data = new List<Person> { new Person { Salary = 1234.5m } };

            using (new CultureScope("en-US"))
            {
                var cut = Render(ctx, data, Columns.Of(
                    Columns.Property<Person, decimal>(p => p.Salary)));

                Assert.Equal(1234.5m.ToString(CultureInfo.CurrentCulture), CellsOfColumn(cut, 0).Single());
            }
        }

        [Fact]
        public void ChangingFormatBetweenRendersRecompilesTheCellText()
        {
            using var ctx = new TestContext();
            var data = new List<Person> { new Person { Salary = 1234.5m } };

            using (new CultureScope("en-US"))
            {
                ctx.JSInterop.Mode = JSRuntimeMode.Loose;

                var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, data);
                    p.Add(g => g.ChildContent, Columns.Of(
                        Columns.Property<Person, decimal>(p2 => p2.Salary, title: "Salary")));
                });

                Assert.Equal(1234.5m.ToString(CultureInfo.CurrentCulture), CellsOfColumn(cut, 0).Single());

                cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, decimal>(p2 => p2.Salary, title: "Salary", format: "C"))));

                Assert.Equal(1234.5m.ToString("C", CultureInfo.CurrentCulture), CellsOfColumn(cut, 0).Single());
            }
        }

        [Fact]
        public void TitleDefaultsToTheDerivedPath()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First),
                Columns.Property<Person, string>(p => p.Customer.Name),
                Columns.Property<Person, object>(p => (object)p.Id)));

            Assert.Equal(new[] { "First", "Customer.Name", "Id" }, HeaderTitles(cut));
        }

        [Fact]
        public void ExplicitTitleWinsOverTheDerivedPath()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "Given name")));

            Assert.Equal(new[] { "Given name" }, HeaderTitles(cut));
        }

        [Fact]
        public void ComputedColumnWithoutATitle_HasAnEmptyHeader()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last)));

            Assert.Equal(new[] { string.Empty }, HeaderTitles(cut));
        }

        [Fact]
        public void PropertyPathIsDerivedFromTheExpression()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Customer.Name)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal("Customer.Name", column.PropertyPath);
            Assert.True(column.CanSort);
        }

        [Fact]
        public void ComputedColumn_HasNoPathAndCannotSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Null(column.PropertyPath);
            Assert.False(column.CanSort);
        }

        [Fact]
        public void ComputedColumnWithAnExplicitSortBy_HasThatPathAndCanSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last, sortBy: p => p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal("Last", column.PropertyPath);
            Assert.True(column.CanSort);
        }

        [Fact]
        public void SortableFalse_DisablesSortingOnAColumnThatHasAPath()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First, sortable: false)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal("First", column.PropertyPath);
            Assert.False(column.CanSort);
        }

        [Fact]
        public void CssClassIsAppendedToTheCellClass()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First, cssClass: "numeric"),
                Columns.Property<Person, int>(p => p.Id)));

            var cells = cut.FindAll("tbody tr")[0].QuerySelectorAll("td");

            // The td carries the column's own class and nothing else; rz-cell-data lives on the inner
            // span, matching RadzenDataGrid, whose theme rules for it are all descendant selectors.
            Assert.Equal("numeric", cells[0].GetAttribute("class"));
            Assert.Null(cells[1].GetAttribute("class"));
            Assert.All(cells, c => Assert.Contains("rz-cell-data",
                c.QuerySelector("span")!.GetAttribute("class")));
        }

        [Fact]
        public void CellMarkupMatchesTheThemeContract()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample().Take(1), Columns.Of(
                Columns.Property<Person, string>(p => p.First)));

            var cell = cut.Find("tbody tr td");

            Assert.Equal("gridcell", cell.GetAttribute("role"));

            var span = cell.QuerySelector("span");

            Assert.NotNull(span);

            // rz-text-truncate is the default WhiteSpace, and what RadzenDataGrid emits: the ellipsis
            // on an over-wide cell comes from this class, not from the td.
            Assert.Equal("rz-cell-data rz-text-truncate", span.GetAttribute("class"));
            Assert.Equal("Carol", span.TextContent);
        }

        // The column compiles its expression to a Func<TItem, string> so the value never reaches
        // RenderTreeBuilder as an object. There is no generic AddContent<T>, so handing it a value type
        // binds the object overload, which boxes and then stringifies. That is not visible in the markup -
        // both routes produce the same text - so it is asserted as behaviour: rendering a value-typed
        // column must not allocate more than rendering the same column through the boxing overload.
        [Fact]
        public void ValueTypedCellText_DoesNotBox()
        {
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { Id = 123456 };

            var cut = Render(ctx, new[] { item }, Columns.Of(
                Columns.Property<Person, int>(p => p.Id)));

            // The real column under test, taken from a rendered grid.
            var column = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            // The same work done the boxing way, as the yardstick. Both produce identical characters, so
            // the string allocation is identical and the difference between them is the box.
            var boxing = new BoxingColumn<Person, int>(p => p.Id);

            Assert.Equal(item.Id.ToString(CultureInfo.CurrentCulture), cut.Find("tbody td span").TextContent);

            var typedBytes = Allocation.PerCell(column, item, iterations);
            var boxingBytes = Allocation.PerCell(boxing, item, iterations);

            // A boxed int is 24 bytes on a 64-bit runtime. Requiring only a third of that leaves room for
            // measurement noise while still failing outright if the typed column starts boxing.
            Assert.True(boxingBytes - typedBytes > 8,
                $"expected the boxing route to allocate materially more per cell; typed={typedBytes}, boxing={boxingBytes}");
        }

        // Formatting a value type used to go through a cast to IFormattable, which boxes: 32 bytes per
        // cell for a decimal, on every row of every currency column, for the life of the grid. The
        // formatter is built at the value's own type instead, so the interface call is made under a
        // constraint and the struct never leaves the stack.
        [Fact]
        public void FormattedValueTypedCellText_DoesNotBox()
        {
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { Salary = 1234.5m };

            var cut = Render(ctx, new[] { item }, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, format: "C")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal>>().Instance;

            Assert.Equal(item.Salary.ToString("C", CultureInfo.CurrentCulture),
                cut.Find("tbody td span").TextContent);

            // Weighed against the same text produced through the boxing route. Both allocate the
            // formatted string; the difference between them is the box.
            var typedBytes = Allocation.PerCell(column, item, iterations);
            var boxingBytes = Allocation.PerCell(new FormattingBoxingColumn<Person, decimal>(p => p.Salary, "C"),
                item, iterations);

            Assert.True(boxingBytes - typedBytes > 8,
                $"expected the boxing route to allocate materially more per cell; typed={typedBytes}, boxing={boxingBytes}");
        }

        [Fact]
        public void FormattedNullableCellText_DoesNotBox()
        {
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { Bonus = 250.5m };

            var cut = Render(ctx, new[] { item }, Columns.Of(
                Columns.Property<Person, decimal?>(p => p.Bonus, format: "C")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal?>>().Instance;

            Assert.Equal(item.Bonus!.Value.ToString("C", CultureInfo.CurrentCulture),
                cut.Find("tbody td span").TextContent);

            var typedBytes = Allocation.PerCell(column, item, iterations);
            var boxingBytes = Allocation.PerCell(
                new FormattingBoxingColumn<Person, decimal?>(p => p.Bonus, "C"), item, iterations);

            Assert.True(boxingBytes - typedBytes > 8,
                $"expected the boxing route to allocate materially more per cell; typed={typedBytes}, boxing={boxingBytes}");
        }

        [Fact]
        public void AFormattedNullReadsAsEmptyRatherThanThrowing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, new[] { new Person { Bonus = null } }, Columns.Of(
                Columns.Property<Person, decimal?>(p => p.Bonus, format: "C")));

            Assert.Equal(string.Empty, cut.Find("tbody td span").TextContent);
        }

        [Fact]
        public void ReferenceTypedCellText_AllocatesNothingPerCell()
        {
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { First = "Carol" };

            var cut = Render(ctx, new[] { item }, Columns.Of(
                Columns.Property<Person, string>(p => p.First)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            // A string property has nothing to convert, so a correct cell allocates zero bytes per row.
            Assert.True(Allocation.PerCell(column, item, iterations) < 1,
                "rendering a string cell should allocate nothing");
        }
    }

    /// <summary>Pins <see cref="CultureInfo.CurrentCulture" /> for the duration of a test.</summary>
    sealed class CultureScope : IDisposable
    {
        readonly CultureInfo previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }
}
