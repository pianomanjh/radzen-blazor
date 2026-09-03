using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Column resizing. The drag is the browser's, so what is testable here is everything around it:
    /// what the grid emits, what it does with the widths handed back, and - the point of most of these -
    /// that a grid which does not allow resizing emits none of it.
    /// </summary>
    public class FastGridColumnResizeTests
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

        [Fact]
        public void AGridThatDoesNotResizeEmitsNothingForIt()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll(".rz-column-resizer"));
            Assert.Empty(cut.FindAll("col[id]"));
            Assert.Empty(cut.FindAll("th.rz-resizable-column"));
        }

        [Fact]
        public void AllowColumnResizePutsAHandleOnEveryColumn()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            Assert.Equal(3, cut.FindAll(".rz-column-resizer").Count);
            Assert.Equal(3, cut.FindAll("th.rz-resizable-column").Count);
        }

        [Fact]
        public void TheHandleLivesInsideTheHeadersLoadBearingDiv()
        {
            // Not decoration: the theme positions the handle against the th, and the th only becomes a
            // containing block through rz-resizable-column. A handle emitted as a sibling of the header
            // chain would position against the table and every column's handle would stack in one place.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            Assert.Equal(3, cut.FindAll("th.rz-resizable-column > div > .rz-column-resizer").Count);
        }

        [Fact]
        public void AColumnCanOptOut()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", resizable: false)));
            });

            Assert.Single(cut.FindAll(".rz-column-resizer"));
            Assert.Single(cut.FindAll("th.rz-resizable-column"));
        }

        [Fact]
        public void TheHandleIdsMatchTheColIdsTheScriptResolvesAgainst()
        {
            // The script is handed one base id and appends '-col' and '-resizer' to reach the two
            // elements. If what the markup emits stops matching that convention it finds no col, writes
            // the width to the th instead, and table-layout:fixed discards it - a drag that runs, raises
            // its callback, and moves nothing. Which is exactly what happened before this was pinned.
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            var cols = cut.FindAll("colgroup col").Select(c => c.Id).ToArray();
            var handles = cut.FindAll(".rz-column-resizer").Select(h => h.Id).ToArray();

            Assert.Equal(3, cols.Length);
            Assert.All(cols, id => Assert.EndsWith("-col", id, StringComparison.Ordinal));

            // Both derive from the same base by suffix, which is the contract the script relies on.
            var bases = cols.Select(id => id[..^"-col".Length]).ToArray();

            Assert.Equal(bases.Select(b => b + "-resizer").ToArray(), handles);
            Assert.All(bases, b => Assert.False(b.EndsWith("-col", StringComparison.Ordinal)));
        }

        [Fact]
        public void ResizingWritesTheWidthOntoTheColgroup()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 250, new double[] { 250, 120, 0 }));

            var styles = cut.FindAll("colgroup col").Select(c => c.GetAttribute("style")).ToArray();

            Assert.Equal("width:250px", styles[0]);
            Assert.Equal("width:120px", styles[1]);

            // Zero means the script never pinned that column, so it keeps whatever it had rather than
            // being frozen at a width nobody chose.
            Assert.Null(styles[2]);
        }

        [Fact]
        public void TheResizedWidthSurvivesAParameterSet()
        {
            // The whole reason a drag does not write to the Width parameter: Blazor would put the
            // declared value back on the next parameter set and the column would snap to it.
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First", width: "100px"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last", width: "100px")));
            });

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 300, new double[] { 300, 100 }));

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Sample()));

            Assert.Equal("width:300px", cut.FindAll("colgroup col")[0].GetAttribute("style"));
        }

        [Fact]
        public void AResizedWidthIsCapturedAndRestored()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 250, new double[] { 250, 0, 0 }));

            var settings = cut.Instance.CaptureSettings();
            var first = settings.Columns.Single(c => c.Property == nameof(Person.First));

            Assert.Equal("250px", first.Width);

            using var second = Context();

            var restored = second.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.Settings, settings);
            });

            Assert.Equal("width:250px", restored.FindAll("colgroup col")[0].GetAttribute("style"));
        }

        [Fact]
        public void AGridWithNoWidthsStillGetsAColgroupOnceItCanResize()
        {
            // The colgroup is otherwise written only when some column declares a width. Resize needs one
            // regardless: it is where the script writes, and without it a drag has nowhere to land.
            using var ctx = Context();

            Assert.Empty(Render(ctx).FindAll("colgroup"));

            using var resizing = Context();

            Assert.Single(Render(resizing, p => p.Add(g => g.AllowColumnResize, true)).FindAll("colgroup"));
        }

        [Fact]
        public void ColumnResizedCarriesTheColumnAndTheWidth()
        {
            using var ctx = Context();

            FastGridColumnResizedEventArgs<Person> seen = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.ColumnResized, EventCallback.Factory
                    .Create<FastGridColumnResizedEventArgs<Person>>(new object(), args => seen = args));
            });

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(1, 180, new double[] { 0, 180, 0 }));

            Assert.NotNull(seen);
            Assert.Equal(180, seen.Width);
            Assert.Equal("Last", seen.Column.Title);
        }

        [Fact]
        public void ResizingDoesNotGiveTheGridAFooterItNeverAskedFor()
        {
            // The colgroup has a second reason to exist - a resize needs somewhere to write a width -
            // and that condition was copied into the footer with it. The theme makes tfoot sticky at
            // the bottom over a background, so it drew a grey bar across every resizable grid.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(4));
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First)));
            });

            Assert.Empty(cut.FindAll("tfoot"));

            // The colgroup still is emitted, which is the reason the condition was there at all.
            Assert.Single(cut.FindAll("colgroup"));
        }
    }
}
