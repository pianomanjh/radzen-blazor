using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The check-box-list filter mode: a multi-select of the column's distinct values, filtering with
    /// <c>In</c>. RadzenDropDown in Multiple mode already draws a check box per item, so there is no
    /// popup, toggle button or apply step of the grid's own.
    /// </summary>
    public class CheckBoxListFilterTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IEnumerable<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                extra?.Invoke(p);
            });
        }

        static string[] CellsOfColumn(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static RadzenDropDown<IEnumerable> Picker(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindComponents<RadzenDropDown<IEnumerable>>()[index].Instance;

        static object[] Offered(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            Picker(cut, index).Data.Cast<object>().ToArray();

        static void Pick(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, params object[] values) =>
            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<IEnumerable>>()[index]
                .Instance.Change.InvokeAsync(values.ToList()));

        [Fact]
        public void SimpleModeStillGivesATextBox()
        {
            using var ctx = new TestContext();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
                p.Add(g => g.AllowFiltering, true);
            });

            Assert.NotEmpty(cut.FindAll("input.rz-textbox"));
            Assert.Empty(cut.FindComponents<RadzenDropDown<IEnumerable>>());
        }

        [Fact]
        public void CheckBoxListModeGivesAMultiSelectInstead()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First)));

            Assert.Single(cut.FindComponents<RadzenDropDown<IEnumerable>>());
            Assert.True(Picker(cut, 0).Multiple);
            Assert.Empty(cut.FindAll(".rz-cell-filter input.rz-textbox"));
        }

        [Fact]
        public void AColumnCanOverrideTheGridsMode()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, int>(x => x.Id, filterMode: FilterMode.Simple)));

            Assert.Single(cut.FindComponents<RadzenDropDown<IEnumerable>>());
            Assert.Single(cut.FindAll(".rz-cell-filter input.rz-textbox"));
        }

        [Fact]
        public void OffersTheDistinctValuesOfTheColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.Customer.Name)));

            Assert.Equal(new object[] { "Whisky", "Xray", "Yankee", "Zeta" }, Offered(cut, 0));
        }

        [Fact]
        public void DistinctMeansDistinct()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            data[1].Customer = data[0].Customer;

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Customer.Name)), data: data);

            Assert.Equal(new object[] { "Whisky", "Xray", "Zeta" }, Offered(cut, 0));
        }

        [Fact]
        public void PickingValuesFiltersToThem()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Customer.Name),
                Columns.Property<Person, string>(x => x.First)));

            Pick(cut, 0, "Zeta", "Xray");

            Assert.Equal(new[] { "Carol", "Dave" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void PickingUsesTheInOperator()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.Customer.Name)));

            Pick(cut, 0, "Zeta");

            Assert.Equal(FilterOperator.In, Assert.Single(cut.Instance.Filters).FilterOperator);
        }

        [Fact]
        public void TickingNothingIsNoFilterRatherThanNoRows()
        {
            // An empty selection is what clearing the last box looks like. Treating it as "in the empty
            // set" would leave the grid blank with no visible filter to remove.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Customer.Name),
                Columns.Property<Person, string>(x => x.First)));

            Pick(cut, 0, "Zeta");

            Assert.Single(cut.FindAll("tbody tr"));

            Pick(cut, 0);

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void OffersTheMembersOfACollectionColumn()
        {
            // The column's values are lists, so what the reader picks from is the members, not the lists.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, List<string>>(x => x.Regions)));

            Assert.Equal(new object[] { "East", "North", "South", "West" }, Offered(cut, 0));
        }

        [Fact]
        public void PickingAMemberMatchesEveryRowThatHasIt()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(x => x.Regions),
                Columns.Property<Person, string>(x => x.First)));

            Pick(cut, 0, "North");

            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void PickingSeveralMembersMatchesARowThatHasAnyOfThem()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(x => x.Regions),
                Columns.Property<Person, string>(x => x.First)));

            Pick(cut, 0, "West", "South");

            Assert.Equal(new[] { "Carol", "Alice", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void SuppliedLookupDataReplacesTheDistinctScan()
        {
            // What a large or remote source wants: the caller already knows the values.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Customer.Name,
                    filterLookupData: new object[] { "Zeta", "Nowhere" })));

            Assert.Equal(new object[] { "Zeta", "Nowhere" }, Offered(cut, 0));
        }

        [Fact]
        public void TheLookupIsRebuiltWhenTheDataChanges()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First)));

            Assert.Equal(4, Offered(cut, 0).Length);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(2)));

            Assert.Equal(new object[] { "First1", "First2" }, Offered(cut, 0));
        }

        [Fact]
        public void WorksOverAQueryableToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Customer.Name),
                Columns.Property<Person, string>(x => x.First)),
                data: People.Sample().AsQueryable());

            Assert.Equal(new object[] { "Whisky", "Xray", "Yankee", "Zeta" }, Offered(cut, 0));

            Pick(cut, 0, "Whisky");

            Assert.Equal(new[] { "Bob" }, CellsOfColumn(cut, 1));
        }
    }
}
