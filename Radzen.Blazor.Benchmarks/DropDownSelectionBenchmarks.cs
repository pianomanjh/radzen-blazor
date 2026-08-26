using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Bunit;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Renders a multiselect <c>RadzenDropDown</c> bound by ValueProperty with a set of selected values.
/// For every rendered item the component calls IsSelected 3-4x, and (with ValueProperty set) each call
/// does a linear scan of the selected-values collection - so the per-render cost is O(items x selected).
/// This measures that as the selected count grows.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class DropDownSelectionBenchmarks
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

    [Benchmark(Description = "Render multiselect dropdown (Items x Selected)")]
    public int Render()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<DropDownHost>(p => p
            .Add(x => x.Data, data)
            .Add(x => x.Selected, selected));

        return host.Markup.Length;
    }
}
