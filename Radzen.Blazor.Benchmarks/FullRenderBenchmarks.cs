using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Bunit;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// End-to-end render of a real <see cref="Radzen.Blazor.RadzenDataGrid{TItem}"/> with 10 columns
/// (2 nested) over a full, non-virtualized page. Captures the aggregate per-cell render cost:
/// value access, cell style, cell CSS class (frozen/composite), and the render tree itself.
/// The baseline/optimized delta cancels out the fixed bUnit renderer overhead.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class FullRenderBenchmarks
{
    [Params(500)]
    public int Rows { get; set; }

    // 0 = non-interactive, 1 = RowClick (1 onclick closure/cell), 2 = RowClick + dblclick + contextmenu.
    [Params(0, 1, 2)]
    public int Interactive { get; set; }

    private List<Person> data;

    [GlobalSetup]
    public void Setup() => data = Person.Generate(Rows);

    [Benchmark(Description = "Render full grid (Rows x 10 columns)")]
    public int RenderGrid()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<GridHost>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.PageSize, Rows)
            .Add(x => x.Interactive, Interactive));

        return host.Markup.Length;
    }
}
