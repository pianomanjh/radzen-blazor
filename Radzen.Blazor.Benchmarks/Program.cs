using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Running;
using Bunit;
using Radzen;

namespace Radzen.Blazor.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "profile")
        {
            Profile(args);
            return;
        }

        if (args.Length > 0 && args[0] == "filter")
        {
            ProfileFilter(args);
            return;
        }

        if (args.Length > 0 && args[0] == "rerender")
        {
            MeasureReRender(args);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    // Measures whether a forced parent re-render (no data change) re-renders all N row components.
    // If the second render allocates about as much as the first, every row re-rendered unnecessarily.
    static void MeasureReRender(string[] args)
    {
        int rows = args.Length > 1 && int.TryParse(args[1], out var it) ? it : 500;
        var data = Person.Generate(rows);

        static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var b0 = Alloc();
        var host = ctx.RenderComponent<GridHost>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.PageSize, rows));
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

        Console.WriteLine($"Rows={rows}");
        Console.WriteLine($"First render          : {firstRender / 1024.0,9:F1} KB   (RenderCount {firstCount})");
        Console.WriteLine($"Forced re-render (avg) : {perReRender / 1024.0,9:F1} KB   (+{totalCount - firstCount} root renders over {reRenders} forced)");
        Console.WriteLine($"re-render / first      : {perReRender / firstRender,9:P0}");
        Console.WriteLine(perReRender > firstRender * 0.5
            ? ">> A no-op re-render costs about as much as the first: children re-render unnecessarily."
            : ">> A no-op re-render is much cheaper: children are largely skipped.");
    }

    // Allocation attribution harness. Renders a real grid and reports how many bytes are allocated
    // building the render tree vs. serialising it to markup (a bUnit-only cost real Blazor never pays).
    // Pass "profile loop" to render continuously so an external profiler (dotnet-trace) can sample it.
    static void Profile(string[] args)
    {
        int rows = 500;
        var data = Person.Generate(rows);

        static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

        RenderedComponent Render(bool serialize)
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            var host = ctx.RenderComponent<GridHost>(p => p
                .Add(x => x.Data, data)
                .Add(x => x.PageSize, rows)
                .Add(x => x.Interactive, 1));
            int markupLen = serialize ? host.Markup.Length : 0;
            return new RenderedComponent(ctx, markupLen);
        }

        // Warm up the JIT and the getter/type caches.
        for (int i = 0; i < 3; i++) { Render(true).Ctx.Dispose(); Render(false).Ctx.Dispose(); }

        const int iterations = 20;

        long buildBytes = 0, serializeBytes = 0;
        for (int i = 0; i < iterations; i++)
        {
            var before = Alloc();
            var r = Render(false);            // build render tree only
            buildBytes += Alloc() - before;
            r.Ctx.Dispose();

            before = Alloc();
            var r2 = Render(true);            // build + serialize to markup
            serializeBytes += Alloc() - before;
            r2.Ctx.Dispose();
        }

        double buildMB = buildBytes / (double)iterations / (1024 * 1024);
        double totalMB = serializeBytes / (double)iterations / (1024 * 1024);
        Console.WriteLine($"Rows={rows}, Interactive=1, iterations={iterations}");
        Console.WriteLine($"Render tree build only : {buildMB,7:F2} MB / render");
        Console.WriteLine($"Build + markup serialize: {totalMB,7:F2} MB / render");
        Console.WriteLine($"Markup serialization    : {totalMB - buildMB,7:F2} MB / render  (bUnit-only; real Blazor does not pay this)");

        if (args.Length > 1 && args[1] == "loop")
        {
            Console.WriteLine("Looping renders for profiler capture. Ctrl-C to stop.");
            long n = 0;
            while (true)
            {
                var r = Render(false);
                r.Ctx.Dispose();
                if (++n % 50 == 0) Console.WriteLine($"  {n} renders");
            }
        }
    }

    // Profiles the filter-operation work a search/filter triggers: the grid rebuilds the filter
    // FilterDescriptors + expression tree and re-executes filter+sort+count+page over the data. This is
    // what OnFilter -> View recompute does on every applied filter and every debounced search keystroke.
    static void ProfileFilter(string[] args)
    {
        int rows = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 10_000;
        var data = Person.Generate(rows);

        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
        var host = ctx.RenderComponent<GridHost>(p => p
            .Add(x => x.Data, data.Take(10).ToList())
            .Add(x => x.PageSize, 10));

        var columns = host.Instance.Grid.ColumnsCollection.ToList();
        var firstName = columns.First(c => c.Property == "FirstName");

        static long Alloc() => GC.GetAllocatedBytesForCurrentThread();

        // One filter/search operation: rebuild filter descriptors + expression, filter, order, count, page.
        int Operate(int i)
        {
            firstName.SetFilterValue("First" + (i % 9)); // changes each iteration so it is not a no-op
            var q = data.AsQueryable().Where<Person>(columns).OrderBy("LastName desc");
            var count = q.Count();
            var page = q.Skip(0).Take(50).ToList();
            return count + page.Count;
        }

        for (int i = 0; i < 3; i++) Operate(i); // warm up

        const int iterations = 20;
        long bytes = 0;
        var before = Alloc();
        int sink = 0;
        for (int i = 0; i < iterations; i++) sink += Operate(i);
        bytes = Alloc() - before;

        Console.WriteLine($"Rows={rows}, one filter operation = rebuild descriptors+expression, filter+sort+count+page");
        Console.WriteLine($"Allocated: {bytes / (double)iterations / 1024:F1} KB / operation  (sink={sink})");

        if (args.Length > 2 && args[2] == "loop")
        {
            Console.WriteLine("Looping filter operations for profiler capture. Ctrl-C to stop.");
            long n = 0;
            while (true) { sink += Operate((int)(n % 9)); if (++n % 200 == 0) Console.WriteLine($"  {n} ops"); }
        }

        ctx.Dispose();
    }

    sealed class RenderedComponent
    {
        public TestContext Ctx { get; }
        public int MarkupLength { get; }
        public RenderedComponent(TestContext ctx, int markupLength) { Ctx = ctx; MarkupLength = markupLength; }
    }
}
