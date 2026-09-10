using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class FastGridSortingTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns, bool allowSorting = true)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.AllowSorting, allowSorting);
                p.Add(g => g.ChildContent, columns);
            });
        }

        static string[] Column(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[index].TextContent).ToArray();

        // --- ApplySort, straight on the column -------------------------------------------------

        [Fact]
        public void ApplySort_OrdersAStringColumn()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, string>(p => p.Last)));
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal(
                new[] { "Adams", "Bell", "Cook", "Draper" },
                column.ApplySort(data.AsQueryable(), descending: false).Select(p => p.Last).ToArray());

            Assert.Equal(
                new[] { "Draper", "Cook", "Bell", "Adams" },
                column.ApplySort(data.AsQueryable(), descending: true).Select(p => p.Last).ToArray());
        }

        [Fact]
        public void ApplySort_OrdersAnIntColumn()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, int>(p => p.Id)));
            var column = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            Assert.Equal(
                new[] { 1, 2, 3, 4 },
                column.ApplySort(data.AsQueryable(), descending: false).Select(p => p.Id).ToArray());

            Assert.Equal(
                new[] { 4, 3, 2, 1 },
                column.ApplySort(data.AsQueryable(), descending: true).Select(p => p.Id).ToArray());
        }

        [Fact]
        public void ApplySort_OrdersADateTimeColumn()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, DateTime>(p => p.Hired)));
            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            Assert.Equal(
                new[] { 2018, 2019, 2020, 2021 },
                column.ApplySort(data.AsQueryable(), descending: false).Select(p => p.Hired.Year).ToArray());

            Assert.Equal(
                new[] { 2021, 2020, 2019, 2018 },
                column.ApplySort(data.AsQueryable(), descending: true).Select(p => p.Hired.Year).ToArray());
        }

        [Fact]
        public void ApplySort_OrdersANestedColumn()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, string>(p => p.Customer.Name)));
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal(
                new[] { "Whisky", "Xray", "Yankee", "Zeta" },
                column.ApplySort(data.AsQueryable(), descending: false).Select(p => p.Customer.Name).ToArray());
        }

        [Fact]
        public void ApplySort_UsesSortByRatherThanTheDisplayedExpression()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last, sortBy: p => p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            // Ordering by Last is not the ordering by "First Last": Alice Draper sorts first by the
            // displayed text and last by Last, so this cannot pass on the wrong key.
            Assert.Equal(
                new[] { "Adams", "Bell", "Cook", "Draper" },
                column.ApplySort(data.AsQueryable(), descending: false).Select(p => p.Last).ToArray());
        }

        [Fact]
        public void ApplySort_TranslatesToAQueryableExpressionRatherThanEnumerating()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, int>(p => p.Id)));
            var column = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            var sorted = column.ApplySort(data.AsQueryable(), descending: false);

            // The point of typed columns is that the ordering stays an expression the provider can see,
            // not a delegate applied after the fact.
            Assert.Contains("OrderBy", sorted.Expression.ToString(), StringComparison.Ordinal);
            Assert.Contains("Id", sorted.Expression.ToString(), StringComparison.Ordinal);
        }

        // --- SortBy on the grid ----------------------------------------------------------------

        [Fact]
        public void SortBy_SortsAscendingThenTogglesToDescending()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal(new[] { "Adams", "Draper", "Bell", "Cook" }, Column(cut, 0));

            cut.InvokeAsync(() => cut.Instance.SortBy(column));

            Assert.Same(column, cut.Instance.SortColumn);
            Assert.False(cut.Instance.SortDescending);
            Assert.Equal(new[] { "Adams", "Bell", "Cook", "Draper" }, Column(cut, 0));

            cut.InvokeAsync(() => cut.Instance.SortBy(column));

            Assert.True(cut.Instance.SortDescending);
            Assert.Equal(new[] { "Draper", "Cook", "Bell", "Adams" }, Column(cut, 0));

            cut.InvokeAsync(() => cut.Instance.SortBy(column));

            Assert.False(cut.Instance.SortDescending);
            Assert.Equal(new[] { "Adams", "Bell", "Cook", "Draper" }, Column(cut, 0));
        }

        [Fact]
        public void SortBy_ADifferentColumn_StartsAscendingAgain()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last),
                Columns.Property<Person, int>(p => p.Id)));

            var last = cut.FindComponent<PropertyColumn<Person, string>>().Instance;
            var id = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            cut.InvokeAsync(() => cut.Instance.SortBy(last));
            cut.InvokeAsync(() => cut.Instance.SortBy(last));

            Assert.True(cut.Instance.SortDescending);

            cut.InvokeAsync(() => cut.Instance.SortBy(id));

            Assert.Same(id, cut.Instance.SortColumn);
            Assert.False(cut.Instance.SortDescending);
            Assert.Equal(new[] { "1", "2", "3", "4" }, Column(cut, 1));
        }

        [Fact]
        public void ClickingASortableHeaderSorts_AndMarksAriaSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            Assert.Null(cut.Find("thead th").GetAttribute("aria-sort"));

            cut.Find("thead th div").Click();

            Assert.Equal(new[] { "Adams", "Bell", "Cook", "Draper" }, Column(cut, 0));
            Assert.Equal("ascending", cut.Find("thead th").GetAttribute("aria-sort"));

            cut.Find("thead th div").Click();

            Assert.Equal(new[] { "Draper", "Cook", "Bell", "Adams" }, Column(cut, 0));
            Assert.Equal("descending", cut.Find("thead th").GetAttribute("aria-sort"));
        }

        [Fact]
        public void AllowSortingFalse_LeavesTheHeaderInert()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)), allowSorting: false);

            Assert.DoesNotContain("rz-sortable-column", cut.Find("thead th").GetAttribute("class"));

            // Rule 3: nothing is paid for when switched off. With sorting off the header carries no
            // handler at all, which bUnit surfaces as a missing-handler exception rather than a no-op.
            Assert.Throws<MissingEventHandlerException>(() => cut.Find("thead th div").Click());

            Assert.Null(cut.Instance.SortColumn);
            Assert.Equal(new[] { "Adams", "Draper", "Bell", "Cook" }, Column(cut, 0));
        }

        [Fact]
        public void AComputedColumnHeaderCarriesNoSortHandler()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last)));

            Assert.Throws<MissingEventHandlerException>(() => cut.Find("thead th div").Click());
            Assert.Null(cut.Instance.SortColumn);
        }

        // --- computed columns ------------------------------------------------------------------

        [Fact]
        public void ComputedColumn_IsNotSortableAndSortByIsANoOp()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.False(column.CanSort);
            Assert.DoesNotContain("rz-sortable-column", cut.Find("thead th").GetAttribute("class"));

            cut.InvokeAsync(() => cut.Instance.SortBy(column));

            // No exception, no sort: a display-only column renders, it just cannot be ordered.
            Assert.Null(cut.Instance.SortColumn);
            Assert.False(cut.Instance.SortDescending);
            Assert.Equal(
                new[] { "Carol Adams", "Alice Draper", "Dave Bell", "Bob Cook" },
                Column(cut, 0));
        }

        [Fact]
        public void ComputedColumn_DoesNotDisturbAnExistingSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last),
                Columns.Property<Person, string>(p => p.First + "!" + p.Last)));

            var declared = cut.FindComponents<PropertyColumn<Person, string>>();
            var sortable = declared[0].Instance;
            var computed = declared[1].Instance;

            Assert.True(sortable.CanSort);
            Assert.False(computed.CanSort);

            cut.InvokeAsync(() => cut.Instance.SortBy(sortable));
            cut.InvokeAsync(() => cut.Instance.SortBy(sortable));

            Assert.True(cut.Instance.SortDescending);

            cut.InvokeAsync(() => cut.Instance.SortBy(computed));

            Assert.Same(sortable, cut.Instance.SortColumn);
            Assert.True(cut.Instance.SortDescending);
            Assert.Equal(new[] { "Draper", "Cook", "Bell", "Adams" }, Column(cut, 0));
        }

        [Fact]
        public void ComputedColumnWithAnExplicitSortBy_IsSortableAndSortsOnTheSortKey()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First + " " + p.Last, sortBy: p => p.Last)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.True(column.CanSort);
            Assert.Contains("rz-sortable-column", cut.Find("thead th").GetAttribute("class"));

            cut.Find("thead th div").Click();

            // Ordered by Last, which is a different order from the displayed "First Last" text.
            Assert.Equal(
                new[] { "Carol Adams", "Dave Bell", "Bob Cook", "Alice Draper" },
                Column(cut, 0));

            cut.Find("thead th div").Click();

            Assert.Equal(
                new[] { "Alice Draper", "Bob Cook", "Dave Bell", "Carol Adams" },
                Column(cut, 0));
        }

        [Fact]
        public void SortingAnIQueryableSourceDoesNotReWrapIt()
        {
            using var ctx = new TestContext();
            var data = People.Sample().AsQueryable();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, int>(p => p.Id)));

            cut.Find("thead th div").Click();

            Assert.Equal(new[] { "1", "2", "3", "4" }, Column(cut, 0));
        }

        [Fact]
        public void SortSurvivesADataChange()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            cut.Find("thead th div").Click();

            var replacement = People.Sample();
            replacement.Add(new Person
            {
                Id = 9, First = "Erin", Last = "Zane", Customer = new Company { Name = "Uniform" }
            });

            cut.SetParametersAndRender(p => p.Add(g => g.Data, replacement));

            Assert.Equal(new[] { "Adams", "Bell", "Cook", "Draper", "Zane" }, Column(cut, 0));
        }

        // --- The sort glyph ---------------------------------------------------------------------

        [Fact]
        public void ASortableColumnDrawsTheGlyphBeforeItIsSorted()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            var glyph = cut.Find("thead th .rz-sortable-column-icon");

            Assert.Contains("rzi-grid-sort", glyph.ClassList);
            Assert.DoesNotContain("rzi-sort-asc", glyph.ClassList);
            Assert.DoesNotContain("rzi-sort-desc", glyph.ClassList);
        }

        // The header is an inline-flex whose glyph carries a reserved width, so a glyph that arrives
        // with the first click is inserted into that line: the title re-truncates and the header
        // visibly jumps. What has to hold is the count, not the presence.
        [Fact]
        public void SortingAColumnDoesNotChangeHowManyElementsItsHeaderHolds()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            var before = cut.FindAll("thead th .rz-column-title > *").Count;

            cut.Find("thead th div").Click();

            Assert.Equal(before, cut.FindAll("thead th .rz-column-title > *").Count);
        }

        [Fact]
        public void TheGlyphTakesItsDirectionFromTheSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)));

            cut.Find("thead th div").Click();
            Assert.Contains("rzi-sort-asc", cut.Find("thead th .rz-sortable-column-icon").ClassList);

            cut.Find("thead th div").Click();
            Assert.Contains("rzi-sort-desc", cut.Find("thead th .rz-sortable-column-icon").ClassList);
        }

        [Fact]
        public void AGridThatDoesNotSortDrawsNoGlyph()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.Last)), allowSorting: false);

            Assert.Empty(cut.FindAll("thead th .rz-sortable-column-icon"));
        }
    }
}
