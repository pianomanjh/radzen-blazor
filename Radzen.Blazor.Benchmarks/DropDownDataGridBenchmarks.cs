using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Bunit;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Renders a multiselect RadzenDropDownDataGrid with a bound Value. Its SelectItemFromValue resolves each
/// bound value with a per-value Query.Where(...) and a per-value selectedItems scan - O(items x selected) -
/// the same shape fixed in DropDownBase.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class DropDownDataGridBenchmarks
{
    [Params(500)]
    public int Items { get; set; }

    [Params(10, 100, 250)]
    public int Selected { get; set; }

    private List<Item> data;
    private List<int> selected;

    [GlobalSetup]
    public void Setup()
    {
        data = Item.Generate(Items);
        selected = data.Take(Selected).Select(i => i.Id).ToList();
    }

    [Benchmark(Description = "Render multiselect DropDownDataGrid (Items x Selected)")]
    public int Render()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<DropDownDataGridHost>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.Selected, selected));

        return host.Markup.Length;
    }
}
