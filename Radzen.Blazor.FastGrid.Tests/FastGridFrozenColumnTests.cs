using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Frozen columns. The theme makes a .rz-frozen-cell sticky and gives it a background and a seam,
    /// but not an inset - and sticky without an inset does nothing. So the classes are only half of what
    /// these pin; the other half is where each column says it is pinned, which is the part that has to
    /// be right for anything to hold still. That it actually holds still is
    /// <c>GeometryParityTests.A_frozen_column_is_actually_pinned</c>, in a browser.
    /// </summary>
    public class FastGridFrozenColumnTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });

        static string[] BodyClasses(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr:first-child td").Select(td => td.GetAttribute("class") ?? "").ToArray();

        static string[] BodyStyles(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr:first-child td").Select(td => td.GetAttribute("style") ?? "").ToArray();

        [Fact]
        public void AGridWithNoFrozenColumnEmitsNothingForIt()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            Assert.Empty(cut.FindAll(".rz-frozen-cell"));
            Assert.All(BodyStyles(cut), style => Assert.DoesNotContain("left:", style, StringComparison.Ordinal));
        }

        [Fact]
        public void TheFirstFrozenColumnIsPinnedAtTheEdge()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            var classes = BodyClasses(cut);

            Assert.Contains("rz-frozen-cell", classes[0], StringComparison.Ordinal);
            Assert.Contains("rz-frozen-cell-left", classes[0], StringComparison.Ordinal);
            Assert.Contains("left:0", BodyStyles(cut)[0], StringComparison.Ordinal);
            Assert.Empty(classes[1]);
        }

        [Fact]
        public void ASecondFrozenColumnClearsTheFirstByItsWidth()
        {
            // The whole point of computing the inset on the server: the second column is pinned at the
            // first one's width, so the two sit side by side instead of on top of each other.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id")));

            var styles = BodyStyles(cut);

            Assert.Contains("left:0", styles[0], StringComparison.Ordinal);
            Assert.Contains("left:90px", styles[1], StringComparison.Ordinal);
        }

        [Fact]
        public void AThirdFrozenColumnAddsTheWidthsWithCalc()
        {
            // Added with calc rather than parsed into a number, so the widths may be in any unit.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "10%", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id", width: "4rem", frozen: true),
                Columns.Property<Person, decimal>(x => x.Salary, title: "Salary")));

            var styles = BodyStyles(cut);

            Assert.Contains("left:0", styles[0], StringComparison.Ordinal);
            Assert.Contains("left:90px", styles[1], StringComparison.Ordinal);
            Assert.Contains("left:calc(90px + 10%)", styles[2], StringComparison.Ordinal);
        }

        [Fact]
        public void TheLastColumnOfTheRunCarriesTheSeam()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id")));

            var classes = BodyClasses(cut);

            Assert.DoesNotContain("rz-frozen-cell-left-end", classes[0], StringComparison.Ordinal);
            Assert.Contains("rz-frozen-cell-left-end", classes[1], StringComparison.Ordinal);
        }

        [Fact]
        public void TheHeaderIsPinnedWithTheBody()
        {
            // Or the title scrolls away from its own cells, which is worse than not freezing at all.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            var th = cut.FindAll("thead th")[0];

            Assert.Contains("rz-frozen-cell", th.GetAttribute("class"), StringComparison.Ordinal);
            Assert.Contains("left:0", th.GetAttribute("style") ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public void ARightFrozenColumnIsPinnedToTheOtherEdge()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px",
                    frozen: true, frozenPosition: FrozenColumnPosition.Right),
                Columns.Property<Person, int>(x => x.Id, title: "Id", width: "90px",
                    frozen: true, frozenPosition: FrozenColumnPosition.Right)));

            var classes = BodyClasses(cut);
            var styles = BodyStyles(cut);

            Assert.Empty(classes[0]);

            // Counted from the right: the last column sits at the edge, the one before it clears it.
            Assert.Contains("right:0", styles[2], StringComparison.Ordinal);
            Assert.Contains("right:90px", styles[1], StringComparison.Ordinal);
            Assert.Contains("rz-frozen-cell-right-end", classes[1], StringComparison.Ordinal);
        }

        [Fact]
        public void BothEdgesCanBeFrozenAtOnce()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last"),
                Columns.Property<Person, int>(x => x.Id, title: "Id", width: "80px",
                    frozen: true, frozenPosition: FrozenColumnPosition.Right)));

            var styles = BodyStyles(cut);

            Assert.Contains("left:0", styles[0], StringComparison.Ordinal);
            Assert.Empty(styles[1]);
            Assert.Contains("right:0", styles[2], StringComparison.Ordinal);
        }

        [Fact]
        public void AColumnStrandedInTheMiddleIsNotFrozen()
        {
            // RadzenDataGrid has '-inner' classes for a frozen column that does not touch an edge. This
            // grid does not build that case, so such a column is drawn as an ordinary one rather than
            // pinned to a position nothing has worked out.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id")));

            Assert.Empty(cut.FindAll(".rz-frozen-cell"));
        }

        [Fact]
        public void ARunStopsAtTheFirstColumnWithNoWidth()
        {
            // A column with no width can still be pinned - where it sits depends only on what is between
            // it and the edge. What cannot be placed is everything after it, so the run ends there and
            // the widthless column carries the seam.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id", width: "80px", frozen: true),
                Columns.Property<Person, decimal>(x => x.Salary, title: "Salary")));

            var classes = BodyClasses(cut);

            Assert.Contains("rz-frozen-cell", classes[0], StringComparison.Ordinal);
            Assert.DoesNotContain("rz-frozen-cell-left-end", classes[0], StringComparison.Ordinal);

            // Pinned at the first column's width, and the end of the run because nothing after it can
            // be placed.
            Assert.Contains("rz-frozen-cell-left-end", classes[1], StringComparison.Ordinal);
            Assert.Contains("left:90px", BodyStyles(cut)[1], StringComparison.Ordinal);

            Assert.DoesNotContain("rz-frozen-cell", classes[2], StringComparison.Ordinal);
        }

        [Fact]
        public void TheToggleColumnIsClearedByTheFirstFrozenColumn()
        {
            // The toggle is a cell in every row before the first data column, and its width is a theme
            // variable rather than a number - so the inset has to name the variable rather than a value.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last")),
                p => p.Add(g => g.Template, (RenderFragment<Person>)(person => b => b.AddContent(0, person.First))));

            var frozen = cut.FindAll("tbody tr:first-child td")
                .First(td => (td.GetAttribute("class") ?? "").Contains("rz-frozen-cell", StringComparison.Ordinal));

            Assert.Contains("left:var(--rz-grid-column-icon-width)",
                frozen.GetAttribute("style") ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public void FreezingSurvivesTheColumnMovingUnderReorder()
        {
            // The runs are worked out from the drawn order, so dragging an unfrozen column to the front
            // takes the freeze off the column that is no longer at the edge.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last"),
                Columns.Property<Person, int>(x => x.Id, title: "Id")),
                p => p.Add(g => g.AllowColumnReorder, true));

            Assert.Contains("rz-frozen-cell", BodyClasses(cut)[0], StringComparison.Ordinal);

            cut.InvokeAsync(() => cut.Instance.ReorderColumn(2, 0));

            // Id is now at the edge and is not frozen; First has been displaced into the middle.
            Assert.Empty(cut.FindAll(".rz-frozen-cell"));
        }

        [Fact]
        public void HidingTheFirstFrozenColumnPinsTheNextOneAtTheEdge()
        {
            // The runs are read off the columns as drawn, so hiding one does not strand the rest: the
            // next frozen column simply becomes the one at the edge, and is pinned at zero.
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnReorder, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First", width: "90px",
                        frozen: true, visible: false),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px", frozen: true),
                    Columns.Property<Person, int>(x => x.Id, title: "Id")));
            });

            var classes = BodyClasses(cut);

            Assert.Contains("rz-frozen-cell", classes[0], StringComparison.Ordinal);
            Assert.Contains("left:0", BodyStyles(cut)[0], StringComparison.Ordinal);
        }

        [Fact]
        public void AResizedWidthMovesTheColumnPinnedBehindIt()
        {
            // The inset is built from EffectiveWidth, so a drag that widens the first frozen column has
            // to push the second one across. Nothing recomputes it but the next render.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true),
                Columns.Property<Person, string>(x => x.Last, title: "Last", width: "120px", frozen: true),
                Columns.Property<Person, int>(x => x.Id, title: "Id")),
                p => p.Add(g => g.AllowColumnResize, true));

            Assert.Contains("left:90px", BodyStyles(cut)[1], StringComparison.Ordinal);

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 200, new double[] { 200, 120, 0 }));

            Assert.Contains("left:200px", BodyStyles(cut)[1], StringComparison.Ordinal);
        }
    }
}
