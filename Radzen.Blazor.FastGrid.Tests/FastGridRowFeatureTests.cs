using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Grid chrome, row styling, selection and the cell-level events. Everything here is either free -
    /// a class name chosen per grid, a lookup per row - or wired only when something listens for it.
    /// </summary>
    public class FastGridRowFeatureTests
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
                extra?.Invoke(p);
            });

        // --- chrome ----------------------------------------------------------------------------

        [Fact]
        public void TheHeaderCanBeSwitchedOff()
        {
            using var ctx = Context();

            Assert.Single(Render(ctx).FindAll("thead"));
            Assert.Empty(Render(ctx, p => p.Add(g => g.ShowHeader, false)).FindAll("thead"));
        }

        [Fact]
        public void StripingIsOnByDefaultAndCanBeSwitchedOff()
        {
            using var ctx = Context();

            Assert.Contains("rz-grid-table-striped", Render(ctx).Find("table").ClassName);
            Assert.DoesNotContain("rz-grid-table-striped",
                Render(ctx, p => p.Add(g => g.AllowAlternatingRows, false)).Find("table").ClassName);
        }

        [Theory]
        [InlineData(DataGridGridLines.Default, null)]
        [InlineData(DataGridGridLines.Both, "rz-grid-gridlines-both")]
        [InlineData(DataGridGridLines.None, "rz-grid-gridlines-none")]
        [InlineData(DataGridGridLines.Horizontal, "rz-grid-gridlines-horizontal")]
        [InlineData(DataGridGridLines.Vertical, "rz-grid-gridlines-vertical")]
        public void GridLinesPickTheThemeClass(DataGridGridLines lines, string expected)
        {
            using var ctx = Context();

            var className = Render(ctx, p => p.Add(g => g.GridLines, lines)).Find("table").ClassName;

            if (expected is null)
            {
                Assert.DoesNotContain("rz-grid-gridlines", className);
            }
            else
            {
                Assert.Contains(expected, className);
            }
        }

        [Fact]
        public void ResponsiveEmitsTheClassTheThemeScopesTheWholeFeatureUnder()
        {
            // The per-cell titles are only half of Responsive, and the half that does nothing alone.
            // Both theme rules are nested under .rz-datatable-reflow - the one hiding the title on a
            // wide screen (_grid.scss "rz-datatable-reflow tbody td > .rz-column-title { display:none }")
            // and the max-width:768px block that stacks the rows into cards. RadzenDataGrid sets it
            // from the same parameter. Without it the titles show beside every value at every width and
            // nothing ever stacks, so the feature is worse than leaving it off - and it costs 1.40x.
            using var ctx = Context();

            Assert.DoesNotContain("rz-datatable-reflow", Render(ctx).Find("div.rz-data-grid").ClassName);

            Assert.Contains("rz-datatable-reflow",
                Render(ctx, p => p.Add(g => g.Responsive, true)).Find("div.rz-data-grid").ClassName);
        }

        [Fact]
        public void ResponsiveKeepsTheGridsOtherRootClasses()
        {
            // The class is added to a switch that returns whole literals, so the arm a grid lands on
            // has to carry everything the other arms do.
            using var ctx = Context();

            var className = Render(ctx, p =>
            {
                p.Add(g => g.Responsive, true);
                // Both halves: the grid only counts as showing a selection when something is listening
                // for one, which is what SelectsOnRowClick asks.
                p.Add(g => g.AllowRowSelectOnRowClick, true);
                p.Add(g => g.SelectionChanged, (ICollection<Person> _) => { });
                p.Add(g => g.CssClass, "mine");
            }).Find("div.rz-data-grid").ClassName;

            Assert.Contains("rz-datatable", className);
            Assert.Contains("rz-selectable", className);
            Assert.Contains("rz-datatable-reflow", className);
            Assert.Contains("mine", className);
        }

        // The title a narrow-screen theme shows once the table is stacked into cards.
        [Fact]
        public void ResponsiveRepeatsTheColumnTitleInEachCell()
        {
            using var ctx = Context();

            Assert.Empty(Render(ctx).FindAll("tbody td .rz-column-title"));

            var responsive = Render(ctx, p => p.Add(g => g.Responsive, true));

            Assert.Equal(8, responsive.FindAll("tbody td .rz-column-title").Count);
            Assert.Equal("First", responsive.Find("tbody td .rz-column-title").TextContent);
        }

        [Fact]
        public void DensityReachesThePager()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
                p.Add(g => g.Density, Density.Compact);
            });

            Assert.Contains("rz-density-compact", cut.Find(".rz-pager").ClassName);
        }

        // --- row class and style ---------------------------------------------------------------

        [Fact]
        public void RowClassAndRowStyleAreAppliedPerRow()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.RowClass, (Func<Person, string>)(person => person.Id > 2 ? "over-two" : null));
                p.Add(g => g.RowStyle, (Func<Person, string>)(person => person.Id > 2 ? "color:red" : null));
            });

            var rows = cut.FindAll("tbody tr");

            // Sample order is Carol(3), Alice(1), Dave(4), Bob(2).
            Assert.Equal("rz-data-row over-two", rows[0].ClassName);
            Assert.Equal("color:red", rows[0].GetAttribute("style"));
            Assert.Equal("rz-data-row", rows[1].ClassName);
            Assert.Null(rows[1].GetAttribute("style"));
        }

        // The composed class is memoized against the string the callback returned, so a caller handing
        // back one of a few constants pays for one composition, not one per row.
        [Fact]
        public void TheComposedRowClassIsNotRebuiltPerRow()
        {
            using var ctx = Context();

            var calls = 0;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(200));
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.RowClass, (Func<Person, string>)(_ => { calls++; return "flagged"; }));
            });

            var rows = cut.FindAll("tbody tr");

            Assert.Equal(200, rows.Count);
            Assert.Equal(200, calls);

            // Every row got the same composed string, and the markup proves the composition happened.
            Assert.All(rows, row => Assert.Equal("rz-data-row flagged", row.ClassName));
        }

        // --- row identity ------------------------------------------------------------------------

        // Without a key the diff matches rows by position; with one it matches them by identity. The
        // rendered markup is the same either way - what changes is how the renderer gets there - so what
        // is assertable is that the grid still renders correctly through a reorder.
        [Fact]
        public void ItemKeySurvivesAReorder()
        {
            using var ctx = Context();

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.ItemKey, (Func<Person, object>)(person => person.Id));
            });

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" },
                cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent).ToArray());

            people.Reverse();
            cut.SetParametersAndRender(p => p.Add(g => g.Data, people.ToList()));

            Assert.Equal(new[] { "Bob", "Dave", "Alice", "Carol" },
                cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent).ToArray());
        }

        [Fact]
        public void NoItemKeyStillRenders()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        // --- selection -------------------------------------------------------------------------

        [Fact]
        public void ClickingARowRaisesTheNewSelection()
        {
            using var ctx = Context();

            ICollection<Person> selection = null;
            Person selected = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.SelectionChanged, (ICollection<Person> value) => selection = value);
                p.Add(g => g.RowSelect, (Person person) => selected = person);
            });

            cut.FindAll("tbody tr")[1].Click();

            Assert.NotNull(selection);
            Assert.Equal("Alice", Assert.Single(selection).First);
            Assert.Equal("Alice", selected.First);
        }

        [Fact]
        public void SingleSelectionReplacesTheChosenRow()
        {
            using var ctx = Context();

            // The same instances the grid is bound to: selection is membership, and People.Sample()
            // hands back fresh rows on every call.
            var people = People.Sample();
            var selection = new List<Person> { people[0] };
            ICollection<Person> next = null;
            var deselected = new List<Person>();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.Selection, selection);
                p.Add(g => g.SelectionChanged, (ICollection<Person> value) => next = value);
                p.Add(g => g.RowDeselect, (Person person) => deselected.Add(person));
            });

            cut.FindAll("tbody tr")[1].Click();

            Assert.Equal("Alice", Assert.Single(next).First);
            Assert.Equal("Carol", Assert.Single(deselected).First);

            // The collection the caller passed in is never written to.
            Assert.Single(selection);
            Assert.Equal("Carol", selection[0].First);
        }

        [Fact]
        public void MultipleSelectionTogglesTheClickedRow()
        {
            using var ctx = Context();

            var people = People.Sample();
            ICollection<Person> selection = new List<Person> { people[0] };
            ICollection<Person> next = null;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.SelectionMode, DataGridSelectionMode.Multiple);
                p.Add(g => g.Selection, selection);
                p.Add(g => g.SelectionChanged, (ICollection<Person> value) => next = value);
            });

            // Adding.
            cut.FindAll("tbody tr")[1].Click();
            Assert.Equal(2, next.Count);

            // The grid renders from Selection and never writes to it, so a caller binding it feeds the
            // new one back - which is what makes the next click a toggle rather than another add.
            cut.SetParametersAndRender(p => p.Add(g => g.Selection, next));

            cut.FindAll("tbody tr")[0].Click();
            Assert.DoesNotContain(next, person => person.First == "Carol");
        }

        [Fact]
        public void ASelectedRowIsMarkedForTheThemeAndForAssistiveTechnology()
        {
            using var ctx = Context();

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.Selection, new List<Person> { people[0] });
            });

            var rows = cut.FindAll("tbody tr");

            Assert.Contains("rz-state-highlight", rows[0].ClassName);
            Assert.Equal("true", rows[0].GetAttribute("aria-selected"));
            Assert.Null(rows[1].GetAttribute("aria-selected"));
        }

        [Fact]
        public void SelectionOnRowClickCanBeSwitchedOff()
        {
            using var ctx = Context();

            var raised = false;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowRowSelectOnRowClick, false);
                p.Add(g => g.SelectionChanged, (ICollection<Person> _) => raised = true);
            });

            // Not merely inert: with selection off the click handler is never bound, so a grid whose
            // only interest in clicks was selection pays nothing per row for having declared it.
            Assert.False(cut.FindAll("tbody tr")[0].HasAttribute("onclick"));

            // And with RowClick handled too, the click reaches that and leaves the selection alone.
            cut.SetParametersAndRender(p => p.Add(g => g.RowClick, (Person _) => { }));
            cut.FindAll("tbody tr")[0].Click();

            Assert.False(raised);
        }

        // --- events, and what they cost when nothing listens ------------------------------------

        [Fact]
        public void NoRowOrCellHandlerMeansNoAttribute()
        {
            using var ctx = Context();

            var cut = Render(ctx);
            var row = cut.FindAll("tbody tr")[0];

            Assert.False(row.HasAttribute("onclick"));
            Assert.False(row.HasAttribute("ondblclick"));
            Assert.All(row.QuerySelectorAll("td"), td =>
            {
                Assert.False(td.HasAttribute("onclick"));
                Assert.False(td.HasAttribute("oncontextmenu"));
            });
        }

        [Fact]
        public void CellClickReportsItsRowAndColumn()
        {
            using var ctx = Context();

            FastGridCellEventArgs<Person> args = null;

            var cut = Render(ctx, p =>
                p.Add(g => g.CellClick, (FastGridCellEventArgs<Person> value) => args = value));

            cut.FindAll("tbody tr")[1].QuerySelectorAll("td")[1].Click();

            Assert.Equal("Alice", args.Data.First);
            Assert.Equal("Id", args.Column.HeaderText);
        }

        [Fact]
        public void RowDoubleClickIsRaised()
        {
            using var ctx = Context();

            Person clicked = null;

            var cut = Render(ctx, p => p.Add(g => g.RowDoubleClick, (Person person) => clicked = person));

            cut.FindAll("tbody tr")[0].DoubleClick();

            Assert.Equal("Carol", clicked.First);
        }

        [Fact]
        public void TheCellTooltipCarriesTheCellText()
        {
            using var ctx = Context();

            Assert.False(Render(ctx).Find("tbody td span").HasAttribute("title"));

            var cut = Render(ctx, p => p.Add(g => g.ShowCellDataAsTooltip, true));

            Assert.Equal("Carol", cut.Find("tbody td span").GetAttribute("title"));
        }

        // A template's content is markup, not a string, so there is nothing to put in a title.
        [Fact]
        public void ATemplateColumnHasNoTooltipText()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ShowCellDataAsTooltip, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Template<Person>(person => builder => builder.AddContent(0, person.First),
                        title: "First")));
            });

            Assert.False(cut.Find("tbody td span").HasAttribute("title"));
        }
    }
}
