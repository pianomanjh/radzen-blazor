using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Radzen.Documents.Spreadsheet;

namespace Radzen.Benchmarks.Spreadsheet;

// Does the streamed write allocate the same whether it is given ten thousand rows or two hundred
// thousand?
//
// That is the whole claim of §65, and a single arm cannot answer it: a number without a second row
// count beside it is a number that a linear path would also produce. So the row count is the parameter
// and the built save is the arm beside it - the one that is *known* to grow, because it holds a cell
// per value. If the sweep does not move the built arm the instrument is not resolving anything, and
// neither arm's flatness means a thing.
//
// The rows are generated, not stored: an index becomes a row through a reused object, and the strings
// come from a fixed pool of a hundred. Nothing here allocates per row except the writer, which is the
// only reason the writer's own figure can be read off the total.
[MemoryDiagnoser]
public class StreamBenchmarks
{
    private const int Cols = 11;
    private const int Names = 100;

    [Params(10_000, 50_000, 200_000)]
    public int Rows { get; set; }

    private string[] names = null!;
    private DateTime seed;

    private sealed class Row
    {
        public int Index;
        public string Name = string.Empty;
        public DateTime Joined;
        public bool Active;
    }

    [GlobalSetup]
    public void Setup()
    {
        seed = new DateTime(2020, 1, 1);
        names = new string[Names];

        for (var i = 0; i < Names; i++)
        {
            names[i] = "name " + i;
        }
    }

    // One instance, refilled: the source is a row count, not a collection, so the arms measure a writer
    // rather than the cost of holding the data twice.
    private async IAsyncEnumerable<Row> Source()
    {
        var row = new Row();

        for (var i = 0; i < Rows; i++)
        {
            row.Index = i;
            row.Name = names[i % Names];
            row.Joined = seed.AddDays(i % 900);
            row.Active = i % 2 == 0;

            yield return row;
        }

        await Task.CompletedTask;
    }

    private StreamedSheet<Row> Sheet()
    {
        var sheet = new StreamedSheet<Row> { Name = "Sheet1", Rows = Source() };

        for (var c = 0; c < Cols; c++)
        {
            var column = c;

            sheet.Columns.Add(new StreamedColumn<Row>
            {
                Title = "col " + column,
                Value = (column % 4) switch
                {
                    0 => r => r.Name,
                    1 => r => r.Index * Cols + column,
                    2 => r => r.Joined,
                    _ => r => r.Active,
                },
            });
        }

        return sheet;
    }

    // The same sheet with every column reading a pooled string, so the accessors box nothing. What is
    // left is the writer alone, and whether *that* is flat is the question §65 actually asks: the arm
    // above allocates a box per cell because a `Func<T, object?>` is what a column is, and those boxes
    // die in gen0 without ever being written to.
    private StreamedSheet<Row> StringSheet()
    {
        var sheet = new StreamedSheet<Row> { Name = "Sheet1", Rows = Source() };

        for (var c = 0; c < Cols; c++)
        {
            sheet.Columns.Add(new StreamedColumn<Row> { Title = "col " + c, Value = r => r.Name });
        }

        return sheet;
    }

    [Benchmark(Description = "Streamed, nothing boxed")]
    public async Task StreamedStrings()
    {
        await Workbook.SaveToStreamAsync(Stream.Null, StringSheet());
    }

    [Benchmark(Description = "Streamed, no workbook")]
    public async Task Streamed()
    {
        await Workbook.SaveToStreamAsync(Stream.Null, Sheet());
    }

    // The same rows through the model. Present so the sweep has an arm it is known to move: this one
    // holds a value per cell before it writes one.
    [Benchmark(Baseline = true, Description = "Built, then saved")]
    public void Built()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", Rows + 1, Cols);

        var line = new object?[Cols];
        var block = new List<IReadOnlyList<object?>?>(1) { line };

        for (var c = 0; c < Cols; c++)
        {
            line[c] = "col " + c;
        }

        sheet.Cells.SetValues(0, 0, block);

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                line[c] = (c % 4) switch
                {
                    0 => names[r % Names],
                    1 => (double)(r * Cols + c),
                    2 => seed.AddDays(r % 900),
                    _ => (object)(r % 2 == 0),
                };
            }

            sheet.Cells.SetValues(r + 1, 0, block);
        }

        workbook.SaveToStream(Stream.Null);
    }
}
