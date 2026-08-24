using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Running;
using Bunit;

namespace Radzen.Blazor.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "dropdown")
        {
            ProfileDropDown(args);
            return;
        }

        if (args.Length > 0 && args[0] == "rerender")
        {
            MeasureReRender(args);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    // Measures whether a forced parent re-render (no data change) re-renders all N item components.
    // If the second render allocates about as much as the first, every item re-rendered unnecessarily.
    static void MeasureReRender(string[] args)
    {
        int items = args.Length > 1 && int.TryParse(args[1], out var it) ? it : 500;
        var data = Item.Generate(items);
        var selected = data.Take(10).Select(i => i.Id).ToList();

        static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var b0 = Alloc();
        var host = ctx.RenderComponent<DropDownHost>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.Selected, selected));
        var firstRender = Alloc() - b0;
        var firstCount = host.RenderCount;

        // Force several parent re-renders with NO data change.
        const int reRenders = 10;
        var b1 = Alloc();
        for (int i = 0; i < reRenders; i++)
        {
            host.Render();
        }
        var perReRender = (Alloc() - b1) / (double)reRenders;
        var totalCount = host.RenderCount;

        Console.WriteLine($"Items={items}");
        Console.WriteLine($"First render         : {firstRender / 1024.0,9:F1} KB   (RenderCount {firstCount})");
        Console.WriteLine($"Forced re-render (avg): {perReRender / 1024.0,9:F1} KB   (+{totalCount - firstCount} root renders over {reRenders} forced)");
        Console.WriteLine($"re-render / first     : {perReRender / firstRender,9:P0}");
        Console.WriteLine(perReRender > firstRender * 0.5
            ? ">> A no-op re-render costs about as much as the first: children re-render unnecessarily."
            : ">> A no-op re-render is much cheaper: children are largely skipped.");
    }

    static void ProfileDropDown(string[] args)
    {
        int items = args.Length > 1 && int.TryParse(args[1], out var it) ? it : 500;
        int selectedCount = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 250;
        var data = Item.Generate(items);
        var selected = data.Take(selectedCount).Select(i => i.Id).ToList();

        void RenderOnce()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            var host = ctx.RenderComponent<DropDownHost>(p => p
                .Add(x => x.Data, data)
                .Add(x => x.Selected, selected));
            _ = host.Markup.Length;
        }

        for (int i = 0; i < 2; i++) RenderOnce();

        Console.WriteLine($"Rendering multiselect dropdown items={items}, selected={selectedCount} in a loop. Ctrl-C to stop.");
        long n = 0;
        while (true)
        {
            RenderOnce();
            if (++n % 20 == 0) Console.WriteLine($"  {n} renders");
        }
    }
}
