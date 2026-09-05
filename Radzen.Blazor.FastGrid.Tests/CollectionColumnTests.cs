using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// A column bound to a collection lists its members instead of stringifying the collection, and its
    /// filter matches a row when any member matches. Before this, every such column needed a template
    /// that did nothing but <c>string.Join</c>.
    /// </summary>
    public class CollectionColumnTests
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
                extra?.Invoke(p);
            });
        }

        static string[] CellsOfColumn(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        [Fact]
        public void ListsTheMembersInsteadOfStringifyingTheCollection()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions)));

            Assert.Equal(
                new[] { "North, West", "South", string.Empty, "North, East, South" },
                CellsOfColumn(cut, 0));
        }

        [Fact]
        public void TheSeparatorIsConfigurable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions, separator: " | ")));

            Assert.Equal("North | West", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void AnArrayIsACollectionToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int[]>(p => p.Codes)));

            Assert.Equal(new[] { "10, 20", "20", string.Empty, "30" }, CellsOfColumn(cut, 0));
        }

        [Fact]
        public void AStringIsNotTreatedAsACollectionOfCharacters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First)));

            Assert.Equal("Carol", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void ACollectionValuedPropertyDeclaredAsObjectIsStillListed()
        {
            // The static type says nothing, so the value has to decide.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, object>(p => p.Regions)));

            Assert.Equal("North, West", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void ANullCollectionRendersEmpty()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            data[0].Regions = null!;

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions)), data: data);

            Assert.Equal(string.Empty, CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void TheFormatAppliesToEachMember()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int[]>(p => p.Codes, format: "D4")));

            Assert.Equal("0010, 0020", CellsOfColumn(cut, 0)[0]);
        }

        [Fact]
        public void ACollectionColumnIsNotSortable()
        {
            // No provider can order rows by a list, so offering the header would be a broken promise.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowSorting, true));

            var headers = cut.FindAll("thead th");

            Assert.DoesNotContain("rz-sortable-column", headers[0].ClassName);
            Assert.Contains("rz-sortable-column", headers[1].ClassName);
        }

        [Fact]
        public void ASortByOfTheCollectionsOwnTypeDoesNotMakeItSortable()
        {
            // SortBy on a PropertyColumn is typed at TProp, which for a collection column is the
            // collection - so the only sort key the type parameter admits is another uncomparable one.
            // Offering it produced a header with rz-sortable-column and an onclick that threw
            // InvalidOperationException from Comparer<List<string>>.Default on the first click.
            // CollectionColumn, whose SortBy names a member, is the way to sort one of these.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions, sortByPath: x => x.Regions)),
                p => p.Add(g => g.AllowSorting, true));

            var header = cut.FindAll("thead th")[0];

            Assert.DoesNotContain("rz-sortable-column", header.ClassName);

            // No handler at all, so there is nothing to click and nothing to throw.
            Assert.Null(header.QuerySelector("div")!.GetAttribute("onclick"));
        }

        [Fact]
        public void FilteringMatchesARowWhenAnyMemberMatches()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("North");

            Assert.Equal(new[] { "Carol", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AStringCollectionFiltersOnSubstringsOfItsMembers()
        {
            // Contains is the default for a collection of strings, exactly as for a plain string column.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("out");

            Assert.Equal(new[] { "Alice", "Bob" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AValueTypeCollectionFiltersOnEqualMembers()
        {
            // Contains is meaningless for an int, so the element type has to decide the operator - the
            // property type would say "a list", which decides nothing.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int[]>(p => p.Codes),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("20");

            Assert.Equal(new[] { "Carol", "Alice" }, CellsOfColumn(cut, 1));
        }

        [Fact]
        public void AnEmptyCollectionMatchesNoFilter()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("o");

            Assert.DoesNotContain("Dave", CellsOfColumn(cut, 1));
        }

        [Fact]
        public void TheDescriptorNamesTheCollectionAndCarriesItsType()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions)),
                p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("North");

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal("Regions", filter.Property);
            Assert.Equal("North", filter.FilterValue);
            Assert.Equal(FilterOperator.Contains, filter.FilterOperator);
        }

        [Fact]
        public void FilteringACollectionWorksThroughAQueryableToo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions),
                Columns.Property<Person, string>(p => p.First)),
                p => p.Add(g => g.AllowFiltering, true),
                data: People.Sample().AsQueryable());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("East");

            Assert.Equal(new[] { "Bob" }, CellsOfColumn(cut, 1));
        }
    }
}
