using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Row detail. The only feature here whose use is not cheap - a delegate and a cell per row - so
    /// what these pin hardest is that none of it happens without a Template.
    /// </summary>
    public class FastGridRowDetailTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static RenderFragment TwoColumns => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First"),
            Columns.Property<Person, int>(p => p.Id, title: "Id"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.AllowFiltering, true);
                extra?.Invoke(p);
            });

        static RenderFragment<Person> Detail =>
            person => builder => builder.AddContent(0, "detail for " + person.First);

        // The whole point of the design: no Template, nothing to pay for.
        [Fact]
        public void WithNoTemplateThereIsNoTogglerAnywhere()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll(".rz-col-icon"));
            Assert.Empty(cut.FindAll(".rz-row-toggler"));
            Assert.All(cut.FindAll("tbody tr"), row => Assert.Equal(2, row.QuerySelectorAll("td").Length));
        }

        [Fact]
        public void ATemplateAddsAToggleCellToEveryRowAndAMatchingCellToEveryOtherRow()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            // Header, filter row and every data row gain one cell, or the columns stop lining up.
            Assert.Single(cut.FindAll("thead tr")[0].QuerySelectorAll("th.rz-col-icon"));
            Assert.Single(cut.FindAll("thead tr")[1].QuerySelectorAll("th.rz-col-icon"));
            Assert.All(cut.FindAll("tbody tr"), row => Assert.Equal(3, row.QuerySelectorAll("td").Length));
            Assert.Equal(4, cut.FindAll("tbody .rz-row-toggler").Count);
        }

        [Fact]
        public void ShowExpandColumnFalseKeepsTheTemplateAndDropsTheToggle()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.Template, Detail);
                p.Add(g => g.ShowExpandColumn, false);
            });

            Assert.Empty(cut.FindAll(".rz-col-icon"));

            // Still expandable, just not by clicking - which is what the API is for.
            cut.InvokeAsync(() => cut.Instance.ToggleRow(cut.Instance.Data.First()));

            Assert.Single(cut.FindAll("tr.rz-expanded-row-content"));
        }

        [Fact]
        public void ClickingTheTogglerShowsTheDetailBeneathTheRow()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            Assert.Empty(cut.FindAll("tr.rz-expanded-row-content"));

            cut.FindAll("tbody .rz-row-toggler")[0].Click();

            var detail = cut.Find("tr.rz-expanded-row-content");

            Assert.Equal("detail for Carol", detail.TextContent);

            // Spanning every column including the toggle, or the detail sits under one column.
            Assert.Equal("3", detail.QuerySelector("td").GetAttribute("colspan"));

            // And immediately after the row it belongs to.
            var rows = cut.FindAll("tbody tr");
            Assert.Contains("Carol", rows[0].TextContent);
            Assert.Equal("rz-expanded-row-content", rows[1].ClassName);
        }

        [Fact]
        public void TheTogglerReportsAndReflectsTheExpandedState()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            Assert.Equal("false", cut.FindAll("tbody button")[0].GetAttribute("aria-expanded"));

            cut.FindAll("tbody .rz-row-toggler")[0].Click();

            Assert.Equal("true", cut.FindAll("tbody button")[0].GetAttribute("aria-expanded"));
            Assert.Contains("rzi-chevron-circle-down", cut.FindAll("tbody .rz-row-toggler")[0].ClassName);

            cut.FindAll("tbody .rz-row-toggler")[0].Click();

            Assert.Equal("false", cut.FindAll("tbody button")[0].GetAttribute("aria-expanded"));
            Assert.Empty(cut.FindAll("tr.rz-expanded-row-content"));
        }

        [Fact]
        public void SingleModeCollapsesTheRowThatWasOpen()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            cut.FindAll("tbody .rz-row-toggler")[0].Click();
            cut.FindAll("tbody .rz-row-toggler")[1].Click();

            var detail = Assert.Single(cut.FindAll("tr.rz-expanded-row-content"));

            Assert.Equal("detail for Alice", detail.TextContent);
        }

        [Fact]
        public void MultipleModeKeepsThemOpen()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.Template, Detail);
                p.Add(g => g.ExpandMode, DataGridExpandMode.Multiple);
            });

            cut.FindAll("tbody .rz-row-toggler")[0].Click();
            cut.FindAll("tbody .rz-row-toggler")[1].Click();

            Assert.Equal(2, cut.FindAll("tr.rz-expanded-row-content").Count);
        }

        // A row that leaves the screen without an event is a row the caller still thinks is expanded.
        [Fact]
        public void SingleModeReportsTheCollapseItCauses()
        {
            using var ctx = Context();

            var expanded = new System.Collections.Generic.List<string>();
            var collapsed = new System.Collections.Generic.List<string>();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.Template, Detail);
                p.Add(g => g.RowExpand, (Person person) => expanded.Add(person.First));
                p.Add(g => g.RowCollapse, (Person person) => collapsed.Add(person.First));
            });

            cut.FindAll("tbody .rz-row-toggler")[0].Click();
            cut.FindAll("tbody .rz-row-toggler")[1].Click();

            Assert.Equal(new[] { "Carol", "Alice" }, expanded);
            Assert.Equal(new[] { "Carol" }, collapsed);
        }

        // The toggle cell carries no empty rz-column-title span, unlike RadzenDataGrid's. The geometry
        // check is what established it takes no space - the button sits at the same offset inside the
        // cell either way - so this pins the markup that measurement licensed.
        [Fact]
        public void TheToggleCellHoldsOnlyTheButton()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));
            var cell = cut.Find("tbody td.rz-col-icon");

            Assert.Equal(1, cell.Children.Length);
            Assert.Equal("BUTTON", cell.Children[0].TagName);
        }

        [Fact]
        public void TheEmptyMessageSpansTheToggleColumnToo()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, Array.Empty<Person>());
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.Template, Detail);
                p.Add<RenderFragment>(g => g.EmptyTemplate, builder => builder.AddContent(0, "Nothing"));
            });

            Assert.Equal("3", cut.Find(".rz-datatable-emptymessage").GetAttribute("colspan"));
        }
    }
}
