using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen.FastGrid;

/// <summary>
/// §26's allocation ladder, over <c>RadzenFastGrid</c> instead of <c>RadzenDataGrid</c>.
/// </summary>
/// <remarks>
/// The reference row's ladder has two steps: ~938 bytes per row that dynamic PGO stack-allocates, and
/// ~48.5 bytes per row that tier-0 allocates and optimised code does not. This asks whether the second
/// belongs to the Blazor renderer, which both grids share, or to RadzenDataGrid's own per-row work.
/// Rendered through the same columns <c>FastGridFeatureBench</c> uses rather than a copy of them.
/// </remarks>
static class FastLadder
{
    public static async Task Run(int n, int iterations)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);

        // This must stay the `bare` row of FastGridFeatureBench - same rows, same columns, same renderer -
        // for the same reason PoolProbe carries the equivalent comment about ReferenceDataGrid: CI never
        // compiles gridbench, so a parameter added to that bench's dictionary and not here would make
        // this a control over a different workload, silently. The columns are shared rather than copied
        // (that is what the `internal` on Plain buys); the dictionary is not, so `Data` and anything the
        // bench's `configure` hook adds are the two places that can drift. `Bare()` passes no configure
        // and no columns, which is what makes this the same render today.
        async Task RenderOnce()
        {
            using var r = new BenchmarkRenderer(services);

            await r.RenderComponent(typeof(RadzenFastGrid<Person>), ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Data"] = people,
                    ["ChildContent"] = FastGridFeatureBench.Plain,
                }));
        }

        Console.WriteLine($"RadzenFastGrid allocation per render ({n} rows):");
        Console.WriteLine();
        Console.WriteLine("   #    ctx KB   precise KB");
        Console.WriteLine("  --  --------  -----------");

        for (var i = 0; i < iterations; i++)
        {
            var preciseBefore = GC.GetTotalAllocatedBytes(precise: true);
            var before = GC.GetAllocatedBytesForCurrentThread();

            await RenderOnce();

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var precise = GC.GetTotalAllocatedBytes(precise: true) - preciseBefore;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {i,2}  {allocated / 1024.0,8:0.0}  {precise / 1024.0,11:0.0}"));
        }
    }
}
