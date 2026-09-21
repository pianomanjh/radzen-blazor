using System;
using System.Globalization;
using Xunit;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>
    /// The popup sizing itself, laid out by Chromium against the real theme and placed by the real
    /// upstream script. §29.
    /// </summary>
    /// <remarks>
    /// The design rests on a claim about code this branch does not own - that <c>Radzen.openPopup</c>
    /// positions a panel correctly when the panel is already its final width - and every part of it can
    /// be checked without that script except the part the whole thing depends on. So the shipped
    /// <c>Radzen.Blazor.js</c> is on the page and is called, and what is asserted here is the placed
    /// panel rather than either side's arithmetic about it.
    /// <para>
    /// Three of these exist because a reading of the shipped upstream file said they would hold:
    /// <c>openPopup</c>'s early return wants a class ours does not carry, it reparents the panel to
    /// <c>document.body</c> and never puts it back on close, and the panel is <c>content-box</c> so a
    /// width written on it is not the width that overflows a window. A reading is not a check.
    /// </para>
    /// </remarks>
    [Collection(GridParityCollection.Name)]
    public sealed class PopupGeometryTests
    {
        /// <summary>How far a measured box may sit from the figure it is compared with, in CSS pixels.</summary>
        const double Tolerance = 1.0;

        readonly GridParityFixture fixtures;

        public PopupGeometryTests(GridParityFixture fixtures) => this.fixtures = fixtures;

        PopupRun Popup
        {
            get
            {
                var popup = fixtures.Geometry.Popup;

                ParityAssert.True(popup?.Initial is not null,
                    "the popup pane was measured at all",
                    "every assertion below is vacuous if the probe found no panel, no control or no "
                    + "upstream script - and a vacuous check is worse than an absent one",
                    "a measured popup pane",
                    popup is null ? "the probe returned nothing" : "the probe returned no opening");

                return popup;
            }
        }

        static string Px(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

        [Fact]
        public void The_pass_takes_the_width_over()
        {
            var open = Popup.Initial;

            ParityAssert.True(open.Sized && open.Written > 0,
                "the popup reports having sized itself",
                "syncWidth is turned off only on this answer; a pass that did nothing and said it had "
                + "would leave the panel with no width at all rather than with the control's",
                "sized, with a width written onto the panel",
                $"sized {open.Sized}, written {Px(open.Written)}");
        }

        [Fact]
        public void Leaving_the_panel_displayed_does_not_make_openPopup_return_early()
        {
            // openPopup returns early for a display:block panel carrying rz-autocomplete-panel. Ours
            // carries rz-dropdown-panel, which is why the two calls can share one layout - and reading
            // that condition in the shipped file is not the same as watching it not fire.
            var open = Popup.Initial;

            ParityAssert.True(open.Placed && open.Display == "block",
                "openPopup places a panel our pass has already displayed",
                "the two calls share one layout by leaving display and visibility set, which is only "
                + "safe while openPopup's early return does not match this panel",
                "the panel placed, with a left written onto it",
                $"placed {open.Placed}, display {open.Display}");
        }

        [Fact]
        public void The_panels_own_chrome_lands_outside_the_width_we_wrote()
        {
            // .rz-dropdown-panel is box-sizing:content-box, set deliberately against the reset that
            // makes every other rz- element border-box. The cap has to be applied to the outer width,
            // because the outer width is what overflows the window.
            var open = Popup.Initial;

            ParityAssert.True(System.Math.Abs(open.Outer - (open.Written + open.Chrome)) <= Tolerance,
                "the panel's border and padding sit outside the width written on it",
                "a cap applied to the content width lets the panel exceed it by its own chrome, which "
                + "is the amount that then hangs off the right of the window",
                $"outer = written + chrome = {Px(open.Written + open.Chrome)}",
                $"outer {Px(open.Outer)}, written {Px(open.Written)}, chrome {Px(open.Chrome)}");
        }

        [Fact]
        public void A_grown_panel_stays_inside_the_window()
        {
            var open = Popup.Initial;
            var width = Popup.Viewport.InnerWidth;

            ParityAssert.True(open.Right <= width + Tolerance && open.Left >= -Tolerance,
                "a panel grown to its content is still on the page",
                "growth with no cap puts the panel off the right edge, and openPopup only shifts one "
                + "left while the window is wider than the panel - past that there is nothing to pull "
                + "it back",
                $"the panel within 0..{Px(width)}",
                $"left {Px(open.Left)}, right {Px(open.Right)}");
        }

        [Fact]
        public void A_panel_wider_than_its_control_expands_leftward_of_it()
        {
            // The control sits hard against the right edge of the page, which is the only place this
            // can be seen. The expansion is upstream's clamp, working because what it measured was
            // already final - it is the whole reason the width is written before the popup is opened.
            var open = Popup.Initial;

            ParityAssert.True(open.Outer > open.Control.Right - open.Control.Left
                && open.Left < open.Control.Left - Tolerance,
                "a panel too wide to fit under its control opens to the left of it",
                "a panel left-aligned to a control near the right edge would run off the page; the "
                + "clamp that prevents that is upstream's, and it is only correct if the width it "
                + "measured was the final one",
                $"a panel left of {Px(open.Control.Left)}",
                $"panel left {Px(open.Left)}, control left {Px(open.Control.Left)}, "
                + $"panel outer {Px(open.Outer)}");
        }

        [Fact]
        public void The_viewport_beats_a_floor_three_times_its_width()
        {
            // Floors compose by maximum and then the viewport wins over all of them. A panel wider
            // than the window is the one outcome none of this is allowed to produce, so the cap is
            // applied last rather than as just another candidate.
            var open = Popup.Capped;
            var width = Popup.Viewport.InnerWidth;

            ParityAssert.True(open.Outer <= width - Tolerance && open.Right <= width + Tolerance,
                "the viewport cap wins over an author's min-width",
                "openPopup's leftward clamp is switched off entirely once the panel is wider than the "
                + "window, so a cap that yields to a floor removes the only thing keeping the panel "
                + "on the page",
                $"an outer width below {Px(width)}",
                $"outer {Px(open.Outer)}, right {Px(open.Right)}, innerWidth {Px(width)}");
        }

        [Fact]
        public void The_panel_is_never_narrower_than_the_control_it_drops_out_of()
        {
            // syncWidth used to write this floor and is being turned off, so it is written here now.
            // A popup narrower than its control reads as a rendering fault rather than as a fit.
            var open = Popup.Floored;
            var control = open.Control.Right - open.Control.Left;

            ParityAssert.True(open.Outer >= control - Tolerance,
                "a panel is at least as wide as its control",
                "turning syncWidth off removes the floor upstream used to write, and a 900px control "
                + "over a narrow grid would otherwise get a panel a third of its width",
                $"an outer width of at least {Px(control)}",
                $"outer {Px(open.Outer)}, control {Px(control)}");
        }

        [Fact]
        public void A_second_open_measures_a_reparented_panel_and_agrees_with_the_first()
        {
            // openPopup moves the panel to document.body and closePopup does not put it back - only a
            // full teardown does. So every open after the first is measured in a different place in
            // the tree, and the pass is only insensitive to that because every input it takes is a
            // width it writes itself or an element it resolves by id.
            var first = Popup.Initial;
            var again = Popup.Reopened;

            ParityAssert.True(again.InBody,
                "the second open is measured with the panel living in document.body",
                "if the panel had been put back, this test would be a duplicate of the first open and "
                + "would prove nothing about the state a real second open happens in",
                "the panel parented to document.body",
                $"inBody {again.InBody}");

            ParityAssert.True(System.Math.Abs(again.Written - first.Written) <= Tolerance,
                "reopening a popup gives the same width as opening it",
                "the panel's containing block changes when it is reparented, so a pass that read any "
                // ReSharper disable once StringLiteralTypo
                + "geometry off its parent would answer differently on the second open",
                $"written {Px(first.Written)}",
                $"first {Px(first.Written)}, again {Px(again.Written)}");
        }

        [Fact]
        public void Content_wider_than_the_window_is_capped_short_of_its_edge()
        {
            // Two guards in one measurement, because they are only reachable together: the page's own
            // scrollbar is taken away, so the margin is the whole allowance, and the content is made
            // wider than the window, so the cap on the growth is the only thing bounding it. Without
            // the cap the panel is thousands of pixels wide; without the margin's floor it is exactly
            // the window width, which is the one width at which openPopup stops clamping at all.
            var open = Popup.Wide;
            var width = Popup.Viewport.InnerWidth;

            ParityAssert.True(open.Outer <= width - 8 + Tolerance && open.Right <= width + Tolerance,
                "content wider than the window is capped short of the edge",
                "a panel exactly the window's width disarms the leftward clamp, and a panel wider than "
                + "it has nothing left that would pull it back onto the page",
                $"an outer width of at most {Px(width - 8)}",
                $"outer {Px(open.Outer)}, right {Px(open.Right)}, innerWidth {Px(width)}");
        }

        [Fact]
        public void A_floor_past_the_window_is_capped_even_with_no_growth_to_rescue_it()
        {
            // Growth caps its own answer, so an uncapped base is invisible whenever growth runs. Only
            // a pass that does not grow can say whether the base was bounded on the way in - and the
            // base is what every measurement below it is taken against.
            var open = Popup.Unfitted;
            var width = Popup.Viewport.InnerWidth;

            ParityAssert.True(open.Outer <= width - Tolerance,
                "the base width is capped before anything is measured against it",
                "the columns are measured inside whatever the base turned out to be, so a base past "
                + "the window measures the grid against a container that cannot exist",
                $"an outer width below {Px(width)}",
                $"outer {Px(open.Outer)}, innerWidth {Px(width)}");
        }

        [Fact]
        public void A_shorter_popup_actually_gets_shorter()
        {
            // scrollHeight is never less than the box, so a wrapper measured without clearing its own
            // height can only ever grow: twenty rows then four would stay at twenty.
            var heights = Popup.TallThenShort;
            var row = Popup.Initial.RowHeight;

            // By sixteen rows, not merely by some. A box measured without being cleared still shrinks -
            // it loses the rows between what was rendered and what was asked for - so "shorter" on its
            // own is true either way and says nothing.
            ParityAssert.True(row > 0 && heights.Before - heights.After >= 15 * row,
                "asking for four rows instead of twenty takes sixteen rows off the popup",
                "a height read off a box that still carries the previous answer shrinks by the rows "
                + "that were not rendered rather than by the rows that were not asked for",
                $"a drop of at least {Px(15 * row)}",
                $"before {Px(heights.Before)}, after {Px(heights.After)}, "
                + $"dropped {Px(heights.Before - heights.After)}, row {Px(row)}");
        }

        [Fact]
        public void A_popup_with_no_rows_gives_up_waiting_for_them_quickly()
        {
            // The ceiling is the popup's own and is shorter than the grid's on purpose: a grid already
            // on screen pays nothing visible for a slow fit, and a popup pays its own appearance. This
            // is the one measurement here that is a duration, because the thing under test is one.
            var open = Popup.Empty;

            ParityAssert.True(open.WaitedMs < 600,
                "a popup waiting on rows that never arrive opens anyway, and soon",
                "the panel does not appear until the measurement is done, so the grid's own second-long "
                + "ceiling would be a second of a click having produced nothing",
                "under 600ms",
                $"{open.WaitedMs}ms");
        }

        [Fact]
        public void An_empty_result_does_not_grow_the_panel()
        {
            // Header widths are real content and the columns are still apportioned by them, but a
            // panel sized to five column titles is chrome sized to chrome - and it gives a popup that
            // is narrow while a filter matches nothing and wide the moment it is cleared.
            var empty = Popup.Empty;
            var full = Popup.Initial;

            ParityAssert.True(empty.Outer < full.Outer - Tolerance,
                "a popup with no rows keeps its unfitted width",
                "growing on the strength of column titles alone makes the panel jump between two "
                + "widths as a filter is applied and cleared",
                $"an outer width below the grown {Px(full.Outer)}",
                $"empty {Px(empty.Outer)}, grown {Px(full.Outer)}");

            // And not narrower than the floor either, which is the half the first assertion cannot
            // see: a panel sized from its column titles is also below the grown width, so "smaller"
            // alone is true whether the growth was skipped or merely produced a small answer.
            ParityAssert.True(empty.Outer >= empty.AuthorFloor + empty.Chrome - Tolerance,
                "a popup with no rows is not sized from its column titles",
                "headers are real content and the columns are still apportioned by them, but a panel "
                + "sized to five titles is chrome sized to chrome",
                $"an outer width of at least {Px(empty.AuthorFloor + empty.Chrome)}",
                $"empty {Px(empty.Outer)}, floor {Px(empty.AuthorFloor)}, chrome {Px(empty.Chrome)}");
        }

        [Fact]
        public void MaxRows_bounds_the_popup_to_that_many_rows()
        {
            // Measured rather than multiplied out. MaxRows x ItemSize bounds the whole grid - header,
            // filter row, pager - so eight rows of pixels shows about five rows, and ItemSize is the
            // row height only when virtualizing.
            var open = Popup.Initial;
            var rows = open.RowHeight;
            var height = open.WrapperHeight;

            ParityAssert.True(rows > 0 && height > 4 * rows && height < GridParityFixture.RowCount * rows,
                "MaxRows shows four rows and the chrome above them, not eight",
                "the wrapper bounds the whole grid rather than only its rows, so a height that is a "
                + "multiple of the row height alone shows fewer rows than it names",
                $"a height between {Px(4 * rows)} and {Px(GridParityFixture.RowCount * rows)}",
                $"wrapper {Px(height)}, row {Px(rows)}");
        }

        // --- The multi-select panel, which §29 inferred rather than opened ---------------------

        MultiPopupOpen Multi
        {
            get
            {
                var open = fixtures.Geometry.MultiPopup;

                ParityAssert.True(open is not null,
                    "the multi-select pane was measured at all",
                    "§29 inferred that this panel behaves like the single-select one; a pane the probe "
                    + "could not find would leave that inference exactly where it was",
                    "a measured multi-select pane",
                    "the probe returned nothing");

                return open;
            }
        }

        [Fact]
        public void The_multiselect_panel_is_sized_the_same_way()
        {
            // A different class with its own rules elsewhere in the theme's sheet, and the only thing
            // the component varies by is which of the two it emits. That is a reason to expect this to
            // hold, not a reason to skip asking.
            var open = Multi;

            ParityAssert.True(open.PanelClass.Contains("rz-multiselect-panel", StringComparison.Ordinal),
                "the pane measured is the multi-select panel",
                "an assertion about multi-select that ran against a dropdown panel would pass for the "
                + "wrong reason and leave the gap open",
                "a panel carrying rz-multiselect-panel",
                open.PanelClass);

            ParityAssert.True(open.Sized && open.Placed && open.Written > 0,
                "the multi-select popup sizes itself and is placed",
                "the code path is shared, so the thing this checks is that the theme's other panel class "
                + "does not change what the measurements mean",
                "sized, placed, with a width written",
                $"sized {open.Sized}, placed {open.Placed}, written {Px(open.Written)}");
        }

        [Fact]
        public void The_multiselect_panels_chrome_also_lands_outside_the_width()
        {
            // The content-box fact was read off the theme for both classes and asserted for one.
            var open = Multi;

            ParityAssert.True(System.Math.Abs(open.Outer - (open.Written + open.Chrome)) <= Tolerance,
                "the multi-select panel is content-box too",
                "the cap is applied to the outer width, so a panel class whose chrome sat inside the "
                + "width would be capped wrongly by exactly its border",
                $"outer = written + chrome = {Px(open.Written + open.Chrome)}",
                $"outer {Px(open.Outer)}, written {Px(open.Written)}, chrome {Px(open.Chrome)}");
        }

        [Fact]
        public void The_multiselect_popup_stays_inside_the_window()
        {
            var open = Multi;
            var width = Popup.Viewport.InnerWidth;

            ParityAssert.True(open.Right <= width + Tolerance && open.Left >= -Tolerance,
                "the multi-select popup is on the page",
                "it hangs from a control at the right edge like the other pane, so it is the same "
                + "leftward clamp being relied on",
                $"the panel within 0..{Px(width)}",
                $"left {Px(open.Left)}, right {Px(open.Right)}");
        }

        [Fact]
        public void MaxRows_carries_a_pager_it_does_not_know_about()
        {
            // The other pane has paging off, so no pane was carrying a pager and §29 recorded that as a
            // gap. MaxRows subtracts the rendered rows from the whole box and keeps everything else, so
            // a pager should need no special handling - which is a prediction until something has one.
            var open = Multi;

            ParityAssert.True(open.Pagers > 0,
                "the multi-select pane actually carries a pager",
                "this test is about chrome MaxRows does not account for; without a pager present it "
                + "asserts nothing and would pass forever",
                "at least one .rz-pager-pages inside the panel",
                $"{open.Pagers} pagers");

            // Three rows asked for against five rendered: the bound has to be below the content and
            // above three rows plus the header and pager it keeps.
            ParityAssert.True(open.RowHeight > 0
                && open.WrapperHeight > 3 * open.RowHeight
                && open.WrapperHeight < open.RenderedRows * open.RowHeight + 3 * open.RowHeight,
                "MaxRows bounds a paged popup to its rows plus its chrome",
                "a pager is height inside the scrolling box that the arithmetic never names, so it is "
                + "kept only because the subtraction is of rows rather than of everything-but-rows",
                $"a height between {Px(3 * open.RowHeight)} and "
                + $"{Px(open.RenderedRows * open.RowHeight + 3 * open.RowHeight)}",
                $"wrapper {Px(open.WrapperHeight)}, row {Px(open.RowHeight)}, "
                + $"rendered {open.RenderedRows}");
        }

        [Fact]
        public void Nothing_inside_the_grown_popup_overflows_it()
        {
            // The point of growing and then fitting: growth answers what the columns want, the fit
            // answers what to do when the cap said they could not have it. Either way the table ends
            // up inside the panel.
            var open = Popup.Initial;

            ParityAssert.True(open.TableWidth <= open.Written + Tolerance,
                "the fitted table sits inside the panel that was grown for it",
                "a table wider than its panel is a horizontal scrollbar inside a popup that is also "
                + "scrolling vertically, which is the outcome the whole feature exists to avoid",
                $"a table no wider than {Px(open.Written)}",
                $"table {Px(open.TableWidth)}, panel content {Px(open.Written)}");
        }
    }
}
