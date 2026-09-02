using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Column reordering. As with resize the drag itself belongs to the browser, so what is pinned here
    /// is the contract around it: what the grid emits for the script to resolve, what a settled drop
    /// does to the order, and that a grid which does not reorder emits none of it.
    /// </summary>
    public class FastGridColumnReorderTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        static RenderFragment ThreeColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"),
            Columns.Property<Person, int>(x => x.Id, title: "Id"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                extra?.Invoke(p);
            });

        static string[] Titles(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("th .rz-column-title-content").Select(e => e.TextContent).ToArray();

        [Fact]
        public void AGridThatDoesNotReorderEmitsNothingForIt()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll(".rz-column-drag"));
            Assert.Empty(cut.FindAll("th[data-column-index]"));

            // The script attaches its mousemove to the grid root, so the root carries an id only for it.
            Assert.Empty(cut.FindAll(".rz-data-grid[id]"));
        }

        [Fact]
        public void AllowColumnReorderPutsAHandleOnEveryColumn()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            Assert.Equal(3, cut.FindAll(".rz-column-drag").Count);
            Assert.Equal(3, cut.FindAll("th[data-column-index]").Count);
            Assert.Single(cut.FindAll(".rz-data-grid[id]"));
        }

        [Fact]
        public void TheHandleLivesWhereTheScriptWalksUpFromIt()
        {
            // Radzen.startColumnReorder resolves the dragged header as el.parentNode.parentNode. The
            // handle therefore has to be a grandchild of the th - a child of the header's padding div,
            // beside the title span. One level out and the script clones the wrong element; one level
            // in and it clones the title instead of the header.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            Assert.Equal(3, cut.FindAll("th > div > .rz-column-drag").Count);
        }

        [Fact]
        public void TheHandleIdMatchesTheSuffixTheScriptAppends()
        {
            // The grid hands the script a base id and the script appends '-drag' to find the handle.
            // The same base is what resize appends '-col' and '-resizer' to, so all three stay in step.
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.AllowColumnResize, true);
            });

            var handles = cut.FindAll(".rz-column-drag").Select(h => h.Id).ToArray();
            var resizers = cut.FindAll(".rz-column-resizer").Select(h => h.Id).ToArray();

            Assert.All(handles, id => Assert.EndsWith("-drag", id, StringComparison.Ordinal));

            var bases = handles.Select(id => id[..^"-drag".Length]).ToArray();

            Assert.Equal(bases.Select(b => b + "-resizer").ToArray(), resizers);
        }

        [Fact]
        public void DataColumnIndexTracksThePositionDrawn()
        {
            // The touch path reads this attribute off the th it was dropped on and hands the number
            // back. It has to mean "where this column is drawn now", which after a reorder is not
            // where it was declared.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            Assert.Equal(new[] { "0", "1", "2" },
                cut.FindAll("th[data-column-index]").Select(e => e.GetAttribute("data-column-index")));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            Assert.Equal(new[] { "0", "1", "2" },
                cut.FindAll("th[data-column-index]").Select(e => e.GetAttribute("data-column-index")));
            Assert.Equal(new[] { "Id", "First", "Last" }, Titles(cut));
        }

        [Fact]
        public void AColumnCanOptOut()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", reorderable: false)));
            });

            Assert.Single(cut.FindAll(".rz-column-drag"));
        }

        [Fact]
        public void ADropMovesTheColumnToWhereItWasDropped()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            Assert.Equal(new[] { "First", "Last", "Id" }, Titles(cut));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.Equal(new[] { "Last", "Id", "First" }, Titles(cut));
        }

        [Fact]
        public void ADropMovesTheCellsWithTheHeader()
        {
            // The header order is the visible half of it; the body has to follow or the values land
            // under the wrong titles, which is the one failure a header-only assertion would miss.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            var first = cut.FindAll("tbody tr")[0].QuerySelectorAll("td .rz-cell-data")
                .Select(e => e.TextContent).ToArray();
            var people = People.Sample();

            Assert.Equal(people[0].Id.ToString(), first[0]);
            Assert.Equal(people[0].First, first[1]);
            Assert.Equal(people[0].Last, first[2]);
        }

        [Fact]
        public void DraggingRightwardsLandsBeforeTheColumnDroppedOn()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 1));

            Assert.Equal(new[] { "Last", "First", "Id" }, Titles(cut));
        }

        [Fact]
        public void DroppingAColumnOnItselfChangesNothing()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(1, 1));

            Assert.Equal(new[] { "First", "Last", "Id" }, Titles(cut));
        }

        [Fact]
        public void ASecondDragArrangesFromWhereTheFirstLeftThings()
        {
            // The test that pins why every visible column is given its index outright rather than only
            // the one that moved. One drag from a pristine order reads the same either way - the
            // unindexed columns happen to fill the gaps in the order wanted - so it takes a second drag
            // over four columns to tell the two apart. Recording only the moved column answers this
            // with First and Id transposed.
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last"),
                    Columns.Property<Person, int>(x => x.Id, title: "Id"),
                    Columns.Property<Person, decimal>(x => x.Salary, title: "Salary")));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.Equal(new[] { "Last", "Id", "First", "Salary" }, Titles(cut));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(3, 0));

            Assert.Equal(new[] { "Salary", "Last", "Id", "First" }, Titles(cut));
        }

        [Fact]
        public void TheNewOrderSurvivesAParameterSet()
        {
            // The same reason a drag does not write to Width: OrderIndex is a parameter, so a reorder
            // that wrote to it would be undone the next time Blazor set parameters, and the columns
            // would snap back to the markup's order on the next unrelated re-render.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Sample()));

            Assert.Equal(new[] { "Id", "First", "Last" }, Titles(cut));
        }

        [Fact]
        public void ADeclaredOrderIndexStillPlacesAColumnUntilSomethingIsDragged()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", orderIndex: 0)));
            });

            Assert.Equal(new[] { "Last", "First" }, Titles(cut));
        }

        [Fact]
        public void ColumnReorderingCanCancelTheMove()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ColumnReordering, EventCallback.Factory
                    .Create<FastGridColumnReorderingEventArgs<Person>>(new object(), args => args.Cancel = true));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.Equal(new[] { "First", "Last", "Id" }, Titles(cut));
        }

        [Fact]
        public void ColumnReorderingCarriesBothColumns()
        {
            using var ctx = Context();

            FastGridColumnReorderingEventArgs<Person> seen = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ColumnReordering, EventCallback.Factory
                    .Create<FastGridColumnReorderingEventArgs<Person>>(new object(), args => seen = args));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.NotNull(seen);
            Assert.Equal("First", seen.Column.Title);
            Assert.Equal("Id", seen.ToColumn.Title);
        }

        [Fact]
        public void ColumnReorderedCarriesTheColumnAndWhereItLanded()
        {
            using var ctx = Context();

            FastGridColumnReorderedEventArgs<Person> seen = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ColumnReordered, EventCallback.Factory
                    .Create<FastGridColumnReorderedEventArgs<Person>>(new object(), args => seen = args));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.NotNull(seen);
            Assert.Equal("First", seen.Column.Title);
            Assert.Equal(2, seen.OrderIndex);
        }

        [Fact]
        public void ColumnReorderedDoesNotFireForACancelledMove()
        {
            using var ctx = Context();

            var reordered = 0;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ColumnReordering, EventCallback.Factory
                    .Create<FastGridColumnReorderingEventArgs<Person>>(new object(), args => args.Cancel = true));
                p.Add(g => g.ColumnReordered, EventCallback.Factory
                    .Create<FastGridColumnReorderedEventArgs<Person>>(new object(), _ => reordered++));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.Equal(0, reordered);
        }

        [Fact]
        public void AReorderIsCapturedAndRestored()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            var settings = cut.Instance.CaptureSettings();

            Assert.Equal(0, settings.Columns.Single(c => c.Property == nameof(Person.Id)).OrderIndex);
            Assert.Equal(1, settings.Columns.Single(c => c.Property == nameof(Person.First)).OrderIndex);
            Assert.Equal(2, settings.Columns.Single(c => c.Property == nameof(Person.Last)).OrderIndex);

            using var second = Context();

            var restored = second.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.Settings, settings);
            });

            Assert.Equal(new[] { "Id", "First", "Last" }, Titles(restored));
        }

        [Fact]
        public void AGridThatWasNeverDraggedStoresNoOrder()
        {
            // Same rule as Width and Visible: null records no choice, so the markup's own OrderIndex
            // stands on the way back in. Storing a position nobody chose would freeze the declared
            // order against a later change to the markup.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            Assert.All(cut.Instance.CaptureSettings().Columns, c => Assert.Null(c.OrderIndex));
        }

        [Fact]
        public void AReorderIsAnnouncedThroughSettingsChanged()
        {
            using var ctx = Context();

            FastGridSettings seen = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.SettingsChanged, EventCallback.Factory
                    .Create<FastGridSettings>(new object(), s => seen = s));
            });

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            Assert.NotNull(seen);
            Assert.Equal(0, seen.Columns.Single(c => c.Property == nameof(Person.Id)).OrderIndex);
        }

        [Fact]
        public void AHiddenColumnKeepsItsPlaceInTheOrderWhenThePickerBringsItBack()
        {
            // A reorder is over the columns as drawn. When the picker later restores a hidden column it
            // has no recorded index and falls into the gap its declaration leaves, rather than
            // displacing the columns the user did arrange.
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.AllowColumnPicking, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", visible: false),
                    Columns.Property<Person, int>(x => x.Id, title: "Id")));
            });

            Assert.Equal(new[] { "First", "Id" }, Titles(cut));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(1, 0));

            Assert.Equal(new[] { "Id", "First" }, Titles(cut));
        }

        [Fact]
        public void ADropOutsideTheColumnsIsIgnored()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnReorder, true));

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 7));
            cut.InvokeAsync(() => cut.Instance.ReorderColumn(-1, 0));

            Assert.Equal(new[] { "First", "Last", "Id" }, Titles(cut));
        }
    }
}
