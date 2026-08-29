using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{Grid}: header {HeaderCell}px, body {BodyCell}px, table {Table}px ({RowCount} rows)");
    }

    /// <summary>One stylesheet request the page made, and how it came back.</summary>
    public sealed class StylesheetLoad
    {
        [JsonPropertyName("url")] public string Url { get; set; }

        [JsonPropertyName("status")] public int Status { get; set; }

        public override string ToString() => $"{Status} {Url}";
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
