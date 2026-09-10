using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;

static class Program
{
    static int ImageBytes = 32 * 1024;

    static readonly int[] Steps = [500, 1_000, 2_000];

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

    sealed record Row(int Id, StreamedImage Photo);

    static async Task Main(string[] args)
    {
        if (args.Length > 0)
        {
            ImageBytes = int.Parse(args[0], CultureInfo.InvariantCulture) * 1024;
        }

        var random = new Random(7);
        var rows = new Row[Steps.Max()];

        for (var i = 0; i < rows.Length; i++)
        {
            var data = new byte[ImageBytes];

            random.NextBytes(data);
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);

            rows[i] = new Row(i, new StreamedImage(data));
        }

        async Task<long> Export(int count, bool spill)
        {
            var sheet = new StreamedSheet<Row>
            {
                WidthMode = ColumnWidthMode.Declared,
                RowCount = count,
                DataRowHeight = 48,
                SpillImages = spill,
                Rows = Walk(rows, count),
                Columns =
                {
                    new StreamedColumn<Row> { Title = "Id", Value = r => r.Id },
                    new StreamedColumn<Row> { Title = "Photo", Image = r => r.Photo, Width = 48 },
                },
            };

            using var sink = new Sink();

            await Workbook.SaveToStreamAsync(sink, sheet).ConfigureAwait(false);

            if (sink.Length < (long)count * ImageBytes)
            {
                throw new InvalidOperationException(
                    $"{(spill ? "spill" : "memory")} at {count} wrote {sink.Length} bytes, less than its images");
            }

            return sink.Length;
        }

        await Export(Steps[0], spill: false).ConfigureAwait(false);
        await Export(Steps[0], spill: true).ConfigureAwait(false);

        var memory = Steps.ToDictionary(s => s, _ => new List<double>());
        var spilled = Steps.ToDictionary(s => s, _ => new List<double>());

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var step in Steps)
            {
                memory[step].Add(await MeasureAsync(() => Export(step, spill: false)).ConfigureAwait(false));
                spilled[step].Add(await MeasureAsync(() => Export(step, spill: true)).ConfigureAwait(false));
            }
        }

        Console.WriteLine($"{ImageBytes / 1024} KB distinct images, allocated MB, median of 3 interleaved passes");
        Console.WriteLine();
        Console.WriteLine("  images   memory    spill   image bytes");

        foreach (var step in Steps)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {step,6} {Median(memory[step]),8:0.0} {Median(spilled[step]),8:0.0} {step * (double)ImageBytes / 1024 / 1024,11:0.0}"));
        }

        Console.WriteLine();
        Console.WriteLine("  growth per added image, KB");

        for (var i = 1; i < Steps.Length; i++)
        {
            var added = Steps[i] - Steps[i - 1];

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Steps[i - 1],6} -> {Steps[i],-6} memory {(Median(memory[Steps[i]]) - Median(memory[Steps[i - 1]])) * 1024 / added,8:0.00}"
                + $"  spill {(Median(spilled[Steps[i]]) - Median(spilled[Steps[i - 1]])) * 1024 / added,8:0.00}"
                + $"  image {ImageBytes / 1024.0,6:0.00}"));
        }
    }

    static async IAsyncEnumerable<Row> Walk(Row[] rows, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return rows[i];
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    static double Median(List<double> runs) => runs.Order().ElementAt(runs.Count / 2);

    static async Task<double> MeasureAsync(Func<Task<long>> work)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);

        await work().ConfigureAwait(false);

        return (GC.GetTotalAllocatedBytes(precise: true) - before) / 1024.0 / 1024.0;
    }
}
