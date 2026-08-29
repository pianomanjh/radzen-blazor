using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Regressions for state that outlives the parameter set that produced it: a format decided from a
    /// static type that says nothing about the value, a header defaulted by writing to its own parameter,
    /// and a sort holding a column that has left the grid.
    /// </summary>
    public class ColumnStateTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns, bool allowSorting = false)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowSorting, allowSorting);
            });
        }

        static string[] CellsOfColumn(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static string[] HeaderTitles(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("thead th .rz-column-title-content").Select(e => e.TextContent).ToArray();

        [Fact]
        public void FormatAppliesToANullableValue()
        {
            // Nullable<decimal> is not itself IFormattable, so a check on TProp alone drops the format
            // silently and the cell renders the round-trip value instead of the currency.
            using var ctx = new TestContext();
            var expected = 250.5m.ToString("C", CultureInfo.CurrentCulture);

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, decimal?>(p => p.Bonus, format: "C")));

            Assert.Equal(expected, CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void ANullNullableValueRendersEmptyUnderAFormat()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, decimal?>(p => p.Bonus, format: "C")));

            Assert.Equal(string.Empty, CellsOfColumn(cut, 0)[1]);
        }

        [Fact]
        public void FormatAppliesToAValueAuthoredAsObject()
        {
            // The spec endorses `p => (object)p.Salary`; the static type is object, so only the value
            // itself can say whether it formats.
            using var ctx = new TestContext();
            var expected = 4000m.ToString("C", CultureInfo.CurrentCulture);

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, object>(p => (object)p.Salary, format: "C")));

            Assert.Equal(expected, CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void AnUnformattableValueAuthoredAsObjectStillRenders()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, object>(p => p.Customer, format: "C")));

            Assert.Equal(typeof(Company).ToString(), CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void ChangingThePropertyMovesTheDefaultedHeaderWithIt()
        {
            // Defaulting the header by assigning to Title leaves the parameter non-null, so the next
            // parameter set finds nothing to default and the header keeps naming the old property.
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(Columns.Property<Person, string>(p => p.First)));

            Assert.Equal(new[] { "First" }, HeaderTitles(cut));

            cut.SetParametersAndRender(p => p.Add(
                g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.Last))));

            Assert.Equal(new[] { "Last" }, HeaderTitles(cut));
            Assert.Equal("Adams", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void AnExplicitTitleStillWins()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "Given name")));

            Assert.Equal(new[] { "Given name" }, HeaderTitles(cut));
        }

        [Fact]
        public void DroppingTheSortedColumnClearsTheSort()
        {
            // The grid holds the sort column by reference. If a column set replaces it, the grid would
            // otherwise keep ordering by a column no header names and no click can toggle.
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(
                Columns.Property<Person, string>(p => p.First),
                Columns.Property<Person, int>(p => p.Id)), allowSorting: true);

            cut.FindAll("thead th")[1].QuerySelector("div").Click();

            Assert.Equal(new[] { "1", "2", "3", "4" }, CellsOfColumn(cut, 1));

            cut.SetParametersAndRender(p => p.Add(
                g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First))));

            Assert.Null(cut.Instance.SortColumn);
            Assert.Equal(
                data.Select(p => p.First).ToArray(),
                CellsOfColumn(cut, 0));
        }

        [Fact]
        public void KeepingTheSortedColumnKeepsTheSort()
        {
            using var ctx = new TestContext();
            var data = People.Sample();
            var id = Columns.Property<Person, int>(p => p.Id);

            var cut = Render(ctx, data, Columns.Of(
                Columns.Property<Person, string>(p => p.First), id), allowSorting: true);

            cut.FindAll("thead th")[1].QuerySelector("div").Click();
            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Sample()));

            Assert.NotNull(cut.Instance.SortColumn);
            Assert.Equal(new[] { "1", "2", "3", "4" }, CellsOfColumn(cut, 1));
        }
    }
}
