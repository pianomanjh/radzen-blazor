using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Column auto-fit, with the measurement stubbed. What can be tested here is every decision either
    /// side of the browser: which columns are offered to it, what it is told about them, what is done
    /// with the answer, and that a grid which does not fit asks nothing at all. The measurement itself
    /// is layout, and layout is GeometryParityTests.
    /// </summary>
    public class FastGridAutoFitTests
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        static RenderFragment ThreeColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"),
            Columns.Property<Person, int>(x => x.Id, title: "Id"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            RenderFragment columns = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, columns ?? ThreeColumns());
                extra?.Invoke(p);
            });

        /// <summary>
        /// The call's one argument. This used to be a record declared here beside a decoder that read
        /// <c>invocation.Arguments</c> by index - which is the caller's own bug copied into the thing
        /// meant to catch it, since swapping Min and Max was silent in the caller, in the script and
        /// here at once. The type is <see cref="AutoFitAsk" /> now and it is the one the call is made
        /// with, so there is nothing left to decode.
        /// </summary>
        static AutoFitAsk Read(JSRuntimeInvocation invocation) =>
            (AutoFitAsk)invocation.Arguments[0]!;

        // --- A grid that does not fit -----------------------------------------------------------

        [Fact]
        public void AGridThatDoesNotFitAsksTheBrowserForNothing()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var cut = Render(ctx);

            Assert.Empty(module.Invocations["autoFit"]);
            Assert.Empty(cut.FindAll("table[id]"));
            Assert.Empty(cut.FindAll("colgroup"));
        }

        [Fact]
        public void AGridThatFitsEmitsTheTableAndTheColgroupItNeeds()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule(ModulePath);

            // No column declares a width, which is exactly the grid that wants fitting - and exactly
            // the grid that would otherwise emit no colgroup for the widths to be written into.
            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand));

            Assert.Single(cut.FindAll("table[id]"));
            Assert.Equal(3, cut.FindAll("colgroup col").Count);
        }

        // --- Waiting for names it cannot measure without ------------------------------------------

        static RenderFragment LookupColumns(FastGridLookup<int> lookup) => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Lookup<Person, int>(x => x.CategoryId, lookup, title: "Category"));

        static FastGridLookup<int> Fetched() =>
            FastGridLookup.Query(Lookups.CategoryRows().AsQueryable(), c => c.Id, c => c.Name);

        [Fact]
        public void AFitWaitsForNamesThatHaveNotArrived()
        {
            // autoFitPending is disarmed by the attempt, and the script waits for rows rather than for
            // cell content - so a fit taken now would measure blank cells, settle the column at its
            // header width, and the names would arrive into a column too narrow for them. Nothing
            // invalidates a fit, so that is permanent.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var executor = new GatedLookupExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), LookupColumns(Fetched()));

            Assert.Empty(module.Invocations["autoFit"]);
        }

        [Fact]
        public void TheFitItWasOwedRunsOnceTheNamesArrive()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new GatedLookupExecutor { Holds = 0 });

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), LookupColumns(Fetched()));

            Assert.Single(module.Invocations["autoFit"]);
        }

        [Fact]
        public void AFitWaitingOnNamesThatFailToArriveStillRuns()
        {
            // Deferring gives back the property that disarming on the attempt was there to provide, so
            // every way out of the fetch has to hand it over again. A lookup that never resolves would
            // otherwise be a fit that never fires.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(
                new GatedLookupExecutor { Holds = 0, Fails = new InvalidOperationException("no") });

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), LookupColumns(Fetched()));

            Assert.Single(module.Invocations["autoFit"]);
        }

        [Fact]
        public void AFitDeferredAcrossADropStillRuns()
        {
            // Reload while the names are in flight discards the answer and asks again, so the fit is
            // owed across two fetches rather than one. Deferring is what gives back the property that
            // disarming on the attempt provides, and a second round of it must not lose the fit.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var executor = new GatedLookupExecutor { Holds = 1 };

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once),
                LookupColumns(Fetched()));

            var stale = executor.Pending;

            cut.InvokeAsync(() => cut.Instance.Reload()).Wait();

            Assert.Empty(module.Invocations["autoFit"]);

            stale.Release();

            cut.WaitForAssertion(() => Assert.Equal(2, executor.Materializations));

            // A render whose own OnAfterRender would fire the fit if it had not already: the deferral
            // never lifting is what this fails on, not the render the names arrived in.
            cut.Render();

            Assert.Single(module.Invocations["autoFit"]);
        }

        [Fact]
        public void AFitIsNotHeldUpByNamesThatAreAlreadyInHand()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once),
                LookupColumns(FastGridLookup.Map(Lookups.Categories())));

            Assert.Single(module.Invocations["autoFit"]);
        }

        // --- When it fires ----------------------------------------------------------------------

        [Fact]
        public void OnceFitsWithoutBeingAsked()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once));

            var ask = Assert.Single(module.Invocations["autoFit"]);

            // The script waits for rows rather than the server deciding there are any: Virtualize
            // re-renders itself, so its window arrives without a render of the grid.
            Assert.True(Read(ask).Wait);
        }

        [Fact]
        public void OnceFitsOnlyOnce()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "10px", "20px", null });

            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once));

            cut.Find("thead th div").Click();
            cut.Render();

            Assert.Single(module.Invocations["autoFit"]);
        }

        [Fact]
        public void OnDemandDoesNotFitUntilItIsAsked()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand));

            Assert.Empty(module.Invocations["autoFit"]);

            cut.InvokeAsync(() => cut.Instance.AutoFitAsync()).Wait();

            Assert.Single(module.Invocations["autoFit"]);
        }

        [Fact]
        public void DoubleClickingAResizeHandleFitsThatColumnAlone()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand);
                p.Add(g => g.AllowColumnResize, true);
            });

            cut.FindAll(".rz-column-resizer")[1].DoubleClick();

            var ask = Read(Assert.Single(module.Invocations["autoFit"]));

            Assert.Equal(new[] { 1 }, ask.Indices);

            // Fitting one column must not move the stretch to it from wherever it currently sits.
            Assert.Equal(-1, ask.Bare);
        }

        [Fact]
        public void FittingOneColumnLeavesTheBareColumnWhereTheLastFullFitPutIt()
        {
            // Sending -1 is only half of it. Recording -1 clears the column the last full fit left
            // bare, so the trailing column quietly regains the grid's ColumnWidth on some later
            // unrelated render - and with nothing frozen there is no render here to make it visible.
            // The test that only checked what was sent passed while that was happening.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var planned = module.Setup<string[]>("autoFit", _ => true);

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand);
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.ColumnWidth, "150px");
            });

            planned.SetResult(new[] { "40px", "50px", null });
            cut.InvokeAsync(() => cut.Instance.AutoFitAsync()).Wait();
            cut.Render();
            Assert.Null(cut.FindAll("colgroup col")[2].GetAttribute("style"));

            var single = module.Setup<string[]>("autoFit", i => Read(i).Indices.Count == 1);
            single.SetResult(new[] { "44px" });
            cut.FindAll(".rz-column-resizer")[0].DoubleClick();
            cut.Render();

            Assert.Equal("width:44px", cut.FindAll("colgroup col")[0].GetAttribute("style"));
            Assert.Null(cut.FindAll("colgroup col")[2].GetAttribute("style"));
        }

        [Fact]
        public void AGridThatDoesNotFitPutsNoHandlerOnTheHandle()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnResize, true));

            // bUnit refuses an event the element has no handler for, which is the assertion: the
            // handle is there for the drag and carries nothing for a fit.
            Assert.Throws<MissingEventHandlerException>(
                () => cut.FindAll(".rz-column-resizer")[1].DoubleClick());

            Assert.Empty(module.Invocations["autoFit"]);
        }

        // --- Which columns are offered ----------------------------------------------------------

        [Fact]
        public void AColumnThatDeclaresItsOwnWidthIsLeftAlone()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, string>(x => x.Last, width: "300px"),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Equal(new[] { 0, 2 }, Read(Assert.Single(module.Invocations["autoFit"])).Indices);
        }

        [Fact]
        public void AColumnThatOptsOutIsLeftAlone()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, string>(x => x.Last, autoFit: false),
                Columns.Property<Person, int>(x => x.Id)));

            Assert.Equal(new[] { 0, 2 }, Read(Assert.Single(module.Invocations["autoFit"])).Indices);
        }

        [Fact]
        public void TheBoundsGoOverAsTheyWereAuthored()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), Columns.Of(
                Columns.Property<Person, string>(x => x.First, minWidth: "10rem", maxWidth: "30%"),
                Columns.Property<Person, string>(x => x.Last)));

            var ask = Read(Assert.Single(module.Invocations["autoFit"]));

            // Not parsed into pixels here, and not parsed there either: clamp() is what compares a rem
            // with a percentage, for the same reason a frozen inset is summed with calc().
            Assert.Equal(new[] { "10rem", null }, ask.Min);
            Assert.Equal(new[] { "30%", null }, ask.Max);
        }

        [Fact]
        public void TheToggleColumnShiftsEveryPositionAlong()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                p.Add(g => g.Template, (RenderFragment<Person>)(_ => b => b.AddContent(0, "detail")));
            });

            Assert.Equal(1, Read(Assert.Single(module.Invocations["autoFit"])).ToggleOffset);
        }

        // --- The bare column --------------------------------------------------------------------

        [Fact]
        public void TheLastFittedColumnIsLeftBare()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once));

            Assert.Equal(2, Read(Assert.Single(module.Invocations["autoFit"])).Bare);
        }

        [Fact]
        public void AFrozenColumnIsNeverTheBareOne()
        {
            // A frozen run ends at the first frozen column declaring no width, so leaving one bare
            // would unpin every column after it.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, string>(x => x.Last),
                Columns.Property<Person, int>(x => x.Id, frozen: true,
                    frozenPosition: FrozenColumnPosition.Right)));

            Assert.Equal(1, Read(Assert.Single(module.Invocations["autoFit"])).Bare);
        }

        [Fact]
        public void EveryColumnBeingFrozenLeavesNoneBare()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once), Columns.Of(
                Columns.Property<Person, string>(x => x.First, frozen: true),
                Columns.Property<Person, string>(x => x.Last, frozen: true)));

            Assert.Equal(-1, Read(Assert.Single(module.Invocations["autoFit"])).Bare);
        }

        // --- What comes back --------------------------------------------------------------------

        [Fact]
        public void TheWidthsThatComeBackAreTheWidthsThatAreReEmitted()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true)
                .SetResult(new[] { "clamp(10rem,120px,30%)", "88px", null });

            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.Once));

            cut.Render();

            var cols = cut.FindAll("colgroup col");

            Assert.Equal("width:clamp(10rem,120px,30%)", cols[0].GetAttribute("style"));
            Assert.Equal("width:88px", cols[1].GetAttribute("style"));

            // The bare one carries no width, so the browser hands it what the others did not take.
            Assert.Null(cols[2].GetAttribute("style"));
        }

        [Fact]
        public void TheBareColumnStaysBareUnderAGridWideColumnWidth()
        {
            // Skipping the column is not the same as storing no width for it: ColumnWidth would
            // otherwise come back and give it one on the very next render.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "40px", "50px", null });

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                p.Add(g => g.ColumnWidth, "150px");
            });

            cut.Render();

            Assert.Null(cut.FindAll("colgroup col")[2].GetAttribute("style"));
        }

        [Fact]
        public void TheGridTellsTheBrowserWhetherItIsFittingToTheContainer()
        {
            // The browser honours this flag and a Chromium test covers that end, but only the grid
            // knows which mode it is in, so what it sends is the whole of its side. Nothing pinned it
            // before: hard-coding the flag to false, which turns the entire feature off, passed every
            // test in the suite.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "40px", "50px", null });

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                p.Add(g => g.AllowColumnResize, true);
                p.Add(g => g.AutoFitOverflow, AutoFitOverflow.Scroll);
            });

            cut.Render();
            Assert.Equal("scroll", Read(module.Invocations["autoFit"].First()).Overflow);

            // Changing the mode re-arms the Once fit, which is the only reason this second render
            // asks again at all.
            cut.SetParametersAndRender(p => p.Add(g => g.AutoFitOverflow, AutoFitOverflow.Fit));
            Assert.Equal("fit", Read(module.Invocations["autoFit"].Last()).Overflow);

            // Fitting to the container is a whole-grid answer: one column cannot be redistributed
            // against, and a double-click is a user pointing at that column rather than at the layout.
            // "keep", not "scroll": a single column cannot be redistributed against, but saying so
            // with the same value that means "this grid has left Fit" tore the container fit down.
            cut.FindAll(".rz-column-resizer")[0].DoubleClick();
            Assert.Equal("keep", Read(module.Invocations["autoFit"].Last()).Overflow);
        }

        [Fact]
        public void TheGridTellsTheBrowserWhichColumnsMustKeepTheirWidth()
        {
            // AutoFitPriority is the other half of the same wiring, and was equally unpinned. The
            // flags travel positionally against the same target list, so this checks the alignment
            // rather than only the count.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "40px", "50px", null });

            var cut = Render(ctx, p =>
                {
                    p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                    p.Add(g => g.AutoFitOverflow, AutoFitOverflow.Fit);
                },
                Columns.Of(
                    Columns.Property<Person, string>(x => x.First, required: true),
                    Columns.Property<Person, string>(x => x.Last),
                    Columns.Property<Person, decimal>(x => x.Salary, required: true)));

            cut.Render();

            var ask = Read(Assert.Single(module.Invocations["autoFit"]));

            Assert.Equal(new[] { true, false, true }, ask.Required);
            Assert.Equal(ask.Indices.Count, ask.Required.Count);
        }

        [Fact]
        public void OnlyAFitTheUserAskedForIsAnimated()
        {
            // The browser honours this flag - a separate test covers that - but only the grid knows
            // which kind of fit it is running, so what it sends is the whole of its side of the rule.
            // The fit Once runs is the grid settling into its first layout, and animating that reads
            // as a page still loading rather than as an answer to anything the user did.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "40px", "50px", null });

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                p.Add(g => g.AllowColumnResize, true);
            });

            cut.Render();
            Assert.False(Read(module.Invocations["autoFit"].First()).Animate);

            cut.InvokeAsync(() => cut.Instance.AutoFitAsync()).Wait();
            Assert.True(Read(module.Invocations["autoFit"].Last()).Animate);

            cut.FindAll(".rz-column-resizer")[0].DoubleClick();
            Assert.True(Read(module.Invocations["autoFit"].Last()).Animate);
        }

        [Fact]
        public void TheAutomaticFitLeavesAWidthTheUserChoseAlone()
        {
            // resizedWidth is where a width restored from the settings lands as well as where a drag
            // does - a restored width being a drag from a previous visit. So a Once fit that cleared
            // it would wipe every width a user had saved, and because the settings capture reads that
            // same slot the next sort or page turn would then persist the absence. The width would be
            // gone rather than overridden.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "60px", "70px" });

            FastGridSettings captured = null;

            var restored = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = "First", Width = "333px" }
                }
            };

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.Once);
                p.Add(g => g.Settings, restored);
                p.Add(g => g.SettingsChanged, EventCallback.Factory
                    .Create<FastGridSettings>(this, s => captured = s));
            });

            cut.Render();

            // Not measured at all: it already carries a width somebody chose.
            Assert.Equal(new[] { 1, 2 }, Read(Assert.Single(module.Invocations["autoFit"])).Indices);
            Assert.Equal("width:333px", cut.FindAll("colgroup col")[0].GetAttribute("style"));

            // And still there to be saved again.
            cut.Find("thead th div").Click();
            Assert.Equal("333px", captured.Columns.Single(c => c.Property == "First").Width);
        }

        [Fact]
        public void AFitTheUserAsksForTakesTheColumnBackFromADrag()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "60px", "70px", null });

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand);
                p.Add(g => g.AllowColumnResize, true);
            });

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 400, new[] { 400d, 0, 0 })).Wait();
            Assert.Equal("width:400px", cut.FindAll("colgroup col")[0].GetAttribute("style"));

            // A fit of a grid with nothing frozen does not render: the script already wrote the widths
            // to the page, and there is no inset composed here that would go stale. So the next render
            // is what shows the server agreeing with what it was told - which is the property that
            // matters, since a server that re-derived the width would drift from the page.
            //
            // This one is asked for rather than automatic, so it does take the column back: a fit that
            // visibly did nothing to the column under the pointer is the worse answer.
            cut.InvokeAsync(() => cut.Instance.AutoFitAsync()).Wait();
            cut.Render();
            Assert.Equal("width:60px", cut.FindAll("colgroup col")[0].GetAttribute("style"));

            cut.InvokeAsync(() => cut.Instance.OnColumnsResized(0, 500, new[] { 500d, 0, 0 })).Wait();
            Assert.Equal("width:500px", cut.FindAll("colgroup col")[0].GetAttribute("style"));
        }

        [Fact]
        public void AFittedWidthIsNotCapturedIntoTheSettings()
        {
            // A drag is a choice a user made; a fit is derived from data that will not be the same data
            // next time, and restoring one computed against a different result set is worse than
            // measuring again.
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<string[]>("autoFit", _ => true).SetResult(new[] { "60px", "70px", null });

            FastGridSettings settings = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand);
                p.Add(g => g.SettingsChanged, EventCallback.Factory
                    .Create<FastGridSettings>(this, s => settings = s));
            });

            cut.InvokeAsync(() => cut.Instance.AutoFitAsync()).Wait();
            cut.Find("thead th div").Click();

            Assert.NotNull(settings);
            Assert.All(settings.Columns, c => Assert.Null(c.Width));
        }

        [Fact]
        public void AFitThatLandsInADifferentViewIsThrownAway()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            var planned = module.Setup<string[]>("autoFit", _ => true);

            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand));

            var fit = cut.InvokeAsync(() => cut.Instance.AutoFitAsync());

            // The rows it measured are not the rows the grid is showing by the time it answers.
            cut.Find("thead th div").Click();

            planned.SetResult(new[] { "60px", "70px", null });
            fit.Wait();

            Assert.All(cut.FindAll("colgroup col"), col => Assert.Null(col.GetAttribute("style")));
        }
    }
}
