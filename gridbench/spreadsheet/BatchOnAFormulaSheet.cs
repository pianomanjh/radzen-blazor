using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Radzen.Documents.Spreadsheet;

// akorchev's case: a small block written to a sheet that carries many formulas none of which read it.
const int Formulas = 5_000;
const int Block = 10;

var block = new object?[Block][];

for (var r = 0; r < Block; r++)
{
    var line = new object?[Block];

    for (var c = 0; c < Block; c++)
    {
        line[c] = r * Block + c;
    }

    block[r] = line;
}

static Worksheet Sheet()
{
    var sheet = new Workbook().AddSheet("Sheet1", Formulas + Block + 2, Block + 2);

    // Unrelated formulas: each reads the column beside the block, never the block itself.
    for (var r = 0; r < Formulas; r++)
    {
        sheet.Cells[r + Block, Block] = sheet.Cells[r + Block, Block];
        sheet.Cells[r + Block, Block].Formula = "=1+" + (r % 7);
    }

    return sheet;
}

static (double Mb, double Ms) Measure(Worksheet sheet, Action<Worksheet> fill)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();

    fill(sheet);

    watch.Stop();

    return ((GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0,
        watch.Elapsed.TotalMilliseconds);
}

void Bulk(Worksheet sheet) => sheet.Cells.SetValues(0, 0, block);

void Batched(Worksheet sheet) => sheet.Batch(() => sheet.Cells.SetValues(0, 0, block));

Measure(Sheet(), Bulk);
Measure(Sheet(), Batched);

var plain = new List<(double Mb, double Ms)>();
var batched = new List<(double Mb, double Ms)>();

for (var pass = 0; pass < 3; pass++)
{
    plain.Add(Measure(Sheet(), Bulk));
    batched.Add(Measure(Sheet(), Batched));
}

Console.WriteLine($"a {Block}x{Block} block written to a sheet carrying {Formulas:N0} unrelated formulas");
Console.WriteLine();

foreach (var (mb, ms) in plain)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  SetValues, no batch      {mb,8:0.00} MB  {ms,8:0.0} ms"));
}

foreach (var (mb, ms) in batched)
{
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  the same inside a batch  {mb,8:0.00} MB  {ms,8:0.0} ms"));
}
