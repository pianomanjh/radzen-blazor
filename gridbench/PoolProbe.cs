using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen.Blazor;

// §11's measurement debt, paid or refuted. The `= RadzenDataGrid, same columns` reference row is
// bimodal - two stable values about 990 KB apart - and README has recorded the cause as
// `RenderTreeBuilder`'s pooled frame arrays since the first sighting. That has always been an
// inference from a correlation: the low runs record gen1 and gen2 collections and the high one records
// none. A correlation is not a mechanism, and README says so itself: it "stays a hypothesis until
// something measures the pool directly".
//
// This measures it directly. `ArrayPool<T>` has an EventSource of its own, and it says which buffers
// were rented, which had to be allocated because the pool could not satisfy the rental, and which were
// trimmed. So a rental of the frame array stops being a guess about a 990 KB step and becomes a bucket
// with a size and a reason attached to it.
sealed class PoolListener : EventListener
{
    // ArrayPool's own source. Named rather than matched by type because the generic instantiation is
    // not visible from here - every `ArrayPool<T>.Shared` reports through this one source, with a
    // poolId to tell them apart.
    const string SourceName = "System.Buffers.ArrayPoolEventSource";

    public sealed record Rental(string What, int BufferSize, int PoolId, int BucketId, string Reason);

    public readonly List<Rental> Events = new();

    EventSource source;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == SourceName)
        {
            source = eventSource;
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventSource?.Name != SourceName)
        {
            return;
        }

        int At(int i) => e.Payload is not null && e.Payload.Count > i && e.Payload[i] is int v ? v : -1;

        // Payload shapes are ArrayPool's own: (bufferId, bufferSize, poolId, bucketId[, reason]).
        lock (Events)
        {
            Events.Add(new Rental(
                e.EventName ?? "(unnamed)",
                At(1),
                At(2),
                At(3),
                e.Payload is { Count: > 4 } ? e.Payload[4]?.ToString() ?? "" : ""));
        }
    }

    public void Clear()
    {
        lock (Events)
        {
            Events.Clear();
        }
    }

    public Rental[] Drain()
    {
        lock (Events)
        {
            var taken = Events.ToArray();
            Events.Clear();
            return taken;
        }
    }

    public override void Dispose()
    {
        if (source is not null)
        {
            DisableEvents(source);
        }

        base.Dispose();
    }
}

static class PoolProbe
{
    public static async Task Run(int n, int iterations)
    {
        // Attached before anything renders, so the first render's rentals are visible too - the first
        // one is the interesting one if the array is grown rather than pooled.
        using var listener = new PoolListener();

        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);

        var frameSize = Unsafe.SizeOf<RenderTreeFrame>();

        // Which pool is the frame pool, established rather than assumed. The events carry a poolId and
        // no element type, so every `ArrayPool<T>.Shared` looks alike in them. Renting from the frame
        // pool deliberately makes it name itself, and any rental reported under that id afterwards is a
        // frame array rather than something the same length that happens to be pooled too.
        listener.Clear();
        var named = System.Buffers.ArrayPool<RenderTreeFrame>.Shared.Rent(1024);
        var framePool = listener.Drain().Select(e => e.PoolId).FirstOrDefault(-1);
        System.Buffers.ArrayPool<RenderTreeFrame>.Shared.Return(named);

        Console.WriteLine($"RenderTreeFrame is {frameSize} bytes.");
        Console.WriteLine($"ArrayPool<RenderTreeFrame>.Shared is pool {framePool}.");
        Console.WriteLine($"Rendering RadzenDataGrid over {n} rows, {iterations} times.");
        Console.WriteLine();
        Console.WriteLine("  #   alloc KB   g0 g1 g2   pool events");
        Console.WriteLine("  --  ---------  --------   -----------");

        for (var i = 0; i < iterations; i++)
        {
            // Every third iteration, take the pool's buffers away. README's correlation is with gen1
            // and gen2 collections, and a gen2 is what makes ArrayPool drop what it is holding - so if
            // the step is a pooled rental, forcing the collection should force the step.
            var trimmed = i > 0 && i % 3 == 0;

            if (trimmed)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }

            listener.Drain();

            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var before = GC.GetAllocatedBytesForCurrentThread();

            using (var r = new BenchmarkRenderer(services))
            {
                await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
                    new Dictionary<string, object?>
                    {
                        ["Data"] = people,
                        ["Columns"] = SlimBench.RadzenColumnsForComparison,
                    }));
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var events = listener.Drain();

            var summary = Summarise(events, frameSize);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i,2}  {allocated / 1024.0,9:0.0}  {GC.CollectionCount(0) - g0,3}{GC.CollectionCount(1) - g1,3}{GC.CollectionCount(2) - g2,3}   {summary}")
                + (trimmed ? "   (gen2 forced before this one)" : ""));
        }

        Console.WriteLine();

        // The decisive one. Forcing a gen2 does not reliably make `ArrayPool` let go - it trims on a
        // gen2 callback under its own pressure heuristics, and above it kept every buffer through three
        // forced collections. So rather than argue with the runtime, take the buffers away: rent the
        // same buckets and hold them, and the next render's rental cannot be satisfied from the pool
        // and must allocate. That makes a pool miss observable on demand, and its size is the whole
        // question - if the reference row's step is the frame array re-growing from scratch, the step
        // and the miss are the same number.
        Console.WriteLine("A render whose frame arrays the pool cannot satisfy:");

        var held = new List<RenderTreeFrame[]>();

        foreach (var size in new[] { 65536, 32768, 16384, 8192 })
        {
            // Two of each, because the renderer holds one while this holds the other.
            held.Add(System.Buffers.ArrayPool<RenderTreeFrame>.Shared.Rent(size));
            held.Add(System.Buffers.ArrayPool<RenderTreeFrame>.Shared.Rent(size));
        }

        listener.Drain();

        var heldBefore = GC.GetAllocatedBytesForCurrentThread();

        using (var r = new BenchmarkRenderer(services))
        {
            await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Data"] = people,
                    ["Columns"] = SlimBench.RadzenColumnsForComparison,
                }));
        }

        var starved = GC.GetAllocatedBytesForCurrentThread() - heldBefore;
        var starvedEvents = listener.Drain();

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  allocated {starved / 1024.0:0.0} KB with the pool held empty"));
        Console.WriteLine("  " + Summarise(starvedEvents, frameSize));

        var missed = starvedEvents
            .Where(e => e.What == "BufferAllocated")
            .Sum(e => e.BufferSize * (long)frameSize);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  of which the pool had to allocate {missed / 1024.0:0.0} KB of frame array"));

        GC.KeepAlive(held);
    }

    static string Summarise(PoolListener.Rental[] events, int frameSize)
    {
        if (events.Length == 0)
        {
            return "(none)";
        }

        var parts = events
            .GroupBy(e => (e.What, e.BufferSize, e.PoolId))
            .OrderByDescending(g => g.Key.BufferSize)
            .Take(5)
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{Short(g.Key.What)}x{g.Count()} {g.Key.BufferSize}@p{g.Key.PoolId} ({g.Key.BufferSize * (long)frameSize / 1024.0:0.0}KB)"));

        return string.Join(", ", parts);
    }

    static string Short(string what) => what switch
    {
        "BufferRented" => "rent",
        "BufferAllocated" => "ALLOC",
        "BufferReturned" => "ret",
        "BufferTrimmed" => "trim",
        _ => what,
    };
}
