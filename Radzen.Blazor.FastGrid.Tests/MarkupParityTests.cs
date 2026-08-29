using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Xunit;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>
    /// The structural half of the parity check: the class names and element nesting the Radzen themes
    /// actually select on.
    /// </summary>
    /// <remarks>
    /// Every rule here is anchored against <c>RadzenDataGrid</c> rendered over the same data in the same
    /// run, so the assertions cannot drift into describing a contract Radzen does not keep. The one
    /// exception is the scrollable-wrapper rule, which is deliberately asymmetric and says so.
    /// </remarks>
    [Collection(GridParityCollection.Name)]
    public sealed class MarkupParityTests
    {
        readonly GridParityFixture fixtures;

        public MarkupParityTests(GridParityFixture fixtures) => this.fixtures = fixtures;

        /// <summary>A class token that alternates per row, e.g. <c>rz-datatable-odd</c> / <c>-even</c>.</summary>
        static readonly Regex AlternatingClass =
            new(@"(^|-)(odd|even)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        [Fact]
        public void Both_grids_render_the_same_columns_over_the_same_rows()
        {
            // Not a styling rule - the anchor that makes every comparison below like-for-like. If the two
            // grids were fed different shapes, matching geometry would prove nothing.
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                var titles = grid.QuerySelectorAll("thead th span.rz-column-title-content")
                    .Select(e => e.TextContent.Trim()).ToArray();

                ParityAssert.True(titles.SequenceEqual(GridParityFixture.ColumnTitles),
                    $"{grid.Name} renders the columns under test",
                    "the two grids must be compared over identical data",
                    string.Join(", ", GridParityFixture.ColumnTitles),
                    titles.Length == 0 ? "(no column titles found)" : string.Join(", ", titles));

                var rows = grid.QuerySelectorAll("tbody > tr").Count;

                ParityAssert.True(rows == GridParityFixture.RowCount,
                    $"{grid.Name} renders the rows under test",
                    "the two grids must be compared over identical data",
                    $"{GridParityFixture.RowCount} rows",
                    $"{rows} rows");
            }
        }

        [Fact]
        public void Table_carries_rz_grid_table_and_rz_grid_table_striped()
        {
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                var table = grid.QuerySelector("table");

                ParityAssert.True(table is not null, $"{grid.Name} renders a table",
                    "everything below hangs off the table element",
                    "one <table>", "none", ParityAssert.Excerpt(grid.Root));

                foreach (var required in new[] { "rz-grid-table", "rz-grid-table-striped" })
                {
                    ParityAssert.True(table.ClassList.Contains(required),
                        $"{grid.Name} table carries '{required}'",
                        required == "rz-grid-table"
                            ? "every cell and header rule in the theme is scoped under .rz-grid-table; without it the grid is unstyled"
                            : "row striping is a :nth-child rule hung off this table-level class, so dropping it silently unstripes every row",
                        $"table class contains '{required}'",
                        $"class=\"{table.ClassName}\"",
                        ParityAssert.OpeningTag(table));
                }
            }
        }

        [Fact]
        public void Rows_carry_rz_data_row()
        {
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                foreach (var row in grid.QuerySelectorAll("tbody > tr"))
                {
                    ParityAssert.True(row.ClassList.Contains("rz-data-row"),
                        $"{grid.Name} rows carry 'rz-data-row'",
                        "selection, hover and focus styling are all scoped to tr.rz-data-row",
                        "class contains 'rz-data-row'",
                        $"class=\"{row.ClassName}\"",
                        ParityAssert.OpeningTag(row));
                }
            }
        }

        [Fact]
        public void Rows_carry_no_alternating_odd_or_even_class()
        {
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                var rows = grid.QuerySelectorAll("tbody > tr");

                // Named form: the classes Radzen's older markup used, which the current theme stripes
                // without.
                foreach (var row in rows)
                {
                    var alternating = ParityAssert.ClassTokens(row).Where(t => AlternatingClass.IsMatch(t)).ToArray();

                    ParityAssert.True(alternating.Length == 0,
                        $"{grid.Name} rows carry no alternating odd/even class",
                        "striping is a :nth-child rule off rz-grid-table-striped; computing a class per row is both wrong and paid for on every row",
                        "no odd/even class token",
                        $"found '{string.Join("', '", alternating)}' in class=\"{row.ClassName}\"",
                        ParityAssert.OpeningTag(row));
                }

                // General form: catches any per-row varying class, whatever it is called.
                var distinct = rows.Select(ParityAssert.NormalizeClasses).Distinct(StringComparer.Ordinal).ToArray();

                ParityAssert.True(distinct.Length == 1,
                    $"{grid.Name} rows all carry the same classes",
                    "a class that varies row to row is an alternating class by another name, and striping does not need one",
                    "one class list across all rows",
                    $"{distinct.Length} distinct class lists: \"{string.Join("\", \"", distinct)}\"");
            }
        }

        [Fact]
        public void Cells_are_gridcell_tds_wrapping_a_rz_cell_data_span()
        {
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                foreach (var row in grid.QuerySelectorAll("tbody > tr"))
                {
                    var cells = row.Children;

                    ParityAssert.True(cells.Length == GridParityFixture.ColumnTitles.Length,
                        $"{grid.Name} rows have one cell per column",
                        "a row that is short or long of cells breaks the fixed table layout",
                        $"{GridParityFixture.ColumnTitles.Length} cells",
                        $"{cells.Length} cells",
                        ParityAssert.Excerpt(row));

                    foreach (var cell in cells)
                    {
                        ParityAssert.True(cell.LocalName == "td",
                            $"{grid.Name} cells are <td>",
                            "every body-cell rule in the theme is written as '.rz-grid-table td ...'",
                            "<td>", $"<{cell.LocalName}>", ParityAssert.OpeningTag(cell));

                        ParityAssert.True(cell.GetAttribute("role") == "gridcell",
                            $"{grid.Name} cells carry role=\"gridcell\"",
                            "the grid role model is what assistive technology reads the table through",
                            "role=\"gridcell\"",
                            cell.GetAttribute("role") is null ? "no role attribute" : $"role=\"{cell.GetAttribute("role")}\"",
                            ParityAssert.OpeningTag(cell));

                        var span = cell.Children.FirstOrDefault(c =>
                            c.LocalName == "span" && c.ClassList.Contains("rz-cell-data"));

                        ParityAssert.True(span is not null,
                            $"{grid.Name} cells wrap their content in <span class=\"rz-cell-data\">",
                            "'.rz-grid-table td .rz-cell-data' is what sets the cell's colour, font size, line height and ellipsis truncation; text placed straight in the td gets none of it",
                            "a direct child <span class=\"rz-cell-data\">",
                            cell.Children.Length == 0
                                ? "the td has no element children"
                                : $"direct children: {string.Join(", ", cell.Children.Select(ParityAssert.OpeningTag))}",
                            ParityAssert.Excerpt(cell));
                    }
                }
            }
        }

        [Fact]
        public void Header_cell_keeps_the_load_bearing_th_div_span_chain()
        {
            // The fault this whole check was built around. The theme gives th `padding: 0` and hangs the
            // header padding off a direct child div; lose the div and every class name is still right, the
            // markup diff still looks fine, and the header row silently renders short.
            const string Chain = "th > div:not(.rz-cell-filter) > span.rz-column-title > span.rz-column-title-content";

            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                var matches = grid.QuerySelectorAll("thead " + Chain).Count;
                var headers = grid.QuerySelectorAll("thead th");

                ParityAssert.True(matches == GridParityFixture.ColumnTitles.Length,
                    $"{grid.Name} header cells keep the '{Chain}' chain",
                    "the theme gives th padding:0 and puts the header padding on a direct child div, so losing the div renders the header row short while leaving every class name correct",
                    $"{GridParityFixture.ColumnTitles.Length} header cells matching the chain",
                    $"{matches} matched, out of {headers.Count} header cells",
                    headers.Count == 0 ? null : ParityAssert.Excerpt(headers[0]));

                // Pin the div as the th's own first element child, so a chain buried under some other
                // wrapper cannot pass: the padding rule is `th > div`, one level, no deeper.
                foreach (var th in headers)
                {
                    var first = th.FirstElementChild;

                    ParityAssert.True(first is not null && first.LocalName == "div",
                        $"{grid.Name} header cell's first element child is the padding div",
                        "the theme's header padding rule is 'th > div', a direct child selector",
                        "<div> directly inside the th",
                        first is null ? "the th has no element children" : ParityAssert.OpeningTag(first),
                        ParityAssert.Excerpt(th));
                }
            }
        }

        [Fact]
        public void Fast_grid_wrapper_does_not_claim_rz_datatable_scrollable()
        {
            // Asymmetric on purpose: RadzenDataGrid *does* carry this class, and earns it with the nested
            // scroll container the variant's CSS expects. The rule is that the class must not be claimed
            // without that structure - a class that lies about the markup around it.
            foreach (var grid in new[] { fixtures.DataGrid, fixtures.FastGrid })
            {
                var claimants = grid.QuerySelectorAll(".rz-datatable-scrollable");

                foreach (var claimant in claimants)
                {
                    var scrollContainer = claimant.QuerySelector(".rz-data-grid-data[role='grid']");

                    ParityAssert.True(scrollContainer is not null,
                        $"{grid.Name} does not claim 'rz-datatable-scrollable' without the scrollable structure",
                        "that variant's CSS positions a separate scrolling header, body and footer; on flat markup the class is simply a lie about what is underneath it, and the styling it selects has nothing to act on",
                        "either no 'rz-datatable-scrollable', or the nested '.rz-data-grid-data[role=grid]' container it implies",
                        "'rz-datatable-scrollable' claimed with no scroll container inside",
                        ParityAssert.OpeningTag(claimant));
                }
            }

            // RadzenFastGrid renders flat markup, so for it the rule above reduces to: never claim it.
            ParityAssert.True(fixtures.FastGrid.QuerySelectorAll(".rz-datatable-scrollable").Count == 0,
                "RadzenFastGrid wrapper does not claim 'rz-datatable-scrollable'",
                "the grid renders one flat table with no scroll container, so the class describes structure that is not there",
                "no element carrying 'rz-datatable-scrollable'",
                $"class=\"{fixtures.FastGrid.Root.ClassName}\"",
                ParityAssert.OpeningTag(fixtures.FastGrid.Root));
        }
    }
}
