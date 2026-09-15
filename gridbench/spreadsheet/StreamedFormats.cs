using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;

// Adapted from StreamedExportFloor.cs: prebuilt values, sink, calibration, warm-up and interleaved passes.
// Compile with FORMATTED_ROWS when the referenced checkout supports tuple rows.
static class Program
{
    const int Rows = 50_000;
    const int Cols = 11;
    const int Passes = 3;

    sealed class Sink : Stream
    {
        long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => written += buffer.Length;
        public override void WriteByte(byte value) => written++;
    }

    sealed class Wide { public object? a, b, c, d, e, f, g, h, i, j; }
    sealed class Narrow { public object? a, b; }
    record Measurement(long Allocated, double Ms, int Gen0, int Gen1, int Gen2, long Output);

    static async Task Main()
    {
        var block = new CellData?[Rows][];
        var seed = new DateTime(2020, 1, 1);
        for (var r = 0; r < Rows; r++)
        {
            var line = new CellData?[Cols];
            for (var c = 0; c < Cols; c++)
            {
                line[c] = (c % 4) switch
                {
                    0 => CellData.FromString("row " + r + " col " + c),
                    1 => CellData.FromNumber(r * Cols + c + 0.1234),
                    2 => CellData.FromDate(seed.AddDays(r % 900)),
                    _ => CellData.FromBoolean((r + c) % 2 == 0),
                };
            }
            block[r] = line;
        }

        var arms = new List<(string Name, Func<Task<long>> Work)>
        {
            ("values-shared", () => SaveValues(block, false)),
            ("values-inline", () => SaveValues(block, true)),
        };
#if FORMATTED_ROWS
        arms.Add(("tuples-null-inline", () => SaveFormats(block, true, "null")));
        arms.Add(("formats-reused-shared", () => SaveFormats(block, false, "reused")));
        arms.Add(("formats-reused-inline", () => SaveFormats(block, true, "reused")));
        arms.Add(("formats-fresh-inline", () => SaveFormats(block, true, "fresh")));
#endif
        arms.Add(("calibration", Calibration));

        Console.WriteLine($"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; serverGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"rows={Rows}; columns={Cols}; cells={Rows * Cols}; passes={Passes}; warmups=1");
        Console.WriteLine("arm,pass,allocated_bytes,elapsed_ms,gen0,gen1,gen2,output_bytes");
        foreach (var arm in arms)
            await Measure(arm.Work);

        var results = arms.ToDictionary(arm => arm.Name, _ => new List<Measurement>());
        for (var pass = 0; pass < Passes; pass++)
        {
            // Rotate order between passes to reduce fixed-order bias.
            for (var offset = 0; offset < arms.Count; offset++)
            {
                var arm = arms[(offset + pass) % arms.Count];
                var m = await Measure(arm.Work);
                results[arm.Name].Add(m);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{arm.Name},{pass + 1},{m.Allocated},{m.Ms:F3},{m.Gen0},{m.Gen1},{m.Gen2},{m.Output}"));
            }
        }
        Console.WriteLine("MEDIANS: arm,allocated_bytes,elapsed_ms");
        foreach (var arm in arms)
        {
            var runs = results[arm.Name];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{arm.Name},{runs.Select(m => m.Allocated).Order().ElementAt(Passes / 2)},{runs.Select(m => m.Ms).Order().ElementAt(Passes / 2):F3}"));
        }
        const long Cells = (long)Rows * Cols;
        const long Arithmetic = Cells * 96 + Cells * 32 + 2 * (24 + Cells * 8);
        Console.WriteLine($"calibration_expected_bytes={Arithmetic}");
    }

    static Workbook Frame()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 1, Cols);
        for (var c = 0; c < Cols; c++)
            sheet.Cells[0, c].SetValue("Column " + c);
        return workbook;
    }

    static async Task<long> SaveValues(CellData?[][] block, bool inline)
    {
        using var sink = new Sink();
        await Frame().SaveToStreamAsync(sink, Walk(block), inline).ConfigureAwait(false);
        return CheckOutput(sink.Length);
    }

    static async IAsyncEnumerable<CellData?[]> Walk(CellData?[][] block)
    {
        foreach (var line in block)
            yield return line;
        await Task.CompletedTask.ConfigureAwait(false);
    }

#if FORMATTED_ROWS
    static Format ColumnFormat(int column) => new()
    {
        NumberFormat = (column % 4) switch { 1 => "$#,##0.0000", 2 => "yyyy-mm-dd", _ => null },
    };

    static async Task<long> SaveFormats(CellData?[][] block, bool inline, string mode)
    {
        using var sink = new Sink();
        await Frame().SaveToStreamAsync(sink, Formatted(block, mode), inline).ConfigureAwait(false);
        return CheckOutput(sink.Length);
    }

    static async IAsyncEnumerable<(CellData? Data, Format? Format)[]> Formatted(CellData?[][] block, string mode)
    {
        // One row buffer and one format per column, unless explicitly measuring the fresh-format mistake.
        var row = new (CellData? Data, Format? Format)[Cols];
        var formats = new Format?[Cols];
        if (mode == "reused")
            for (var c = 0; c < Cols; c++)
                formats[c] = ColumnFormat(c);
        foreach (var line in block)
        {
            for (var c = 0; c < Cols; c++)
                row[c] = (line[c], mode == "fresh" ? ColumnFormat(c) : formats[c]);
            yield return row;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }
#endif

    static long CheckOutput(long bytes)
    {
        if (bytes < 100_000)
            throw new InvalidOperationException($"Only {bytes} output bytes; the arm measured nothing.");
        return bytes;
    }

    static Task<long> Calibration()
    {
        var wide = new object[Rows * Cols];
        var narrow = new object[Rows * Cols];
        for (var i = 0; i < wide.Length; i++)
        {
            wide[i] = new Wide();
            narrow[i] = new Narrow();
        }
        GC.KeepAlive(wide);
        GC.KeepAlive(narrow);
        return Task.FromResult(0L);
    }

    static async Task<Measurement> Measure(Func<Task<long>> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        var watch = Stopwatch.StartNew();
        var output = await work().ConfigureAwait(false);
        watch.Stop();
        return new Measurement(GC.GetTotalAllocatedBytes(precise: true) - before, watch.Elapsed.TotalMilliseconds,
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2, output);
    }
}
