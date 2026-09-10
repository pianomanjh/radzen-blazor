using System.Linq;
using Bunit;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// A lookup column that draws its cell with a template.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case is a column whose cell is more than the name it resolved - a link to the row the id
    /// points at, a name coloured by some other property, an icon beside it. Without this the author
    /// has to reach for <see cref="TemplateColumn{TItem}" />, and that trades away everything the
    /// lookup column exists for: the check-box list stops offering <em>names</em> and offers the
    /// <em>ids</em> the row carries, which is not something a reader can use.
    /// </para>
    /// <para>
    /// So the template replaces the cell's markup and nothing else. Filtering, sorting, the list, the
    /// settings key and the exported value all still come from the lookup.
    /// </para>
    /// </remarks>
    public class LookupColumnTemplateTests
    {
        static FastGridLookup<int> Categories() => FastGridLookup.Items(
            new[]
            {
                new Category { Id = 10, Name = "Hardware" },
                new Category { Id = 20, Name = "Software" },
            },
            c => c.Id, c => c.Name);

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Microsoft.AspNetCore.Components.RenderFragment<Person> template)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Lookup<Person, int>(x => x.CategoryId, Categories(), title: "Category",
                        template: template)));
            });
        }

        [Fact]
        public void TheTemplateDrawsTheCell()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, person => builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "class", "mine");
                builder.AddContent(2, person.First);
                builder.CloseElement();
            });

            var drawn = cut.FindAll("tbody tr.rz-data-row td span.mine")
                .Select(e => e.TextContent).ToArray();

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, drawn);
        }

        /// <summary>
        /// The whole point: the list still offers names, where a template column would offer the ids.
        /// </summary>
        [Fact]
        public void TheColumnStillFiltersAndListsByTheLookupsNames()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, person => builder => builder.AddContent(0, "drawn by the template"));

            var column = cut.Instance.VisibleColumns[0];

            Assert.True(column.CanFilter);
            Assert.Equal("CategoryId", column.FilterPropertyPath);

            // ToString is the entry's text - the name the lookup resolved, which is what a reader ticks.
            var offered = column.FilterValues!.Cast<object>().Select(v => v.ToString()).ToArray();

            Assert.Contains("Hardware", offered);
            Assert.Contains("Software", offered);
        }

        /// <summary>
        /// The exported value is still the name, because an export is not markup - and a template that
        /// took the text with it would silently empty the column in a spreadsheet.
        /// </summary>
        [Fact]
        public void ItStillExportsTheResolvedName()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, person => builder => builder.AddContent(0, "drawn by the template"));

            var column = cut.Instance.VisibleColumns[0];

            Assert.Equal("Hardware", column.CellTextOf(People.Sample()[0]));
        }

        [Fact]
        public void WithNoTemplateTheCellIsStillTheName()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Lookup<Person, int>(x => x.CategoryId, Categories(), title: "Category")));
            });

            var drawn = cut.FindAll("tbody tr.rz-data-row td")
                .Select(e => e.TextContent.Trim()).ToArray();

            Assert.Equal(new[] { "Hardware", "Software", "Hardware", "30" }, drawn);
        }
    }
}
