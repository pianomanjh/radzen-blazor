using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The same column one cardinality up: the row carries a collection of ids and the cell lists the
    /// names they stand for. The split is on cardinality alone - where the names come from is the same
    /// closed type either way.
    /// </summary>
    public class LookupCollectionColumnTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            IEnumerable<Person> data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Filtered(TestContext ctx, RenderFragment columns,
            IEnumerable<Person> data = null) =>
            Render(ctx, columns, p =>
            {
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
            }, data);

        static string[] Cells(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        static object Named(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, string name) =>
            cut.FindComponents<RadzenDropDown<System.Collections.IEnumerable>>()[index]
                .Instance.Data.Cast<object>().Single(entry => entry.ToString() == name);

        static void Pick(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, params object[] entries) =>
            cut.InvokeAsync(() => cut.FindComponents<RadzenDropDown<System.Collections.IEnumerable>>()[index]
                .Instance.Change.InvokeAsync(entries.ToList())).Wait();

        static void Filtering(ComponentParameterCollectionBuilder<RadzenFastGrid<Person>> p) =>
            p.Add(g => g.AllowFiltering, true);

        static RenderFragment Brands() => Columns.Of(Columns.LookupCollection<Person, int>(
            x => x.BrandIds, FastGridLookup.Map(Lookups.Brands())));

        [Fact]
        public void ACellListsTheNamesItsIdsStandFor()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Brands());

            Assert.Equal(new[] { "Acme, Globex", "Globex", "", "Umbrella, Acme" }, Cells(cut, 0));
        }

        [Fact]
        public void TheSeparatorIsTheColumnsToChoose()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.LookupCollection<Person, int>(
                x => x.BrandIds, FastGridLookup.Map(Lookups.Brands()), separator: " / ")));

            Assert.Equal("Acme / Globex", Cells(cut, 0)[0]);
        }

        [Fact]
        public void PickingANameFiltersToTheRowsCarryingItsId()
        {
            // The predicate appends to the authored expression rather than rewriting it:
            // p => p.BrandIds.Any(id => selected.Contains(id)). Every generic argument there is TKey,
            // so there is no MakeGenericMethod and nothing behind DynamicCode.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Brands());

            Pick(cut, 0, Named(cut, 0, "Acme"));

            Assert.Equal(new[] { "Acme, Globex", "Umbrella, Acme" }, Cells(cut, 0));
        }

        [Fact]
        public void TheDescriptorItReportsIsTheConventionUpstreamAlreadyTranslates()
        {
            // An earlier draft had this as an encoding of the grid's own, on the assumption that a bare
            // "BrandIds In [100]" would read as a scalar comparison anywhere else. Upstream already has
            // the convention - an empty FilterProperty means the element itself - so what is emitted is
            // run through upstream's own builder here rather than only through this grid's.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Brands());

            Pick(cut, 0, Named(cut, 0, "Acme"));

            var descriptor = Assert.Single(cut.Instance.Filters);

            Assert.Equal("BrandIds", descriptor.Property);
            Assert.True(string.IsNullOrEmpty(descriptor.FilterProperty));
            Assert.Equal(FilterOperator.In, descriptor.FilterOperator);

            var upstream = People.Sample().AsQueryable()
                .Where(new[] { descriptor }, LogicalFilterOperator.And, FilterCaseSensitivity.Default)
                .Select(p => p.First)
                .ToArray();

            Assert.Equal(new[] { "Carol", "Bob" }, upstream);
        }

        [Fact]
        public void ACollectionOfIdsIsNotSortableEither()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.LookupCollection<Person, int>(x => x.BrandIds,
                    FastGridLookup.Map(Lookups.Brands()))),
                extra: p => p.Add(g => g.AllowSorting, true));

            var headers = cut.FindAll("thead th");

            Assert.Contains("rz-sortable-column", headers[0].ClassName, StringComparison.Ordinal);
            Assert.DoesNotContain("rz-sortable-column", headers[1].ClassName, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTwoRoutesAgreeAboutARowCarryingNoIdsAtAll()
        {
            // A grid over a List composes a delegate and one over a queryable composes an expression,
            // and the last time those two disagreed about a null it was In reading a null string one way
            // in each builder. A NotIn is the case where the null guard has to be inside the negation
            // rather than outside it.
            using var ctx = new TestContext();
            var data = People.Sample();

            data[2].BrandIds = null;

            var columns = Columns.Of(Columns.LookupCollection<Person, int>(
                x => x.BrandIds, FastGridLookup.Map(Lookups.Brands()),
                filterValue: new List<int> { 100 }, filterOperator: FilterOperator.NotIn));

            var inMemory = Render(ctx, columns, Filtering, data);
            var composed = Render(ctx, columns, Filtering, data.AsQueryable());

            // The row carrying no ids at all is in neither answer or in both, and "not one of these
            // brands" is true of it either way it is read - so what is pinned is that the two agree.
            Assert.Equal(Cells(composed, 0), Cells(inMemory, 0));
            Assert.DoesNotContain("Acme", string.Join("|", Cells(inMemory, 0)), StringComparison.Ordinal);
        }

        [Fact]
        public void ACellDoesNotBoxTheIdsItLists()
        {
            // The joined string is unavoidable and is what §14 budgeted for. A box per member per cell
            // per render was not, and the untyped join costs one - so the control here is that same
            // cell listed through it, producing identical characters.
            const int iterations = 20000;

            using var ctx = new TestContext();
            var item = new Person { BrandIds = new List<int> { 100, 200, 300 } };

            var cut = Render(ctx, Brands(), data: new[] { item });

            Assert.Equal("Acme, Globex, Umbrella", cut.Find("tbody td span").TextContent);

            var column = cut.FindComponent<LookupCollectionColumn<Person, int>>().Instance;
            var boxing = new BoxingJoinColumn<Person, int>(x => x.BrandIds, Lookups.Brands(), ", ");

            var typedBytes = Allocation.PerCell(column, item, iterations);
            var boxingBytes = Allocation.PerCell(boxing, item, iterations);

            // Three boxed ints are 72 bytes on a 64-bit runtime. Requiring a third of that leaves room
            // for measurement noise while still failing outright if the typed join starts boxing.
            Assert.True(boxingBytes - typedBytes > 24,
                $"expected the untyped join to allocate materially more per cell; typed={typedBytes}, boxing={boxingBytes}");
        }

        [Fact]
        public void TheListOffersNoBlankEntry()
        {
            // A row with no ids at all is a different question from a row whose id is null, and In over
            // the elements does not ask it.
            using var ctx = new TestContext();

            var cut = Filtered(ctx, Brands());

            Assert.Equal(new[] { "Acme", "Globex", "Umbrella" },
                cut.FindComponents<RadzenDropDown<System.Collections.IEnumerable>>()[0]
                    .Instance.Data.Cast<object>().Select(entry => entry.ToString()).ToArray());
        }
    }
}
