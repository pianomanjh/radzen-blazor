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
    /// The grid filtering through a mixture of column-composed predicates and reflectively built ones.
    /// </summary>
    /// <remarks>
    /// A typed column composes its own predicate; a template column filtering by a string path cannot,
    /// and the grid builds that one from its path instead. The two have to add up to the same answer as
    /// either alone would - and under <c>Or</c> they cannot simply be applied one after the other, since
    /// two Wheres are an And whatever the columns were joined by.
    /// </remarks>
    public class FilterCompositionTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                extra?.Invoke(p);
            });
        }

        // A typed column beside one that filters by a string path, so both builders are in play.
        static RenderFragment Mixed() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Template<Person>(person => builder => builder.AddContent(0, person.Id),
                title: "Id", sortProperty: nameof(Person.Id)));

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        static void Filter(IRenderedComponent<RadzenFastGrid<Person>> cut, int column, string text) =>
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[column].Change(text);

        [Fact]
        public void ATypedColumnAndAPathColumnAndTogether()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Mixed());

            Filter(cut, 0, "a");
            Filter(cut, 1, "4");

            Assert.Equal(new[] { "Dave" }, FirstNames(cut));
        }

        // The case the two-Where composition gets wrong: a row that matches only the column the grid
        // filtered reflectively still has to survive.
        [Fact]
        public void ATypedColumnAndAPathColumnOrTogether()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Mixed(),
                p => p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or));

            Filter(cut, 0, "Carol");
            Filter(cut, 1, "4");

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }

        // Only the path column filters, so there is no composed predicate to combine with.
        [Fact]
        public void OnlyThePathColumnFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Mixed(),
                p => p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or));

            Filter(cut, 1, "4");

            Assert.Equal(new[] { "Dave" }, FirstNames(cut));
        }

        // Only typed columns, which is the path that never reaches the reflective builder at all.
        [Fact]
        public void TwoTypedColumnsOrTogether()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, int>(x => x.Id, title: "Id")),
                p => p.Add(g => g.LogicalFilterOperator, LogicalFilterOperator.Or));

            Filter(cut, 0, "Carol");
            Filter(cut, 1, "4");

            Assert.Equal(new[] { "Carol", "Dave" }, FirstNames(cut));
        }
    }
}
