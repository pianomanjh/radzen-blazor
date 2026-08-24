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

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    // Renders a multiselect dropdown (Items x Selected) continuously so a profiler can attribute the cost.
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

        for (int i = 0; i < 2; i++) RenderOnce(); // warm up

        Console.WriteLine($"Rendering multiselect dropdown items={items}, selected={selectedCount} in a loop. Ctrl-C to stop.");
        long n = 0;
        while (true)
        {
            RenderOnce();
            if (++n % 20 == 0) Console.WriteLine($"  {n} renders");
        }
    }
}
