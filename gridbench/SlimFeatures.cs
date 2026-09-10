using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen;

// Marginal cost of each feature a read-only grid might keep, measured by adding exactly one to the
// bare slim renderer. Everything emits the same rows and cells; only the named feature is switched on.
// This is what decides which features a slim grid can afford, rather than guessing.

[Flags]
public enum SlimFeature
{
    None = 0,
    CellTemplate = 1,       // cell content through a RenderFragment<TItem> rather than a direct getter
    Selection = 2,          // per-row selected lookup + aria-selected + selected class
    RowClick = 4,           // an EventCallback bound per row
    CellTooltip = 8,        // title="value" on every cell
    Responsive = 16,        // per-cell column-title span
    RowStyleCallback = 32,  // a user Func<TItem,int,string> consulted per row
    CellClick = 64,         // an EventCallback bound per cell
}

public sealed class SlimFeatureColumn<TItem>
{
    public string Property { get; init; }
    public string Title { get; init; }
    internal Func<TItem, object> Getter;
    public RenderFragment<TItem> Template { get; set; }
}

public sealed class SlimFeatureGrid<TItem> : ComponentBase
{
    [Parameter] public IEnumerable<TItem> Data { get; set; }
    [Parameter] public IReadOnlyList<SlimFeatureColumn<TItem>> Columns { get; set; }
    [Parameter] public SlimFeature Features { get; set; }
    [Parameter] public HashSet<TItem> Selected { get; set; }
    [Parameter] public Func<TItem, int, string> RowStyle { get; set; }

    protected override void OnParametersSet()
    {
        foreach (var c in Columns)
        {
            c.Getter ??= PropertyAccess.NullSafeGetter<TItem>(c.Property);
        }
    }

    bool Has(SlimFeature f) => (Features & f) != 0;

    void OnRowClick(TItem item) { }
    void OnCellClick(TItem item, int col) { }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var cols = Columns;
        b.OpenElement(0, "div");
        b.AddAttribute(1, "class", "rz-data-grid rz-datatable");
        b.OpenElement(2, "table");
        b.OpenElement(3, "tbody");

        var index = 0;
        foreach (var item in Data)
        {
            b.OpenElement(4, "tr");
            b.AddAttribute(5, "role", "row");

            if (Has(SlimFeature.Selection))
            {
                var selected = Selected != null && Selected.Contains(item);
                b.AddAttribute(6, "aria-selected", selected ? "true" : "false");
                b.AddAttribute(7, "class", selected ? "rz-data-row rz-state-highlight" : "rz-data-row");
            }
            else if (Has(SlimFeature.RowStyleCallback))
            {
                b.AddAttribute(7, "class", RowStyle?.Invoke(item, index) ?? "rz-data-row");
            }
            else
            {
                b.AddAttribute(7, "class", "rz-data-row");
            }

            if (Has(SlimFeature.RowClick))
            {
                var captured = item;
                b.AddAttribute(8, "onclick",
                    EventCallback.Factory.Create<MouseEventArgs>(this, _ => OnRowClick(captured)));
            }

            for (var j = 0; j < cols.Count; j++)
            {
                var c = cols[j];
                b.OpenElement(9, "td");
                b.AddAttribute(10, "role", "gridcell");
                b.AddAttribute(11, "class", "rz-cell-data");

                if (Has(SlimFeature.CellClick))
                {
                    var capturedItem = item;
                    var capturedCol = j;
                    b.AddAttribute(12, "onclick",
                        EventCallback.Factory.Create<MouseEventArgs>(this, _ => OnCellClick(capturedItem, capturedCol)));
                }

                if (Has(SlimFeature.Responsive))
                {
                    b.OpenElement(13, "span");
                    b.AddAttribute(14, "class", "rz-column-title");
                    b.AddContent(15, c.Title);
                    b.CloseElement();
                }

                b.OpenElement(16, "span");
                b.AddAttribute(17, "class", "rz-cell-data");

                if (Has(SlimFeature.CellTooltip))
                {
                    b.AddAttribute(18, "title", $"{c.Getter(item)}");
                }

                if (Has(SlimFeature.CellTemplate) && c.Template != null)
                {
                    b.AddContent(19, c.Template(item));
                }
                else
                {
                    b.AddContent(20, c.Getter(item));
                }

                b.CloseElement();
                b.CloseElement();
            }

            b.CloseElement();
            index++;
        }

        b.CloseElement();
        b.CloseElement();
        b.CloseElement();
    }
}

[MemoryDiagnoser]
public class SlimFeatureBench
{
    [Params(1000)] public int N;

    IServiceProvider services;
    List<Person> people;
    SlimFeatureColumn<Person>[] cols;
    HashSet<Person> selected;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);
        selected = new HashSet<Person>(people.Take(N / 10));
        cols = new[] { "Id", "Name", "Age", "Hired", "Salary" }
            .Select(p =>
            {
                // The template has to render the same value the direct path does, or the comparison
                // measures "constant string vs property access + boxing" instead of the template itself.
                var getter = PropertyAccess.NullSafeGetter<Person>(p);
                return new SlimFeatureColumn<Person>
                {
                    Property = p,
                    Title = p,
                    Getter = getter,
                    Template = item => tb => tb.AddContent(0, getter(item)),
                };
            }).ToArray();
    }

    async Task Render(SlimFeature features)
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(typeof(SlimFeatureGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                ["Data"] = people,
                ["Columns"] = cols,
                ["Features"] = features,
                ["Selected"] = selected,
                ["RowStyle"] = (Func<Person, int, string>)((_, i) => (i & 1) == 0 ? "rz-data-row" : "rz-data-row rz-datatable-odd"),
            }));
    }

    [Benchmark(Baseline = true, Description = "bare (no features)")]
    public Task Bare() => Render(SlimFeature.None);

    [Benchmark(Description = "+ row style callback")] public Task RowStyle() => Render(SlimFeature.RowStyleCallback);
    [Benchmark(Description = "+ selection")] public Task Selection() => Render(SlimFeature.Selection);
    [Benchmark(Description = "+ row click")] public Task RowClick() => Render(SlimFeature.RowClick);
    [Benchmark(Description = "+ cell tooltip")] public Task Tooltip() => Render(SlimFeature.CellTooltip);
    [Benchmark(Description = "+ responsive titles")] public Task Responsive() => Render(SlimFeature.Responsive);
    [Benchmark(Description = "+ cell template")] public Task Template() => Render(SlimFeature.CellTemplate);
    [Benchmark(Description = "+ cell click")] public Task CellClick() => Render(SlimFeature.CellClick);

    [Benchmark(Description = "all of the above")]
    public Task All() => Render(SlimFeature.CellTemplate | SlimFeature.Selection | SlimFeature.RowClick
        | SlimFeature.CellTooltip | SlimFeature.Responsive | SlimFeature.CellClick);
}
