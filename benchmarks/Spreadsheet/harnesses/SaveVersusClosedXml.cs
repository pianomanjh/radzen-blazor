// The save alone, both engines, armed rather than subtracted.
//
// §42 put ClosedXML's save at 279 MB by subtracting its `InsertData` fill from a fill-and-save. This
// fills both workbooks *outside* the measured window and arms only the write, so neither figure is a
// difference of two larger numbers - and the arms interleave, because the machine drifts.
//
// The `SetValues` arm needs a checkout that has it; against one that does not, drop that arm and the
// `Radzen export` arm falls back to the indexer fill.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using Radzen.Documents.Spreadsheet;

static class SaveVs
{
    const int Rows = 50_000;
    const int Cols = 11;

    static object?[][] Block()
    {
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

        return block;
    }

    static Workbook FillRadzen(object?[][] block)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", Rows, Cols);

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                sheet.Cells[r, c].Value = block[r][c];
            }
        }

        return workbook;
    }

    static Workbook FillRadzenBulk(object?[][] block)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", Rows, Cols);

        sheet.Cells.SetValues(0, 0, block);

        return workbook;
    }

    static XLWorkbook FillClosedXml(object?[][] block)
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Sheet1");

        sheet.Cell(1, 1).InsertData(block);

        return workbook;
    }

    // The fill is done and thrown away before the clock starts; only the write is inside.
    static (double Mb, double Ms, long Bytes) Measure(Func<Action<Stream>> prepare)
    {
        var save = prepare();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var stream = new MemoryStream();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        save(stream);

        watch.Stop();

        return ((GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0,
            watch.Elapsed.TotalMilliseconds, stream.Length);
    }

    public static void Run()
    {
        var block = Block();

        var arms = new (string Name, Func<Action<Stream>> Prepare)[]
        {
            // The fill arms do their work inside the returned action, so the same window covers it.
            ("Radzen fill", () => _ => FillRadzen(block)),
            ("Radzen SetValues", () => _ => FillRadzenBulk(block)),
            ("ClosedXML fill", () => _ => FillClosedXml(block).Dispose()),
            ("Radzen SaveToStream", () =>
            {
                var workbook = FillRadzenBulk(block);

                return stream => workbook.SaveToStream(stream);
            }),
            ("ClosedXML SaveAs", () =>
            {
                var workbook = FillClosedXml(block);

                return stream => workbook.SaveAs(stream);
            }),
            // The whole export, measured rather than added up.
            ("Radzen export", () => stream => FillRadzenBulk(block).SaveToStream(stream)),
            ("ClosedXML export", () => stream =>
            {
                using var workbook = FillClosedXml(block);

                workbook.SaveAs(stream);
            }),
        };

        foreach (var (_, prepare) in arms)
        {
            Measure(prepare);
        }

        var results = new Dictionary<string, List<(double Mb, double Ms, long Bytes)>>();

        foreach (var (name, _) in arms)
        {
            results[name] = [];
        }

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var (name, prepare) in arms)
            {
                results[name].Add(Measure(prepare));
            }
        }

        Console.WriteLine($"{Rows:N0} rows x {Cols} columns = {Rows * Cols:N0} cells, the save only");
        Console.WriteLine();
        Console.WriteLine("  arm                    median MB    median ms    file KB");
        Console.WriteLine("  -------------------  -----------  -----------  ---------");

        foreach (var (name, _) in arms)
        {
            var runs = results[name];

            runs.Sort((a, b) => a.Mb.CompareTo(b.Mb));
            var mb = runs[1].Mb;

            runs.Sort((a, b) => a.Ms.CompareTo(b.Ms));
            var ms = runs[1].Ms;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name,-19}  {mb,10:0.0}  {ms,10:0}  {runs[1].Bytes / 1024.0,9:0}"));
        }
    }
}

static class Program
{
    static void Main() => SaveVs.Run();
}
