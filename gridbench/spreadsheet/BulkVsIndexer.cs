using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Radzen.Documents.Spreadsheet;

const int Rows = 50_000;
const int Cols = 11;

// One block of mixed types, built once so neither arm pays for it.
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
    var mb = (GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0;

    return (mb, watch.Elapsed.TotalMilliseconds);
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

void Bulk()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, block);
}

// Warm up, then interleave the arms so a drifting machine cannot favour one.
Measure(Indexer);
Measure(Bulk);

var indexer = new List<(double Mb, double Ms)>();
var bulk = new List<(double Mb, double Ms)>();

for (var pass = 0; pass < 3; pass++)
{
    indexer.Add(Measure(Indexer));
    bulk.Add(Measure(Bulk));
}

static void Report(string name, List<(double Mb, double Ms)> runs)
{
    foreach (var (mb, ms) in runs)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {name,-22} {mb,8:0.0} MB  {ms,8:0} ms"));
    }
}

Console.WriteLine($"{Rows:N0} rows x {Cols} columns = {Rows * Cols:N0} cells");
Console.WriteLine();
Report("a cell at a time", indexer);
Report("SetValues", bulk);
