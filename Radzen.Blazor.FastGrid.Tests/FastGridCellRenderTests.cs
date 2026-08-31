using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The three render hooks, which hand a cell's attributes to the application before it is drawn.
    /// </summary>
    /// <remarks>
    /// <c>CellRender</c> is the only hook on this component that runs per cell rather than per row or
    /// per column, so what these pin hardest is the other half of that: that an unset hook is not
    /// called, allocates nothing, and leaves the markup byte for byte where it was.
    /// </remarks>
    public class FastGridCellRenderTests
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

        static RenderFragment TwoColumnsWithFooters => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First",
                footerTemplate: _ => builder => builder.AddContent(0, "total")),
            Columns.Property<Person, int>(p => p.Id, title: "Id",
                footerTemplate: _ => builder => builder.AddContent(0, "count")));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            RenderFragment columns = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(3));
                p.Add(g => g.ChildContent, columns ?? TwoColumns);
                extra?.Invoke(p);
            });

        static string[] BodyCellAttribute(IRenderedComponent<RadzenFastGrid<Person>> cut, string name) =>
            cut.FindAll("tbody td").Select(td => td.GetAttribute(name)).ToArray();

        // --- body cells -------------------------------------------------------------------------

        [Fact]
        public void CellRenderReachesEveryBodyCell()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.CellRender,
                args => args.Attributes["data-seen"] = "yes"));

            Assert.Equal(6, cut.FindAll("tbody td").Count);
            Assert.All(BodyCellAttribute(cut, "data-seen"), value => Assert.Equal("yes", value));
        }

        [Fact]
        public void CellRenderIsToldWhichRowAndColumn()
        {
            using var ctx = Context();
            var seen = new List<string>();

            Render(ctx, p => p.Add(g => g.CellRender,
                args => seen.Add($"{args.Data.First}/{args.Column.Title}")));

            Assert.Equal(
                new[] { "First1/First", "First1/Id", "First2/First", "First2/Id", "First3/First", "First3/Id" },
                seen);
        }

        // Splatted after the grid's own attributes, which is the only order that lets a hook do the
        // thing hooks exist for.
        [Fact]
        public void CellRenderCanOverrideAnAttributeTheGridWrote()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.CellRender,
                args => args.Attributes["role"] = "presentation"));

            Assert.All(BodyCellAttribute(cut, "role"), value => Assert.Equal("presentation", value));
        }

        // The dictionary is allocated on first touch, so a hook that only looks costs one small object
        // per cell rather than two - and must not put an empty splat on the element either.
        [Fact]
        public void AHookThatAddsNothingChangesNothing()
        {
            using var ctx = Context();
            var calls = 0;

            var withHook = Render(ctx, p => p.Add(g => g.CellRender, _ => calls++));

            using var bare = Context();
            var without = Render(bare);

            Assert.Equal(6, calls);
            Assert.Equal(without.Find("tbody").InnerHtml, withHook.Find("tbody").InnerHtml);
        }

        [Fact]
        public void AnUnsetHookIsNeverConsultedAndLeavesTheMarkupAlone()
        {
            using var ctx = Context();
            using var other = Context();

            var withNullHook = Render(ctx, p => p.Add(g => g.CellRender, null));
            var bare = Render(other);

            Assert.Equal(bare.Find("table").InnerHtml, withNullHook.Find("table").InnerHtml);
        }

        // The toggle is the grid's own control, not a column's cell, so no column could be handed for
        // it - and a hook that had to guard against a null column would be a worse hook.
        [Fact]
        public void CellRenderSkipsTheRowDetailToggleCell()
        {
            using var ctx = Context();
            var calls = 0;

            var cut = Render(ctx, p =>
            {
                p.Add<RenderFragment<Person>>(g => g.Template, person => builder => builder.AddContent(0, "d"));
                p.Add(g => g.CellRender, _ => calls++);
            });

            Assert.Equal(3, cut.FindAll("td.rz-col-icon").Count);
            Assert.Equal(6, calls);
            Assert.All(cut.FindAll("td.rz-col-icon"), td => Assert.Null(td.GetAttribute("data-seen")));
        }

        // --- the reused instance ------------------------------------------------------------------

        // One object for every cell of every render. The rule this buys - read them, do not keep them -
        // is the whole reason the hook is affordable, so it is worth stating in a test as well as in the
        // documentation.
        [Fact]
        public void TheSameArgumentsObjectIsHandedToEveryCell()
        {
            using var ctx = Context();
            var seen = new HashSet<FastGridCellRenderEventArgs<Person>>();

            Render(ctx, p => p.Add(g => g.CellRender, args => seen.Add(args)));

            Assert.Single(seen);
        }

        // The failure mode reuse invites: a cell that writes nothing must not inherit what the last one
        // wrote. Only the Id column is marked here, so the First column must come back clean.
        [Fact]
        public void AttributesDoNotLeakFromOneCellToTheNext()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.CellRender, args =>
            {
                if (args.Column.Title == "Id")
                {
                    args.Attributes["data-marked"] = "yes";
                }
            }));

            Assert.Equal(new[] { null, "yes", null, "yes", null, "yes" },
                BodyCellAttribute(cut, "data-marked"));
        }

        // The clearing happens before the handler runs rather than after the splat, so every cell sees an
        // empty set on entry whatever the last one did - including the first cell, and including one
        // whose predecessor left the handler early.
        [Fact]
        public void EveryCellStartsWithAnEmptyAttributeSet()
        {
            using var ctx = Context();
            var emptyOnEntry = new List<bool>();

            Render(ctx, p => p.Add(g => g.CellRender, args =>
            {
                emptyOnEntry.Add(args.Attributes.Count == 0);

                args.Attributes["data-x"] = args.Column.Title;
            }));

            Assert.Equal(6, emptyOnEntry.Count);
            Assert.All(emptyOnEntry, Assert.True);
        }

        // A handler is application code running inside a render, and a bug in it is the application's to
        // see. Swallowing it here would turn a broken hook into a grid that silently draws the wrong
        // attributes.
        [Fact]
        public void AHandlerThatThrowsIsNotSwallowed()
        {
            using var ctx = Context();

            var thrown = Assert.Throws<InvalidOperationException>(() =>
                Render(ctx, p => p.Add(g => g.CellRender,
                    _ => throw new InvalidOperationException("from the handler"))));

            Assert.Equal("from the handler", thrown.Message);
        }

        // The header row is drawn before the body and the footer after it, so all three hooks can share
        // one object - but only if the body's cells start from a clean sheet.
        [Fact]
        public void TheHeaderHooksWritesDoNotReachTheBody()
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.HeaderCellRender, args => args.Attributes["data-where"] = "header");
                p.Add(g => g.CellRender, _ => { });
            });

            Assert.All(BodyCellAttribute(cut, "data-where"), Assert.Null);
            Assert.All(cut.FindAll("thead th"),
                th => Assert.Equal("header", th.GetAttribute("data-where")));
        }

        // --- what the arguments cost --------------------------------------------------------------

        // The dictionary behind Attributes is allocated on first touch, and the splat is skipped when
        // nothing was written. Neither is visible in the markup - an empty splat renders as nothing -
        // so the only way to hold the line is to assert on the object itself.
        [Fact]
        public void ArgumentsThatWereOnlyReadCarryNothingToSplat()
        {
            using var ctx = Context();
            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            var args = new FastGridCellRenderEventArgs<Person>(People.Many(1)[0], column);

            Assert.Null(args.Written);
        }

        // Touched but not written to. The dictionary exists by now, and splatting it would still be an
        // empty splat on every cell of every row.
        [Fact]
        public void ArgumentsTouchedButLeftEmptyCarryNothingToSplat()
        {
            using var ctx = Context();
            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            var args = new FastGridCellRenderEventArgs<Person>(People.Many(1)[0], column);

            Assert.Empty(args.Attributes);
            Assert.Null(args.Written);
        }

        [Fact]
        public void ArgumentsWrittenToCarryExactlyWhatWasWritten()
        {
            using var ctx = Context();
            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            var args = new FastGridCellRenderEventArgs<Person>(People.Many(1)[0], column);

            args.Attributes["data-x"] = "1";

            Assert.Same(args.Attributes, args.Written);
            Assert.Equal("1", args.Written["data-x"]);
        }

        // --- header cells -----------------------------------------------------------------------

        [Fact]
        public void HeaderCellRenderReachesEveryHeaderCellOnce()
        {
            using var ctx = Context();
            var seen = new List<string>();

            var cut = Render(ctx, p => p.Add(g => g.HeaderCellRender, args =>
            {
                seen.Add(args.Column.Title);
                args.Attributes["data-header"] = args.Column.Title;
            }));

            Assert.Equal(new[] { "First", "Id" }, seen);
            Assert.Equal(new[] { "First", "Id" },
                cut.FindAll("thead th").Select(th => th.GetAttribute("data-header")).ToArray());
        }

        // A header cell belongs to a column, not to a row, so there is no row to hand over.
        [Fact]
        public void HeaderCellRenderGetsNoRow()
        {
            using var ctx = Context();
            Person seen = new Person();

            Render(ctx, p => p.Add(g => g.HeaderCellRender, args => seen = args.Data));

            Assert.Null(seen);
        }

        // --- footer cells -----------------------------------------------------------------------

        [Fact]
        public void FooterCellRenderReachesEveryFooterCell()
        {
            using var ctx = Context();

            var cut = Render(ctx,
                p => p.Add(g => g.FooterCellRender, args => args.Attributes["data-footer"] = args.Column.Title),
                TwoColumnsWithFooters);

            Assert.Equal(new[] { "First", "Id" },
                cut.FindAll("tfoot td").Select(td => td.GetAttribute("data-footer")).ToArray());
        }

        // No footer template, no footer row - so the hook has nothing to be called for.
        [Fact]
        public void FooterCellRenderIsNotCalledWithoutAFooter()
        {
            using var ctx = Context();
            var calls = 0;

            var cut = Render(ctx, p => p.Add(g => g.FooterCellRender, _ => calls++));

            Assert.Empty(cut.FindAll("tfoot"));
            Assert.Equal(0, calls);
        }

        // The three are independent: one set does not drag the others in.
        [Fact]
        public void EachHookIsCalledOnlyForItsOwnCells()
        {
            using var ctx = Context();
            var body = 0;
            var header = 0;
            var footer = 0;

            Render(ctx, p =>
            {
                p.Add(g => g.CellRender, _ => body++);
                p.Add(g => g.HeaderCellRender, _ => header++);
                p.Add(g => g.FooterCellRender, _ => footer++);
            }, TwoColumnsWithFooters);

            Assert.Equal(6, body);
            Assert.Equal(2, header);
            Assert.Equal(2, footer);
        }
    }
}
