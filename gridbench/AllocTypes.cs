using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen.Blazor;

// §28's remaining question, asked of the runtime rather than of a hypothesis.
//
// §28 established a mechanism and stopped: unoptimised code allocates ~48.6 bytes per row that
// optimised code does not. It could not name the object, and said so - "boxing elimination is the
// obvious candidate ... but that is a hypothesis with no measurement behind it and is written here as
// one". Five knobs off and "what is left is" carries no positive evidence.
//
// This is the positive evidence. The runtime raises `GCAllocationTick` roughly every 100 KB with the
// *type* of the object that crossed the threshold, so a tick count per type is a sample of allocated
// bytes weighted by volume - which is exactly the shape needed to compare two codegen configurations
// of the same code and read off what changed.
//
// The comparison is one variable, established in §28's own switch table and re-confirmed before this
// probe was written:
//
//   DOTNET_TieredCompilation=1 DOTNET_TC_CallCounting=0   nothing is ever promoted   rung A, 14,157 KB
//   DOTNET_TieredCompilation=0                            everything starts optimised  rung B, 14,110 KB
//
// Same binary, same rows, same columns; 47.4 KB apart at 1000 rows, which is the step. Anything that
// differs between the two histograms is the step or is noise, and the arithmetic below says which.
sealed class AllocationListener : EventListener
{
    // The runtime's own source. GCAllocationTick lives under the GC keyword at Verbose - anything less
    // gets the collection events and not the allocation ones.
    const string SourceName = "Microsoft-Windows-DotNETRuntime";
    const EventKeywords GCKeyword = (EventKeywords)0x1;

    static readonly Dictionary<string, Tally> Collected = new(StringComparer.Ordinal);

    public sealed class Tally
    {
        public long Ticks;
        public long ObjectBytes;
        public readonly SortedDictionary<long, long> Sizes = new();
    }

    EventSource source;
    volatile bool armed;

    // Set once, from the first tick that carries them, so the payload shape is reported rather than
    // assumed. A probe that silently read the wrong index would produce a confident histogram of
    // nothing, which is the failure §24 and §27 both record in other forms.
    public static string PayloadShape { get; private set; }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
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

        // Read by name rather than by index. The event is versioned - V1 through V4 carry different
        // payloads - and an index that is TypeName in one version is a heap number in another.
        var names = e.PayloadNames;

        if (names is null)
        {
            return;
        }

        PayloadShape ??= string.Join(", ", names);

        string type = null;
        long size = 0;

        for (var i = 0; i < names.Count && i < e.Payload.Count; i++)
        {
            if (names[i] == "TypeName")
            {
                type = e.Payload[i] as string;
            }
            else if (names[i] == "ObjectSize")
            {
                size = e.Payload[i] switch
                {
                    ulong u => (long)u,
                    long l => l,
                    uint u => u,
                    int v => v,
                    _ => 0,
                };
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
            tally.Sizes.TryGetValue(size, out var n);
            tally.Sizes[size] = n + 1;
        }
    }

    public static Dictionary<string, Tally> Drain()
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

static class AllocTypes
{
    public static async Task Run(int n, int iterations)
    {
        using var listener = new AllocationListener();

        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);

        // Identical to PoolProbe.RenderOnce and to FastGridFeatureBench.ReferenceDataGrid, for the same
        // reason PoolProbe says so: nothing but this comment links them, and a workload that drifted
        // would explain a step that is not the step.
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

        // The first render of a process is 21 MB of one-off work and belongs to nothing - §28's ladder
        // excludes it and so does this. Two more after it, unarmed, so anything the harness does once
        // is behind us before the histogram starts.
        for (var i = 0; i < 3; i++)
        {
            await RenderOnce();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        listener.Arm();

        for (var i = 0; i < iterations; i++)
        {
            await RenderOnce();
        }

        listener.Disarm();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var tallies = AllocationListener.Drain();
        var ticks = tallies.Values.Sum(t => t.Ticks);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{iterations} renders of {n} rows, {allocated / 1024.0 / 1024.0:0.00} MB allocated on this thread"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ticks} allocation ticks, {allocated / 1024.0 / Math.Max(ticks, 1):0.0} KB of allocation each"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"per render: {allocated / 1024.0 / iterations:0.0} KB, {ticks / (double)iterations:0.00} ticks"));
        Console.WriteLine($"payload: {AllocationListener.PayloadShape}");
        Console.WriteLine();

        // Sorted by tick count, which is the quantity that is proportional to allocated bytes. The
        // object size column is what says whether a difference is one object per row or two.
        Console.WriteLine("   ticks   per render        %   size(s)  type");
        Console.WriteLine("  ------  -----------  -------  --------  ----");

        foreach (var (type, tally) in tallies.OrderByDescending(p => p.Value.Ticks))
        {
            var sizes = string.Join("/", tally.Sizes.OrderByDescending(s => s.Value).Take(2)
                .Select(s => s.Key.ToString(CultureInfo.InvariantCulture)));

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {tally.Ticks,6}  {tally.Ticks / (double)iterations,11:0.000}  {100.0 * tally.Ticks / Math.Max(ticks, 1),6:0.00}%  {sizes,8}  {type}"));
        }
    }
}
