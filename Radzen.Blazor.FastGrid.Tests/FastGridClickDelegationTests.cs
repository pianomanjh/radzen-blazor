using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Clicks are raised from one listener on the tbody rather than a delegate per cell, and the grid
    /// puts the delegates back when the script does not confirm it attached.
    /// </summary>
    /// <remarks>
    /// The fallback is the whole reason these tests can be written the obvious way. bUnit has no DOM
    /// listeners, so a grid that only delegated would answer no click at all - and a test asserting on
    /// one would pass while proving nothing, which is worse than a slow grid.
    /// </remarks>
    public class FastGridClickDelegationTests
    {
        static RenderFragment TwoColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra,
            IList<Person>? data = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, TwoColumns());
                extra(p);
            });

        [Fact]
        public void WithoutAConfirmedListenerTheCellKeepsItsHandler()
        {
            // Strict mode: the import throws, which is one of the two ways bUnit says "no script".
            using var ctx = new TestContext();

            var seen = new List<string>();

            var cut = Render(ctx, p => p.Add(g => g.CellClick, EventCallback.Factory
                .Create<FastGridCellEventArgs<Person>>(this, a => seen.Add(a.Column.Title!))));

            cut.FindAll("tbody td")[1].Click();

            Assert.Equal(new[] { "Last" }, seen);
        }

        [Fact]
        public void LooseModeAnswersFalseAndAlsoKeepsTheHandler()
        {
            // The other way: loose mode answers every call with default, so attach returns false.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var seen = new List<string>();

            var cut = Render(ctx, p => p.Add(g => g.CellClick, EventCallback.Factory
                .Create<FastGridCellEventArgs<Person>>(this, a => seen.Add(a.Column.Title!))));

            cut.FindAll("tbody td")[0].Click();

            Assert.Equal(new[] { "First" }, seen);
        }

        [Fact]
        public void ARowClickStillReachesTheRowFromACellClick()
        {
            using var ctx = new TestContext();

            var clicked = new List<Person>();

            var cut = Render(ctx, p => p.Add(g => g.RowClick,
                EventCallback.Factory.Create<Person>(this, clicked.Add)));

            cut.FindAll("tbody tr[role=row]")[1].QuerySelectorAll("td")[1].Click();

            Assert.Single(clicked);
            Assert.Equal(People.Sample()[1].First, clicked[0].First);
        }

        [Fact]
        public void AGridListeningForNothingCarriesNeitherHandlersNorTheListenersMarkup()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, _ => { });

            Assert.Null(cut.Find("tbody").GetAttribute("id"));
            Assert.All(cut.FindAll("tbody tr[role=row]"), r => Assert.Null(r.GetAttribute("data-r")));
        }

        [Fact]
        public void TheBodyIsIdentifiedOnlyWhenSomethingListens()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.CellClick, EventCallback.Factory
                .Create<FastGridCellEventArgs<Person>>(this, _ => { })));

            Assert.NotNull(cut.Find("tbody").GetAttribute("id"));
        }

        [Fact]
        public void VirtualizationKeepsTheHandlersWhateverTheScriptSays()
        {
            // Not a fallback but a scope: Virtualize hands its ChildContent an item and no position, so
            // there is no row index for a listener to resolve. It renders a window rather than every
            // row, so the cost the delegation exists to remove is not there to remove.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var seen = new List<string>();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowVirtualization, true);
                p.Add(g => g.CellClick, EventCallback.Factory
                    .Create<FastGridCellEventArgs<Person>>(this, a => seen.Add(a.Column.Title!)));
            });

            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody td")));

            cut.FindAll("tbody td")[1].Click();

            Assert.Equal(new[] { "Last" }, seen);
            Assert.Null(cut.Find("tbody").GetAttribute("id"));
        }

        [Fact]
        public void TheDelegatedPathResolvesTheSameRowAndColumn()
        {
            // What the listener calls, driven directly - the one part of this no bUnit click can reach.
            using var ctx = new TestContext();

            FastGridCellEventArgs<Person>? seen = null;
            var rows = new List<Person>();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, rows.Add));
                p.Add(g => g.CellClick, EventCallback.Factory
                    .Create<FastGridCellEventArgs<Person>>(this, a => seen = a));
            });

            cut.InvokeAsync(() => cut.Instance.OnDelegatedPointer("click", 2, 1));

            Assert.NotNull(seen);
            Assert.Equal("Last", seen!.Column.Title);
            Assert.Equal(People.Sample()[2].First, seen.Data.First);

            // One click is one row click and one cell click, in that order.
            Assert.Single(rows);
            Assert.Equal(People.Sample()[2].First, rows[0].First);
        }

        [Fact]
        public void AClickThatMissedACellIsStillARowClick()
        {
            using var ctx = new TestContext();

            var rows = new List<Person>();
            var cells = 0;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, rows.Add));
                p.Add(g => g.CellClick, EventCallback.Factory
                    .Create<FastGridCellEventArgs<Person>>(this, _ => cells++));
            });

            cut.InvokeAsync(() => cut.Instance.OnDelegatedPointer("click", 0, -1));

            Assert.Single(rows);
            Assert.Equal(0, cells);
        }


        static IRenderedComponent<RadzenFastGrid<Person>> RenderDelegated(TestContext ctx, int rows,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            // Answering attach with true is what a browser does, and the only way to reach the delegated
            // render from a test.
            ctx.JSInterop
                .SetupModule("./_content/Radzen.Blazor.FastGrid/fastgrid.js")
                .Setup<bool>("attach", _ => true)
                .SetResult(true);

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(rows));
                p.Add(g => g.ChildContent, TwoColumns());
                extra(p);
            });
        }

        [Fact]
        public void AConfirmedListenerTakesTheHandlersOutOfTheMarkup()
        {
            using var ctx = new TestContext();

            var cut = RenderDelegated(ctx, 5, p => p.Add(g => g.CellClick, EventCallback.Factory
                .Create<FastGridCellEventArgs<Person>>(this, _ => { })));

            // Every row is addressable, and no cell carries a handler any more.
            Assert.All(cut.FindAll("tbody tr[role=row]"), r => Assert.NotNull(r.GetAttribute("data-r")));

            // bUnit says so itself: there is no handler on the cell, nor on any ancestor. Routing the
            // click is the browser's job once the listener is attached - and this is what every test
            // that is not this one would hit if the fallback did not exist, which is the argument for
            // it in one line.
            Assert.Throws<Bunit.MissingEventHandlerException>(() => cut.FindAll("tbody td")[1].Click());
        }

        [Fact]
        public void DelegatingCostsLessThanTheHandlersItReplaces()
        {
            // The harness allocates the same either way, so the difference between the two renders is
            // the feature's own - which is the only figure a bUnit render can report honestly.
            const int Rows = 400;

            static long Measure(Func<IRenderedComponent<RadzenFastGrid<Person>>> render)
            {
                using var warm = render();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetAllocatedBytesForCurrentThread();

                using var cut = render();

                return GC.GetAllocatedBytesForCurrentThread() - before;
            }

            var handlers = Measure(() =>
            {
                var ctx = new TestContext();

                return Render(ctx, p => p.Add(g => g.CellClick, EventCallback.Factory
                    .Create<FastGridCellEventArgs<Person>>(this, _ => { })), People.Many(Rows));
            });

            var delegated = Measure(() =>
            {
                var ctx = new TestContext();

                return RenderDelegated(ctx, Rows, p => p.Add(g => g.CellClick, EventCallback.Factory
                    .Create<FastGridCellEventArgs<Person>>(this, _ => { })));
            });

            Assert.True(delegated < handlers,
                $"delegated {delegated:N0} B should be below handlers {handlers:N0} B");
        }


        [Fact]
        public void TheRowDetailToggleIsDelegatedToo()
        {
            using var ctx = new TestContext();

            var cut = RenderDelegated(ctx, 4, p => p.Add<RenderFragment<Person>>(g => g.Template,
                row => b => b.AddContent(0, "detail")));

            // The button is marked for the listener rather than carrying a delegate of its own.
            Assert.Equal(4, cut.FindAll("td.rz-col-icon button[data-toggle]").Count);
            Assert.Throws<Bunit.MissingEventHandlerException>(
                () => cut.FindAll("td.rz-col-icon button")[0].Click());
        }

        [Fact]
        public void TheToggleStillExpandsThroughTheDelegatedPath()
        {
            using var ctx = new TestContext();

            var cut = RenderDelegated(ctx, 4, p => p.Add<RenderFragment<Person>>(g => g.Template,
                row => b => b.AddContent(0, "detail for " + row.First)));

            cut.InvokeAsync(() => cut.Instance.OnDelegatedPointer("toggle", 1, -1));

            Assert.Contains("detail for", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("true",
                cut.FindAll("td.rz-col-icon button")[1].GetAttribute("aria-expanded"));
        }

        [Fact]
        public void ATemplateArrivingLateStillGetsAWorkingToggle()
        {
            // The bug this pins, found in a browser and by no assertion: whether a click was a toggle
            // used to be decided by a flag settled when the listener attached, so a grid that gained a
            // Template afterwards drew a toggle the listener had never heard of. It rendered, it looked
            // right, and clicking it counted as a row click instead of expanding anything.
            //
            // The toggle is now read from the markup at click time, which cannot go stale, so no
            // re-attach is needed for this case at all.
            using var ctx = new TestContext();

            var cut = RenderDelegated(ctx, 4, p => p.Add(g => g.RowClick,
                EventCallback.Factory.Create<Person>(this, _ => { })));

            Assert.Empty(cut.FindAll("[data-toggle]"));

            cut.SetParametersAndRender(p => p.Add<RenderFragment<Person>>(g => g.Template,
                row => b => b.AddContent(0, "detail for " + row.First)));

            Assert.Equal(4, cut.FindAll("td.rz-col-icon button[data-toggle]").Count);

            cut.InvokeAsync(() => cut.Instance.OnDelegatedPointer("toggle", 0, -1));

            Assert.Contains("detail for", cut.Markup, StringComparison.Ordinal);
        }

        [Fact]
        public void AnEventTheListenerWasNotAttachedForReattachesIt()
        {
            // contextmenu is a different browser event rather than a shape of click, so a grid that
            // gains CellContextMenu after the first render needs the listener told.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var attach = ctx.JSInterop
                .SetupModule("./_content/Radzen.Blazor.FastGrid/fastgrid.js")
                .Setup<bool>("attach", _ => true);

            attach.SetResult(true);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(4));
                p.Add(g => g.ChildContent, TwoColumns());
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, _ => { }));
            });

            Assert.Single(attach.Invocations);

            cut.SetParametersAndRender(p => p.Add(g => g.CellContextMenu, EventCallback.Factory
                .Create<FastGridCellEventArgs<Person>>(this, _ => { })));

            Assert.Equal(2, attach.Invocations.Count);
        }

        [Fact]
        public void AnOutOfRangeRowRaisesNothing()
        {
            using var ctx = new TestContext();

            var rows = new List<Person>();

            var cut = Render(ctx, p => p.Add(g => g.RowClick,
                EventCallback.Factory.Create<Person>(this, rows.Add)));

            cut.InvokeAsync(() => cut.Instance.OnDelegatedPointer("click", 999, 0));

            Assert.Empty(rows);
        }
    }
}
