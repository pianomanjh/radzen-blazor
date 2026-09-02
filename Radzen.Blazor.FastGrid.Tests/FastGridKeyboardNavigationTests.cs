using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Where the cursor goes for each key, and what Enter and Space do when it gets there.
    /// </summary>
    /// <remarks>
    /// These can be written at all because the algorithm is in C# rather than in the script. What the
    /// browser is asked for is the paint, the writing direction and the height of the viewport in rows;
    /// none of those decide anything, so a test host with no DOM can drive every rule the feature has.
    /// The paint is checked in <c>GeometryParityTests</c>, which is the layer that can see it.
    /// </remarks>
    public class FastGridKeyboardNavigationTests
    {
        static RenderFragment TwoColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"));

        static IRenderedComponent<RadzenFastGrid<Person>> Grid(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IList<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, TwoColumns());
                p.Add(g => g.AllowKeyboardNavigation, true);
                extra?.Invoke(p);
            });
        }

        /// <summary>A grid the user has tabbed into, which is the state every key below is pressed in.</summary>
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IList<Person>? data = null)
        {
            var cut = Grid(ctx, extra, data);

            cut.Find(".rz-data-grid-data").Focus();

            return cut;
        }

        static IElement View(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.Find(".rz-data-grid-data");

        static void Press(IRenderedComponent<RadzenFastGrid<Person>> cut, string key,
            bool ctrl = false, bool shift = false) =>
            View(cut).KeyDown(new KeyboardEventArgs { Key = key, CtrlKey = ctrl, ShiftKey = shift });

        // ---- the model ----

        [Fact]
        public void TabbingInLandsOnTheFirstCellOfTheFirstRow()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx);

            Assert.Null(cut.Instance.FocusedCell);

            cut.Find(".rz-data-grid-data").Focus();

            Assert.Equal((0, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void AKeyThatBeatsTheFocusEventEstablishesTheCursorRatherThanMovingIt()
        {
            // The first row is where the cursor goes, not where it starts from.
            using var ctx = new TestContext();

            var cut = Grid(ctx);

            Press(cut, "ArrowDown");

            Assert.Equal((0, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void UpAndDownMoveARowAndLeftAndRightMoveACell()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowDown");
            Press(cut, "ArrowRight");

            Assert.Equal((1, 1), cut.Instance.FocusedCell);

            Press(cut, "ArrowUp");
            Press(cut, "ArrowLeft");

            Assert.Equal((0, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void TheHeaderSitsAboveTheFirstRow()
        {
            // Row 0 to the user, and the only keyboard route to sorting there is.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowUp");

            Assert.Equal((RadzenFastGrid<Person>.HeaderRow, 0), cut.Instance.FocusedCell);

            Press(cut, "ArrowDown");

            Assert.Equal((0, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void TheColumnStaysPutWhenTheRowChanges()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowRight");
            Press(cut, "ArrowDown");

            Assert.Equal((1, 1), cut.Instance.FocusedCell);
        }

        [Fact]
        public void NeitherEndOfARowWraps()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowLeft");

            Assert.Equal((0, 0), cut.Instance.FocusedCell);

            Press(cut, "ArrowRight");
            Press(cut, "ArrowRight");

            Assert.Equal((0, 1), cut.Instance.FocusedCell);
        }

        [Fact]
        public void TheLastRowOfTheLastPageIsTheEnd()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            for (var i = 0; i < 10; i++)
            {
                Press(cut, "ArrowDown");
            }

            Assert.Equal((3, 0), cut.Instance.FocusedCell);
        }

        // ---- Home, End and the two that take Ctrl ----

        [Fact]
        public void HomeAndEndAreTheRowRatherThanTheGrid()
        {
            // The divergence from RadzenDataGrid, which binds these to the first and last row - the
            // pattern's Ctrl+Home and Ctrl+End. On a ten-column grid the row meaning is the useful one.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowDown");
            Press(cut, "End");

            Assert.Equal((1, 1), cut.Instance.FocusedCell);

            Press(cut, "Home");

            Assert.Equal((1, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void CtrlHomeAndCtrlEndAreTheGrid()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "End", ctrl: true);

            Assert.Equal((3, 1), cut.Instance.FocusedCell);

            Press(cut, "Home", ctrl: true);

            Assert.Equal((0, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void PageDownMovesAViewportAndStopsAtTheLastRow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, data: People.Many(30));

            Press(cut, "PageDown");

            // No browser has been asked how tall the viewport is, so the step is the unmeasured one.
            Assert.Equal((10, 0), cut.Instance.FocusedCell);

            Press(cut, "PageDown");
            Press(cut, "PageDown");
            Press(cut, "PageDown");

            Assert.Equal((29, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void PageUpStopsAtTheHeader()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, data: People.Many(30));

            Press(cut, "PageDown");
            Press(cut, "PageUp");

            Assert.Equal((0, 0), cut.Instance.FocusedCell);

            Press(cut, "PageUp");

            Assert.Equal((RadzenFastGrid<Person>.HeaderRow, 0), cut.Instance.FocusedCell);
        }

        // ---- what Enter and Space do ----

        [Fact]
        public void EnterRaisesRowClickAndSpaceDoesNot()
        {
            // Upstream binds both keys to selection and offers no keyboard route to a row click at all,
            // which on a grid whose RowClick opens a detail page means a keyboard user can select rows
            // and never open one.
            using var ctx = new TestContext();

            var clicked = new List<Person>();

            var cut = Render(ctx, p => p.Add(g => g.RowClick,
                EventCallback.Factory.Create<Person>(this, clicked.Add)));

            Press(cut, "Enter");

            Assert.Equal(new[] { "Carol" }, clicked.Select(p => p.First));

            Press(cut, " ");

            Assert.Single(clicked);
        }

        [Fact]
        public void SpaceSelectsTheFocusedRow()
        {
            using var ctx = new TestContext();

            ICollection<Person>? selection = null;

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.SelectionMode, Radzen.DataGridSelectionMode.Multiple);
                p.Add(g => g.SelectionChanged,
                    EventCallback.Factory.Create<ICollection<Person>>(this, s => selection = s));
            });

            Press(cut, "ArrowDown");
            Press(cut, " ");

            Assert.Equal(new[] { "Alice" }, selection!.Select(p => p.First));
        }

        [Fact]
        public void EnterOnTheHeaderSortsTheFocusedColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowSorting, true));

            Press(cut, "ArrowUp");
            Press(cut, "ArrowRight");
            Press(cut, "Enter");

            Assert.Equal("Last", cut.Instance.SortColumn?.Title);
            Assert.False(cut.Instance.SortDescending);
        }

        [Fact]
        public void EnterOnTheToggleCellExpandsTheRow()
        {
            // Every rendered cell is navigable, the toggle included, and Enter activates whatever is in
            // it. One rule, rather than ArrowRight meaning expand on the grids with the most columns.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.Template,
                (RenderFragment<Person>)(person => b => b.AddContent(0, person.First))));

            Press(cut, "Enter");

            Assert.Single(cut.FindAll("tr.rz-expanded-row-content"));
        }

        [Fact]
        public void TheToggleColumnIsOneMoreCellToCross()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.Template,
                (RenderFragment<Person>)(person => b => b.AddContent(0, person.First))));

            Press(cut, "End");

            Assert.Equal((0, 2), cut.Instance.FocusedCell);
        }

        // ---- the page boundary ----

        [Fact]
        public void ArrowingPastTheLastRowAdvancesThePage()
        {
            // RadzenDataGrid simply stops here - nothing calls ChangePage - which on 11,700 rows makes
            // the keyboard useless past the first page.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
            }, People.Many(12));

            Press(cut, "ArrowRight");

            for (var i = 0; i < 4; i++)
            {
                Press(cut, "ArrowDown");
            }

            Assert.Equal(0, cut.Instance.CurrentPage);

            Press(cut, "ArrowDown");

            Assert.Equal(1, cut.Instance.CurrentPage);

            // The first row of the new page, in the column the cursor was already in.
            Assert.Equal((0, 1), cut.Instance.FocusedCell);
        }

        [Fact]
        public void ArrowingUpFromTheHeaderPagesBack()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
            }, People.Many(12));

            cut.InvokeAsync(() => cut.Instance.GoToPage(1)).Wait();

            Press(cut, "ArrowUp");

            Assert.Equal(RadzenFastGrid<Person>.HeaderRow, cut.Instance.FocusedCell!.Value.Row);

            Press(cut, "ArrowUp");

            Assert.Equal(0, cut.Instance.CurrentPage);
            Assert.Equal((4, 0), cut.Instance.FocusedCell);
        }

        [Fact]
        public void TheLastPageDoesNotPageOnward()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
            }, People.Many(7));

            cut.InvokeAsync(() => cut.Instance.GoToPage(1)).Wait();

            Press(cut, "ArrowDown");
            Press(cut, "ArrowDown");

            Assert.Equal(1, cut.Instance.CurrentPage);
            Assert.Equal((1, 0), cut.Instance.FocusedCell);
        }

        // ---- following the rows, and not the columns ----

        [Fact]
        public void FocusFollowsTheItemThroughASort()
        {
            // A sort is an act on the rows whose whole purpose is to move the one being looked for, so
            // the cursor goes with it. ItemKey is what makes that possible, and it already backs
            // selection membership.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.Id));
            });

            Press(cut, "ArrowDown");

            var focused = cut.FindAll("tbody tr")[1].QuerySelectorAll("td")[0].TextContent;

            var first = cut.FindComponents<PropertyColumn<Person, string>>()[0].Instance;

            cut.InvokeAsync(() => cut.Instance.SortBy(first)).Wait();

            var row = cut.Instance.FocusedCell!.Value.Row;

            Assert.Equal(focused, cut.FindAll("tbody tr")[row].QuerySelectorAll("td")[0].TextContent);
        }

        [Fact]
        public void WithoutAnItemKeyFocusKeepsItsPosition()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowSorting, true));

            Press(cut, "ArrowDown");

            var first = cut.FindComponents<PropertyColumn<Person, string>>()[0].Instance;

            cut.InvokeAsync(() => cut.Instance.SortBy(first)).Wait();

            Assert.Equal((1, 0), cut.Instance.FocusedCell);
        }

        // ---- what it costs the markup ----

        [Fact]
        public void TheGridIsOneTabStopOnTheElementThatCarriesTheGridRole()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var view = View(cut);

            Assert.Equal("grid", view.GetAttribute("role"));
            Assert.Equal("0", view.GetAttribute("tabindex"));

            // Roving focus would put one of these on every cell, which is an attribute frame per cell.
            Assert.Empty(cut.FindAll("td[tabindex]"));
            Assert.Empty(cut.FindAll("td[id]"));
        }

        [Fact]
        public void NavigationCostsTheRowsNoAttributeOfTheirOwn()
        {
            // An attribute per row measured +16 KB at 1000 rows, eight times this feature's whole
            // budget - a pre-cached value costs nothing, the frame that carries it does not. So the
            // script counts rendered data rows instead, and this is the contract that makes that
            // legitimate: the nth tr.rz-data-row is the nth row of the model, with a row expanded and
            // its detail row sitting in between.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.Template,
                (RenderFragment<Person>)(person => b => b.AddContent(0, person.First))));

            Press(cut, "Enter");

            Assert.Empty(cut.FindAll("tbody tr[data-r]"));
            Assert.Single(cut.FindAll("tbody tr.rz-expanded-row-content"));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" },
                cut.FindAll("tbody tr.rz-data-row")
                    .Select(r => r.QuerySelectorAll("td")[1].TextContent)
                    .ToArray());
        }

        [Fact]
        public void AVirtualizedGridDoesCarryTheIndex()
        {
            // There the rendered rows are a window and the index is a position in the whole data set,
            // so it cannot be counted off the DOM - and there are tens of rows rather than a thousand.
            using var ctx = new TestContext();

            var cut = Grid(ctx, p => p.Add(g => g.AllowVirtualization, true), People.Many(6));

            Assert.Equal(new[] { "0", "1", "2", "3", "4", "5" },
                cut.FindAll("tbody tr[data-r]").Select(r => r.GetAttribute("data-r")).ToArray());
        }

        [Fact]
        public void NothingIsEmittedWhenNavigationIsOff()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns());
            });

            var view = cut.Find(".rz-data-grid-data");

            Assert.False(view.HasAttribute("tabindex"));
            Assert.False(view.HasAttribute("id"));

            Assert.Empty(cut.FindAll("tbody tr[data-r]"));
        }

        [Fact]
        public void TheFilterRowSwallowsKeydown()
        {
            // It holds real inputs that Tab already reaches. In the arrow space, every keystroke would
            // have to decide whether it is navigation or typing.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowFiltering, true));

            Press(cut, "ArrowDown");

            var box = cut.FindAll("thead tr")[1].QuerySelector("input");

            Assert.NotNull(box);

            // The contract rather than the attribute: bUnit walks up from the target looking for a
            // handler and honours the swallow on the way, so there is nothing left to find - and
            // without it the grid's own handler is one element up and the cursor moves a row. Asserted
            // on the cursor, since that is what a user would see go wrong.
            try
            {
                box!.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
            }
            catch (MissingEventHandlerException)
            {
                // Nothing above the filter cell answers a keydown, which is the point of this test.
            }

            Assert.Equal((1, 0), cut.Instance.FocusedCell);
        }

        // ---- nothing to focus ----

        [Fact]
        public void AnEmptyGridPutsTheCursorOnTheHeader()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, data: new List<Person>());

            Assert.Equal((RadzenFastGrid<Person>.HeaderRow, 0), cut.Instance.FocusedCell);

            Press(cut, "ArrowDown");

            Assert.Equal((RadzenFastGrid<Person>.HeaderRow, 0), cut.Instance.FocusedCell);

            Press(cut, "Enter");
        }

        [Fact]
        public void TabbingInRestoresWhereTheCursorWas()
        {
            // Tabbing out to a filter box and back is a constant gesture, and starting over each time is
            // the difference between keyboard support existing and anyone using it.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Press(cut, "ArrowDown");
            Press(cut, "ArrowRight");

            View(cut).Blur();
            View(cut).Focus();

            Assert.Equal((1, 1), cut.Instance.FocusedCell);
        }

        [Fact]
        public void ACursorPastTheLastCellIsBroughtBack()
        {
            // A column picked out of the grid leaves the cursor beyond the last cell, and the position
            // has to be settled before the next key rather than by every branch that reads it.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnPicking, true));

            Press(cut, "End");

            Assert.Equal((0, 1), cut.Instance.FocusedCell);

            var last = cut.FindComponents<PropertyColumn<Person, string>>()[1].Instance;

            cut.InvokeAsync(() => last.SetPicked(false)).Wait();
            cut.Render();

            Press(cut, "ArrowDown");

            Assert.Equal((1, 0), cut.Instance.FocusedCell);
        }
    }
}
