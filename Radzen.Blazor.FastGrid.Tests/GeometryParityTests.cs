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

            ParityAssert.True(overlap.BodyOnTop,
                "a frozen body cell is drawn over the row scrolling under it",
                "an unfrozen cell in the body is static, so being positioned at all should be enough to win",
                "the frozen cell on top",
                overlap.ToString(),
                report.Describe());

            ParityAssert.True(overlap.HeaderOnTop,
                "a frozen header cell is drawn over the header scrolling under it",
                "every header cell is sticky at z-index 1, so a frozen one ties with its neighbours and the later column wins unless it is raised above them",
                "the frozen header cell on top",
                overlap.ToString(),
                report.Describe());
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
