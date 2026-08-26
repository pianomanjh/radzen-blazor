using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Bunit;
using Radzen.Blazor;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Realistic per-render cell-value cost. Renders a real <see cref="RadzenDataGrid{TItem}"/>
/// with ten columns (two of them nested properties) once, then measures the work the grid
/// performs to produce the display string for every visible cell on a page — i.e. calling
/// <c>column.GetValue(item)</c> for rows x columns cells, which is what the render tree does
/// on every re-render.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class CellValueBenchmarks
{
    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    private TestContext ctx;
    private IReadOnlyList<RadzenDataGridColumn<Person>> columns;
    private List<Person> page;

    [GlobalSetup]
    public void Setup()
    {
        // The data set the value getter is invoked over. Decoupled from the rendered page so we can
        // scale to large row counts without paying a huge one-time bUnit render cost.
        page = Person.Generate(Rows);

        ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        // Render a small grid only to obtain fully-initialized columns (compiled getters, filter types, ...).
        var host = ctx.RenderComponent<GridHost>(p => p
            .Add(x => x.Data, page.Take(10).ToList())
            .Add(x => x.PageSize, 10));

        columns = host.Instance.Grid.ColumnsCollection.ToList();
    }

    [GlobalCleanup]
    public void Cleanup() => ctx?.Dispose();

    [Benchmark(Description = "GetValue for every cell on a page (rows x 10 columns)")]
    public object CellValues()
    {
        object last = null;
        var cols = columns;
        foreach (var item in page)
        {
            for (int c = 0; c < cols.Count; c++)
            {
                last = cols[c].GetValue(item);
            }
        }
        return last;
    }
}
