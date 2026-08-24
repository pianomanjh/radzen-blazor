using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Bunit;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Renders a RadzenAutoComplete suggestion list (OpenOnFocus shows all items). RadzenAutoComplete does
/// not derive from DropDownBase, so it has no compiled-getter cache: each rendered suggestion reads its
/// TextProperty via uncached reflection (PropertyAccess.GetItemOrValueFromProperty -> GetValue), per item
/// per render. This measures that.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class AutoCompleteBenchmarks
{
    [Params(200, 1000)]
    public int Items { get; set; }

    private List<Item> data;

    [GlobalSetup]
    public void Setup() => data = Item.Generate(Items);

    [Benchmark(Description = "Render autocomplete suggestion list")]
    public int Render()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<AutoCompleteHost>(p => p.Add(x => x.Data, data));
        return host.Markup.Length;
    }
}
