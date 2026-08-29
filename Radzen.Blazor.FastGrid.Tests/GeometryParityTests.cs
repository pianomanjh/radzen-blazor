using System;
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

        static double? Height(GridGeometry geometry, string what) => what switch
        {
            "header cell" => geometry.HeaderCell,
            "body cell" => geometry.BodyCell,
            "table" => geometry.Table,
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "unknown measurement"),
        };

        static string Format(double? value) =>
            value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + "px" : "(none)";
    }
}
