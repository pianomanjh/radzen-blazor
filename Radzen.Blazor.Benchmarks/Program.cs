using System;
using System.Collections.Generic;
using BenchmarkDotNet.Running;
using Bunit;

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

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
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

    sealed class RenderedComponent
    {
        public TestContext Ctx { get; }
        public int MarkupLength { get; }
        public RenderedComponent(TestContext ctx, int markupLength) { Ctx = ctx; MarkupLength = markupLength; }
    }
}
