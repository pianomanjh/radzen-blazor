using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The four calls into the browser module that nothing asked about, and the answer nothing staged.
    /// </summary>
    /// <remarks>
    /// §15 said seven of the nine exports had no coverage and that the RTL arrow flip could not be
    /// executed by any test because the doubles answer null. The count was four, and the second half
    /// was a diagnosis rather than a fact: staging an answer is what <see cref="TestContext.JSInterop" />
    /// is for, one test file over already does it, and <c>NavigationMetrics</c> was made internal
    /// precisely so that it could be done. What was missing was these.
    /// </remarks>
    public class FastGridBrowserSeamTests
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        // How a test finds the view. Not part of BrowserContract: the script is handed the view's id
        // and never selects it by class, so a constant for it there would be a name in a list of
        // shared names that is not shared.
        const string ViewSelector = ".rz-data-grid-data";

        static BunitJSModuleInterop Module(TestContext ctx)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.JSInterop.SetupModule(ModulePath);
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Navigating(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, int>(x => x.Id, title: "Id")));
                p.Add(g => g.AllowKeyboardNavigation, true);
                extra?.Invoke(p);
            });

        // --- what the browser measures, and what the grid does with it -------------------------

        // The half of navigation that depends on an answer rather than on a binding. The arrow keys are
        // named for the screen and the model is logical, so under RTL they have to mean the opposite
        // cell - and the only thing that knows the writing direction is the browser.
        [Theory]
        [InlineData(false, 1, 0)]
        [InlineData(true, 0, 1)]
        public void TheArrowsFollowTheWritingDirectionTheBrowserReports(bool rtl, int afterRight,
            int afterLeft)
        {
            using var ctx = new TestContext();

            Module(ctx).Setup<RadzenFastGrid<Person>.NavigationMetrics>("attachNavigation", _ => true)
                .SetResult(new RadzenFastGrid<Person>.NavigationMetrics { Rtl = rtl, Rows = 5 });

            var cut = Navigating(ctx);
            var view = cut.Find(ViewSelector);

            view.Focus();
            view.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

            Assert.Equal(afterRight, cut.Instance.FocusedCell!.Value.Cell);

            view.KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });

            Assert.Equal(afterLeft, cut.Instance.FocusedCell!.Value.Cell);
        }

        // --- the four exports nothing asked about -----------------------------------------------

        // The cursor's paint is the browser's job: the class goes on a cell the grid names, and the
        // scroll that brings it into view is measured against the frozen runs and the virtualized row
        // height. None of that is observable in the markup, so what a test can check is that the grid
        // asks, and asks about the cell it says the cursor is on.
        [Fact]
        public void MovingTheCursorAsksTheBrowserToPaintTheCellTheGridSaysItIsOn()
        {
            using var ctx = new TestContext();

            var module = Module(ctx);
            var cut = Navigating(ctx);
            var view = cut.Find(ViewSelector);

            view.Focus();
            view.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

            // Sideways as well as down, and that is not decoration: the cursor starts at cell 0, so a
            // grid that always painted cell 0 satisfied this test until the row and the cell were both
            // moved off their starting values. The mutation that pins it is exactly that.
            view.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });

            var painted = module.Invocations["focusCell"].Last();
            var (row, cell) = cut.Instance.FocusedCell!.Value;

            Assert.Equal(cut.Instance.ViewElementId, painted.Arguments[0]);
            Assert.Equal(row, painted.Arguments[1]);
            Assert.Equal(cell, painted.Arguments[2]);
            Assert.NotEqual(0, cell);
        }

        // And the paint comes off when the grid stops being the thing with the cursor - otherwise a
        // focused cell stays lit in a grid the user has tabbed out of, which is the browser's own
        // focus ring saying one thing while the painted one says another.
        [Fact]
        public void LeavingTheGridAsksTheBrowserToTakeThePaintOff()
        {
            using var ctx = new TestContext();

            var module = Module(ctx);
            var cut = Navigating(ctx);
            var view = cut.Find(ViewSelector);

            view.Focus();
            view.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

            Assert.Empty(module.Invocations["blurCell"]);

            view.Blur();

            Assert.Equal(cut.Instance.ViewElementId,
                Assert.Single(module.Invocations["blurCell"]).Arguments[0]);
        }

        // The container observer is held by the script, not by anything the circuit owns, so a grid
        // that went away without releasing it leaves the browser redistributing a table nobody is
        // looking at for as long as the page lives. Asked unconditionally and for the reason the code
        // gives: a grid switched out of Fit still has the observer it started with.
        [Fact]
        public void DisposingAsksTheBrowserToStopWatchingTheTable()
        {
            using var ctx = new TestContext();

            var module = Module(ctx);
            var cut = Navigating(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand));
            var table = cut.Instance.TableElementId;

            Assert.Empty(module.Invocations["releaseFit"]);

            cut.Instance.DisposeAsync().AsTask().Wait();

            Assert.Equal(table, Assert.Single(module.Invocations["releaseFit"]).Arguments[0]);
        }

        // Re-measuring without re-binding. A page turn changes how many rows are drawn and can change
        // nothing else, and the page step is computed from that count - so the grid asks again rather
        // than keeping what the attach told it.
        [Fact]
        public void TheGridReMeasuresTheViewWithoutRebindingTheKeyGuard()
        {
            using var ctx = new TestContext();

            var module = Module(ctx);

            module.Setup<RadzenFastGrid<Person>.NavigationMetrics>("attachNavigation", _ => true)
                .SetResult(new RadzenFastGrid<Person>.NavigationMetrics { Rtl = false, Rows = 5 });
            module.Setup<RadzenFastGrid<Person>.NavigationMetrics>("measureNavigation", _ => true)
                .SetResult(new RadzenFastGrid<Person>.NavigationMetrics { Rtl = false, Rows = 9 });

            var cut = Navigating(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 2);
            });

            cut.Find(ViewSelector).Focus();

            var bound = module.Invocations["attachNavigation"].Count;

            cut.InvokeAsync(() => cut.Instance.GoToPage(1)).Wait();

            Assert.NotEmpty(module.Invocations["measureNavigation"]);
            Assert.Equal(cut.Instance.ViewElementId,
                module.Invocations["measureNavigation"].Last().Arguments[0]);

            // The binding is untouched: a re-measure is not a re-attach, and treating it as one would
            // rebind a live listener on every page turn.
            Assert.Equal(bound, module.Invocations["attachNavigation"].Count);
        }
    }
}
