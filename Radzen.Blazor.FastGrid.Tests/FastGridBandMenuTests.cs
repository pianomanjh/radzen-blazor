using System;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §39's band half: when the band is drawn, what is in it, and what the one entry does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These do not settle whether it looks like one control, and they are not meant to.</strong>
    /// §39 said so in advance - <em>"a test asserting the menu renders is not a test that it looks like
    /// one control"</em> - and the section was right twice over. The band's height, the trigger's
    /// contrast and the panel's own chrome were all measured in a browser, and one of the three found a
    /// fault nothing here could see: <c>rz-menuitem-link</c> on a <c>button</c> left the user agent's
    /// own control showing, border and all, in a themed panel. What these cover is the logic around it.
    /// </para>
    /// <para>
    /// The band, the trigger and the chip list are found by class because that is what a consumer
    /// restyles and what §37 named them for.
    /// </para>
    /// </remarks>
    public class FastGridBandMenuTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            bool menu = true, bool pills = false,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ShowGridMenu, menu);
                p.Add(g => g.ShowFilterPills, pills);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First"),
                    Columns.Property<Person, string>(c => c.Last, title: "Last"),
                    Columns.Property<Person, decimal>(c => c.Salary, title: "Salary")));
                extra?.Invoke(p);
            });
        }

        // --- when the band is drawn ------------------------------------------------------------

        [Fact]
        public void NoMenuAndNoFilterDrawsNoBand()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, menu: false, pills: true);

            Assert.Empty(cut.FindAll(".rz-filter-pills"));
        }

        /// <summary>
        /// §37's rule was <em>nothing when nothing is filtered</em>, and the menu is the second reason.
        /// </summary>
        /// <remarks>
        /// This is the case §39 flagged as the one that could still be wrong - a menu makes the band
        /// unconditional in practice - and it is deliberately a parameter for that reason. Measured in
        /// the playground the band costs 53px in this state, so a grid that has not asked for the menu
        /// must still get nothing, which is the test above.
        /// </remarks>
        [Fact]
        public void AMenuDrawsTheBandWithNothingFiltered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Single(cut.FindAll(".rz-filter-pills"));
            Assert.Single(cut.FindAll(".rz-filter-pills .rz-menu-toggle"));
        }

        /// <summary>An empty list with an accessible name is worse than no list.</summary>
        [Fact]
        public void AMenuWithNothingFilteredWritesNoChipList()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, pills: true);

            Assert.Single(cut.FindAll(".rz-filter-pills"));
            Assert.Empty(cut.FindAll(".rz-filter-pills .rz-chip-list"));
        }

        [Fact]
        public async Task AFilterAndAMenuShareOneBand()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, pills: true);

            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns[0],
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Contains, "a"))));

            // One band, holding both. Two bands is the 136px arrangement §37b measured and §39 chose
            // this shape to avoid; a second .rz-filter-pills is what that would look like from here.
            Assert.Single(cut.FindAll(".rz-filter-pills"));
            Assert.Single(cut.FindAll(".rz-filter-pills .rz-chip-list"));
            Assert.Single(cut.FindAll(".rz-filter-pills .rz-menu-toggle"));
        }

        // --- the trigger -----------------------------------------------------------------------

        /// <summary>
        /// The trigger names the panel it opens, and the panel answers to that name.
        /// </summary>
        /// <remarks>
        /// Not decoration: <c>Radzen.setPopupAriaExpanded</c> looks the anchor up <em>by</em>
        /// <c>aria-controls</c> and does nothing at all when it is missing, which is the fault §35's
        /// review found on the filter icon.
        /// </remarks>
        [Fact]
        public void TheTriggerNamesThePanelAndSaysItIsClosed()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var trigger = cut.Find(".rz-filter-pills .rz-menu-toggle");

            Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
            Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
            Assert.Equal(cut.Instance.GridMenuElementId, trigger.GetAttribute("aria-controls"));
            Assert.Equal(cut.Instance.GridMenuText, trigger.GetAttribute("aria-label"));
        }

        /// <summary>
        /// The trigger says the menu is open while it is, and closed again after.
        /// </summary>
        /// <remarks>
        /// <strong>Only a test can see this, which is why it is one.</strong> The first draft left
        /// <c>RadzenPopup.Close</c> off the panel, so the close never redrew the grid and the render
        /// tree kept saying <c>"true"</c> - and a browser looked perfectly correct throughout, because
        /// <c>Radzen.setPopupAriaExpanded</c> writes that attribute into the DOM itself. §35 wrote the
        /// rule: two writers agreeing by luck is still one of them being wrong. Nothing here runs
        /// upstream's JavaScript, so this reads the writer that was wrong.
        /// </remarks>
        [Fact]
        public async Task TheTriggerTracksWhetherTheMenuIsOpen()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal("false", cut.Find(".rz-filter-pills .rz-menu-toggle").GetAttribute("aria-expanded"));

            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            Assert.Equal("true", cut.Find(".rz-filter-pills .rz-menu-toggle").GetAttribute("aria-expanded"));

            // Through the popup's own OnClose, because that is the method the browser calls: a click
            // outside reaches JavaScript, which invokes it back. Calling CloseAsync here would only
            // reach a JavaScript double that does nothing, and the popup would still believe it is
            // open - so the close this asserts would not be the close that happens.
            var popup = cut.FindComponent<Radzen.Blazor.RadzenPopup>();
            await cut.InvokeAsync(() => popup.Instance.OnClose());

            Assert.Equal("false", cut.Find(".rz-filter-pills .rz-menu-toggle").GetAttribute("aria-expanded"));
        }

        /// <summary>
        /// The glyph is not the drag handle's.
        /// </summary>
        /// <remarks>
        /// The themes draw the column drag handle with <c>.rz-column-drag:after { content: "more_vert" }</c>,
        /// so a vertical kebab already means "pick this column up" one band away in this same component.
        /// A test rather than a comment because the two are only a word apart and nothing else would
        /// notice them converging.
        /// </remarks>
        [Fact]
        public void TheTriggerDoesNotWearTheDragHandlesGlyph()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal("more_horiz", cut.Find(".rz-filter-pills .rz-menu-toggle i").TextContent.Trim());
        }

        /// <summary>
        /// The band is a row when the menu is in it, which is the whole reason it stays 67px.
        /// </summary>
        /// <remarks>
        /// The one assertion here that is about pixels, made where pixels cannot be. Measured in the
        /// playground: as a block sibling of the chip list inside the block-level band the trigger wraps
        /// to a second line and the band grows to 86.5px, 88px or 103px depending on the control; as a
        /// flex item it stays at the 67px the bar costs without it. So <c>display:flex</c> is not
        /// decoration and losing it is silent - the menu still renders, still opens, and every other
        /// test here still passes. It survived the mutation that deleted it until this was written.
        /// <c>Contains</c> rather than the whole string, so reordering the declaration does not fail.
        /// </remarks>
        [Fact]
        public void AMenuLaysTheBandOutAsARow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Contains("display:flex", cut.Find(".rz-filter-pills").GetAttribute("style"));
            Assert.Contains("margin-inline-start:auto",
                cut.Find(".rz-filter-pills .rz-menu-toggle").GetAttribute("style"));
        }

        /// <summary>The band is only laid out as a row when there is something to put at its end.</summary>
        /// <remarks>
        /// An inline style is the one thing a consumer's own rule cannot override without
        /// <c>!important</c>, and §37 named this band so that it could be restyled. A grid using the
        /// pills alone keeps the block band it has always had.
        /// </remarks>
        [Fact]
        public async Task PillsWithoutAMenuLeaveTheBandUnstyled()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, menu: false, pills: true);

            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns[0],
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Contains, "a"))));

            Assert.Null(cut.Find(".rz-filter-pills").GetAttribute("style"));
        }

        // --- the entry -------------------------------------------------------------------------

        /// <summary>The panel is not written until something asks for it.</summary>
        /// <remarks>
        /// §29's rule, and the reason the grid holds one panel rather than one per anything: a menu
        /// nobody has opened costs the render tree nothing.
        /// </remarks>
        [Fact]
        public void ThePanelIsNotBuiltUntilTheMenuIsOpened()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll("#" + cut.Instance.GridMenuElementId));

            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            Assert.Single(cut.FindAll("#" + cut.Instance.GridMenuElementId));
        }

        /// <summary>
        /// With nothing registered to export, the menu holds one entry.
        /// </summary>
        /// <remarks>
        /// The half of the export seam that costs an application nothing: a grid whose host never
        /// referenced the export package resolves no <c>IFastGridExporter</c> and draws no entry for it.
        /// </remarks>
        [Fact]
        public void TheMenuOffersResettingTheLayoutAndNothingElse()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            var items = cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]");

            Assert.Single(items);
            Assert.Contains(cut.Instance.ResetLayoutText, items[0].TextContent);
        }

        /// <summary>
        /// An exporter in the service provider puts a second entry in the menu.
        /// </summary>
        /// <remarks>
        /// <strong>This is how §39's menu and §40's package are reconciled.</strong> They could not both
        /// have what they asked for: the menu is drawn by the core package, <c>ToWorkbook</c> lives in
        /// the export package, and the export package references the core rather than the other way
        /// round - because reversing that roots <c>XlsxWriter</c> in every consumer, measured at 400 KB
        /// over the wire. The service provider carries it across instead, and the seam itself costs
        /// nothing: publishing the trim test with and without <c>IFastGridExporter</c> in the core gave
        /// byte-identical output.
        /// </remarks>
        [Fact]
        public void ARegisteredExporterAddsAnEntry()
        {
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridExporter>(new Exporter());

            var cut = Render(ctx);
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            var items = cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]");

            Assert.Equal(2, items.Count);
            Assert.Contains(cut.Instance.ResetLayoutText, items[0].TextContent);
            Assert.Contains(cut.Instance.ExportText, items[1].TextContent);
        }

        [Fact]
        public void TheEntryIsClickedAndTheExporterIsHandedTheGrid()
        {
            using var ctx = new TestContext();

            var exporter = new Exporter();

            ctx.Services.AddSingleton<IFastGridExporter>(exporter);

            var cut = Render(ctx);
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]")[1].Click();

            Assert.Same(cut.Instance, exporter.Exported);
        }

        /// <summary>
        /// The grid names the entry, and whatever is registered cannot.
        /// </summary>
        /// <remarks>
        /// <strong>This is the line §39 drew, and the first draft crossed it.</strong>
        /// <c>IFastGridExporter</c> had a <c>Text</c> of its own, which meant a registration could label
        /// the entry anything - <em>Send to SAP</em> - and at that point the interface is a one-slot
        /// menu extension point wearing a suggestive name, which is what §39 refused: <em>"an
        /// application that wants its own actions in the band is asking for a HeaderTemplate"</em>. The
        /// property is gone. What remains is one verb the grid names, through a parameter that is
        /// localized like every other word it draws and settable per grid.
        /// </remarks>
        [Fact]
        public void TheGridNamesTheEntryAndTheExporterCannot()
        {
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridExporter>(new Exporter());

            var cut = Render(ctx, extra: p => p.Add(g => g.ExportText, "Save as a spreadsheet"));
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            var items = cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]");

            Assert.Contains("Save as a spreadsheet", items[1].TextContent);

            // And there is nowhere on the interface to say otherwise.
            Assert.Empty(typeof(IFastGridExporter).GetProperties());
        }

        /// <summary>
        /// The menu is told to shut before the export starts, not after it finishes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An export of any size blocks - measured at about four seconds for 50,000 rows - and a menu
        /// left open over a frozen page is worse than one that shut before the wait began. Asserted from
        /// inside the exporter, because reading it afterwards cannot tell "closed first" from "closed
        /// eventually".
        /// </para>
        /// <para>
        /// <strong>It counts the call to <c>Radzen.closePopup</c> rather than reading the popup's own
        /// <c>IsOpen</c>, and the first draft did the latter and could not fail.</strong>
        /// <c>RadzenPopup.CloseAsync</c> only asks JavaScript to close; <c>IsOpen</c> flips when
        /// JavaScript invokes <c>OnClose</c> back. Nothing here runs upstream's JavaScript, so the flag
        /// stays true through a close that did happen - which is the doubled-module blindness §39 met
        /// from the other direction.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheMenuIsToldToShutBeforeTheExportRuns()
        {
            using var ctx = new TestContext();

            var closedWhenCalled = -1;

            ctx.Services.AddSingleton<IFastGridExporter>(new Exporter
            {
                OnExport = () => closedWhenCalled = ctx.JSInterop.Invocations["Radzen.closePopup"].Count,
            });

            var cut = Render(ctx);
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]")[1].Click();

            cut.WaitForAssertion(() => Assert.True(closedWhenCalled > 0,
                $"the popup had been asked to close {closedWhenCalled} times when the export ran"));
        }

        /// <summary>
        /// A grid can decline the export entry without declining the menu.
        /// </summary>
        /// <remarks>
        /// Registering enables the entry everywhere, which is the point of registering. Before this a
        /// grid that should not offer it could only decline <see cref="RadzenFastGrid{TItem}.ShowGridMenu" />,
        /// which took <em>Reset layout</em> with it.
        /// </remarks>
        [Fact]
        public void AGridCanDeclineTheExportAndKeepTheMenu()
        {
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridExporter>(new Exporter());

            var cut = Render(ctx, extra: p => p.Add(g => g.ShowExport, false));
            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();

            var items = cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]");

            Assert.Single(items);
            Assert.Contains(cut.Instance.ResetLayoutText, items[0].TextContent);
        }

        /// <summary>
        /// The grid shows its loading scrim while an export runs.
        /// </summary>
        /// <remarks>
        /// An export of a large grid takes over a second - 1.74 s at 50,000 rows - and a page that looks
        /// idle through it invites a second click. Asserted from inside the exporter, because by the time
        /// the call returns the scrim is gone again.
        /// </remarks>
        [Fact]
        public void TheGridLooksBusyWhileTheExportRuns()
        {
            using var ctx = new TestContext();

            var cut = default(IRenderedComponent<RadzenFastGrid<Person>>);
            var scrimWhileRunning = 0;

            ctx.Services.AddSingleton<IFastGridExporter>(new Exporter
            {
                OnExport = () => scrimWhileRunning = cut.FindAll(".rz-datatable-loading").Count,
            });

            cut = Render(ctx, extra: p => p.Add(g => g.ShowLoadingIndicator, true));

            Assert.Empty(cut.FindAll(".rz-datatable-loading"));

            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();
            cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]")[1].Click();

            cut.WaitForAssertion(() => Assert.Equal(1, scrimWhileRunning));

            // And gone again afterwards.
            Assert.Empty(cut.FindAll(".rz-datatable-loading"));
        }

        sealed class Exporter : IFastGridExporter
        {
            public object Exported { get; private set; }

            public Action OnExport { get; set; }

            // Yields before doing anything, which is what a real exporter does - it writes a file and
            // talks to the browser. Without the yield the await never suspends, the render queued by
            // the caller's StateHasChanged never runs, and anything asserted from in here sees the
            // state as it was before the click. §39's doubled-module blindness, one layer out.
            public async Task ExportAsync<TItem>(RadzenFastGrid<TItem> grid)
            {
                Exported = grid;

                await Task.Yield();

                OnExport?.Invoke();
            }
        }

        /// <summary>
        /// The entry resets the layout, and the arms move.
        /// </summary>
        /// <remarks>
        /// A width and a visibility, because those are the two a reset restores by clearing an override
        /// rather than by computing anything - which is the rule §39 wrote for the reset and the thing
        /// an edit could most easily break. Both are checked to have actually changed first: a reset
        /// that is asserted against a grid already in its declared state cannot fail.
        /// </remarks>
        [Fact]
        public async Task TheEntryPutsTheLayoutBack()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            await cut.InvokeAsync(() => cut.Instance.OnColumnResized(0, 321));
            await cut.InvokeAsync(() => cut.Instance.VisibleColumns[1].SetPicked(false));
            cut.Render();

            Assert.Equal("321px", cut.Instance.VisibleColumns[0].EffectiveWidth);
            Assert.Equal(new[] { "First", "Salary" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());

            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();
            await cut.InvokeAsync(() =>
                cut.Find("#" + cut.Instance.GridMenuElementId + " [role=menuitem]").Click());
            cut.Render();

            Assert.Null(cut.Instance.VisibleColumns[0].EffectiveWidth);
            Assert.Equal(new[] { "First", "Last", "Salary" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());
        }

        /// <summary>The band survives its own reset, because the menu is why it is there.</summary>
        /// <remarks>
        /// A reset clears every filter, so on a grid whose band was drawn for the pills alone the band
        /// goes with them. With a menu it must not - and a reset that removed the control it was invoked
        /// from would leave no way to invoke it again.
        /// </remarks>
        [Fact]
        public async Task TheBandOutlivesAResetThatClearsTheFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, pills: true);

            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns[0],
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Contains, "a"))));

            Assert.Single(cut.FindAll(".rz-filter-pills .rz-chip-list"));

            await cut.InvokeAsync(() => cut.Instance.ClearSettings());
            cut.Render();

            Assert.Empty(cut.FindAll(".rz-filter-pills .rz-chip-list"));
            Assert.Single(cut.FindAll(".rz-filter-pills .rz-menu-toggle"));
        }
    }
}
