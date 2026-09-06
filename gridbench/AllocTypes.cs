using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

// §28's remaining question, asked of the runtime rather than of a hypothesis.
//
// §28 established a mechanism and stopped: unoptimised code allocates ~48.6 bytes per row that
// optimised code does not. It could not name the object, and said so - "boxing elimination is the
// obvious candidate ... but that is a hypothesis with no measurement behind it and is written here as
// one". Five knobs off and "what is left is" carries no positive evidence.
//
// The runtime raises `GCAllocationTick` roughly every 100 KB with the *type* of the object that crossed
// the threshold, so a tick count per type is a sample of allocated bytes weighted by volume - the shape
// needed to compare two codegen configurations of the same code and read off what changed.
//
// **The comparison is one variable only after a correction, and choosing the pins took two.** The
// obvious tier-0 pin is `DOTNET_TC_CallCounting=0`, and it is not a pin: nothing is *counted*, but OSR
// still promotes a long-running loop, so a process leaves rung A partway through a run and the arm
// silently becomes a mixture. `DOTNET_TC_CallCountingDelayMs=600000` holds it. And the two pins differ
// in a second way that has to be ruled out rather than assumed - the tier-0 side compiles *Instrumented*
// Tier0, so PGO's instrumentation is a candidate for the whole step.
//
//   DOTNET_TC_CallCountingDelayMs=600000   never promoted            rung A, 14,157 KB
//   DOTNET_TieredCompilation=0             optimised from render 1   rung B, 14,110 KB
//   DOTNET_TieredPGO=0 + the delay         uninstrumented tier-0     rung A - the confound is dead
//
// **Every run prints its own KB per render, and that is not decoration.** It is the check that the arm
// held the rung it was supposed to: an arm that drifted is a mixture, and its histogram is of two
// configurations at once. §30 quotes it for every run it reports.
sealed class AllocListener : EventListener
{
    // The runtime's own source. GCAllocationTick lives under the GC keyword at Verbose - anything less
    // gets the collection events and not the allocation ones.
    const string SourceName = "Microsoft-Windows-DotNETRuntime";
    const EventKeywords GCKeyword = (EventKeywords)0x1;

    // Static and initialised eagerly, for the reason PoolListener gives for the same shape:
    // `OnEventSourceCreated` can fire from the base constructor, before this class's own field
    // initialisers have run, and an instance field would still be null there.
    static readonly Dictionary<string, Tally> Collected = new(StringComparer.Ordinal);
    static string payloadShape;

    public sealed class Tally
    {
        public long Ticks;
        public long ObjectBytes;

        // Large-object allocations tick once per object rather than once per ~100 KB, so they are not
        // the same unit as the rest and are counted apart rather than averaged in. Nothing in §30's
        // argument is an LOH type, but a `%` column that silently mixed two units would be wrong in a
        // way nobody could see.
        public long LargeTicks;
        public readonly SortedDictionary<long, long> Sizes = new();
    }

    EventSource source;
    volatile bool armed;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Kept, not enabled. Nothing is collected until Arm(), so the warm-up renders pay nothing.
        if (eventSource.Name == SourceName)
        {
            source = eventSource;
        }
    }

    public void Arm()
    {
        // A probe that cannot reach its own instrument has to say so rather than print zeroes: without
        // this the report below is a header, a rule and no rows, which reads exactly like "the type is
        // absent" - which is the finding this probe exists to make.
        if (source is null)
        {
            throw new InvalidOperationException(
                $"'{SourceName}' never announced itself, so no allocation tick can arrive and every "
                + "row below would be missing for a reason that has nothing to do with the subject.");
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

        // Read by name rather than by index. The event is versioned - V1 through V4 carry different
        // payloads - and an index that is TypeName in one version is a heap number in another.
        var names = e.PayloadNames;

        if (names is null || e.Payload is null)
        {
            return;
        }

        string type = null;
        long size = 0;
        var large = false;

        for (var i = 0; i < names.Count && i < e.Payload.Count; i++)
        {
            switch (names[i])
            {
                case "TypeName":
                    type = e.Payload[i] as string;
                    break;
                case "ObjectSize":
                    size = Number(e.Payload[i]);
                    break;
                // 1 is the large-object heap. Read rather than ignored, so the units below stay honest.
                case "AllocationKind":
                    large = Number(e.Payload[i]) == 1;
                    break;
            }
        }

        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        lock (Collected)
        {
            // Inside the lock with everything else, so this file has one locking discipline rather than
            // one and an exception. Ticks arrive on whichever thread allocated.
            payloadShape ??= string.Join(", ", names);

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

            tally.Sizes.TryGetValue(size, out var n);
            tally.Sizes[size] = n + 1;
        }
    }

    static long Number(object value) => value switch
    {
        ulong u => (long)u,
        long l => l,
        uint u => u,
        int v => v,
        _ => 0,
    };

    public (Dictionary<string, Tally> Tallies, string Shape) Drain()
    {
        lock (Collected)
        {
            var taken = new Dictionary<string, Tally>(Collected, StringComparer.Ordinal);
            Collected.Clear();
            return (taken, payloadShape);
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
        using var listener = new AllocListener();

        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);

        // Shared with the other probe of this row rather than copied into both, so the two cannot drift
        // from each other. What it must stay identical to and why nothing enforces it are said where
        // the helper is declared.
        Task RenderOnce() => SlimBench.RenderReferenceDataGrid(services, people);

        // The first render of a process is 21 MB of one-off work and belongs to nothing - §28's ladder
        // excludes it and so does this. Two more after it, unarmed, so anything the harness does once
        // is behind us before the histogram starts.
        for (var i = 0; i < 3; i++)
        {
            await RenderOnce();
        }

        // Taken before Arm(), so the listener's own allocation is inside the window on both arms and
        // cancels in the difference. Measured at +1.5 KB a render against `pool-probe`'s ladder, against
        // a step of 47.5 - which is also why the KB below is comparable with that ladder's rungs.
        var before = GC.GetAllocatedBytesForCurrentThread();

        listener.Arm();

        for (var i = 0; i < iterations; i++)
        {
            await RenderOnce();
        }

        listener.Disarm();

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var (tallies, shape) = listener.Drain();
        var ticks = tallies.Values.Sum(t => t.Ticks);

        // The second half of the same discipline as Arm()'s throw. A run that observed nothing prints a
        // table with no rows, and "no rows" is indistinguishable from the finding.
        if (ticks == 0 || shape is null)
        {
            throw new InvalidOperationException(
                "No GCAllocationTick carried a TypeName, so nothing below would mean anything.");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{iterations} renders of {n} rows, {allocated / 1024.0 / 1024.0:0.00} MB allocated on this thread"));

        // The rung check. A pin that did not hold makes this arm a mixture of two configurations, and
        // the histogram a histogram of neither.
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"per render: {allocated / 1024.0 / iterations:0.0} KB, {ticks / (double)iterations:0.00} ticks"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ticks} allocation ticks, {allocated / 1024.0 / ticks:0.0} KB of allocation each"));
        Console.WriteLine($"payload: {shape}");
        Console.WriteLine();

        // Sorted by tick count, which is the quantity proportional to allocated bytes. Object bytes is
        // printed beside it so a reader can derive bytes per row without trusting this file's
        // arithmetic, and every size group is printed rather than a top-N: a summary that silently
        // drops rows is how a display artefact becomes a finding, which §26 records happening here.
        Console.WriteLine("   ticks   per render        %   LOH   obj bytes  size(s) x count  type");
        Console.WriteLine("  ------  -----------  -------  ----  ----------  ---------------  ----");

        foreach (var (type, tally) in tallies.OrderByDescending(p => p.Value.Ticks))
        {
            var sizes = string.Join(" ", tally.Sizes.OrderByDescending(s => s.Value)
                .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key}x{s.Value}")));

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {tally.Ticks,6}  {tally.Ticks / (double)iterations,11:0.000}  {100.0 * tally.Ticks / ticks,6:0.00}%  {tally.LargeTicks,4}  {tally.ObjectBytes,10}  {sizes,-15}  {type}"));
        }
    }
}
