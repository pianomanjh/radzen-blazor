using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;

// What a streamed export allocates, run against a checkout that carries the fork's slot store and one
// that does not.
//
// The question this answers is whether the slot store still earns its keep. The streamed writer builds
// no cells, so in principle it cannot care which store the library has - but "in principle" is read from
// the code, and the code is what is in question. Run the same program against two checkouts and compare.
//
//     dotnet run -c Release      # with the project reference pointed at one checkout
//     dotnet run -c Release      # and then at the other
//
// The built arm is here for scale and because it is the path the slot store does pay off on: a handler
// that asked for the workbook asked for the model.
//
// The calibration arm touches nothing in Radzen.Blazor and its size is arithmetic, printed beside the
// measurement. If the two checkouts disagree on it, they are not comparable rigs and neither export
// figure may be quoted - that is the whole reason it is here.
static class Program
{
    const int Rows = 50_000;
    const int Cols = 11;

    // A destination that costs nothing to write to, so the download is not measured with the write.
    sealed class Sink : Stream
    {
        private long written;

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

    // Ten reference fields on a 16-byte header: 96 bytes, the size a Cell was measured at.
    sealed class Wide { object? a, b, c, d, e, f, g, h, i, j; }

    // Two reference fields on a 16-byte header: 32 bytes.
    sealed class Narrow { object? a, b; }

    static async Task Main()
    {
        // One block of mixed types, built once so no arm pays for it.
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

        var columns = new List<StreamedColumn<object?[]>>();

        for (var c = 0; c < Cols; c++)
        {
            var index = c;

            columns.Add(new StreamedColumn<object?[]>
            {
                Title = "Column " + c,
                Value = line => line[index],
            });
        }

        async Task Streamed()
        {
            var sheet = new StreamedSheet<object?[]>
            {
                Name = "Sheet1",
                RowCount = Rows,
                Rows = Walk(block),
            };

            foreach (var column in columns)
            {
                sheet.Columns.Add(column);
            }

            using var sink = new Sink();

            await Workbook.SaveToStreamAsync(sink, sheet).ConfigureAwait(false);

            Bytes("streamed", sink.Length);
        }

        void Built()
        {
            var workbook = new Workbook();
            var sheet = workbook.AddSheet("Sheet1", Rows, Cols);

            sheet.Cells.SetValues(0, 0, block);

            using var sink = new Sink();

            workbook.SaveToStream(sink);

            Bytes("built", sink.Length);
        }

        void Calibration()
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
        }

        // Warm up, then interleave the arms so a drifting machine cannot favour one.
        await MeasureAsync(Streamed).ConfigureAwait(false);
        Measure(Built);
        Measure(Calibration);

        var streamed = new List<(double Mb, double Ms)>();
        var built = new List<(double Mb, double Ms)>();
        var calibration = new List<(double Mb, double Ms)>();

        for (var pass = 0; pass < 3; pass++)
        {
            streamed.Add(await MeasureAsync(Streamed).ConfigureAwait(false));
            built.Add(Measure(Built));
            calibration.Add(Measure(Calibration));
        }

        const long Cells = (long)Rows * Cols;
        const long Arithmetic = (Cells * 96) + (Cells * 32) + (2 * (24 + (Cells * 8)));

        Console.WriteLine($"{Rows:N0} rows x {Cols} columns = {Cells:N0} cells");
        Console.WriteLine();
        Report("streamed export", streamed);
        Report("built and saved", built);
        Report("calibration", calibration);
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  calibration by arithmetic {Arithmetic / 1024.0 / 1024.0,8:0.0} MB"));
    }

    static readonly HashSet<string> Seen = [];

    static void Bytes(string arm, long length)
    {
        if (Seen.Add(arm))
        {
            Console.WriteLine($"  {arm} wrote {length:N0} bytes");
        }

        if (length < 100_000)
        {
            throw new InvalidOperationException($"{arm} wrote only {length} bytes; the arm measured nothing");
        }
    }

    static async IAsyncEnumerable<object?[]> Walk(object?[][] block)
    {
        foreach (var line in block)
        {
            yield return line;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    static (double Mb, double Ms) Measure(Action work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        work();

        watch.Stop();
        var mb = (GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0;

        return (mb, watch.Elapsed.TotalMilliseconds);
    }

    static async Task<(double Mb, double Ms)> MeasureAsync(Func<Task> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        await work().ConfigureAwait(false);

        watch.Stop();
        var mb = (GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0;

        return (mb, watch.Elapsed.TotalMilliseconds);
    }

    static void Report(string name, List<(double Mb, double Ms)> runs)
    {
        foreach (var (mb, ms) in runs)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name,-22} {mb,8:0.0} MB  {ms,8:0} ms"));
        }
    }
}
