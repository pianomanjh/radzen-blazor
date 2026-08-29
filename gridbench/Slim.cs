using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using QG = Microsoft.AspNetCore.Components.QuickGrid;

// Prototype of a read-only "slim" grid: keeps RadzenDataGrid's markup shape and CSS classes,
// but adopts QuickGrid's architecture -
//   * rows written inline into the parent render tree (no per-row component, no CascadingValue)
//   * cells written directly (no per-cell Dictionary, no per-cell RenderFragment closure)
//   * per-column CSS + getter resolved once in OnParametersSet, not per cell
// Data access uses Radzen's own compiled property getters, so the comparison is fair on that axis.

public sealed class SlimColumn<TItem>
{
    public string Property { get; init; }
    public string Title { get; init; }
    internal Func<TItem, object> Getter;
    internal string CellClass;
}

public sealed class SlimGrid<TItem> : ComponentBase
{
    [Parameter] public IEnumerable<TItem> Data { get; set; }
    [Parameter] public IReadOnlyList<SlimColumn<TItem>> Columns { get; set; }

    protected override void OnParametersSet()
    {
        foreach (var c in Columns)
        {
            c.Getter ??= PropertyAccess.NullSafeGetter<TItem>(c.Property);
            c.CellClass ??= "rz-cell-data";
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        var cols = Columns;
        b.OpenElement(0, "div");
        b.AddAttribute(1, "class", "rz-data-grid rz-datatable");
        b.OpenElement(2, "table");
        b.AddAttribute(3, "class", "rz-grid-table rz-grid-table-fixed rz-grid-table-striped");

        // header
        b.OpenElement(4, "thead");
        b.OpenElement(5, "tr");
        for (var j = 0; j < cols.Count; j++)
        {
            // The theme gives <th> padding:0 and puts the header padding on a direct child <div>, so
            // that wrapper is load-bearing: without it the header row renders shorter than the grid's.
            // The inner rz-column-title-content span carries the ellipsis truncation. Both are per
            // column rather than per row, so the extra elements cost nothing at scale.
            b.OpenElement(6, "th");
            b.AddAttribute(7, "class", "rz-unselectable-text rz-text-align-left");
            b.AddAttribute(8, "role", "columnheader");
            b.AddAttribute(9, "scope", "col");
            b.OpenElement(10, "div");
            b.OpenElement(11, "span");
            b.AddAttribute(12, "class", "rz-column-title");
            b.OpenElement(13, "span");
            b.AddAttribute(14, "class", "rz-column-title-content rz-text-truncate");
            b.AddContent(15, cols[j].Title);
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement();
        b.CloseElement();

        // body - rows and cells inline, no child components
        b.OpenElement(12, "tbody");
        var index = 0;
        foreach (var item in Data)
        {
            b.OpenElement(13, "tr");
            b.AddAttribute(14, "role", "row");
            b.AddAttribute(15, "aria-rowindex", index + 1);
            // No alternating class: rz-grid-table-striped stripes via :nth-child in CSS.
            b.AddAttribute(16, "class", "rz-data-row");
            for (var j = 0; j < cols.Count; j++)
            {
                var c = cols[j];
                b.OpenElement(17, "td");
                b.AddAttribute(18, "role", "gridcell");
                b.AddAttribute(19, "class", c.CellClass);
                b.OpenElement(20, "span");
                b.AddAttribute(21, "class", "rz-cell-data");
                b.AddContent(22, c.Getter(item));
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
public class SlimBench
{
    [Params(200, 1000)] public int N;

    IServiceProvider services;
    List<Person> people;
    IQueryable<Person> queryable;
    SlimColumn<Person>[] slimCols;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);
        queryable = people.AsQueryable();
        slimCols = new[]
        {
            new SlimColumn<Person> { Property = "Id", Title = "Id" },
            new SlimColumn<Person> { Property = "Name", Title = "Name" },
            new SlimColumn<Person> { Property = "Age", Title = "Age" },
            new SlimColumn<Person> { Property = "Hired", Title = "Hired" },
            new SlimColumn<Person> { Property = "Salary", Title = "Salary" },
        };
    }

    static readonly (string prop, string title)[] Cols =
        { ("Id", "Id"), ("Name", "Name"), ("Age", "Age"), ("Hired", "Hired"), ("Salary", "Salary") };

    static readonly RenderFragment RadzenCols = builder =>
    {
        int s = 0;
        foreach (var (prop, title) in Cols)
        {
            builder.OpenComponent<RadzenDataGridColumn<Person>>(s++);
            builder.AddAttribute(s++, "Property", prop);
            builder.AddAttribute(s++, "Title", title);
            builder.CloseComponent();
        }
    };

    static readonly RenderFragment QgCols = builder =>
    {
        int s = 0;
        builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
        builder.AddAttribute(s++, "Property", (System.Linq.Expressions.Expression<Func<Person, int>>)(p => p.Id));
        builder.AddAttribute(s++, "Title", "Id"); builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, string>>(s++);
        builder.AddAttribute(s++, "Property", (System.Linq.Expressions.Expression<Func<Person, string>>)(p => p.Name));
        builder.AddAttribute(s++, "Title", "Name"); builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
        builder.AddAttribute(s++, "Property", (System.Linq.Expressions.Expression<Func<Person, int>>)(p => p.Age));
        builder.AddAttribute(s++, "Title", "Age"); builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, DateTime>>(s++);
        builder.AddAttribute(s++, "Property", (System.Linq.Expressions.Expression<Func<Person, DateTime>>)(p => p.Hired));
        builder.AddAttribute(s++, "Title", "Hired"); builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, decimal>>(s++);
        builder.AddAttribute(s++, "Property", (System.Linq.Expressions.Expression<Func<Person, decimal>>)(p => p.Salary));
        builder.AddAttribute(s++, "Title", "Salary"); builder.CloseComponent();
    };

    [Benchmark(Baseline = true, Description = "RadzenDataGrid")]
    public async Task Radzen()
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["Columns"] = RadzenCols }));
    }

    [Benchmark(Description = "SlimGrid prototype (read-only)")]
    public async Task Slim()
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(typeof(SlimGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["Columns"] = slimCols }));
    }

    [Benchmark(Description = "QuickGrid")]
    public async Task QuickGrid()
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(typeof(QG.QuickGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Items"] = queryable, ["ChildContent"] = QgCols }));
    }
}
