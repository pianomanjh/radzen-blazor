// Where the save's allocation goes, by type.
//
// §42 named the save's cost - about 442 MB over 550,000 cells - without naming a single object in it,
// and it reached that figure by *subtracting* a `SetValues` fill from a fill-and-save. This program
// arms the window around `SaveToStream` alone, so nothing is subtracted; on `upstream/master`, where
// `SetValues` does not exist and the fill is the indexer, it reads 458 MB. The two are the same
// quantity measured two ways, and the direct one is the one to quote about the writer.
//
// `GCAllocationTick` raises the *type* of the object that crossed each ~100 KB boundary, so a tick
// count per type is a sample of allocated bytes weighted by volume: the shape needed to say what the
// writer is spending on before anything is changed. The fill happens outside the armed window, so what
// is reported is `SaveToStream` and nothing else.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using Radzen.Documents.Spreadsheet;

sealed class AllocListener : EventListener
{
    const string SourceName = "Microsoft-Windows-DotNETRuntime";
    const EventKeywords GCKeyword = (EventKeywords)0x1;

    // Static and initialised eagerly: `OnEventSourceCreated` can fire from the base constructor, before
    // this class's own field initialisers have run.
    static readonly Dictionary<string, Tally> Collected = new(StringComparer.Ordinal);

    public sealed class Tally
    {
        public long Ticks;
        public long ObjectBytes;
        public long LargeTicks;
    }

    EventSource? source;
    volatile bool armed;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
        {
            source = eventSource;
        }
    }

    public void Arm()
    {
        // A probe that cannot reach its own instrument has to say so rather than print zeroes, which
        // read exactly like "the type is absent".
        if (source is null)
        {
            throw new InvalidOperationException($"'{SourceName}' never announced itself.");
        }

        if (!armed)
        {
            armed = true;
            EnableEvents(source, EventLevel.Verbose, GCKeyword);
        }
    }

    public void Disarm()
    {
        if (source is not null && armed)
        {
            armed = false;
            DisableEvents(source);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!armed || e.EventName is null
            || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            return;
        }

        // Read by name rather than by index: the event is versioned and an index that is TypeName in one
        // version is a heap number in another.
        var names = e.PayloadNames;

        if (names is null || e.Payload is null)
        {
            return;
        }

        string? type = null;
        long size = 0;
        var large = false;

        for (var i = 0; i < names.Count && i < e.Payload.Count; i++)
        {
            switch (names[i])
            {
                case "TypeName": type = e.Payload[i] as string; break;
                case "ObjectSize": size = Number(e.Payload[i]); break;
                case "AllocationKind": large = Number(e.Payload[i]) == 1; break;
            }
        }

        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        lock (Collected)
        {
            if (!Collected.TryGetValue(type, out var tally))
            {
                Collected[type] = tally = new Tally();
            }

            tally.Ticks++;
            tally.ObjectBytes += size;

            if (large)
            {
                tally.LargeTicks++;
            }
        }
    }

    static long Number(object? value) => value switch
    {
        ulong u => (long)u,
        long l => l,
        uint u => u,
        int v => v,
        _ => 0,
    };

    public Dictionary<string, Tally> Drain()
    {
        lock (Collected)
        {
            var taken = new Dictionary<string, Tally>(Collected, StringComparer.Ordinal);
            Collected.Clear();
            return taken;
        }
    }

    public override void Dispose()
    {
        Disarm();
        base.Dispose();
    }
}

static class SaveAlloc
{
    // One block of mixed types, built before anything is measured, so the run is charged for the sheet
    // and not for the test data.
    static object?[][] Block(int rows, int cols)
    {
        var block = new object?[rows][];
        var seed = new DateTime(2020, 1, 1);

        for (var r = 0; r < rows; r++)
        {
            var line = new object?[cols];

            for (var c = 0; c < cols; c++)
            {
                line[c] = (c % 4) switch
                {
                    0 => "row " + r + " col " + c,
                    1 => r * cols + c,
                    2 => seed.AddDays(r % 900),
                    _ => (r + c) % 2 == 0,
                };
            }

            block[r] = line;
        }

        return block;
    }

    static Workbook Filled(object?[][] block, int rows, int cols)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", rows, cols);

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                sheet.Cells[r, c].Value = block[r][c];
            }
        }

        return workbook;
    }

    public static void Run(int rows, int cols)
    {
        using var listener = new AllocListener();

        var block = Block(rows, cols);

        // A warm-up save on a small sheet, unarmed, so the writer's one-off work - the theme resource,
        // the JIT of every method on the path - is behind us before the histogram starts.
        using (var warm = new MemoryStream())
        {
            Filled(Block(50, cols), 50, cols).SaveToStream(warm);
        }

        var workbook = Filled(block, rows, cols);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var stream = new MemoryStream();

        listener.Arm();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();

        workbook.SaveToStream(stream);

        watch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        listener.Disarm();

        var tallies = listener.Drain();
        var ticks = tallies.Values.Sum(t => t.Ticks);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{rows:N0} rows x {cols} columns = {rows * cols:N0} cells"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"SaveToStream: {allocated / 1024.0 / 1024.0:0.0} MB, {watch.Elapsed.TotalMilliseconds:0} ms, {stream.Length / 1024.0 / 1024.0:0.0} MB written"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ticks:N0} allocation ticks; each is ~100 KB of that total, so the share column is the estimate."));
        Console.WriteLine();
        Console.WriteLine("  type                                                       ticks    share    est MB   avg obj");
        Console.WriteLine("  -------------------------------------------------------  -------  -------  --------  --------");

        foreach (var (type, tally) in tallies.OrderByDescending(p => p.Value.Ticks))
        {
            if (tally.Ticks * 200.0 < ticks)
            {
                continue;
            }

            var share = (double)tally.Ticks / ticks;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {Short(type),-55}  {tally.Ticks,7:N0}  {share,6:P1}  {share * allocated / 1024.0 / 1024.0,8:0.0}  {(double)tally.ObjectBytes / tally.Ticks,8:0}"));
        }
    }

    // Generic type names arrive fully qualified in every argument, which is three lines of assembly
    // names per row and nothing the reader needs.
    static string Short(string type)
    {
        var bracket = type.IndexOf('[', StringComparison.Ordinal);
        var text = bracket >= 0 ? type[..bracket] : type;
        var dot = text.LastIndexOf('.');

        return dot >= 0 && dot < text.Length - 1 ? text[(dot + 1)..] : text;
    }
}

static class Program
{
    static void Main() => SaveAlloc.Run(50_000, 11);
}
