using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Bunit;
using Radzen.Blazor;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Measures <see cref="RadzenDataGridColumn{TItem}.GetStyle(bool, bool, bool)"/>, which the render
/// tree invokes for every data cell (rows x columns) on every render. The returned style does not
/// depend on the row, yet it is recomputed per cell, allocating a List, running a LINQ scan over all
/// columns, and joining strings.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CellStyleBenchmarks
{
    [Params(1_000, 10_000)]
    public int Rows { get; set; }

    private TestContext ctx;
    private IReadOnlyList<RadzenDataGridColumn<Person>> columns;

    [GlobalSetup]
    public void Setup()
    {
        ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<GridHost>(p => p
            .Add(x => x.Data, Person.Generate(10))
            .Add(x => x.PageSize, 10));

        columns = host.Instance.Grid.ColumnsCollection.ToList();
    }

    [GlobalCleanup]
    public void Cleanup() => ctx?.Dispose();

    [Benchmark(Description = "GetStyle for every data cell (rows x 10 columns)")]
    public int CellStyles()
    {
        int total = 0;
        var cols = columns;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < cols.Count; c++)
            {
                total += cols[c].GetStyle(forCell: true).Length;
            }
        }
        return total;
    }
}
