using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
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



// What is actually left in the save, split by who is paying for it.
//
// Writing to a MemoryStream charges the save for the sink growing by doubling, which is not the writer's
// and is 8 MB of it. Stream.Null takes the sink out of the picture. The two string shapes ask the
// question pre-sizing the shared string table turns on: it is sized by the string cells a sheet holds
// rather than the distinct strings among them.
static class Program
{
    const int Rows = 50_000;
    const int Cols = 11;

    // What an export actually looks like: a description that is distinct per row, a status from a short
    // set, and a label that never varies - so the table holds far fewer entries than there are cells.
    static object?[][] MixedBlock()
    {
        var statuses = new[] { "Open", "Closed", "Pending", "On hold", "Cancelled" };
        var block = new object?[Rows][];

        for (var r = 0; r < Rows; r++)
        {
            var line = new object?[Cols];

            for (var c = 0; c < Cols; c++)
            {
                line[c] = (c % 4) switch
                {
                    0 => "Order " + r + " line " + c,
                    1 => statuses[r % statuses.Length],
                    2 => "Region West",
                    _ => r * Cols + c,
                };
            }

            block[r] = line;
        }

        return block;
    }

    // The case pre-sizing would hurt: every string cell carries the same text, so the shared string
    // table holds one entry however many cells there are.
    static object?[][] RepeatedBlock()
    {
        var block = new object?[Rows][];

        for (var r = 0; r < Rows; r++)
        {
            var line = new object?[Cols];

            for (var c = 0; c < Cols; c++)
            {
                line[c] = "the same text everywhere";
            }

            block[r] = line;
        }

        return block;
    }

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

    static Workbook Fill(object?[][] block)
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

    // One arm shape: the workbook is filled outside the window, the sink is made outside it too.
    static double Measure(object?[][] block, Func<Stream> sink)
    {
        var workbook = Fill(block);
        var stream = sink();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);

        workbook.SaveToStream(stream);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        (stream as IDisposable)?.Dispose();

        return allocated / 1024.0 / 1024.0;
    }

    static void Main()
    {
        var block = Block();

        var arms = new (string Name, Func<Stream> Sink)[]
        {
            ("MemoryStream, grown", () => new MemoryStream()),
            ("MemoryStream, pre-sized 4 MB", () => new MemoryStream(4 * 1024 * 1024)),
            ("Stream.Null, no sink at all", () => Stream.Null),
        };

        foreach (var (_, sink) in arms)
        {
            Measure(block, sink);
        }

        Console.WriteLine($"{Rows:N0} rows x {Cols} columns = {Rows * Cols:N0} cells, the save only");
        Console.WriteLine();
        Console.WriteLine("  sink                            pass 1     pass 2     pass 3");
        Console.WriteLine("  ----------------------------  --------   --------   --------");

        var results = new double[arms.Length, 3];

        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < arms.Length; i++)
            {
                results[i, pass] = Measure(block, arms[i].Sink);
            }
        }

        for (var i = 0; i < arms.Length; i++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {arms[i].Name,-28}  {results[i, 0],8:0.0}   {results[i, 1],8:0.0}   {results[i, 2],8:0.0}"));
        }

        Console.WriteLine();
        Console.WriteLine("  a realistic mix - one distinct column, one of five statuses, one constant:");

        var mixed = MixedBlock();

        for (var pass = 0; pass < 3; pass++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    pass {pass + 1}: {Measure(mixed, () => Stream.Null),0:0.0} MB"));
        }

        Console.WriteLine();
        Console.WriteLine("  every string cell the same text, so the table holds one entry:");

        var repeated = RepeatedBlock();

        for (var pass = 0; pass < 3; pass++)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    pass {pass + 1}: {Measure(repeated, () => Stream.Null),0:0.0} MB"));
        }

        // And what the writer's own allocation is made of, with the sink taken out of the picture.
        using var listener = new AllocListener();

        var workbook = Fill(block);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        listener.Arm();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        workbook.SaveToStream(Stream.Null);

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        listener.Disarm();

        var tallies = listener.Drain();
        var ticks = tallies.Values.Sum(t => t.Ticks);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Into Stream.Null: {allocated / 1024.0 / 1024.0:0.0} MB over {ticks} ticks"));
        Console.WriteLine();
        Console.WriteLine("  type                              ticks    est MB   avg object");
        Console.WriteLine("  ------------------------------  -------  --------  -----------");

        foreach (var (type, tally) in tallies.OrderByDescending(p => p.Value.Ticks))
        {
            var share = (double)tally.Ticks / ticks;
            var name = type.Contains('[') ? type[..type.IndexOf('[', StringComparison.Ordinal)] : type;
            var dot = name.LastIndexOf('.');
            name = dot >= 0 ? name[(dot + 1)..] : name;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {name,-30}  {tally.Ticks,7}  {share * allocated / 1024.0 / 1024.0,8:0.0}  {(double)tally.ObjectBytes / tally.Ticks,11:N0}"));
        }
    }
}
