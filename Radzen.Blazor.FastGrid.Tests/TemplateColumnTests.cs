using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class TemplateColumnTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            RenderFragment columns, bool allowSorting = false)
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

        static RenderFragment<Person> Badge => person => builder =>
        {
            builder.OpenElement(0, "b");
            builder.AddAttribute(1, "class", "badge");
            builder.AddContent(2, person.First + "/" + person.Last);
            builder.CloseElement();
        };

        [Fact]
        public void RendersItsTemplateForEveryRow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(Badge, title: "Who")));

            var badges = cut.FindAll("tbody tr td b.badge").Select(e => e.TextContent).ToArray();

            Assert.Equal(
                new[] { "Carol/Adams", "Alice/Draper", "Dave/Bell", "Bob/Cook" },
                badges);
        }

        [Fact]
        public void TemplateIsNestedInsideTheCellSpan()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample().Take(1), Columns.Of(
                Columns.Template<Person>(Badge)));

            var span = cut.Find("tbody tr td > span.rz-cell-data");

            Assert.Equal("b", span.FirstElementChild.NodeName.ToLowerInvariant());
        }

        [Fact]
        public void NoTemplate_RendersAnEmptyCellRatherThanThrowing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(null, title: "Empty")));

            Assert.Equal(4, cut.FindAll("tbody tr td").Count);
            Assert.All(cut.FindAll("tbody tr td"), td => Assert.Equal(string.Empty, td.TextContent));
        }

        [Fact]
        public void SortPropertyDrivesPropertyPath()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(Badge, title: "Who", sortProperty: "Customer.Name")));

            var column = cut.FindComponent<TemplateColumn<Person>>().Instance;

            Assert.Equal("Customer.Name", column.SortPath);
            Assert.True(column.CanSort);
        }

        [Fact]
        public void WithoutSortProperty_HasNoPathAndCannotSort()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(Badge, title: "Who")));

            var column = cut.FindComponent<TemplateColumn<Person>>().Instance;

            Assert.Null(column.SortPath);
            Assert.False(column.CanSort);
        }

        [Fact]
        public void SortableFalse_OverridesAnExplicitSortProperty()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(Badge, sortProperty: "Last", sortable: false)));

            var column = cut.FindComponent<TemplateColumn<Person>>().Instance;

            Assert.Equal("Last", column.SortPath);
            Assert.False(column.CanSort);
        }

        [Fact]
        public void SortPropertyMakesTheHeaderSortable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Sample(), Columns.Of(
                Columns.Template<Person>(Badge, title: "Sortable", sortProperty: "Last"),
                Columns.Template<Person>(Badge, title: "Not sortable")),
                allowSorting: true);

            var headers = cut.FindAll("thead th");

            Assert.Contains("rz-sortable-column", headers[0].GetAttribute("class"));
            Assert.DoesNotContain("rz-sortable-column", headers[1].GetAttribute("class"));
        }

        // TemplateColumn does not override ApplySort, so the base returns null and the grid falls back to
        // the unsorted sequence. Sorting a template column is the caller's job via LoadData / the path.
        [Fact]
        public void ApplySortReturnsNull_AndTheGridKeepsTheOriginalOrder()
        {
            using var ctx = new TestContext();
            var data = People.Sample();

            var cut = Render(ctx, data, Columns.Of(
                Columns.Template<Person>(Badge, title: "Who", sortProperty: "Last")),
                allowSorting: true);

            var column = cut.FindComponent<TemplateColumn<Person>>().Instance;

            Assert.Null(column.ApplySort(data.AsQueryable(), descending: false));

            cut.Find("thead th div").Click();

            Assert.Equal(
                new[] { "Carol/Adams", "Alice/Draper", "Dave/Bell", "Bob/Cook" },
                cut.FindAll("tbody tr td b.badge").Select(e => e.TextContent).ToArray());
        }
    }
}
