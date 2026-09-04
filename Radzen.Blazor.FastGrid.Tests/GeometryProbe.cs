using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>Rendered box heights for one grid, in CSS pixels.</summary>
    public sealed class GridGeometry
    {
        [JsonPropertyName("grid")] public string Grid { get; set; }

        [JsonPropertyName("headerCell")] public double? HeaderCell { get; set; }

        [JsonPropertyName("bodyCell")] public double? BodyCell { get; set; }

        [JsonPropertyName("table")] public double? Table { get; set; }

        [JsonPropertyName("headerCellPadding")] public string HeaderCellPadding { get; set; }

        [JsonPropertyName("rowCount")] public int RowCount { get; set; }

        /// <summary>The row-detail toggle cell, or null on a pane with no row detail.</summary>
        [JsonPropertyName("toggleCell")] public double? ToggleCell { get; set; }

        /// <summary>The first body row, which is what a toggle cell taller than its neighbours moves.</summary>
        [JsonPropertyName("dataRow")] public double? DataRow { get; set; }

        /// <summary>The toggle cell's width - what an empty element with horizontal padding changes.</summary>
        [JsonPropertyName("toggleCellWidth")] public double? ToggleCellWidth { get; set; }

        /// <summary>
        /// The toggle button's offset inside its cell, and its width. The cell sits in a
        /// table-layout:fixed table so its own box cannot reveal its contents; the button's can, because
        /// anything in the cell that takes space moves it.
        /// </summary>
        [JsonPropertyName("toggleButtonLeft")] public double? ToggleButtonLeft { get; set; }

        [JsonPropertyName("toggleButtonWidth")] public double? ToggleButtonWidth { get; set; }

        /// <summary>
        /// What happened to a frozen cell when its container was scrolled sideways. Null on a pane with
        /// no scroll container. The whole feature is this number being zero.
        /// </summary>
        [JsonPropertyName("frozenHold")] public FrozenHold FrozenHold { get; set; }

        /// <summary>
        /// Whether the frozen column is the element actually on top where a scrolled column passes
        /// under it, in the header and in the body. Null on a pane with no frozen column.
        /// </summary>
        [JsonPropertyName("frozenOverlap")] public FrozenOverlap FrozenOverlap { get; set; }

        /// <summary>
        /// Every data cell's width in the first body row, including the toggle cell. A colgroup missing
        /// a col for the toggle column shifts all of these by one, which no markup assertion can see.
        /// </summary>
        [JsonPropertyName("dataCellWidths")] public double[] DataCellWidths { get; set; }

        /// <summary>
        /// The computed background of a selected cell and of an unselected one. Geometry cannot see a
        /// selected row that is not painted - the theme's rule is nested inside <c>.rz-selectable</c>,
        /// so the highlight class can be on exactly the right element and mean nothing. Null on a pane
        /// with no selection.
        /// </summary>
        [JsonPropertyName("selectedRowBackground")] public string SelectedRowBackground { get; set; }

        [JsonPropertyName("unselectedRowBackground")] public string UnselectedRowBackground { get; set; }

        /// <summary>
        /// What is drawn at the keyboard cursor. Null on a pane with no focused cell. Radzen's theme
        /// draws nothing here on a read-only grid - the row rule is nested inside <c>.rz-selectable</c>
        /// and there is no cell rule at all - so this is the only check that can tell a working cursor
        /// from a correctly-classed invisible one.
        /// </summary>
        [JsonPropertyName("focus")] public FocusPaint Focus { get; set; }

        public override string ToString()
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{Grid}: header {HeaderCell}px, body {BodyCell}px, table {Table}px ({RowCount} rows)");

            return ToggleCell is null
                ? text
                : text + string.Create(CultureInfo.InvariantCulture,
                    $", toggle {ToggleCell}x{ToggleCellWidth}px, row {DataRow}px, button +{ToggleButtonLeft}x{ToggleButtonWidth}px");
        }
    }

    /// <summary>What the keyboard cursor is painted as, against what its neighbours are painted as.</summary>
    public sealed class FocusPaint
    {
        [JsonPropertyName("outline")] public string Outline { get; set; }

        [JsonPropertyName("otherOutline")] public string OtherOutline { get; set; }

        [JsonPropertyName("background")] public string Background { get; set; }

        [JsonPropertyName("otherRowBackground")] public string OtherRowBackground { get; set; }

        /// <summary>Whether the focused cell is the element painted at its own rect, once scrolled.</summary>
        [JsonPropertyName("onTop")] public bool? OnTop { get; set; }

        /// <summary>What was painted there instead, so a failure names the element that won.</summary>
        [JsonPropertyName("onTopWas")] public string OnTopWas { get; set; }

        public override string ToString() =>
            $"outline '{Outline}' against '{OtherOutline}', background '{Background}' against " +
            $"'{OtherRowBackground}', on top: {(OnTop is null ? "(not measured)" : OnTop.ToString())}, hit {OnTopWas ?? "(nothing tried)"}";
    }

    /// <summary>Which cell wins where a frozen column and a scrolled one overlap.</summary>
    public sealed class FrozenOverlap
    {
        /// <summary>How many rows were examined, so an empty result cannot pass for a clean one.</summary>
        [JsonPropertyName("rowsChecked")] public int RowsChecked { get; set; }

        /// <summary>How many columns the title row says are pinned; zero means nothing was tested.</summary>
        [JsonPropertyName("pinnedColumns")] public int PinnedColumns { get; set; }

        /// <summary>The rows where something scrolling under the frozen column was drawn over it.</summary>
        [JsonPropertyName("covered")] public string[] Covered { get; set; }

        public override string ToString() => Covered is { Length: > 0 }
            ? $"{RowsChecked} rows x {PinnedColumns} pinned columns, covered in: {string.Join(", ", Covered)}"
            : $"{RowsChecked} rows x {PinnedColumns} pinned columns, none covered";
    }

    /// <summary>How far a pinned cell and a loose one moved when the grid was scrolled sideways.</summary>
    public sealed class FrozenHold
    {
        [JsonPropertyName("scrolled")] public double Scrolled { get; set; }

        [JsonPropertyName("frozenMoved")] public double FrozenMoved { get; set; }

        [JsonPropertyName("unfrozenMoved")] public double UnfrozenMoved { get; set; }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"scrolled {Scrolled}px: frozen moved {FrozenMoved}px, unfrozen moved {UnfrozenMoved}px");
    }

    /// <summary>One stylesheet request the page made, and how it came back.</summary>
    public sealed class StylesheetLoad
    {
        [JsonPropertyName("url")] public string Url { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        public override string ToString() => $"{Status} {Url}";
    }

    /// <summary>One survey of the auto-fit pane: what its columns are, and what is truncated in them.</summary>
    public sealed class AutoFitSurvey
    {
        /// <summary>Rendered width of each column, taken off the header row.</summary>
        [JsonPropertyName("widths")] public double[] Widths { get; set; }

        /// <summary>The table's own rendered width, which is what the bare column keeps constant.</summary>
        [JsonPropertyName("tableWidth")] public double TableWidth { get; set; }

        /// <summary>
        /// How many cells of each column are drawing an ellipsis, by column index. Absent means none,
        /// which is what a fitted column is supposed to be.
        /// </summary>
        [JsonPropertyName("truncated")] public Dictionary<string, int> Truncated { get; set; } = new();

        public int TruncatedIn(int column) =>
            Truncated is not null && Truncated.TryGetValue(column.ToString(CultureInfo.InvariantCulture), out var n)
                ? n
                : 0;

        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"table {TableWidth}px, ") +
            "widths [" + string.Join(", ", Widths ?? Array.Empty<double>()) + "], truncated " +
            (Truncated is null || Truncated.Count == 0
                ? "nowhere"
                : string.Join(", ", Truncated.Select(pair => $"column {pair.Key}: {pair.Value}")));
    }

    /// <summary>A fit with no MinWidth anywhere, squeezed past what the columns can give.</summary>
    /// <summary>What a fit did when one column was left out of it.</summary>
    public sealed class AutoFitWithReserved
    {
        [JsonPropertyName("widths")] public double[] Widths { get; set; }
        [JsonPropertyName("total")] public double Total { get; set; }
        [JsonPropertyName("room")] public double Room { get; set; }

        /// <summary>The width of the column that was not fitted, which it must keep.</summary>
        [JsonPropertyName("reservedColumn")] public double ReservedColumn { get; set; }

        [JsonPropertyName("fitsTheContainer")] public bool FitsTheContainer { get; set; }
        [JsonPropertyName("floorTotal")] public double FloorTotal { get; set; }

        public override string ToString() =>
            $"[{(Widths is null ? "-" : string.Join("/", Widths.Select(w => w.ToString("0"))))}]" +
            $" total {Total:0} in {Room:0}, reserved column {ReservedColumn:0}px, floor {FloorTotal:0}" +
            (FitsTheContainer ? string.Empty : ", OVERFLOWS");
    }

    public sealed class AutoFitPressure
    {
        [JsonPropertyName("pane")] public int Pane { get; set; }
        [JsonPropertyName("widths")] public double[] Widths { get; set; }
        [JsonPropertyName("narrowest")] public double Narrowest { get; set; }
        [JsonPropertyName("total")] public double Total { get; set; }

        /// <summary>The table's min-width: the sum of every hard floor.</summary>
        [JsonPropertyName("floorTotal")] public double FloorTotal { get; set; }

        /// <summary>Which headings are ellipsised at this width.</summary>
        [JsonPropertyName("headings")] public bool[] Headings { get; set; }

        /// <summary>Which values are ellipsised at this width.</summary>
        [JsonPropertyName("values")] public bool[] Values { get; set; }

        static string Clipped(bool[] flags) =>
            flags is null || !flags.Any(f => f)
                ? "none"
                : string.Join(",", flags.Select((f, i) => (f, i)).Where(p => p.f).Select(p => p.i));

        public override string ToString() =>
            $"{Pane}px [{(Widths is null ? "-" : string.Join("/", Widths.Select(w => w.ToString("0"))))}]" +
            $" total {Total:0} against floor {FloorTotal:0}," +
            $" headings clipped {Clipped(Headings)}, values clipped {Clipped(Values)}";
    }

    public sealed class AutoFitDefaultFloor
    {
        /// <summary>Under mild pressure, where the soft floor should still hold every heading.</summary>
        [JsonPropertyName("eased")] public AutoFitPressure Eased { get; set; }

        /// <summary>Past what the columns can give, where a heading may be spent but a value may not.</summary>
        [JsonPropertyName("hard")] public AutoFitPressure Hard { get; set; }

        /// <summary>Whether the hardest squeeze actually reached every hard floor.</summary>
        [JsonPropertyName("restsOnItsFloor")] public bool RestsOnItsFloor { get; set; }

        [JsonPropertyName("headingsHoldWhenEased")] public bool HeadingsHoldWhenEased { get; set; }
        [JsonPropertyName("valuesHoldWhenHard")] public bool ValuesHoldWhenHard { get; set; }
        [JsonPropertyName("narrowest")] public double Narrowest { get; set; }

        public override string ToString() => $"eased {Eased}; hard {Hard}";
    }

    /// <summary>One container width, and what the columns became at it.</summary>
    public sealed class AutoFitStep
    {
        [JsonPropertyName("pane")] public int Pane { get; set; }
        [JsonPropertyName("widths")] public double[] Widths { get; set; }

        /// <summary>Whether the required columns still have the width they were measured at.</summary>
        [JsonPropertyName("requiredHeld")] public bool RequiredHeld { get; set; }

        /// <summary>Whether every best-effort column is still at or above its floor.</summary>
        [JsonPropertyName("aboveFloor")] public bool AboveFloor { get; set; }

        [JsonPropertyName("scrolls")] public bool Scrolls { get; set; }
        [JsonPropertyName("total")] public double Total { get; set; }

        public override string ToString() =>
            $"{Pane}px -> [{(Widths is null ? "-" : string.Join("/", Widths.Select(w => w.ToString("0"))))}]" +
            $" total {Total:0}" +
            (RequiredHeld ? string.Empty : ", REQUIRED MOVED") +
            (AboveFloor ? string.Empty : ", BELOW FLOOR") +
            (Scrolls ? ", scrolls" : string.Empty);
    }

    /// <summary>A fit that keeps the table inside its container, swept across container widths.</summary>
    public sealed class AutoFitToContainer
    {
        /// <summary>The widths at full size, which the required columns must keep at every step.</summary>
        [JsonPropertyName("wide")] public double[] Wide { get; set; }

        [JsonPropertyName("steps")] public AutoFitStep[] Steps { get; set; }

        public override string ToString() =>
            Steps is null ? "(none)" : string.Join("; ", Steps.Select(s => s.ToString()));
    }

    /// <summary>A fit whose columns cannot fit the container they are in.</summary>
    public sealed class AutoFitSqueezed
    {
        [JsonPropertyName("widths")] public double[] Widths { get; set; }

        /// <summary>The width the bare column ended up with.</summary>
        [JsonPropertyName("bare")] public double Bare { get; set; }

        /// <summary>Whether the table overflowed its wrapper, which is the intended answer.</summary>
        [JsonPropertyName("scrolls")] public bool Scrolls { get; set; }

        public override string ToString() =>
            $"[{(Widths is null ? "-" : string.Join("/", Widths.Select(w => w.ToString("0.#"))))}]" +
            $", bare {Bare:0.#}px, {(Scrolls ? "scrolls" : "does not scroll")}";
    }

    /// <summary>A fit sampled mid-flight, to see whether it moved or jumped.</summary>
    public sealed class AutoFitMotion
    {
        [JsonPropertyName("from")] public double[] From { get; set; }
        [JsonPropertyName("settled")] public double[] Settled { get; set; }

        /// <summary>How many columns actually began a width transition.</summary>
        [JsonPropertyName("started")] public int Started { get; set; }

        /// <summary>Whether the transition class was still on the table after it should have come off.</summary>
        [JsonPropertyName("stillAnimating")] public bool StillAnimating { get; set; }

        public override string ToString() =>
            $"{Show(From)} -> {Show(Settled)}, {Started} transitioned" +
            (StillAnimating ? ", CLASS STILL ON" : string.Empty);

        static string Show(double[] widths) =>
            widths is null ? "-" : string.Join("/", widths.Select(w => w.ToString("0.#")));
    }

    /// <summary>Both halves of the animation rule: an asked-for fit moves, an automatic one does not.</summary>
    public sealed class AutoFitAnimation
    {
        [JsonPropertyName("asked")] public AutoFitMotion Asked { get; set; }
        [JsonPropertyName("automatic")] public AutoFitMotion Automatic { get; set; }

        public override string ToString() => $"asked: {Asked}; automatic: {Automatic}";
    }

    /// <summary>What a fit already watching a container did once that table stopped being one.</summary>
    public sealed class AutoFitAfterStacking
    {
        [JsonPropertyName("before")] public string[] Before { get; set; }
        [JsonPropertyName("after")] public string[] After { get; set; }

        /// <summary>Whether every col came through the resize with the width it went in with.</summary>
        [JsonPropertyName("wroteNothing")] public bool WroteNothing { get; set; }

        public override string ToString() =>
            $"[{(Before is null ? "-" : string.Join("/", Before))}] -> " +
            $"[{(After is null ? "-" : string.Join("/", After))}]" +
            (WroteNothing ? ", untouched" : ", WROTE WIDTHS");
    }

    /// <summary>What a fit did when asked to size a table the theme had stacked into cards.</summary>
    public sealed class AutoFitStacked
    {
        /// <summary>The widths it answered with. Declining is null.</summary>
        [JsonPropertyName("answered")] public string[] Answered { get; set; }

        /// <summary>Whether every col came through the attempt with the width it went in with.</summary>
        [JsonPropertyName("wroteNothing")] public bool WroteNothing { get; set; }

        public override string ToString() =>
            $"answered {(Answered is null ? "null" : "[" + string.Join(", ", Answered) + "]")}, " +
            (WroteNothing ? "wrote nothing" : "WROTE WIDTHS");
    }

    /// <summary>
    /// The auto-fit pane before and after the shipped script was run against it. Null when the page
    /// carried no such pane, which the tests treat as a failure rather than a skip.
    /// </summary>
    public sealed class AutoFitRun
    {
        /// <summary>What a live fit's observer did once the table stopped being one.</summary>
        [JsonPropertyName("stackedWhileWatching")] public AutoFitAfterStacking StackedWhileWatching { get; set; }

        /// <summary>A fit that must share the container with a column it is not fitting.</summary>
        [JsonPropertyName("withReserved")] public AutoFitWithReserved WithReserved { get; set; }

        /// <summary>Where columns stop when nothing has told them how narrow they may go.</summary>
        [JsonPropertyName("defaultFloor")] public AutoFitDefaultFloor DefaultFloor { get; set; }

        /// <summary>What a fit that keeps the table inside its container did as that container shrank.</summary>
        [JsonPropertyName("fittedToContainer")] public AutoFitToContainer FittedToContainer { get; set; }

        /// <summary>What a fit did in a container too narrow to hold its own answer.</summary>
        [JsonPropertyName("squeezed")] public AutoFitSqueezed Squeezed { get; set; }

        /// <summary>The same fit run twice, once asked for and once automatic.</summary>
        [JsonPropertyName("animation")] public AutoFitAnimation Animation { get; set; }

        /// <summary>What the fit did when the table was no longer laid out as one.</summary>
        [JsonPropertyName("stacked")] public AutoFitStacked Stacked { get; set; }

        [JsonPropertyName("before")] public AutoFitSurvey Before { get; set; }

        [JsonPropertyName("after")] public AutoFitSurvey After { get; set; }

        /// <summary>The width strings the script wrote, in the order it was given the columns.</summary>
        [JsonPropertyName("written")] public string[] Written { get; set; }

        /// <summary>How long the whole measure-and-write pass took, in milliseconds.</summary>
        [JsonPropertyName("elapsed")] public double Elapsed { get; set; }

        /// <summary>How many rows it walked, so a fast number cannot come from an empty table.</summary>
        [JsonPropertyName("rowsMeasured")] public int RowsMeasured { get; set; }

        [JsonPropertyName("paneWidth")] public double PaneWidth { get; set; }

        public override string ToString() =>
            $"before: {Before}; after: {After}; wrote [{string.Join(", ", Written ?? Array.Empty<string>())}]; " +
            string.Create(CultureInfo.InvariantCulture,
                $"{Elapsed}ms over {RowsMeasured} rows in a {PaneWidth}px pane");
    }

    /// <summary>What one measurement run read back out of the browser.</summary>
    public sealed class GeometryReport
    {
        /// <summary>Computed value of <c>--rz-grid-cell-padding</c>, a property only the Radzen theme sets.</summary>
        [JsonPropertyName("themeProbe")] public string ThemeProbe { get; set; }

        /// <summary>Computed value of <c>--rz-grid-cell-line-height</c>, the variable the row height is built from.</summary>
        [JsonPropertyName("themeCellHeightProbe")] public string ThemeCellHeightProbe { get; set; }

        [JsonPropertyName("stylesheets")] public List<StylesheetLoad> Stylesheets { get; set; } = new();

        [JsonPropertyName("grids")] public List<GridGeometry> Grids { get; set; } = new();

        [JsonPropertyName("autoFit")] public AutoFitRun AutoFit { get; set; }

        public GridGeometry this[string grid] =>
            Grids.Find(g => g.Grid == grid)
            ?? throw new InvalidOperationException(
                $"The measurement returned no geometry for '{grid}'. Panes measured: " +
                string.Join(", ", Grids.ConvertAll(g => g.Grid)));

        public string Describe() =>
            string.Join(Environment.NewLine, Grids.ConvertAll(g => "  " + g)) + Environment.NewLine +
            "  stylesheets: " + (Stylesheets.Count == 0
                ? "(none requested)"
                : string.Join("; ", Stylesheets.ConvertAll(s => s.ToString())));
    }

    /// <summary>
    /// Runs the Playwright measurement script over a page and parses what it prints.
    /// </summary>
    /// <remarks>
    /// This deliberately has no "skip when the browser is missing" path. A geometry check that quietly
    /// disappears in CI is the exact failure this whole check exists to prevent, so a missing node,
    /// Playwright or Chromium fails the run with a message saying which one.
    /// </remarks>
    static class GeometryProbe
    {
        static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public static GeometryReport Run(string pagePath)
        {
            var script = Path.Combine(AppContext.BaseDirectory, "measure-geometry.js");

            if (!File.Exists(script))
            {
                throw new FileNotFoundException(
                    "The geometry measurement script was not copied to the test output directory.", script);
            }

            var info = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            info.ArgumentList.Add(script);
            info.ArgumentList.Add(pagePath);

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process { StartInfo = info };

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not start 'node' to measure rendered geometry. The parity check needs node and " +
                    "Playwright on the machine running it; it does not fall back to markup-only checking, " +
                    "because the fault it exists to catch is invisible in markup.", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(180_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }

                throw new InvalidOperationException("The geometry measurement did not finish within 180s.");
            }

            process.WaitForExit();

            var output = stdout.ToString();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The geometry measurement failed (node exited {process.ExitCode}).{Environment.NewLine}" +
                    $"{stderr}{output}");
            }

            var start = output.IndexOf('{');

            if (start < 0)
            {
                throw new InvalidOperationException(
                    $"The geometry measurement printed no JSON.{Environment.NewLine}{stderr}{output}");
            }

            var report = JsonSerializer.Deserialize<GeometryReport>(output[start..], Json);

            if (report is null || report.Grids.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The geometry measurement returned no grids.{Environment.NewLine}{output}");
            }

            return report;
        }
    }
}
