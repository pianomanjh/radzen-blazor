using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// That the grid still emits what its own script selects for.
    /// </summary>
    /// <remarks>
    /// This is the seam's undeclared half. The script reaches the markup by nine names that appear in
    /// no signature on either side - <c>data-r</c>, <c>rz-data-row</c>, <c>:scope &gt; colgroup</c> and
    /// the rest - and until <see cref="BrowserContract" /> existed there was no list of them anywhere.
    /// A rename in <c>RadzenFastGrid.cs</c> broke the browser and nothing in C# noticed.
    /// <para>
    /// What these do is small and worth being exact about: they assert the markup carries each name,
    /// which catches a rename on the C# side. They cannot catch a rename on the script's side, because
    /// nothing here reads the script. That half is what <c>GeometryParityTests</c> is for, and it
    /// covers fitting and not clicking or the cursor.
    /// </para>
    /// </remarks>
    public class FastGridBrowserContractTests
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        // How a test finds the view. Not part of BrowserContract: the script is handed the view's id
        // and never selects it by class, so a constant for it there would be a name in a list of
        // shared names that is not shared.
        const string ViewSelector = ".rz-data-grid-data";

        /// <summary>
        /// A grid whose listener attached, which is what a browser does and what a loose double does
        /// not: an unanswered <c>attach</c> is <c>false</c>, and the grid then falls back to per-cell
        /// handlers and stops addressing its rows at all. Two of the names below only exist on the
        /// delegated path, so without this they are absent for the right reason and the test reads as
        /// though the markup had lost them.
        /// </summary>
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule(ModulePath).Setup<bool>("attach", _ => true).SetResult(true);

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, int>(x => x.Id, title: "Id")));
                extra?.Invoke(p);
            });
        }

        // A row index on every drawn row, which is the only thing that makes a delegated click
        // resolvable: the script reads `closest('tr[data-r]')` and parses this.
        [Fact]
        public void EveryDrawnRowCarriesTheIndexTheScriptResolvesAClickBy()
        {
            using var ctx = new TestContext();

            // RowClick is one of the callbacks that make the grid address its rows rather than bind a
            // delegate to each one - RowsAreAddressed is what decides the attribute is worth its frame.
            var cut = Render(ctx, p => p.Add(g => g.RowClick, _ => { }));

            var rows = cut.FindAll($"tbody tr[{BrowserContract.RowIndexAttribute}]");

            Assert.Equal(People.Sample().Count, rows.Count);
            Assert.Equal(
                Enumerable.Range(0, rows.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)),
                rows.Select(row => row.GetAttribute(BrowserContract.RowIndexAttribute)));
        }

        // What the cursor counts when the rows have no index of their own - the virtualized path draws
        // a spacer row carrying no class at all, so the script asks for the nth of these.
        [Fact]
        public void EveryDrawnRowCarriesTheClassTheCursorCountsBy()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal(People.Sample().Count,
                cut.FindAll($"tbody tr.{BrowserContract.DataRowClass}").Count);
        }

        // The span a fit measures. On the cell rather than on the td, which the theme's padding rules
        // are the reason for - and the script measures the span, not the cell.
        [Fact]
        public void ACellsTextIsInTheSpanAFitMeasures()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.NotEmpty(cut.FindAll($"tbody td .{BrowserContract.CellDataClass}"));
        }

        // The heading a fit measures to give a column its floor.
        [Fact]
        public void AHeadingIsInTheElementAFitMeasuresItBy()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.NotEmpty(cut.FindAll($"thead .{BrowserContract.ColumnTitleClass}"));
        }

        // The element the key guard binds to, and the one the cursor is measured inside. Its id is what
        // every navigation call is addressed to.
        [Fact]
        public void TheViewCarriesTheClassAndTheIdNavigationIsAddressedTo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowKeyboardNavigation, true));

            var view = cut.Find(ViewSelector);

            Assert.Equal(cut.Instance.ViewElementId, view.GetAttribute("id"));
        }

        // A fit reaches the table as a direct child of the view and writes into a colgroup that is a
        // direct child of the table. Both paths are `:scope > ...`, so an element in between breaks
        // the fit without breaking anything a looser selector would notice.
        [Fact]
        public void AFittableGridPutsTheTableAndTheColgroupWhereTheScriptLooks()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand));

            var view = cut.Find(ViewSelector);
            var table = view.QuerySelector(BrowserContract.TablePath);

            Assert.NotNull(table);
            Assert.Equal(cut.Instance.TableElementId, table!.GetAttribute("id"));
            Assert.NotNull(table.QuerySelector(BrowserContract.ColgroupPath));

            // The head row a fit measures headings in and the body it counts rows in, both reached the
            // same way. A wrapper element around either is invisible to every other test here and
            // stops the script finding them.
            Assert.NotNull(table.QuerySelector(BrowserContract.HeadRowPath));
            Assert.NotNull(table.QuerySelector(BrowserContract.BodyPath));
        }

        // --- the two sides of the one argument that crosses as an object ------------------------

        // `autoFit` takes one object now, which removes the positional coupling and puts a naming one
        // in its place: the record's properties are serialized camelCase and the script destructures
        // by name, so a C# rename is silent unless something compares the two. Nothing else does -
        // every bUnit test hands the record over in process without serializing it, and the geometry
        // harness builds its own object literals in JavaScript.
        //
        // So this reads the script. It is the only test here that looks at both sides of the seam at
        // once, and it is the only way a C# test can catch a rename on either.
        [Fact]
        public void TheFitAskHasExactlyTheFieldsTheScriptTakesOutOfIt()
        {
            var ask = new AutoFitAsk("t", new[] { 0 }, new string?[] { null }, new string?[] { null },
                0, -1, false, false, "fit", new[] { false });

            // The options JS interop serializes with. What reaches the script is these names.
            var sent = JsonDocument.Parse(JsonSerializer.Serialize(ask,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                .RootElement.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();

            // Both directions, and that is the point of comparing sets rather than looking each one up:
            // a field C# sends that the script does not take out is dead weight travelling every fit,
            // and a name the script takes out that C# does not send is `undefined` inside a
            // measurement - which is the failure this whole section is about, silent on both sides.
            Assert.Equal(sent, TakenOutOfTheAsk());
        }

        /// <summary>The names the script destructures out of the object it is handed.</summary>
        static string[] TakenOutOfTheAsk()
        {
            var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fastgrid.js"));
            var start = script.IndexOf("export async function autoFit", StringComparison.Ordinal);

            Assert.True(start >= 0, "the script no longer declares autoFit");

            // The destructuring, not the function body's own brace - which is what `IndexOf('{')`
            // finds and is how this first read `const { table` as a field name.
            var open = script.IndexOf("const {", start, StringComparison.Ordinal);
            var close = script.IndexOf("} = ask;", start, StringComparison.Ordinal);

            Assert.True(open > start && close > open,
                "autoFit no longer destructures the ask it is handed");

            return script[(open + "const {".Length)..close]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                // `min: minWidths` renames on the way in; the name that crossed is the one on the left.
                .Select(entry => entry.Split(':', 2)[0].Trim())
                .Where(name => name.Length > 0)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        // The toggle the delegating listener has to leave alone: a click on it is the detail template
        // opening, not a row click.
        [Fact]
        public void TheRowDetailToggleCarriesWhatTheListenerSkipsItBy()
        {
            using var ctx = new TestContext();

            // The attribute is only written on the delegated path, which is what it is for: without
            // delegation the toggle has a handler of its own and does not need recognising.
            var cut = Render(ctx, p =>
            {
                p.Add(g => g.Template,
                    (RenderFragment<Person>)(person => builder => builder.AddContent(0, person.First)));
                p.Add(g => g.RowClick, _ => { });
            });

            Assert.NotEmpty(cut.FindAll($"tbody [{BrowserContract.ToggleAttribute}]"));
        }
    }
}
