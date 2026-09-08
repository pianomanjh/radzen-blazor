using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Radzen.Documents.Spreadsheet;

// Fills 50,000 x 11 cells of mixed types a cell at a time, and reports what that allocates.
// The values are built before the measured region, so what is reported is the sheet's cost and not
// the test data's. Run at each commit of the series to get the ladder.
const int Rows = 50_000;
const int Cols = 11;

var block = new object?[Rows][];
var seed = new DateTime(2020, 1, 1);

for (var r = 0; r < Rows; r++)
{
    var line = new object?[Cols];

    for (var c = 0; c < Cols; c++)
    {
        line[c] = (c % 4) switch
        {
            0 => "row " + r + " col " + c,
            1 => r * Cols + c,
            2 => seed.AddDays(r % 900),
            _ => (r + c) % 2 == 0,
        };
    }

    block[r] = line;
}

static (double Mb, double Ms) Measure(Action fill)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();

    fill();

    watch.Stop();

    return ((GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0,
        watch.Elapsed.TotalMilliseconds);
}

void Indexer()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            sheet.Cells[r, c].Value = block[r][c];
        }
    }
}

Measure(Indexer);

var runs = new List<(double Mb, double Ms)>();

for (var pass = 0; pass < 3; pass++)
{
    runs.Add(Measure(Indexer));
}

foreach (var (mb, ms) in runs)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  a cell at a time  {mb,8:0.0} MB  {ms,7:0} ms"));
}
