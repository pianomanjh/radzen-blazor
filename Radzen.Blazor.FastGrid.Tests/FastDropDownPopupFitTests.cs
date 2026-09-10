using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The popup sizing itself - §29 - with the measurement stubbed. What is testable here is every
    /// decision either side of the browser: whether a fit is asked for at all, what it is told, which
    /// mode the popup's grid is given, and the one argument to <c>Radzen.openPopup</c> that the whole
    /// ordering rests on. The sizing itself is layout, and layout is <c>GeometryParityTests</c>.
    /// </summary>
    public class FastDropDownPopupFitTests
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        static RenderFragment Columns => FastGrid.Tests.Columns.Of(
            FastGrid.Tests.Columns.Property<Person, string>(p => p.First, title: "First"),
            FastGrid.Tests.Columns.Property<Person, string>(p => p.Last, title: "Last"));

        static IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastDropDownDataGrid<Person, object>>>? extra = null,
            RenderFragment? columns = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            // Columns are a parameter of this helper rather than something `extra` sets, because adding
            // ChildContent twice appends rather than replaces - and two copies of the same column are
            // two columns claiming one identity, which §27's throw catches loudly and confusingly.
            return ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, object>>(p =>
            {
                p.Add(d => d.Data, People.Sample());
                p.Add(d => d.ChildContent, columns ?? Columns);
                p.Add(d => d.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                extra?.Invoke(p);
            });
        }

        /// <summary>
        /// The module with <c>fitPopup</c> staged to answer. Loose mode alone is not enough: the call
        /// wants a value back, and the answer is what decides <c>syncWidth</c>.
        /// </summary>
        static BunitJSModuleInterop Module(TestContext ctx, bool sized = true)
        {
            // Here rather than only in Render, because a test that builds its own component - the
            // multi-select one does, for a different TValue - would otherwise trip over
            // `Radzen.openPopup` rather than over whatever it is checking.
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);

            module.Setup<PopupFitResult>("fitPopup", _ => true)
                .SetResult(new PopupFitResult(sized, new string?[] { "80px", "120px" }));

            return module;
        }

        static void Open(IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> cut) =>
            cut.Find(".rz-dropdown").Click();

        static void Close(IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> cut) =>
            cut.Find(".rz-dropdown").Click();

        /// <summary>The call's one argument, as the type it was made with rather than by index.</summary>
        static PopupFitAsk Ask(JSRuntimeInvocation invocation) => (PopupFitAsk)invocation.Arguments[0]!;

        /// <summary>The popup's own half of that argument.</summary>
        static PopupChrome Chrome(JSRuntimeInvocation invocation) => Ask(invocation).Popup;

        /// <summary>What <c>Radzen.openPopup</c> was told about syncing the width.</summary>
        static bool SyncWidth(TestContext ctx) =>
            (bool)ctx.JSInterop.Invocations["Radzen.openPopup"].Last().Arguments[2]!;

        // --- A popup that does not fit --------------------------------------------------------

        [Fact]
        public void APopupThatIsNotSizedAsksTheBrowserForNothing()
        {
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx));

            Assert.Empty(module.Invocations["fitPopup"]);
        }

        [Fact]
        public void APopupThatIsNotSizedStillHasItsWidthSyncedByUpstream()
        {
            // The default is unchanged behaviour, and this is the line that says so: syncWidth has been
            // true since the component was written and stays true for every drop-down that asks for
            // nothing.
            using var ctx = new TestContext();
            Module(ctx);

            Open(Render(ctx));

            Assert.True(SyncWidth(ctx));
        }

        [Fact]
        public void APopupThatIsNotSizedGivesItsGridNoFitEither()
        {
            using var ctx = new TestContext();
            Module(ctx);

            var cut = Render(ctx);
            Open(cut);

            Assert.Equal(AutoFitMode.None,
                cut.FindComponent<RadzenFastGrid<Person>>().Instance.AutoFitColumns);
        }

        // --- The mode the popup's grid is given ------------------------------------------------

        [Theory]
        [InlineData(PopupFit.Columns)]
        [InlineData(PopupFit.Content)]
        public void APopupThatIsSizedGivesItsGridOnDemandRatherThanOnce(PopupFit fit)
        {
            // OnDemand is the mode the render loop does not fire, which leaves the drop-down as the
            // only thing that decides when a fit happens. Once would give the grid a fit of its own,
            // running against a panel this component has not sized yet.
            using var ctx = new TestContext();
            Module(ctx);

            var cut = Render(ctx, p => p.Add(d => d.PopupFit, fit));
            Open(cut);

            var grid = cut.FindComponent<RadzenFastGrid<Person>>().Instance;

            Assert.Equal(AutoFitMode.OnDemand, grid.AutoFitColumns);
            Assert.Equal(AutoFitOverflow.Fit, grid.AutoFitOverflow);
        }

        // --- What the script is told ------------------------------------------------------------

        [Fact]
        public void TheAskNamesThePanelTheControlAndTheWrapper()
        {
            using var ctx = new TestContext();
            var module = Module(ctx);

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.PopupFit, PopupFit.Content);
                p.Add(d => d.MaxRows, 6);
            });

            Open(cut);

            var chrome = Chrome(module.Invocations["fitPopup"].Single());
            var root = cut.Find(".rz-dropdown").Id;

            Assert.Equal(root, chrome.Control);
            Assert.Equal(root + "-popup", chrome.Panel);
            Assert.Equal(root + "-rows", chrome.Wrapper);
        }

        [Fact]
        public void OnlyContentGrows()
        {
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Columns)));

            Assert.False(Chrome(module.Invocations["fitPopup"].Single()).Grow);
        }

        [Fact]
        public void ContentGrows()
        {
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content)));

            Assert.True(Chrome(module.Invocations["fitPopup"].Single()).Grow);
        }

        [Fact]
        public void TheDeclaredWidthCrossesAsAuthoredRatherThanAsPixels()
        {
            // PopupWidth is authored CSS and may be in any unit. Parsing it here would work for pixels
            // and be quietly wrong for everything else, which is why the browser resolves it.
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p =>
            {
                p.Add(d => d.PopupFit, PopupFit.Content);
                p.Add(d => d.PopupWidth, "40rem");
            }));

            Assert.Equal("40rem", Chrome(module.Invocations["fitPopup"].Single()).Width);
        }

        [Fact]
        public void MaxRowsCrossesAsACount()
        {
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p =>
            {
                p.Add(d => d.PopupFit, PopupFit.Content);
                p.Add(d => d.MaxRows, 9);
            }));

            Assert.Equal(9, Chrome(module.Invocations["fitPopup"].Single()).MaxRows);
        }

        [Fact]
        public void TheColumnsTheGridWouldHaveFittedAreCarriedInTheSameAsk()
        {
            // One call rather than a sizing call and a fitting one: the panel has to carry its final
            // width before openPopup measures it, and two round trips would put a render between them
            // for the popup to be seen at the first.
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content)));

            var fit = Ask(module.Invocations["fitPopup"].Single()).Fit;

            Assert.Equal(2, fit.Indices.Count);
            Assert.Equal("fit", fit.Overflow);

            // Not animated. Columns settling inside a panel that is itself appearing are two motions
            // reading as one glitch, and the animated path is also the one that replaces user widths.
            Assert.False(fit.Animate);
        }

        // --- The argument the ordering rests on -------------------------------------------------

        [Fact]
        public void SyncWidthIsOffWhenThePanelWasSized()
        {
            using var ctx = new TestContext();
            Module(ctx, sized: true);

            Open(Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content)));

            Assert.False(SyncWidth(ctx));
        }

        [Fact]
        public void SyncWidthStaysOnWhenNothingWasSized()
        {
            // A popup whose script never loaded has been sized by nothing, and must still get the width
            // upstream would have given it rather than shrinking to fit. The mode asking for a fit is
            // not the same as a fit having happened.
            using var ctx = new TestContext();
            Module(ctx, sized: false);

            Open(Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content)));

            Assert.True(SyncWidth(ctx));
        }

        [Fact]
        public void AMultipleSelectPopupIsSizedThroughTheSamePath()
        {
            // Multi-select varies exactly one thing - which panel class is emitted - and §29 inferred
            // the rest rather than asking. This is the C# half of closing that: the ask still names the
            // panel, and the panel is the multi-select one.
            using var ctx = new TestContext();
            var module = Module(ctx);

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(d => d.Data, People.Sample());
                p.Add(d => d.ChildContent, Columns);
                p.Add(d => d.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(d => d.Multiple, true);
                p.Add(d => d.PopupFit, PopupFit.Content);
            });

            cut.Find(".rz-dropdown").Click();

            var chrome = (PopupFitAsk)module.Invocations["fitPopup"].Single().Arguments[0]!;

            Assert.Equal(cut.Find(".rz-multiselect-panel").Id, chrome.Popup.Panel);
            Assert.False(SyncWidth(ctx));
        }

        // --- Once per open ----------------------------------------------------------------------

        [Fact]
        public void TheFitRunsAgainOnEveryOpen()
        {
            // Not once per built grid. Under Content a stale fit is a stale panel width - persistent,
            // visible chrome that is wrong until something re-runs it.
            using var ctx = new TestContext();
            var module = Module(ctx);

            var cut = Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content));

            Open(cut);
            Close(cut);
            Open(cut);

            Assert.Equal(2, module.Invocations["fitPopup"].Count);
        }

        // --- The wrapper --------------------------------------------------------------------------

        [Fact]
        public void TheWrapperIsBoundedWhenMaxRowsAsksForIt()
        {
            // The script replaces the height it writes here, so this is what a popup falls back to when
            // there is no script to replace it.
            using var ctx = new TestContext();
            Module(ctx);

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.PopupFit, PopupFit.Content);
                p.Add(d => d.MaxRows, 6);
            });

            Open(cut);

            Assert.Contains("overflow:auto", cut.Find("[id$='-rows']").GetAttribute("style"));
        }

        [Fact]
        public void APopupWhoseColumnsAllDeclareAWidthIsStillSized()
        {
            // The panel is the popup's, not the columns'. A lookup whose columns all carry a Width is
            // an ordinary shape - CanAutoFit excludes every one of them - and the grid's own "nothing
            // to fit, nothing to do" answer would take the panel width, the MaxRows height and the
            // syncWidth decision down with it, silently.
            using var ctx = new TestContext();
            var module = Module(ctx);

            Open(Render(ctx, p =>
            {
                p.Add(d => d.PopupFit, PopupFit.Content);
                p.Add(d => d.MaxRows, 6);
            },
            FastGrid.Tests.Columns.Of(
                FastGrid.Tests.Columns.Property<Person, string>(x => x.First, title: "First", width: "100px"),
                FastGrid.Tests.Columns.Property<Person, string>(x => x.Last, title: "Last", width: "100px"))));

            Assert.Single(module.Invocations["fitPopup"]);
            Assert.False(SyncWidth(ctx));
        }

        [Fact]
        public void MaxRowsWithoutAFitBoundsNothing()
        {
            // The other half of the no-op, and the half that was not one: the wrapper is bounded by
            // AllowVirtualization or by a MaxRows that something will measure, and by nothing else.
            // Clamping the popup to PopupHeight instead would apply the very default MaxRows replaces.
            using var ctx = new TestContext();
            Module(ctx);

            var cut = Render(ctx, p => p.Add(d => d.MaxRows, 6));

            Open(cut);

            Assert.True(string.IsNullOrEmpty(cut.Find("[id$='-rows']").GetAttribute("style")));
        }

        [Fact]
        public void TheWrapperIsUnboundedWhenNothingAsksForIt()
        {
            using var ctx = new TestContext();
            Module(ctx);

            var cut = Render(ctx, p => p.Add(d => d.PopupFit, PopupFit.Content));

            Open(cut);

            Assert.True(string.IsNullOrEmpty(cut.Find("[id$='-rows']").GetAttribute("style")));
        }

        [Fact]
        public void MaxRowsWithoutAFitIsANoOpAndSaysSoByAskingForNothing()
        {
            // Documented as a requirement rather than worked around, the way §13 documented OnDemand
            // needing a resize handle: the height is measured, and it is the fit that puts the code in
            // the browser to measure it.
            using var ctx = new TestContext();
            var module = Module(ctx);

            var cut = Render(ctx, p => p.Add(d => d.MaxRows, 6));

            Open(cut);

            Assert.Empty(module.Invocations["fitPopup"]);
            Assert.Equal(AutoFitMode.None,
                cut.FindComponent<RadzenFastGrid<Person>>().Instance.AutoFitColumns);
        }
    }
}
