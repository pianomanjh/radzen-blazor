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
    /// <c>CollectionColumn&lt;TItem, TElement&gt;</c> is for a collection of objects: the element type is
    /// a type parameter, so the member to show and the member to filter on are expressions rather than
    /// strings. Razor infers the element type from <c>Property</c>; <c>AuthoringSample.razor</c> is what
    /// checks that, since these fragments are built by hand.
    /// </summary>
    public class CollectionColumnMemberTests
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

        static object[] Offered(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindComponents<RadzenDropDown<IEnumerable>>()[index].Instance.Data.Cast<object>().ToArray();

        static void TypeInFilter(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, string text) =>
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[index].Change(text);

        static RenderFragment Accounts(FilterMode? mode = null) => Columns.Of(
            Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, filterMode: mode),
            Columns.Property<Person, string>(x => x.First));

        [Fact]
        public void ShowsTheChosenMemberOfEachElement()
        {
            // Without DisplayProperty this reads "Namespace.Company, Namespace.Company".
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            Assert.Equal(
                new[] { "Acme, Globex", "Initech", string.Empty, "Acme, Umbrella" },
                CellsOfColumn(cut, 0));
        }

        [Fact]
        public void WithoutADisplayPropertyItFallsBackToToString()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Collection<Person, Company>(x => x.Accounts)));

            Assert.Equal(typeof(Company).ToString() + ", " + typeof(Company), CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void TheSeparatorAndFormatApplyToTheMembers()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, int>(x => x.Codes, separator: " | ", format: "D4")));

            Assert.Equal("0010 | 0020", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void FiltersOnTheDisplayedMemberByDefault()
        {
            // Filtering on what the reader can see is almost always what is meant.
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            TypeInFilter(cut, 0, "Acme");

            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AnExplicitFilterPropertyOverridesTheDisplayedOne()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, a => a.Region),
                Columns.Property<Person, string>(x => x.First)));

            TypeInFilter(cut, 0, "West");

            Assert.Equal(new[] { "Carol" }, CellsOfColumn(cut, 1));
            Assert.Equal("Acme, Globex", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void TheDescriptorCarriesTheCollectionAndTheMemberSeparately()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            TypeInFilter(cut, 0, "Acme");

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal("Accounts", filter.Property);
            Assert.Equal("Name", filter.FilterProperty);
        }

        [Fact]
        public void TheMemberDecidesTheDefaultOperator()
        {
            // Contains, because Name is a string - not Equals, which is what the collection type alone
            // would suggest.
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            TypeInFilter(cut, 0, "cm");

            Assert.Equal(FilterOperator.Contains, Assert.Single(cut.Instance.Filters).FilterOperator);
            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AValueTypedMemberResolvesToItsOwnTypeToo()
        {
            // A selector declared as returning object hides the member's type two different ways: a
            // value type is wrapped in a Convert, a reference type is not wrapped at all and the tree
            // just carries a narrower body. Reading the delegate's return type sees object either way.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Size,
                    filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.First)));

            Assert.Equal(new object[] { 10, 20, 30, 40 }, Offered(cut, 0));

            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Change.InvokeAsync(new List<object> { 40 }));

            Assert.Equal(new[] { "Bob" }, CellsOfColumn(cut, 1));
            Assert.Equal(FilterOperator.In, Assert.Single(cut.Instance.Filters).FilterOperator);
        }

        [Fact]
        public void AValueTypedMemberFiltersOnEqualityFromTheBox()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Size),
                Columns.Property<Person, string>(x => x.First)));

            TypeInFilter(cut, 0, "10");

            Assert.Equal(FilterOperator.Equals, Assert.Single(cut.Instance.Filters).FilterOperator);
            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AValueTypedSortKeyOrdersByItsOwnType()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, sortBy: x => x.Id),
                Columns.Property<Person, string>(x => x.First)),
                p => p.Add(g => g.AllowSorting, true));

            cut.FindAll("thead th")[0].QuerySelector("div")!.Click();

            Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave" }, CellsOfColumn(cut, 1));

            cut.FindAll("thead th")[0].QuerySelector("div")!.Click();

            Assert.Equal(new[] { "Dave", "Carol", "Bob", "Alice" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void ADeclaredFilterValueUsesTheMembersDefaultOperatorFromTheFirstRender()
        {
            // The only case where the operator is chosen before anyone touches the filter box, and so
            // the only one that notices if the member's type is read before it has been worked out.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, filterValue: "cm"),
                Columns.Property<Person, string>(x => x.First)));

            Assert.Equal(FilterOperator.Contains, Assert.Single(cut.Instance.Filters).FilterOperator);
            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AValueTypeMemberFiltersOnEquality()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, int>(x => x.Codes),
                Columns.Property<Person, string>(x => x.First)));

            TypeInFilter(cut, 0, "20");

            Assert.Equal(new[] { "Carol", "Alice" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void NoRowMatchesAMemberNoneOfThemHas()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            TypeInFilter(cut, 0, "Nothing");

            Assert.Empty(CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheCheckBoxListOffersTheMembersRatherThanTheElements()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(FilterMode.CheckBoxList));

            Assert.Equal(new object[] { "Acme", "Globex", "Initech", "Umbrella" }, Offered(cut, 0));
        }

        [Fact]
        public void TheCheckBoxListFollowsTheFilterPropertyNotTheDisplayedOne()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, a => a.Region,
                    filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.First)));

            Assert.Equal(new object[] { "East", "North", "South", "West" }, Offered(cut, 0));
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
        public void ACollectionColumnIsNotSortable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(), p => p.Add(g => g.AllowSorting, true));

            Assert.DoesNotContain("rz-sortable-column", cut.FindAll("thead th")[0].ClassName);
        }

        [Fact]
        public void AnExplicitSortByMakesItSortable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name, sortBy: x => x.First),
                Columns.Property<Person, string>(x => x.First)),
                p => p.Add(g => g.AllowSorting, true));

            var header = cut.FindAll("thead th")[0];

            Assert.Contains("rz-sortable-column", header.ClassName);

            header.QuerySelector("div")!.Click();

            Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheHeaderDefaultsToTheCollectionsName()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            Assert.Equal("Accounts", cut.FindAll("thead th .rz-column-title-content")[0].TextContent);
        }

        [Fact]
        public void WorksOverAQueryableToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts(FilterMode.CheckBoxList), data: People.Sample().AsQueryable());

            Assert.Equal(new object[] { "Acme", "Globex", "Initech", "Umbrella" }, Offered(cut, 0));

            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Change.InvokeAsync(new List<object> { "Globex" }));

            Assert.Equal(new[] { "Carol" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AnEmptyCollectionShowsNothingAndMatchesNothing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Accounts());

            Assert.Equal(string.Empty, CellsOfColumn(cut, 0)[2]);

            TypeInFilter(cut, 0, "e");

            Assert.DoesNotContain("Dave", CellsOfColumn(cut, 1));
        }
    }
}
