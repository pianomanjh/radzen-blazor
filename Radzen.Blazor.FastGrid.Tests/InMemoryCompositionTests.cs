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
    /// The grid composing over a list against the grid composing over a queryable.
    /// </summary>
    /// <remarks>
    /// A list is filtered and sorted with delegates, a queryable with expression trees the provider is
    /// meant to translate. Two routes through the same component is two chances to answer differently,
    /// so what these check is that they do not: same data, same filters, same sort, same rows in the
    /// same order.
    /// </remarks>
    public class InMemoryCompositionTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns, Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowSorting, true);
                extra?.Invoke(p);
            });
        }

        static string[] Rows(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr")
                .Select(row => string.Join("|", row.QuerySelectorAll("td").Select(td => td.TextContent)))
                .ToArray();

        static RenderFragment Columns => Radzen.FastGrid.Tests.Columns.Of(
            Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First, title: "First"),
            Radzen.FastGrid.Tests.Columns.Property<Person, int>(x => x.Id, title: "Id"),
            Radzen.FastGrid.Tests.Columns.Property<Person, decimal?>(x => x.Bonus, title: "Bonus"));

        /// <summary>The same grid twice, once over the list and once over the same rows as a queryable.</summary>
        static void BothRoutesAgree(Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra)
        {
            var people = People.Many(20);

            people[3].First = null;
            people[4].Bonus = null;
            people[5].First = "FIRST6";

            using var listContext = new TestContext();
            using var queryableContext = new TestContext();

            var overList = Render(listContext, people, Columns, extra);
            var overQueryable = Render(queryableContext, people.AsQueryable(), Columns, extra);

            Assert.Equal(Rows(overQueryable), Rows(overList));
            Assert.NotEmpty(Rows(overList));
        }

        [Fact]
        public void Unfiltered() => BothRoutesAgree(null);

        [Fact]
        public void FilteredByAString() => BothRoutesAgree(p => p.Add(g => g.ChildContent,
            Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First, filterValue: "First1"),
                Radzen.FastGrid.Tests.Columns.Property<Person, int>(x => x.Id))));

        [Fact]
        public void FilteredByANullableNumber() => BothRoutesAgree(p => p.Add(g => g.ChildContent,
            Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First),
                Radzen.FastGrid.Tests.Columns.Property<Person, decimal?>(x => x.Bonus,
                    filterValue: 9m, filterOperator: FilterOperator.GreaterThan))));

        [Fact]
        public void SortedByOneColumn() => BothRoutesAgree(p =>
            p.Add(g => g.ChildContent, Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First,
                    sortOrder: SortOrder.Descending),
                Radzen.FastGrid.Tests.Columns.Property<Person, int>(x => x.Id))));

        // The nullable column is the one the two routes could most easily disagree about: a comparer
        // sorts a missing value below everything, and so does a lifted comparison, but only if both
        // were asked the same question.
        [Fact]
        public void SortedByANullableColumn() => BothRoutesAgree(p =>
            p.Add(g => g.ChildContent, Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First),
                Radzen.FastGrid.Tests.Columns.Property<Person, decimal?>(x => x.Bonus,
                    sortOrder: SortOrder.Ascending))));

        [Fact]
        public void FilteredAndSortedAtOnce() => BothRoutesAgree(p =>
        {
            p.Add(g => g.ChildContent, Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First, filterValue: "1"),
                Radzen.FastGrid.Tests.Columns.Property<Person, int>(x => x.Id,
                    sortOrder: SortOrder.Descending)));
        });

        [Fact]
        public void FilteredOnTwoColumnsWithOr() => BothRoutesAgree(p =>
        {
            p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or);
            p.Add(g => g.ChildContent, Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First, filterValue: "First2"),
                Radzen.FastGrid.Tests.Columns.Property<Person, int>(x => x.Id,
                    filterValue: 100005, filterOperator: FilterOperator.Equals)));
        });

        // A template column filters by a string path, which the in-memory route cannot compose - so the
        // whole composition has to go back to the expression route rather than half of it.
        [Fact]
        public void AColumnThatCannotComposeSendsItBackToTheOtherRoute() => BothRoutesAgree(p =>
        {
            p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or);
            p.Add(g => g.ChildContent, Radzen.FastGrid.Tests.Columns.Of(
                Radzen.FastGrid.Tests.Columns.Property<Person, string>(x => x.First, filterValue: "First2"),
                Radzen.FastGrid.Tests.Columns.Template<Person>(
                    person => builder => builder.AddContent(0, person.Id),
                    sortProperty: nameof(Person.Id))));
        });
    }
}
