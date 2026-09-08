using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Radzen.Documents.Spreadsheet;

// What keying the dependency graph on an address costs a shift. Today the graph needs no maintenance
// during an insert, because the Cell objects it keys on move themselves. Keyed on an address it has to
// be remapped, which is O(edges).

const int Rows = 20_000;
const int Formulas = 10_000;
const int RangeWidth = 10;   // edges per formula

// A sheet of values with a column of range formulas over them: 10,000 formulas x 10 = 100,000 edges.
static Worksheet Build()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, 4);

    for (var r = 0; r < Formulas + RangeWidth; r++)
    {
        sheet.Cells[r, 0].Value = r;
    }

    for (var r = 0; r < Formulas; r++)
    {
        sheet.Cells[r, 1].Formula = $"=SUM(A{r + 1}:A{r + RangeWidth})";
    }

    return sheet;
}

// The same sheet without the formula column, so the control really is one.
static Worksheet BuildPlain()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, 4);

    for (var r = 0; r < Formulas + RangeWidth; r++)
    {
        sheet.Cells[r, 0].Value = r;
    }

    return sheet;
}

static (double Mb, double Ms) Measure(Func<Worksheet> build, Action<Worksheet> shift)
{
    var sheet = build();          // outside the window: this is the fixture, not the operation

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();

    shift(sheet);

    watch.Stop();

    GC.KeepAlive(sheet);

    return ((GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0,
        watch.Elapsed.TotalMilliseconds);
}

var arms = new (string Name, Func<Worksheet> Build, Action<Worksheet> Shift)[]
{
    // Every formula's text changes, so the graph is rebuilt for all of them either way.
    ("insert at the top", Build, sheet => sheet.InsertRow(0)),
    // Nothing a formula names moves, so today this costs the graph nothing at all.
    ("insert at the bottom", Build, sheet => sheet.InsertRow(Rows - 1)),
    // The control: an empty graph, so the change cannot reach it. Carried in both builds.
    ("insert, no formulas", BuildPlain, sheet => sheet.InsertRow(0)),
};

var results = new Dictionary<string, List<(double Mb, double Ms)>>();

foreach (var (name, build, shift) in arms)
{
    results[name] = [];
    Measure(build, shift);
}

for (var pass = 0; pass < 3; pass++)
{
    foreach (var (name, build, shift) in arms)
    {
        results[name].Add(Measure(build, shift));
    }
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{Formulas:N0} range formulas x {RangeWidth} = {Formulas * RangeWidth:N0} edges over {Rows:N0} rows"));
Console.WriteLine();
Console.WriteLine("  shift                   median MB    median ms");
Console.WriteLine("  ---------------------  ----------  -----------");

foreach (var (name, _, _) in arms)
{
    var runs = results[name];

    runs.Sort((a, b) => a.Mb.CompareTo(b.Mb));
    var mb = runs[1].Mb;

    runs.Sort((a, b) => a.Ms.CompareTo(b.Ms));
    var ms = runs[1].Ms;

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-21}  {mb,10:0.00}  {ms,11:0.0}"));
}
