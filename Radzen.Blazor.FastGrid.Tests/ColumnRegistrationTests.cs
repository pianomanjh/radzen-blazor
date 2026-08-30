using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// How a column joins and leaves the grid. The renderer skips SetParametersAsync entirely when a
    /// retained component's parameters are all known-immutable and unchanged, so a grid that rebuilt its
    /// column list on every render silently lost every column whose parameters were only strings. These
    /// pin the registration protocol rather than any one column's behaviour.
    /// </summary>
    public class ColumnRegistrationTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        // Deliberately parameterless-but-for-strings: a Title and a SortProperty and nothing else. A
        // template or a property expression is a delegate, which the renderer never treats as unchanged,
        // so a column carrying either would have hidden this.
        static RenderFragment StringOnlyColumn => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First"),
            Columns.Template<Person>(null, title: "Constant", sortProperty: "Last"));

        static string[] Headers(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("thead th").Select(th => th.TextContent).ToArray();

        [Fact]
        public void AColumnWhoseParametersNeverChangeSurvivesARerender()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, StringOnlyColumn);
            });

            Assert.Equal(new[] { "First", "Constant" }, Headers(cut));

            cut.Render();

            Assert.Equal(new[] { "First", "Constant" }, Headers(cut));
            Assert.Equal(2, cut.FindAll("tbody tr")[0].QuerySelectorAll("td").Length);
        }

        [Fact]
        public void RepeatedRendersDoNotRegisterTheSameColumnTwice()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, StringOnlyColumn);
            });

            for (var i = 0; i < 5; i++)
            {
                cut.Render();
            }

            Assert.Equal(2, Headers(cut).Length);
        }

        [Fact]
        public void AColumnThatLeavesTheMarkupLeavesTheGrid()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First"),
                    Columns.Property<Person, string>(p => p.Last, title: "Last")));
            });

            Assert.Equal(new[] { "First", "Last" }, Headers(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"))));

            Assert.Equal(new[] { "First" }, Headers(cut));
            Assert.Single(cut.FindAll("tbody tr")[0].QuerySelectorAll("td"));
        }

        [Fact]
        public void TheSortDoesNotOutliveTheColumnItOrdersBy()
        {
            // A sort left pointing at a column nothing on screen names is one nothing can clear.
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First"),
                    Columns.Property<Person, string>(p => p.Last, title: "Last")));
            });

            cut.FindAll("thead th")[1].QuerySelector("div")!.Click();

            Assert.NotNull(cut.Instance.SortColumn);

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"))));

            Assert.Null(cut.Instance.SortColumn);
            Assert.False(cut.Instance.SortDescending);
        }

        [Fact]
        public void AColumnAddedLaterJoinsTheGrid()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First")));
            });

            Assert.Equal(new[] { "First" }, Headers(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last"))));

            Assert.Equal(new[] { "First", "Last" }, Headers(cut));
        }
    }
}
