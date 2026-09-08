using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Radzen.Documents.Spreadsheet;

const int Rows = 50_000;
const int Cols = 11;

// One block of mixed types, built before anything is measured, so every arm is charged for the sheet
// it builds and none of them for the test data.
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

void RadzenIndexer()
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

void RadzenBulk()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, block);
}

static void Set(IXLCell cell, object? value)
{
    switch (value)
    {
        case null: break;
        case string s: cell.Value = s; break;
        case int i: cell.Value = i; break;
        case double d: cell.Value = d; break;
        case bool b: cell.Value = b; break;
        case DateTime t: cell.Value = t; break;
        default: cell.Value = value.ToString(); break;
    }
}

void ClosedXmlCell()
{
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Sheet1");

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            Set(ws.Cell(r + 1, c + 1), block[r][c]);
        }
    }
}

void ClosedXmlInsertData()
{
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Sheet1");

    ws.Cell(1, 1).InsertData(block);
}

// Fill and save, which is what an export actually does.
void RadzenExport()
{
    var workbook = new Workbook();
    var sheet = workbook.AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, block);

    using var stream = new MemoryStream();

    workbook.SaveToStream(stream);
}

void ClosedXmlExport()
{
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Sheet1");

    ws.Cell(1, 1).InsertData(block);

    using var stream = new MemoryStream();

    wb.SaveAs(stream);
}

var arms = new (string Name, Action Fill)[]
{
    ("Radzen, a cell at a time", RadzenIndexer),
    ("Radzen, SetValues", RadzenBulk),
    ("ClosedXML, a cell at a time", ClosedXmlCell),
    ("ClosedXML, InsertData", ClosedXmlInsertData),
    ("Radzen, fill + save", RadzenExport),
    ("ClosedXML, fill + save", ClosedXmlExport),
};

foreach (var (_, fill) in arms)
{
    Measure(fill);
}

var results = new Dictionary<string, List<(double Mb, double Ms)>>();

foreach (var (name, _) in arms)
{
    results[name] = [];
}

// Interleaved: one pass of every arm, three times over, so a drifting machine cannot favour one.
for (var pass = 0; pass < 3; pass++)
{
    foreach (var (name, fill) in arms)
    {
        results[name].Add(Measure(fill));
    }
}

Console.WriteLine($"{Rows:N0} rows x {Cols} columns = {Rows * Cols:N0} cells, ClosedXML 0.104.2");
Console.WriteLine();
Console.WriteLine("  arm                            median MB    median ms");
Console.WriteLine("  ---------------------------  -----------  -----------");

foreach (var (name, _) in arms)
{
    var runs = results[name];

    runs.Sort((a, b) => a.Mb.CompareTo(b.Mb));
    var mb = runs[1].Mb;

    runs.Sort((a, b) => a.Ms.CompareTo(b.Ms));
    var ms = runs[1].Ms;

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  {name,-27}  {mb,10:0.0}  {ms,10:0}"));
}
