using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Bunit;
using Radzen;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Isolates the cost of the per-column filter UI during a grid render. With the default
/// <see cref="PopupRenderMode.Initial"/> every column's filter popup - operator dropdowns, and for date
/// columns a full date-picker calendar - is rendered eagerly on every render even though it is hidden
/// until the user opens it. Compares:
/// - filtering disabled,
/// - filtering enabled, popups eager (the default),
/// - filtering enabled, popups on-demand (lazy).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class FilterRenderBenchmarks
{
    public enum FilterUI { Off, EagerPopups_Default, OnDemandPopups, SimpleWithMenu }

    [Params(FilterUI.Off, FilterUI.EagerPopups_Default, FilterUI.OnDemandPopups, FilterUI.SimpleWithMenu)]
    public FilterUI Mode { get; set; }

    [Params(100)]
    public int Rows { get; set; }

    private List<Person> data;

    [GlobalSetup]
    public void Setup() => data = Person.Generate(Rows);

    [Benchmark(Description = "Render grid (filter UI variant)")]
    public int RenderGrid()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var host = ctx.RenderComponent<GridHost>(p =>
        {
            p.Add(x => x.Data, data);
            p.Add(x => x.PageSize, Rows);
            p.Add(x => x.AllowFiltering, Mode != FilterUI.Off);
            p.Add(x => x.FilterPopup, Mode == FilterUI.OnDemandPopups ? PopupRenderMode.OnDemand : PopupRenderMode.Initial);
            p.Add(x => x.FilterMode, Mode == FilterUI.SimpleWithMenu ? Radzen.FilterMode.SimpleWithMenu : Radzen.FilterMode.Advanced);
        });

        return host.Markup.Length;
    }
}
