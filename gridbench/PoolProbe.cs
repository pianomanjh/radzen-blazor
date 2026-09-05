using System;
using System.Buffers;
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

// §11's measurement debt, paid. The `= RadzenDataGrid, same columns` reference row is bimodal - two
// stable values about 990 KB apart - and README recorded the cause as `RenderTreeBuilder`'s pooled frame
// arrays for three sessions, as an inference from a GC correlation, saying it would stand only "until
// something measures the pool directly".
//
// This measures it directly. `ArrayPool<T>` has an EventSource of its own, so a rental becomes a bucket
// with a size and a reason rather than a shape guessed at from a GC column.
//
// It runs in two passes, and the split is the point. **The listener is not cheap** - subscribing to
// every ArrayPool event costs about a third of this render, because the render rents thousands of small
// buffers. So the allocation ladder is measured with the listener disarmed, which makes its numbers the
// render's own and directly comparable with the benchmark row this is trying to explain; the pool
// questions are then asked in a second pass with it armed, where only the *differences* matter and a
// constant overhead cancels. Reporting instrumented allocation beside benchmark allocation was the
// first version of this file's mistake, and §26 records it.
sealed class PoolListener : EventListener
{
    // ArrayPool's own source. Named rather than matched by type because the generic instantiation is
    // not visible from here - every `ArrayPool<T>.Shared` reports through this one source, with a
    // poolId to tell them apart.
    const string SourceName = "System.Buffers.ArrayPoolEventSource";

    // Only the rental events have the (bufferId, bufferSize, poolId, ...) shape this reads. BufferTrimPoll
    // carries (durationMs, pressure) instead, and firing a gen2 - which this probe does deliberately - is
    // exactly when it arrives, so reading it as a rental invents a buffer that never existed.
    static readonly HashSet<string> Rentals =
        new() { "BufferRented", "BufferAllocated", "BufferReturned", "BufferTrimmed", "BufferDropped" };

    public sealed record Rental(string What, int BufferSize, int PoolId, string Reason);

    // Static and initialised eagerly: `OnEventSourceCreated` can fire from the base constructor, before
    // this class's own field initialisers have run, and an instance field would still be null there.
    static readonly List<Rental> Collected = new();

    EventSource source;
    volatile bool armed;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Kept, not enabled. Nothing is collected until Arm(), so the disarmed pass pays nothing.
        if (eventSource.Name == SourceName)
        {
            source = eventSource;
        }
    }

    public void Arm()
    {
        if (source is not null && !armed)
        {
            armed = true;
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!armed || e.EventSource?.Name != SourceName || e.EventName is null || !Rentals.Contains(e.EventName))
        {
            return;
        }

        int At(int i) => e.Payload is not null && e.Payload.Count > i && e.Payload[i] is int v ? v : -1;

        lock (Collected)
        {
            Collected.Add(new Rental(e.EventName, At(1), At(2), Reason(e)));
        }
    }

    // BufferAllocated's last payload item says why the pool could not serve the rental, as the
    // underlying integer of an enum this assembly cannot see. Named here so the output says something.
    static string Reason(EventWrittenEventArgs e) =>
        e.EventName == "BufferAllocated" && e.Payload is { Count: > 4 } && e.Payload[4] is int reason
            ? reason switch
            {
                0 => "pooled",
                1 => "over-maximum",
                2 => "exhausted",
                _ => "other",
            }
            : "";

    public Rental[] Drain()
    {
        lock (Collected)
        {
            var taken = Collected.ToArray();
            Collected.Clear();
            return taken;
        }
    }

    public override void Dispose()
    {
        if (source is not null && armed)
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
        using var listener = new PoolListener();

        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);
        var frameSize = Unsafe.SizeOf<RenderTreeFrame>();

        // This must stay identical to `FastGridFeatureBench.ReferenceDataGrid` - same container, same
        // rows, same columns, same renderer. That row is the thing this probe exists to explain, and
        // nothing links them but this comment: CI never compiles gridbench, so a parameter added there
        // and not here would make the explanation be of a different workload, silently.
        async Task RenderOnce()
        {
            using var r = new BenchmarkRenderer(services);

            await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Data"] = people,
                    ["Columns"] = SlimBench.RadzenColumnsForComparison,
                }));
        }

        Console.WriteLine($"RenderTreeFrame is {frameSize} bytes.");
        Console.WriteLine();

        // ---- Pass 1: the allocation ladder, uninstrumented -------------------------------------
        //
        // Listener disarmed, so these are the render's own bytes and can be read against the
        // benchmark's MB/op. This is the pass that answers what the bimodal step is.
        Console.WriteLine($"Allocation per render, listener disarmed ({n} rows):");
        Console.WriteLine();
        Console.WriteLine("   #   alloc KB        MB   g0 g1 g2");
        Console.WriteLine("  --  ---------  --------   --------");

        for (var i = 0; i < iterations; i++)
        {
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);
            var before = GC.GetAllocatedBytesForCurrentThread();

            await RenderOnce();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i,2}  {allocated / 1024.0,9:0.0}  {allocated / 1048576.0,8:0.00}   {GC.CollectionCount(0) - g0,3}{GC.CollectionCount(1) - g1,3}{GC.CollectionCount(2) - g2,3}"));
        }

        // ---- Pass 2: what the pool does ---------------------------------------------------------
        //
        // Armed from here. Every figure below is a difference between two armed measurements, so the
        // listener's own cost is a constant on both sides and cancels.
        listener.Arm();

        // Which pool is the frame pool, established rather than assumed. The events carry a poolId and
        // no element type, so every `ArrayPool<T>.Shared` looks alike in them. Renting a known size from
        // the frame pool makes it name itself; matching on that size rather than taking whatever event
        // arrived first means a buffer some other thread rented in the same instant cannot answer for it.
        listener.Drain();

        var named = ArrayPool<RenderTreeFrame>.Shared.Rent(1024);
        var framePool = listener.Drain()
            .Where(e => e.What == "BufferRented" && e.BufferSize >= 1024)
            .Select(e => (int?)e.PoolId)
            .FirstOrDefault();

        ArrayPool<RenderTreeFrame>.Shared.Return(named);

        if (framePool is null)
        {
            // A probe that cannot identify its own subject has to say so rather than print zeroes: an
            // unidentified pool would silently make every count below read as "nothing happened".
            throw new InvalidOperationException(
                "ArrayPool<RenderTreeFrame>.Shared reported no rental, so the frame pool could not be " +
                "identified and nothing below would mean anything.");
        }

        Console.WriteLine();
        Console.WriteLine($"ArrayPool<RenderTreeFrame>.Shared is pool {framePool}. Frame-pool events only:");
        Console.WriteLine();
        Console.WriteLine("   #   what the pool did");
        Console.WriteLine("  --  ------------------");

        for (var i = 0; i < 4; i++)
        {
            // A gen2 before the last one: the recorded hypothesis was that the arrays not surviving
            // between iterations is what makes the next render re-grow them, and a gen2 is what makes
            // ArrayPool let go. If that were the mechanism, this is where it would show.
            var trimmed = i == 3;

            if (trimmed)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }

            listener.Drain();
            await RenderOnce();

            Console.WriteLine($"  {i,2}  {Summarise(listener.Drain(), framePool.Value, frameSize)}"
                + (trimmed ? "   (gen2 forced before this one)" : ""));
        }

        // ---- Pass 3: what a pool miss actually costs ---------------------------------------------
        //
        // Forcing a gen2 does not reliably make ArrayPool let go - it trims on its own pressure
        // heuristics, and above it keeps everything through a forced collection. So rather than argue
        // with the runtime, take the buffers away: rent the same buckets and hold them, and the next
        // render's rental cannot be served from the pool. That makes a miss observable on demand, and
        // its size is the whole question.
        //
        // Held and never returned: the process exits at the end of this method, and returning them would
        // refill the pool this measurement depends on being empty.
        var held = new List<RenderTreeFrame[]>();

        foreach (var size in new[] { 65536, 32768, 16384, 8192 })
        {
            held.Add(ArrayPool<RenderTreeFrame>.Shared.Rent(size));
            held.Add(ArrayPool<RenderTreeFrame>.Shared.Rent(size));
        }

        listener.Drain();

        var steadyBefore = GC.GetAllocatedBytesForCurrentThread();
        await RenderOnce();
        var steadyEvents = listener.Drain();
        var steady = GC.GetAllocatedBytesForCurrentThread() - steadyBefore;

        Console.WriteLine();
        Console.WriteLine("A render whose frame arrays the pool cannot satisfy (armed, so not comparable");
        Console.WriteLine("with pass 1 - read the difference against the armed steady state, not the MB):");
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  allocated {steady / 1024.0:0.0} KB"));
        Console.WriteLine("  " + Summarise(steadyEvents, framePool.Value, frameSize));

        // Reported by bucket rather than as one total. The first version of this file printed a total
        // beside a top-five summary that could not be added up to reach it, and the small-bucket misses
        // that make up most of it never appeared in the line at all - which is how "only the first
        // render allocates" got written down about a render that allocates 1,680 buffers.
        Console.WriteLine();
        Console.WriteLine("  every frame-pool allocation in that render, by bucket:");

        foreach (var group in steadyEvents
            .Where(e => e.What == "BufferAllocated" && e.PoolId == framePool.Value)
            .GroupBy(e => e.BufferSize)
            .OrderByDescending(g => g.Key))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {group.Key,6} x{group.Count(),-5} = {group.Count() * (long)group.Key * frameSize / 1024.0,9:0.0} KB  ({group.First().Reason})"));
        }

        var missed = steadyEvents
            .Where(e => e.What == "BufferAllocated" && e.PoolId == framePool.Value)
            .Sum(e => e.BufferSize * (long)frameSize);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    total {missed / 1024.0:0.0} KB of frame array allocated"));

        GC.KeepAlive(held);
    }

    // Every group, never a truncated top-N: a summary that silently drops rows is how a display artefact
    // becomes a finding, which is recorded in §26 as exactly what happened here.
    static string Summarise(PoolListener.Rental[] events, int framePool, int frameSize)
    {
        var mine = events.Where(e => e.PoolId == framePool).ToArray();

        if (mine.Length == 0)
        {
            return "(no frame-pool events)";
        }

        var parts = mine
            .GroupBy(e => (e.What, e.BufferSize))
            .OrderByDescending(g => g.Key.BufferSize)
            .Select(g => string.Create(CultureInfo.InvariantCulture,
                $"{Short(g.Key.What)}x{g.Count()} {g.Key.BufferSize}({g.Count() * (long)g.Key.BufferSize * frameSize / 1024.0:0.0}KB)"));

        return string.Join(" ", parts);
    }

    static string Short(string what) => what switch
    {
        "BufferRented" => "rent",
        "BufferAllocated" => "ALLOC",
        "BufferReturned" => "ret",
        "BufferTrimmed" => "trim",
        "BufferDropped" => "drop",
        _ => what,
    };
}
