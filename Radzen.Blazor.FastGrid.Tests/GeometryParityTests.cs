using System;
using System.Linq;
using System.Globalization;
using Xunit;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>
    /// The rendered half of the parity check: both grids laid out by Chromium against the real Radzen
    /// stylesheet, with their box heights read back and compared.
    /// </summary>
    /// <remarks>
    /// Markup assertions cannot see a structural coupling. The theme hangs the header padding off a direct
    /// child div of the th, so removing that div leaves every class name correct and shortens the header
    /// row - a fault that survived both a markup diff and a person looking at a screenshot. These tests are
    /// what caught it, and what will catch the next one like it.
    /// </remarks>
    [Collection(GridParityCollection.Name)]
    public sealed class GeometryParityTests
    {
        /// <summary>How far RadzenFastGrid may sit from RadzenDataGrid, in CSS pixels.</summary>
        const double ParityTolerance = 0.5;

        /// <summary>How far either grid may sit from the recorded baseline, in CSS pixels.</summary>
        const double BaselineTolerance = 1.0;

        /// <summary>
        /// The geometry recorded when parity was first established, at 8 rows x 5 columns against
        /// standard-base.css. Its job is to catch the case where neither grid is styled at all: two
        /// unstyled tables agree with each other perfectly and mean nothing.
        /// </summary>
        static readonly (string Name, double Expected)[] Baseline =
        {
            ("header cell", 37),
            ("body cell", 37),
            ("table", 332),
        };

        readonly GridParityFixture fixtures;

        public GeometryParityTests(GridParityFixture fixtures) => this.fixtures = fixtures;

        [Fact]
        public void The_theme_stylesheet_actually_applied()
        {
            var report = fixtures.Geometry;
            var stylesheet = report.Stylesheets.Find(s => s.Url.EndsWith("standard-base.css", StringComparison.Ordinal));

            ParityAssert.True(stylesheet is not null && stylesheet.Status is 0 or 200,
                "the theme stylesheet loaded",
                "an unstyled page makes the two grids agree with each other and proves nothing; this is the check that the comparison below is worth anything at all",
                $"a successful request for {fixtures.ThemeStylesheet}",
                stylesheet is null ? "no request for it was made" : $"HTTP {stylesheet.Status}",
                report.Describe());

            // The stylesheet arriving is not the same as it taking effect, so probe the document too.
            foreach (var (property, value) in new[]
            {
                ("--rz-grid-cell-padding", report.ThemeProbe),
                ("--rz-grid-cell-line-height", report.ThemeCellHeightProbe),
            })
            {
                ParityAssert.True(!string.IsNullOrEmpty(value),
                    "the theme's custom properties resolved",
                    "the grid metrics are all expressed in Radzen CSS variables; if they are unset, the measured heights are browser defaults and the comparison is meaningless",
                    $"{property} resolves to a value",
                    value is null ? "(no value)" : $"'{value}'",
                    report.Describe());
            }
        }

        [Theory]
        [InlineData("header cell")]
        [InlineData("body cell")]
        [InlineData("table")]
        public void Fast_grid_height_matches_the_data_grid(string what)
        {
            var report = fixtures.Geometry;
            var reference = Height(report[fixtures.DataGrid.Name], what);
            var actual = Height(report[fixtures.FastGrid.Name], what);

            ParityAssert.True(reference.HasValue && actual.HasValue,
                $"both grids render a measurable {what}",
                "a missing element cannot be compared, and a null height usually means the element is not there at all",
                "a height for both grids",
                $"{fixtures.DataGrid.Name}: {Format(reference)}, {fixtures.FastGrid.Name}: {Format(actual)}",
                report.Describe());

            var delta = Math.Abs(actual.Value - reference.Value);

            ParityAssert.True(delta <= ParityTolerance,
                $"RadzenFastGrid {what} height matches RadzenDataGrid",
                "the two grids are styled by the same stylesheet over the same data, so any difference in rendered height is a structural difference in the markup - which is how a missing 'th > div' wrapper shows up",
                string.Create(CultureInfo.InvariantCulture,
                    $"{reference.Value}px (RadzenDataGrid), within {ParityTolerance}px"),
                string.Create(CultureInfo.InvariantCulture,
                    $"{actual.Value}px (RadzenFastGrid), off by {Math.Round(delta, 2)}px"),
                report.Describe());
        }

        [Theory]
        [InlineData("header cell")]
        [InlineData("body cell")]
        [InlineData("table")]
        public void Both_grids_match_the_recorded_baseline(string what)
        {
            var report = fixtures.Geometry;
            var expected = Array.Find(Baseline, b => b.Name == what).Expected;

            foreach (var grid in new[] { fixtures.DataGrid.Name, fixtures.FastGrid.Name })
            {
                var actual = Height(report[grid], what);

                ParityAssert.True(actual.HasValue,
                    $"{grid} renders a measurable {what}",
                    "a null height means the element was not found in the rendered page",
                    "a height", "none", report.Describe());

                var delta = Math.Abs(actual.Value - expected);

                ParityAssert.True(delta <= BaselineTolerance,
                    $"{grid} {what} height matches the recorded baseline",
                    "this pins the absolute numbers, so a run where the stylesheet failed to apply cannot pass by having both grids equally unstyled. If the theme's grid metrics genuinely changed, update the baseline in this file deliberately",
                    string.Create(CultureInfo.InvariantCulture, $"{expected}px, within {BaselineTolerance}px"),
                    string.Create(CultureInfo.InvariantCulture,
                        $"{actual.Value}px, off by {Math.Round(delta, 2)}px"),
                    report.Describe());
            }
        }

        // Row detail is the one feature whose markup was copied from RadzenDataGrid rather than derived
        // from the theme, so what the theme actually needs from the toggle cell had to be measured.
        //
        // The cell's own box cannot answer that. It sits in a table-layout:fixed table and fills the row
        // height, so it measures 37x32 whatever is inside it - a 40px element added to it did not move it
        // by a pixel. The button's offset inside the cell is the measurement that sees the contents:
        // anything taking space pushes it. That is what established the empty rz-column-title span
        // RadzenDataGrid puts here takes no space at all, and what will catch it if that changes.
        [Theory]
        [InlineData("toggle cell")]
        [InlineData("toggle cell width")]
        [InlineData("toggle button offset")]
        [InlineData("toggle button width")]
        [InlineData("data row")]
        [InlineData("body cell")]
        [InlineData("table")]
        public void Fast_grid_row_detail_height_matches_the_data_grid(string what)
        {
            var report = fixtures.Geometry;
            var reference = Height(report[GridParityFixture.DataGridDetail], what);
            var actual = Height(report[GridParityFixture.FastGridDetail], what);

            ParityAssert.True(reference.HasValue && actual.HasValue,
                $"both grids render a measurable {what} with row detail on",
                "a null toggle-cell height means the expand column is not there at all, which is the first thing that would break",
                "a height for both grids",
                $"{GridParityFixture.DataGridDetail}: {Format(reference)}, {GridParityFixture.FastGridDetail}: {Format(actual)}",
                report.Describe());

            var delta = Math.Abs(actual.Value - reference.Value);

            ParityAssert.True(delta <= ParityTolerance,
                $"RadzenFastGrid {what} height matches RadzenDataGrid with row detail on",
                "the toggle cell's contents were copied from RadzenDataGrid without knowing which of them the theme needs. If one of them can be dropped, this stays green and the component gets cheaper; if it cannot, this is what says so",
                string.Create(CultureInfo.InvariantCulture,
                    $"{reference.Value}px (RadzenDataGrid), within {ParityTolerance}px"),
                string.Create(CultureInfo.InvariantCulture,
                    $"{actual.Value}px (RadzenFastGrid), off by {Math.Round(delta, 2)}px"),
                report.Describe());
        }

        [Fact]
        public void The_theme_puts_no_padding_on_the_header_cell_itself()
        {
            // The reason `th > div` is load-bearing, asserted directly: the padding is on the div because
            // the theme has taken it off the th. If this ever stops being true the coupling is gone and the
            // header-chain rule should be revisited rather than obeyed.
            var report = fixtures.Geometry;

            foreach (var grid in new[] { fixtures.DataGrid.Name, fixtures.FastGrid.Name })
            {
                var padding = report[grid].HeaderCellPadding;

                ParityAssert.True(padding is not null && padding.Replace("px", "").Replace(" ", "") == "0",
                    $"{grid} header cell has no padding of its own",
                    "the theme zeroes th padding and moves it to a direct child div; that is exactly why the div cannot be dropped",
                    "computed th padding of 0px",
                    padding is null ? "(not measured)" : $"'{padding}'",
                    report.Describe());
            }
        }

        [Fact]
        public void A_selected_row_is_actually_painted()
        {
            // The check that geometry cannot make. Every markup assertion about selection passed while
            // a selected row was drawn identically to its neighbours: the theme nests its rule inside
            // .rz-selectable, which the grid did not emit, so rz-state-highlight sat on the right tr
            // and matched nothing. Compare the two backgrounds rather than pinning a colour, so a theme
            // that changes its palette does not fail this.
            var report = fixtures.Geometry;

            foreach (var grid in new[] { GridParityFixture.DataGridSelected, GridParityFixture.FastGridSelected })
            {
                var geometry = report[grid];

                ParityAssert.True(
                    !string.IsNullOrEmpty(geometry.SelectedRowBackground)
                        && !string.IsNullOrEmpty(geometry.UnselectedRowBackground)
                        && geometry.SelectedRowBackground != geometry.UnselectedRowBackground,
                    $"{grid} draws a selected row differently from an unselected one",
                    "the theme's selected-row rule lives inside .rz-selectable, so a grid that never emits that class highlights nothing however correct the row's own classes are",
                    "a selected cell whose computed background differs from an unselected cell of the same stripe parity",
                    geometry.SelectedRowBackground is null
                        ? "no selected row was found in the pane"
                        : $"selected '{geometry.SelectedRowBackground}' vs unselected '{geometry.UnselectedRowBackground}'",
                    report.Describe());
            }

            // And the colour itself has to match the reference grid's, not merely differ from something.
            // A grid could highlight in any colour it liked and still pass the check above.
            var reference = report[GridParityFixture.DataGridSelected].SelectedRowBackground;
            var actual = report[GridParityFixture.FastGridSelected].SelectedRowBackground;

            ParityAssert.True(reference == actual,
                "RadzenFastGrid highlights a selected row in the same colour as RadzenDataGrid",
                "both read it from the theme, so a difference means one of them is not matching the rule it thinks it is",
                $"'{reference}'",
                $"'{actual}'",
                report.Describe());
        }

        [Fact]
        public void Declared_widths_land_on_the_columns_that_declared_them()
        {
            // The toggle column is a cell in every row with no col of its own, so a colgroup that does
            // not stand one in for it shifts every declared width one column to the left - the toggle
            // takes the first column's width and the last column takes whatever is left. The markup is
            // entirely correct either way; only measured widths show it.
            var report = fixtures.Geometry;

            var reference = report[GridParityFixture.DataGridDetail].DataCellWidths;
            var actual = report[GridParityFixture.FastGridDetail].DataCellWidths;

            ParityAssert.True(reference is { Length: > 0 } && actual is { Length: > 0 },
                "both detail panes reported cell widths",
                "without them this check cannot run at all",
                "a width for every cell in the first body row",
                $"reference {Describe(reference)}, actual {Describe(actual)}",
                report.Describe());

            ParityAssert.True(reference.Length == actual.Length
                    && reference.Zip(actual, (r, a) => Math.Abs(r - a) <= ParityTolerance).All(equal => equal),
                "RadzenFastGrid draws a row-detail grid's columns at the same widths as RadzenDataGrid",
                "both were given the same declared widths, so a difference means one of them is applying them to the wrong columns",
                Describe(reference),
                Describe(actual),
                report.Describe());
        }

        [Fact]
        public void A_frozen_column_is_actually_pinned()
        {
            // The one assertion the whole feature reduces to. The theme makes .rz-frozen-cell sticky and
            // stops there - it supplies no inset, and sticky without an inset does not stick - so a grid
            // can emit every frozen class correctly and scroll away exactly like an ordinary one. Only
            // scrolling the container and watching what moves can tell those apart.
            var report = fixtures.Geometry;
            var hold = report[GridParityFixture.FastGridFrozen].FrozenHold;

            ParityAssert.True(hold is not null,
                "the frozen pane reported what a scroll did to it",
                "without a scroll container there is nothing to hold still against",
                "a measurement",
                "(none)",
                report.Describe());

            ParityAssert.True(Math.Abs(hold.UnfrozenMoved) > 1,
                "the grid actually scrolled",
                "a pane that cannot scroll makes every column look frozen and proves nothing",
                "an unfrozen cell moved by the scroll",
                hold.ToString(),
                report.Describe());

            ParityAssert.True(Math.Abs(hold.FrozenMoved) <= ParityTolerance,
                "a frozen column stays put while the grid scrolls under it",
                "this is the feature; the classes are only the half of it the theme can see",
                "a frozen cell that did not move",
                hold.ToString(),
                report.Describe());
        }

        [Fact]
        public void A_frozen_column_stays_on_top_of_what_scrolls_under_it()
        {
            // Holding still is not enough: the theme makes every header cell sticky at the same
            // z-index, frozen or not, so a frozen header cell ties with its neighbours and document
            // order settles it - the column to its right paints straight over the pinned one while
            // every position and inset stays correct. Only asking what is actually on top can see it.
            var report = fixtures.Geometry;
            var overlap = report[GridParityFixture.FastGridFrozen].FrozenOverlap;

            ParityAssert.True(overlap is not null,
                "the frozen pane reported what is on top where its columns overlap",
                "position being right says nothing about paint order",
                "a measurement",
                "(none)",
                report.Describe());

            // The pane carries a title row, a filter row, a body and a footer for this reason: the
            // theme stacks each section differently, so winning in one says nothing about the next.
            ParityAssert.True(overlap.PinnedColumns > 0,
                "the pane actually has pinned columns to test",
                "with none, every check below is vacuously true",
                "at least one frozen column",
                overlap.ToString(),
                report.Describe());

            ParityAssert.True(overlap.RowsChecked > 3,
                "every section of the grid was examined",
                "a pane with only a body cannot show a frozen column losing in the header or the footer, which is where it does lose",
                "a title row, a filter row, body rows and a footer",
                overlap.ToString(),
                report.Describe());

            ParityAssert.True(overlap.Covered is not { Length: > 0 },
                "no row draws a scrolling column over the frozen one",
                "the theme makes header and footer cells sticky at a fixed z-index whether or not they are frozen, so a frozen cell there ties with its neighbours and document order lets the column to its right paint over it",
                "no covered rows",
                overlap.ToString(),
                report.Describe());
        }

        [Theory]
        [InlineData(GridParityFixture.FastGridFocus)]
        [InlineData(GridParityFixture.FastGridFrozenFocus)]
        public void The_keyboard_cursor_is_actually_painted(string pane)
        {
            // The third instance on this branch of one failure: a class the theme scopes under a parent
            // does nothing until that parent is emitted, and every markup assertion passes meanwhile.
            // Radzen draws a focused *row* only inside .rz-selectable - which a read-only grid never
            // carries - and draws a focused *cell* nowhere at all, which is why RadzenDataGrid's own
            // cell navigation is invisible. These panes wire no selection, so they are exactly the
            // configuration that paints nothing without the package's stylesheet.
            var report = fixtures.Geometry;
            var focus = report[pane].Focus;

            ParityAssert.True(focus is not null,
                $"{pane} has a focused cell to measure",
                "with none, every check below is vacuously true - which is the shape of check that let the filter row ship unpinned",
                "a cell carrying the cursor",
                "(none)",
                report.Describe());

            ParityAssert.True(!string.IsNullOrEmpty(focus.Outline)
                    && focus.Outline != focus.OtherOutline
                    && !focus.Outline.StartsWith("none", StringComparison.Ordinal),
                $"{pane} draws an outline on the focused cell and not on its neighbour",
                "the row background cannot show which cell of the row the cursor is in, and there is no td.rz-state-focused rule in any shipped Radzen theme",
                "an outline the neighbouring cell does not have",
                $"focused '{focus.Outline}', neighbour '{focus.OtherOutline}'",
                report.Describe());

            ParityAssert.True(!string.IsNullOrEmpty(focus.Background)
                    && focus.Background != focus.OtherRowBackground,
                $"{pane} draws the focused row differently from an unfocused one",
                "the theme's focused-row rule lives inside .rz-selectable, so on a grid with no selection wired it matches nothing however correct the row's class is",
                "a focused cell whose computed background differs from a cell of an unfocused row",
                $"focused '{focus.Background}', elsewhere '{focus.OtherRowBackground}'",
                report.Describe());

            // Null, not false, when the question could not be asked - a point outside the window
            // answers nothing, and reporting that as "covered" is how a probe earns its reputation for
            // false positives and gets deleted rather than fixed.
            ParityAssert.True(focus.OnTop is not null,
                $"{pane} could be hit-tested at all",
                "elementFromPoint works in viewport coordinates, so a cell below the fold answers null on a grid that is perfectly correct",
                "the focused cell inside the window",
                focus.ToString(),
                report.Describe());

            ParityAssert.True(focus.OnTop is true,
                $"{pane} draws the focused cell over what scrolls under it",
                "a focused frozen cell that loses its opaque background lets the column sliding beneath show through, and every class on it stays correct",
                "the focused cell painted at its own rect",
                focus.ToString(),
                report.Describe());
        }

        // --- Column auto-fit --------------------------------------------------------------------
        //
        // No RadzenDataGrid pane here, and not for want of trying: upstream has no auto-fit at all, so
        // a parity pane would assert agreement with a grid that does nothing. The same reason the
        // keyboard cursor has none.
        //
        // Every assertion below is read off the page rather than off the fit's own arithmetic. A check
        // that asks the fit what it computed and then agrees with it has measured nothing.

        /// <summary>Column indices in the auto-fit pane, which is the only thing the tests name.</summary>
        const int ShortTitle = 0;
        const int LongTitle = 1;
        const int LongValues = 2;
        const int Clamped = 3;
        const int Bare = 4;

        AutoFitRun Fitted()
        {
            var report = fixtures.Geometry;

            ParityAssert.True(report.AutoFit is not null,
                "the auto-fit pane was measured at all",
                "a pane that never ran the script reports nothing, and nothing must not read as a pass",
                "a survey of the auto-fit pane",
                "(none)",
                report.Describe());

            return report.AutoFit;
        }

        [Fact]
        public void A_fit_changes_what_the_columns_were()
        {
            // The control. Every assertion below compares one column with another, and columns that
            // were already the right widths would satisfy all of them without the fit doing anything.
            var fit = Fitted();

            // Within a pixel of each other rather than identical: an equal division of a table width
            // that does not divide evenly lands on either side of the same number.
            ParityAssert.True(fit.Before.Widths.Max() - fit.Before.Widths.Min() <= 1,
                "the pane starts with every column the same width",
                "table-layout:fixed divides the table equally when no column declares a width, and a pane that did not start there proves nothing about what the fit did",
                "one width shared by every column",
                Describe(fit.Before.Widths),
                fit.ToString());

            ParityAssert.True(!fit.After.Widths.SequenceEqual(fit.Before.Widths),
                "the fit moved the columns",
                "a fit that writes nothing passes every comparison below by leaving them equal",
                "column widths different from where they started",
                Describe(fit.After.Widths),
                fit.ToString());
        }

        [Fact]
        public void A_column_of_long_values_comes_out_wider_than_one_of_short_values()
        {
            var fit = Fitted();

            ParityAssert.True(fit.After.Widths[LongValues] > fit.After.Widths[ShortTitle],
                "the column holding dates is wider than the column holding single digits",
                "this is the body half of the measurement, and it is the half a max-content flip on .rz-cell-data is what makes possible - a block cell's scrollWidth is never less than its column, so without it a fit could only ever grow",
                "the long-valued column wider than the short-valued one",
                Describe(fit.After.Widths),
                fit.ToString());
        }

        [Fact]
        public void A_long_heading_widens_its_column_even_though_the_values_are_short()
        {
            // The two columns hold the same values and differ only in their titles, so this can only
            // have come from the header. It is the assertion the flex trap fails: .rz-column-title is
            // an inline-flex whose content child can shrink to nothing, so a header read without the
            // flip reports the width it already has and both columns come out identical.
            var fit = Fitted();

            ParityAssert.True(fit.After.Widths[LongTitle] > fit.After.Widths[ShortTitle],
                "a long heading widens its column",
                "both columns hold the same values, so a difference between them can only be the header - and a header measured through .rz-column-title without a max-content flip answers with the width it already has",
                "the long-titled column wider than the short-titled one",
                Describe(fit.After.Widths),
                fit.ToString());
        }

        [Fact]
        public void A_fitted_column_draws_no_ellipsis()
        {
            var fit = Fitted();

            foreach (var column in new[] { ShortTitle, LongTitle, LongValues })
            {
                ParityAssert.True(fit.After.TruncatedIn(column) == 0,
                    $"column {column} is not truncated after the fit",
                    "a fitted column showing an ellipsis is the one outcome that makes this look broken, and scrollWidth rounding to an integer is how it happens a pixel at a time",
                    "no cell wider than its box",
                    fit.After.TruncatedIn(column).ToString(CultureInfo.InvariantCulture) + " truncated",
                    fit.ToString());
            }
        }

        [Fact]
        public void A_max_width_is_what_stops_one_column_taking_the_table()
        {
            var fit = Fitted();

            ParityAssert.True(fit.After.Widths[Clamped] <= 41,
                "the clamped column stays inside its MaxWidth",
                "the fitted width is pixels and MaxWidth is authored CSS in any unit, so it is clamp() in the browser that compares them rather than anything parsing the string",
                "the clamped column at 40px or under",
                Describe(fit.After.Widths),
                fit.ToString());

            // And it is still truncated, which is the point: a column allowed to say how wide it may
            // get is a column that has accepted an ellipsis.
            ParityAssert.True(fit.After.TruncatedIn(Clamped) > 0,
                "the clamped column is the one that is still truncated",
                "a clamp that let the column through would satisfy the width assertion above by never having applied",
                "cells truncated in the clamped column",
                fit.After.TruncatedIn(Clamped).ToString(CultureInfo.InvariantCulture),
                fit.ToString());
        }

        [Fact]
        public void The_bare_column_takes_what_the_fitted_ones_left()
        {
            var fit = Fitted();

            ParityAssert.True(fit.Written is { Length: 5 } && fit.Written[4] is null,
                "the last column is the one written with no width",
                "under table-layout:fixed a col with no width is what absorbs the remainder, which is the whole of the distribution pass",
                "no width written for the last column",
                "[" + string.Join(", ", (fit.Written ?? Array.Empty<string>()).Select(w => w ?? "(none)")) + "]",
                fit.ToString());

            // The table being exactly as wide after the fit as before it is what the bare column buys,
            // and it is why there is no slack arithmetic anywhere: the browser did the division.
            // Against the table's own earlier width rather than the pane's, because the two differ by
            // the container's own box and that difference is not what this is about.
            ParityAssert.True(Math.Abs(fit.After.TableWidth - fit.Before.TableWidth) <= 0.5,
                "the table is the same width after the fit as before it",
                "fitting every column to its content leaves the table narrower than the space it has unless something absorbs the difference",
                string.Create(CultureInfo.InvariantCulture, $"{fit.Before.TableWidth}px"),
                string.Create(CultureInfo.InvariantCulture, $"{fit.After.TableWidth}px"),
                fit.ToString());
        }

        [Fact]
        public void A_fit_the_user_asked_for_moves_the_columns_rather_than_jumping_them()
        {
            // Sampled mid-flight rather than by reading the stylesheet: a transition that is declared
            // and not running looks identical to one that is. This run starts from no width at all,
            // which is the case that does not interpolate on its own - `auto` has nothing to leave
            // from - so it also covers the pin.
            var fit = Fitted();

            // Counted rather than sampled part-way: headless Chromium runs the animation clock free of
            // wall time, so an intermediate width is not observable here even though it is correct in a
            // real browser. Whether a transition ran, and for which caller, is the contract anyway.
            //
            // This run starts from no width at all, which is the case that cannot interpolate on its
            // own - `auto` gives the transition nothing to leave from - so a count above zero is also
            // what proves the pin works.
            ParityAssert.True(fit.Animation is { Asked.Started: > 0 },
                "a fit somebody asked for transitions the columns it sizes",
                "auto does not interpolate, so without pinning each column to the width it already has the first fit would land in one frame while every later one glided",
                "a transition on each column being sized",
                fit.Animation?.Asked?.ToString() ?? "(not measured)",
                fit.ToString());

            ParityAssert.True(fit.Animation is { Asked.StillAnimating: false },
                "the transition comes off the table once the fit has settled",
                "the class is on the table, so anything else that writes a column width inherits it - a resize drag most of all, which would then lag 200ms behind the pointer",
                "the class removed",
                fit.Animation?.Asked?.ToString() ?? "(not measured)",
                fit.ToString());
        }

        [Fact]
        public void The_automatic_fit_does_not_animate()
        {
            // The other half of the rule, and the reason the parameter exists at all. The fit Once
            // runs is the grid settling into its first layout; animating that reads as a page still
            // loading rather than as an answer to anything.
            var fit = Fitted();

            ParityAssert.True(fit.Animation is { Automatic.Started: 0 },
                "the fit a grid runs on its own transitions nothing",
                "an animation is a response to something a user did, and there is nothing here for it to be a response to - the grid is settling into its first layout, which reads as a page still loading",
                "no transitions at all",
                fit.Animation?.Automatic?.ToString() ?? "(not measured)",
                fit.ToString());
        }

        [Fact]
        public void A_container_too_narrow_for_the_fit_scrolls_rather_than_losing_a_column()
        {
            // The table overflowing and the wrapper scrolling is the intended answer - the fit sizes
            // columns to their content and does not compress them back. What is not intended is the
            // bare column vanishing: a col with no width in an overflowed table is given nothing at
            // all, so it renders zero pixels wide and its content is simply not there.
            var fit = Fitted();

            ParityAssert.True(fit.Squeezed is { Scrolls: true },
                "a fit wider than its container overflows rather than compressing back",
                "the whole point of fitting is to size a column to its content - compressing it again would undo the measurement that had just been taken",
                "a table wider than its wrapper",
                fit.Squeezed?.ToString() ?? "(not measured)",
                fit.ToString());

            ParityAssert.True(fit.Squeezed is { Bare: > 0 },
                "the bare column keeps a width when there is no slack left to give it",
                "bareness exists to absorb slack, and when the fitted columns already fill the container there is none - so the column is fitted like the rest rather than left with nothing",
                "a bare column wider than zero",
                fit.Squeezed?.ToString() ?? "(not measured)",
                fit.ToString());
        }

        [Fact]
        public void A_table_the_theme_has_stacked_is_not_fitted()
        {
            // Below the Responsive breakpoint the theme gives the table table-layout:auto and
            // display:block, hides the header and stacks the rows into cards - so a colgroup width
            // decides nothing and there are no header cells to measure. §13 asked for this and the
            // first version of the feature shipped without it.
            var fit = Fitted();

            ParityAssert.True(fit.Stacked is { Answered: null },
                "a fit declines a table that is no longer laid out as one",
                "the sixth instance of this branch's oldest failure was Responsive, whose whole feature the theme scopes under a class - a fit that runs there writes widths nothing reads",
                "null, the answer for a grid there is nothing to measure",
                fit.Stacked?.ToString() ?? "(not measured)",
                fit.ToString());

            // Declining has to mean touching nothing, not answering null after writing.
            ParityAssert.True(fit.Stacked is { WroteNothing: true },
                "declining leaves every column exactly as it found it",
                "an answer of null with widths written is the worst of both - the caller records nothing and the page has changed anyway",
                "every col at the width it had",
                fit.Stacked?.ToString() ?? "(not measured)",
                fit.ToString());
        }

        [Fact]
        public void The_pass_costs_about_what_it_should()
        {
            var fit = Fitted();

            ParityAssert.True(fit.RowsMeasured > 0,
                "the timed pass had rows to walk",
                "a pass over an empty table is fast for a reason that has nothing to do with the feature",
                "rows in the measured table",
                fit.RowsMeasured.ToString(CultureInfo.InvariantCulture),
                fit.ToString());

            // Deliberately loose. §13 records the pass at ~1.7ms plus ~0.03ms a rendered row, which
            // puts this pane near 32ms - but those are numbers to read off a quiet machine, and a CI
            // box asserting one of them is a flaky test rather than a budget. What this catches is the
            // order-of-magnitude regression: a write left inside a read loop, which turns the pass's
            // one layout into one per cell.
            ParityAssert.True(fit.Elapsed < 100,
                "the measure-and-write pass is not an order of magnitude off",
                "every read is batched behind one class toggle, and moving a single write between two of them turns one layout into thousands",
                "under 100ms",
                string.Create(CultureInfo.InvariantCulture, $"{fit.Elapsed}ms"),
                fit.ToString());
        }

        static string Describe(double[] widths) =>
            widths is null ? "(none)" : "[" + string.Join(", ", widths.Select(w => w.ToString(CultureInfo.InvariantCulture))) + "]";

        static double? Height(GridGeometry geometry, string what) => what switch
        {
            "header cell" => geometry.HeaderCell,
            "body cell" => geometry.BodyCell,
            "table" => geometry.Table,
            "toggle cell" => geometry.ToggleCell,
            "toggle cell width" => geometry.ToggleCellWidth,
            "toggle button offset" => geometry.ToggleButtonLeft,
            "toggle button width" => geometry.ToggleButtonWidth,
            "data row" => geometry.DataRow,
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "unknown measurement"),
        };

        static string Format(double? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + "px" : "(none)";
    }
}
