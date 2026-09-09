using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using ClosedXML.Excel;
using Radzen.Documents.Spreadsheet;

namespace Radzen.Benchmarks.Spreadsheet;

// The save against ClosedXML's, and the whole export against its.
//
// This is the comparison BenchmarkDotNet is right for: two libraries, one build, one process, and a
// claim about time as well as allocation. The ladder across commits is not - it compares two builds of
// this library, which one process cannot hold - and neither is naming which type an allocation went to.
// Both of those live in harnesses/, and the README there says why.
[MemoryDiagnoser]
public class SaveBenchmarks
{
    private const int Rows = 50_000;
    private const int Cols = 11;

    private object?[][] block = null!;
    private Workbook workbook = null!;
    private XLWorkbook closedXml = null!;

    // The values are built once and outside every measured region, so what is reported is the sheet's
    // cost and not the test data's.
    [GlobalSetup]
    public void Setup()
    {
        var seed = new DateTime(2020, 1, 1);
        block = new object?[Rows][];

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
    }

    // Filled once, not per iteration. IterationSetup would pin InvocationCount to 1, which is what made
    // an earlier run of this report 707.7 ms with an error of 979.3 ms - an interval wide enough to
    // contain both engines and settle nothing.
    //
    // A workbook can be saved repeatedly at one cost only because the writer leaves it alone. Before the
    // first commit on this branch it did not: the first save gave every cell a format object, so a
    // second save of the same workbook allocated 119 MB less than the first. On a build without that
    // commit these two arms measure a re-save and understate it, and the export arms below are the ones
    // to compare across builds.
    // There is no ClosedXML arm beside this one: SaveAs closes the stream it is given and keeps hold of
    // it, so a second save of the same workbook throws ObjectDisposedException. Measuring it this way
    // would need a new workbook per invocation, which is what the export arms below do for both engines.
    [GlobalSetup(Targets = [nameof(Save)])]
    public void FillBeforeSaving()
    {
        Setup();

        workbook = new Workbook();
        workbook.AddSheet("Sheet1", Rows, Cols).Cells.SetValues(0, 0, block);

        // So the first rent of the sort array is not inside the first measured invocation.
        using var warm = new MemoryStream();
        workbook.SaveToStream(warm);
    }

    // The fill on its own, so the export's collections can be attributed rather than guessed at.
    [Benchmark(Description = "Radzen, fill only")]
    public int Fill()
    {
        var book = new Workbook();
        book.AddSheet("Sheet1", Rows, Cols).Cells.SetValues(0, 0, block);

        return book.Sheets.Count;
    }

    // ClosedXML's bulk entrance beside ours. Bulk against bulk is the honest fill comparison: measured
    // against its cell-at-a-time path instead, this library looks three times cheaper than it is.
    [Benchmark(Description = "ClosedXML, fill only")]
    public int ClosedXmlFill()
    {
        using var book = new XLWorkbook();

        book.AddWorksheet("Sheet1").Cell(1, 1).InsertData(block);

        return book.Worksheets.Count;
    }

    // A first save of a workbook nothing has saved before, which is the only save-only arm that means
    // the same thing on every build: before this branch's first commit a save mutated the workbook, so
    // a second one costs less. IterationSetup pins InvocationCount to 1 and the time column with it -
    // read allocation and collections from these two, not milliseconds.
    [IterationSetup(Targets = [nameof(SaveFirst), nameof(ClosedXmlSaveFirst)])]
    public void FillForOneSave()
    {
        workbook = new Workbook();
        workbook.AddSheet("Sheet1", Rows, Cols).Cells.SetValues(0, 0, block);

        closedXml = new XLWorkbook();
        closedXml.AddWorksheet("Sheet1").Cell(1, 1).InsertData(block);
    }

    [Benchmark(Description = "Radzen, save only, first save")]
    public long SaveFirst()
    {
        using var stream = new MemoryStream();

        workbook.SaveToStream(stream);

        return stream.Length;
    }

    [Benchmark(Description = "ClosedXML, save only, first save")]
    public long ClosedXmlSaveFirst()
    {
        using var stream = new MemoryStream();

        closedXml.SaveAs(stream);

        return stream.Length;
    }

    [Benchmark(Baseline = true, Description = "Radzen, save, workbook filled once")]
    public long Save()
    {
        using var stream = new MemoryStream();

        workbook.SaveToStream(stream);

        return stream.Length;
    }

    // The arms to compare across two builds of the library. Each pays for its own fill, so neither
    // depends on what a previous save left behind, and ClosedXML's is the control: it is the same code
    // in both runs, so if it reads the same the machine did not drift between them.
    [Benchmark(Description = "Radzen, fill and save")]
    public long Export()
    {
        var book = new Workbook();
        book.AddSheet("Sheet1", Rows, Cols).Cells.SetValues(0, 0, block);

        using var stream = new MemoryStream();

        book.SaveToStream(stream);

        return stream.Length;
    }

    [Benchmark(Description = "ClosedXML, fill and save")]
    public long ClosedXmlExport()
    {
        using var book = new XLWorkbook();
        book.AddWorksheet("Sheet1").Cell(1, 1).InsertData(block);

        using var stream = new MemoryStream();

        book.SaveAs(stream);

        return stream.Length;
    }
}
