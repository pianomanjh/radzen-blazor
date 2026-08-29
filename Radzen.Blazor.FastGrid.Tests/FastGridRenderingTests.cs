using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class FastGridRenderingTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static RenderFragment ThreeColumns => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First"),
            Columns.Property<Person, string>(p => p.Last, title: "Last"),
            Columns.Property<Person, int>(p => p.Id, title: "Id"));

        // --- shape -----------------------------------------------------------------------------

        [Fact]
        public void RendersOneRowPerItemAndOneCellPerColumn()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(7));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            var rows = cut.FindAll("tbody tr");

            Assert.Equal(7, rows.Count);
            Assert.All(rows, row => Assert.Equal(3, row.QuerySelectorAll("td").Count()));
            Assert.Equal(21, cut.FindAll("tbody tr td").Count);
            Assert.Equal(3, cut.FindAll("thead tr th").Count);
            Assert.Single(cut.FindAll("thead tr"));
        }

        [Fact]
        public void RowCountFollowsTheData()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(3));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal(3, cut.FindAll("tbody tr").Count);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(5)));

            Assert.Equal(5, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void EmitsTheRadzenClassContract()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(1));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal("rz-data-grid rz-datatable", cut.Find("div").GetAttribute("class"));
            Assert.Equal("rz-grid-table rz-grid-table-fixed rz-grid-table-striped",
                cut.Find("table").GetAttribute("class"));
            Assert.Equal("rz-data-row", cut.Find("tbody tr").GetAttribute("class"));

            // The theme gives th padding: 0 and hangs the header padding off a direct child div. Without
            // that div the header row renders shorter than RadzenDataGrid's.
            var header = cut.Find("thead th");

            Assert.Equal("div", header.FirstElementChild.NodeName.ToLowerInvariant());
            Assert.NotNull(header.QuerySelector("div > span.rz-column-title > span.rz-column-title-content"));
        }

        [Fact]
        public void NoAlternatingRowClass()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(4));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            // Striping is :nth-child off the table-level class. Computing odd/even per row is wasted work.
            Assert.All(cut.FindAll("tbody tr"),
                row => Assert.Equal("rz-data-row", row.GetAttribute("class")));
        }

        [Fact]
        public void CssClassIsAppendedToTheWrapper()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(1));
                p.Add(g => g.CssClass, "my-grid");
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal("rz-data-grid rz-datatable my-grid", cut.Find("div").GetAttribute("class"));
        }

        // --- empty -----------------------------------------------------------------------------

        [Fact]
        public void EmptyTemplateShowsWhenThereAreNoRows()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new List<Person>());
                p.Add(g => g.ChildContent, ThreeColumns);
                p.Add(g => g.EmptyTemplate, (RenderFragment)(b => b.AddContent(0, "Nothing here")));
            });

            var cell = cut.Find("tbody tr td");

            Assert.Equal("Nothing here", cell.TextContent);
            Assert.Equal("rz-datatable-emptymessage", cell.GetAttribute("class"));
            Assert.Equal("3", cell.GetAttribute("colspan"));
            Assert.Single(cut.FindAll("tbody tr"));
        }

        [Fact]
        public void EmptyTemplateShowsWhenDataIsNull()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, (IEnumerable<Person>)null);
                p.Add(g => g.ChildContent, ThreeColumns);
                p.Add(g => g.EmptyTemplate, (RenderFragment)(b => b.AddContent(0, "Nothing here")));
            });

            Assert.Equal("Nothing here", cut.Find("tbody tr td").TextContent);
        }

        [Fact]
        public void EmptyTemplateIsHiddenWhenThereAreRows()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(2));
                p.Add(g => g.ChildContent, ThreeColumns);
                p.Add(g => g.EmptyTemplate, (RenderFragment)(b => b.AddContent(0, "Nothing here")));
            });

            Assert.Equal(2, cut.FindAll("tbody tr").Count);
            Assert.DoesNotContain("Nothing here", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("td.rz-datatable-emptymessage"));
        }

        [Fact]
        public void EmptyTemplateAppearsAndDisappearsWithTheData()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(2));
                p.Add(g => g.ChildContent, ThreeColumns);
                p.Add(g => g.EmptyTemplate, (RenderFragment)(b => b.AddContent(0, "Nothing here")));
            });

            cut.SetParametersAndRender(p => p.Add(g => g.Data, new List<Person>()));

            Assert.Equal("Nothing here", cut.Find("tbody tr td").TextContent);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(2)));

            Assert.Equal(2, cut.FindAll("tbody tr").Count);
            Assert.Empty(cut.FindAll("td.rz-datatable-emptymessage"));
        }

        [Fact]
        public void NoEmptyTemplate_RendersAnEmptyBody()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new List<Person>());
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Empty(cut.FindAll("tbody tr"));
            Assert.Equal(3, cut.FindAll("thead th").Count);
        }

        // --- selection -------------------------------------------------------------------------

        [Fact]
        public void SelectionHighlightsOnlyTheSelectedRows()
        {
            using var ctx = Context();
            var data = People.Sample();
            var selection = new List<Person> { data[1], data[3] };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.Selection, selection);
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            var rows = cut.FindAll("tbody tr");

            Assert.Equal(
                new[] { false, true, false, true },
                rows.Select(r => r.GetAttribute("class").Contains("rz-state-highlight", StringComparison.Ordinal))
                    .ToArray());

            Assert.Equal(
                new[] { null, "true", null, "true" },
                rows.Select(r => r.GetAttribute("aria-selected")).ToArray());

            // The base class stays on the highlighted rows too.
            Assert.All(rows, r => Assert.Contains("rz-data-row", r.GetAttribute("class"), StringComparison.Ordinal));
        }

        [Fact]
        public void SelectionFollowsTheCollection()
        {
            using var ctx = Context();
            var data = People.Sample();
            var selection = new List<Person>();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.Selection, selection);
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Empty(cut.FindAll("tbody tr.rz-state-highlight"));

            selection.Add(data[0]);
            cut.Render();

            Assert.Single(cut.FindAll("tbody tr.rz-state-highlight"));
            Assert.Equal("Carol", cut.Find("tbody tr.rz-state-highlight td").TextContent);
        }

        [Fact]
        public void NoSelection_HighlightsNothing()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Empty(cut.FindAll("tbody tr.rz-state-highlight"));
            Assert.All(cut.FindAll("tbody tr"), r => Assert.Null(r.GetAttribute("aria-selected")));
        }

        [Fact]
        public void SelectionFollowsTheSortedOrderRatherThanTheRowIndex()
        {
            using var ctx = Context();
            var data = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.Selection, new List<Person> { data[0] }); // Carol / Adams, first unsorted
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p2 => p2.Last, title: "Last")));
            });

            cut.Find("thead th div").Click();
            cut.Find("thead th div").Click(); // descending: Draper, Cook, Bell, Adams

            var highlighted = cut.FindAll("tbody tr")
                .Select(r => r.GetAttribute("class").Contains("rz-state-highlight", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(new[] { false, false, false, true }, highlighted);
        }

        // --- row click -------------------------------------------------------------------------

        [Fact]
        public void RowClickFiresWithTheClickedItem()
        {
            using var ctx = Context();
            var data = People.Sample();
            var clicked = new List<Person>();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, ThreeColumns);
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, clicked.Add));
            });

            cut.FindAll("tbody tr")[2].Click();

            Assert.Same(data[2], Assert.Single(clicked));

            cut.FindAll("tbody tr")[0].Click();

            Assert.Equal(2, clicked.Count);
            Assert.Same(data[0], clicked[1]);
        }

        [Fact]
        public void RowClickFiresWithTheItemAtTheSortedPosition()
        {
            using var ctx = Context();
            var data = People.Sample();
            Person clicked = null;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p2 => p2.Last, title: "Last")));
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, p2 => clicked = p2));
            });

            cut.Find("thead th div").Click(); // ascending by Last: Adams, Bell, Cook, Draper

            cut.FindAll("tbody tr")[0].Click();

            // Row 0 is Carol Adams once sorted, not Carol only because she happened to be first unsorted:
            // this asserts the row's captured item follows the view, so click the last row too.
            Assert.Same(data[0], clicked);

            cut.FindAll("tbody tr")[3].Click();

            Assert.Same(data[1], clicked); // Alice Draper
        }

        [Fact]
        public void NoRowClickHandler_LeavesRowsWithoutAClickHandler()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            // Rule 2 of the spec: no callback is allocated unless a handler exists. bUnit throws when a
            // dispatched event has no registered handler, which is exactly the observable consequence.
            Assert.Throws<MissingEventHandlerException>(() => cut.FindAll("tbody tr")[0].Click());
        }

        // --- column registration ---------------------------------------------------------------

        [Fact]
        public void ColumnsAppearInDeclarationOrder()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, int>(p2 => p2.Id, title: "Id"),
                    Columns.Property<Person, string>(p2 => p2.Last, title: "Last"),
                    Columns.Template<Person>(item => b => b.AddContent(0, "T:" + item.First), title: "Who"),
                    Columns.Property<Person, string>(p2 => p2.First, title: "First")));
            });

            Assert.Equal(
                new[] { "Id", "Last", "Who", "First" },
                cut.FindAll("thead th .rz-column-title-content").Select(e => e.TextContent).ToArray());

            var firstRow = cut.FindAll("tbody tr")[0].QuerySelectorAll("td").Select(td => td.TextContent).ToArray();

            Assert.Equal(new[] { "3", "Adams", "T:Carol", "Carol" }, firstRow);
        }

        [Fact]
        public void ReRenderingDoesNotDuplicateColumns()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(2));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal(3, cut.FindAll("thead th").Count);

            for (var i = 0; i < 3; i++)
            {
                cut.Render();

                Assert.Equal(3, cut.FindAll("thead th").Count);
                Assert.Equal(3, cut.FindAll("tbody tr")[0].QuerySelectorAll("td").Count());
            }

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(4)));

            Assert.Equal(3, cut.FindAll("thead th").Count);
            Assert.Equal(12, cut.FindAll("tbody tr td").Count);
        }

        [Fact]
        public void SortingDoesNotDuplicateColumns()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            cut.Find("thead th div").Click();
            cut.Find("thead th div").Click();

            Assert.Equal(3, cut.FindAll("thead th").Count);
            Assert.Equal(12, cut.FindAll("tbody tr td").Count);
        }

        [Fact]
        public void ChangingTheColumnSetReplacesTheColumns()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(2));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal(3, cut.FindAll("thead th").Count);

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, string>(p2 => p2.First, title: "First"))));

            Assert.Equal(1, cut.FindAll("thead th").Count);
            Assert.Equal(2, cut.FindAll("tbody tr td").Count);
        }

        [Fact]
        public void ColumnOutsideAGrid_Throws()
        {
            using var ctx = Context();

            System.Linq.Expressions.Expression<Func<Person, string>> property = p2 => p2.First;

            var exception = Assert.Throws<InvalidOperationException>(
                () => ctx.RenderComponent<PropertyColumn<Person, string>>(p =>
                    p.Add(c => c.Property, property)));

            Assert.Contains(nameof(RadzenFastGrid<Person>), exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ColumnsRenderNothingOfTheirOwn()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new List<Person>());
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            // Everything the grid emits lives inside the wrapper div; a column must not leak markup of its
            // own beside the table.
            Assert.Single(cut.Nodes.OfType<AngleSharp.Dom.IElement>());
            Assert.Equal("div", cut.Nodes.OfType<AngleSharp.Dom.IElement>().Single().NodeName.ToLowerInvariant());
        }

        [Fact]
        public void RowsAndCellsCarryTheirAriaRoles()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(2));
                p.Add(g => g.ChildContent, ThreeColumns);
            });

            Assert.Equal("rowgroup", cut.Find("thead").GetAttribute("role"));
            Assert.Equal("rowgroup", cut.Find("tbody").GetAttribute("role"));
            Assert.All(cut.FindAll("tbody tr"), r => Assert.Equal("row", r.GetAttribute("role")));
            Assert.All(cut.FindAll("tbody td"), c => Assert.Equal("gridcell", c.GetAttribute("role")));
            Assert.All(cut.FindAll("thead th"), h =>
            {
                Assert.Equal("columnheader", h.GetAttribute("role"));
                Assert.Equal("col", h.GetAttribute("scope"));
            });
        }
    }
}
