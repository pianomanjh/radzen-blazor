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
    /// FilterProperty names a member of the collection's element, so a column bound to a collection of
    /// objects filters on something inside each one: Accounts.Any(a =&gt; a.Name ...). It is a string
    /// rather than an expression because the element type is not a type parameter of the column, so
    /// there is nothing to write the lambda against.
    /// </summary>
    public class CollectionMemberFilterTests
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
                extra?.Invoke(p);
            });
        }

        static string[] CellsOfColumn(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static RenderFragment Accounts(FilterMode? mode = null) => Columns.Of(
            Columns.Property<Person, List<Company>>(x => x.Accounts, filterProperty: "Name", filterMode: mode),
            Columns.Property<Person, string>(x => x.First));

        [Fact]
        public void FiltersOnAMemberOfEachElement()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Acme");

            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheMemberIsMatchedAsAString()
        {
            // The comparison is against the member, so Contains means a substring of the name - not of
            // whatever the element's ToString happens to produce.
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("tec");

            Assert.Equal(new[] { "Alice" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheDescriptorCarriesTheMemberSeparatelyFromTheCollection()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Acme");

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal("Accounts", filter.Property);
            Assert.Equal("Name", filter.FilterProperty);
        }

        [Fact]
        public void AnElementWithNoMatchingMemberIsNotMatched()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Nothing");

            Assert.Empty(CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheCheckBoxListOffersTheMembersRatherThanTheElements()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(FilterMode.CheckBoxList));

            var offered = cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Data.Cast<object>().ToArray();

            Assert.Equal(new object[] { "Acme", "Globex", "Initech", "Umbrella" }, offered);
        }

        [Fact]
        public void PickingAMemberFromTheListFiltersOnIt()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(FilterMode.CheckBoxList));

            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Change.InvokeAsync(new List<object> { "Umbrella" }));

            Assert.Equal(new[] { "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void WorksOverAQueryableToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(), data: People.Sample().AsQueryable());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Globex");

            Assert.Equal(new[] { "Carol" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void FilterPropertyAlsoNarrowsAScalarColumnsLookup()
        {
            // Not only for collections: a column bound to an object filters on a member of it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Company>(x => x.Customer, filterProperty: "Name",
                    filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.First)));

            var offered = cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Data.Cast<object>().ToArray();

            Assert.Equal(new object[] { "Whisky", "Xray", "Yankee", "Zeta" }, offered);
        }
    }
}
