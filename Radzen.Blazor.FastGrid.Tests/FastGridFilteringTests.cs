using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class FastGridFilteringTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns, Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                extra?.Invoke(p);
            });
        }

        static RenderFragment TwoColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First),
            Columns.Property<Person, int>(x => x.Id));

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        [Fact]
        public void NoFilterRowUnlessFilteringIsAllowed()
        {
            using var ctx = new TestContext();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns());
            });

            Assert.Single(cut.FindAll("thead tr"));
            Assert.Empty(cut.FindAll(".rz-cell-filter"));
        }

        [Fact]
        public void TheFilterRowIsASecondHeaderRow()
        {
            // RadzenDataGrid puts filters in their own tr, whose th holds div.rz-cell-filter directly -
            // no title wrapper. The theme's th padding hangs off that div.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());
            var rows = cut.FindAll("thead tr");

            Assert.Equal(2, rows.Count);

            var cells = rows[1].QuerySelectorAll("th");

            Assert.Equal(2, cells.Length);
            Assert.Equal("rz-cell-filter", cells[0].Children[0].ClassName);
            Assert.Equal("rz-cell-filter-content", cells[0].Children[0].Children[0].ClassName);
            Assert.Equal("rz-cell-filter-label", cells[0].QuerySelector(".rz-cell-filter-label")!.ClassName);
            Assert.Equal("rz-textbox", cells[0].QuerySelector("input")!.ClassName);
        }

        [Fact]
        public void TypingInTheFilterBoxNarrowsTheRows()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");

            // Contains is the default for a string column, and the default comparison is the provider's,
            // which for LINQ to Objects is case sensitive - so Carol and Dave, not Alice.
            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void CaseInsensitiveComparisonIsAvailable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns(),
                p => p.Add(g => g.FilterCaseSensitivity, FilterCaseSensitivity.CaseInsensitive));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");

            Assert.Equal(new[] { "Carol", "Alice", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void ANumericColumnFiltersOnEquality()
        {
            // Contains is meaningless for an int, so the default operator has to depend on the type.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("3");

            Assert.Equal(new[] { "Carol" }, FirstNames(cut));
        }

        [Fact]
        public void TextThatIsNotAValueOfTheColumnTypeFiltersNothing()
        {
            // A half-typed number is what this looks like in practice. Throwing would take the page down.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("not a number");

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void ClearingTheBoxRestoresEveryRow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());
            var input = cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0];

            input.Change("a");

            Assert.Equal(2, cut.FindAll("tbody tr").Count);

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("");

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void TheClearButtonAppearsOnlyWhileFiltered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            Assert.Empty(cut.FindAll(".rz-cell-filter-clear"));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");

            Assert.Single(cut.FindAll(".rz-cell-filter-clear"));

            cut.Find(".rz-cell-filter-clear").Click();

            Assert.Empty(cut.FindAll(".rz-cell-filter-clear"));
            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void FiltersOnEveryFilteredColumnAtOnce()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());
            var inputs = cut.FindAll("thead tr")[1].QuerySelectorAll("input");

            inputs[0].Change("a");
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("4");

            Assert.Equal(new[] { "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void OrCombinesThemInstead()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns(),
                p => p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Carol");
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("4");

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void ADeclaredFilterValueAppliesFromTheFirstRender()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterValue: "Bob"),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Equal(new[] { "Bob" }, FirstNames(cut));
        }

        [Fact]
        public void AnExplicitOperatorOverridesTheDefault()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterValue: "a",
                    filterOperator: FilterOperator.EndsWith),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Empty(FirstNames(cut));
        }

        [Fact]
        public void FilteringIsNotAppliedWhileFilteringIsOff()
        {
            // The column keeps its filter; the grid simply does not use it. Turning filtering back on
            // must not need the value re-entered.
            using var ctx = new TestContext();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, filterValue: "Bob")));
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);

            cut.SetParametersAndRender(p => p.Add(g => g.AllowFiltering, true));

            Assert.Equal(1, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void ChangingTheDeclaredValueReplacesWhatTheBoxPutThere()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterValue: "Bob"),
                Columns.Property<Person, int>(x => x.Id)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Carol");

            Assert.Equal(new[] { "Carol" }, FirstNames(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterValue: "Dave"),
                Columns.Property<Person, int>(x => x.Id))));

            Assert.Equal(new[] { "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void FilteringReturnsToTheFirstPage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), TwoColumns(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(3));

            Assert.Equal(3, cut.Instance.CurrentPage);

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            Assert.Equal(0, cut.Instance.CurrentPage);
        }

        [Fact]
        public void ThePagerCountsWhatTheFilterLeft()
        {
            // Counting the unfiltered source would offer pages that render empty.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), TwoColumns(), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.ShowPagingSummary, true);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            // First1 and First10..First19 - eleven rows, three pages.
            var summary = cut.Find(".rz-pager-summary").TextContent;

            Assert.Contains("11", summary, StringComparison.Ordinal);
            Assert.Contains("3", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void SortingAndFilteringComposeRatherThanReplaceEachOther()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns(), p => p.Add(g => g.AllowSorting, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");
            cut.FindAll("thead tr")[0].QuerySelectorAll("th")[0].QuerySelector("div")!.Click();

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));

            cut.FindAll("thead tr")[0].QuerySelectorAll("th")[0].QuerySelector("div")!.Click();

            Assert.Equal(new[] { "Dave", "Carol" }, FirstNames(cut));
        }

        [Fact]
        public void ExposesItsFiltersAsDescriptors()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            Assert.Empty(cut.Instance.Filters);

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Bob");

            var filter = Assert.Single(cut.Instance.Filters);

            Assert.Equal("First", filter.Property);
            Assert.Equal("Bob", filter.FilterValue);
            Assert.Equal(FilterOperator.Contains, filter.FilterOperator);
            Assert.Equal(typeof(string), filter.Type);
        }

        [Fact]
        public void AcceptsDescriptorsFromElsewhere()
        {
            // This is what makes RadzenDataFilter and restored settings usable: they speak descriptors.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor { Property = "Id", FilterValue = 2, FilterOperator = FilterOperator.GreaterThan },
            }));

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void DescriptorsNamingNoColumnAreIgnored()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor { Property = "Nonexistent", FilterValue = "x" },
            }));

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void ApplyingDescriptorsReplacesWhatWasThereBefore()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Bob");
            cut.InvokeAsync(() => cut.Instance.ApplyFilters(Array.Empty<FilterDescriptor>()));

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void ADescriptorCarryingAnEmptyListIsNoFilter()
        {
            // The rule has to hold wherever the value comes from, not only from the check-box list:
            // "in the empty set" would leave the grid blank with no visible filter to remove.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor
                {
                    Property = "First",
                    FilterValue = new List<string>(),
                    FilterOperator = FilterOperator.In,
                },
            }));

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void ClearFiltersClearsEveryColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), TwoColumns());

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("a");
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("3");

            Assert.Equal(1, cut.FindAll("tbody tr").Count);

            cut.InvokeAsync(() => cut.Instance.ClearFilters());

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void AComputedColumnCannotBeFiltered()
        {
            // It has no property path, so there is nothing to build a predicate against.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First + " " + x.Last),
                Columns.Property<Person, int>(x => x.Id)));

            var cells = cut.FindAll("thead tr")[1].QuerySelectorAll("th");

            Assert.Empty(cells[0].Children);
            Assert.NotEmpty(cells[1].Children);
        }

        [Fact]
        public void FilterableFalseRemovesTheBoxAndTheFilter()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterValue: "Bob", filterable: false),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Empty(cut.FindAll("thead tr")[1].QuerySelectorAll("th")[0].Children);
            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void AFilterTemplateReplacesTheBuiltInBox()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First,
                    filterTemplate: _ => b => b.AddMarkupContent(0, "<span class=\"mine\">x</span>")),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Single(cut.FindAll(".rz-cell-filter .mine"));
            Assert.Empty(cut.FindAll("thead tr")[1].QuerySelectorAll("th")[0].QuerySelectorAll("input"));
        }

        [Fact]
        public void FilterByFiltersADifferentPropertyFromTheOneDisplayed()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterBy: x => x.Last),
                Columns.Property<Person, int>(x => x.Id)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Cook");

            Assert.Equal(new[] { "Bob" }, FirstNames(cut));
        }

        [Fact]
        public void AColumnThatSortsByAnotherPropertyStillFiltersOnItsOwn()
        {
            // The sort key and the filter key are separate: a column that displays First and sorts by
            // Last still filters on what the reader can see.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First, sortBy: x => x.Last),
                Columns.Property<Person, int>(x => x.Id)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Bob");

            Assert.Equal(new[] { "Bob" }, FirstNames(cut));
        }

        // Neither an enum nor a Guid converts from a string through IConvertible, so the framework's own
        // Convert.ChangeType throws for both - and the filter box, which treats a value it cannot convert
        // as a half-typed one, quietly cleared the filter instead of applying it. Typing a whole, valid
        // value into either column has to narrow the grid.
        [Fact]
        public void AnEnumColumnFiltersOnWhatIsTyped()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, Grade>(x => x.Grade)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("Senior");

            Assert.Equal(new[] { "Carol", "Bob" }, FirstNames(cut));
        }

        [Fact]
        public void AnEnumColumnIsNotCaseSensitiveAboutTheName()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, Grade>(x => x.Grade)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("junior");

            Assert.Equal(new[] { "Alice", "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void AGuidColumnFiltersOnWhatIsTyped()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, Guid>(x => x.Reference)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1]
                .Change(People.Reference(4).ToString());

            Assert.Equal(new[] { "Dave" }, FirstNames(cut));
        }

        [Fact]
        public void SomethingThatIsNotAValueOfTheColumnsTypeFiltersNothing()
        {
            // Half a name is what a filter box looks like while it is being typed into, so it must leave
            // the grid alone rather than throwing out of the change handler.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, Grade>(x => x.Grade)));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1].Change("Sen");

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, FirstNames(cut));
        }
    }
}
