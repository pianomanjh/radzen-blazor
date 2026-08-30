using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen.Blazor;
using Radzen.FastGrid;

// ---- Lookup face-off: RadzenDropDownDataGrid vs RadzenFastDropDownDataGrid ----
//
// Both bound to the same rows, showing the same three columns, paging ten at a time, sorting on.
//
// Filtering is off on both, because the two do not offer the same thing: RadzenDropDownDataGrid never
// passes AllowFiltering to its popup grid - it has a single search box above it instead - while this one
// filters through the grid's own per-column filter row. Leaving both on would have measured a filter row
// against nothing and flattered the wrong component.
//
// Two questions, because a lookup is paid for twice:
//
//   Closed - what a form pays per lookup that nobody touches. Most lookups on most forms.
//   Open   - what one costs when the user actually opens it.
//
// The two components differ in kind on the first: RadzenDropDownDataGrid's popup grid is always in the
// DOM, so a closed lookup has already rendered its whole grid. The FastGrid one builds nothing until
// the first open. That difference is the measurement, not a thing to normalise away.
[MemoryDiagnoser]
public class DropDownBench
{
    [Params(50, 1000)] public int N;

    IServiceProvider services;
    List<Person> people;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);
    }

    static readonly (string prop, string title)[] Cols =
        { ("Id", "Id"), ("Name", "Name"), ("Salary", "Salary") };

    static readonly RenderFragment RadzenColumns = builder =>
    {
        var s = 0;

        foreach (var (prop, title) in Cols)
        {
            builder.OpenComponent<RadzenDropDownDataGridColumn>(s++);
            builder.AddAttribute(s++, "Property", prop);
            builder.AddAttribute(s++, "Title", title);
            builder.CloseComponent();
        }
    };

    static readonly RenderFragment FastColumns = builder =>
    {
        var s = 0;

        builder.OpenComponent<PropertyColumn<Person, int>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(p => p.Id));
        builder.AddAttribute(s++, "Title", "Id");
        builder.CloseComponent();

        builder.OpenComponent<PropertyColumn<Person, string>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(p => p.Name));
        builder.AddAttribute(s++, "Title", "Name");
        builder.CloseComponent();

        builder.OpenComponent<PropertyColumn<Person, decimal>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(p => p.Salary));
        builder.AddAttribute(s++, "Title", "Salary");
        builder.CloseComponent();
    };

    ParameterView RadzenParameters() => RadzenParametersFor(people);

    internal static ParameterView RadzenParametersFor(List<Person> people) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Data"] = people,
        ["Columns"] = RadzenColumns,
        ["TextProperty"] = "Name",
        ["ValueProperty"] = "Id",
        ["AllowSorting"] = true,
        ["AllowFiltering"] = false,
        ["AllowPaging"] = true,
        ["PageSize"] = 10,
    });

    ParameterView FastParameters() => FastParametersFor(people);

    internal static ParameterView FastParametersFor(List<Person> people) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
    {
        ["Data"] = people,
        ["ChildContent"] = FastColumns,
        ["TextProperty"] = "Name",
        ["ValueProperty"] = "Id",
        ["AllowSorting"] = true,
        ["AllowFiltering"] = false,
        ["AllowPaging"] = true,
        ["PageSize"] = 10,
    });

    [Benchmark(Baseline = true)]
    public async Task Radzen_Closed()
    {
        using var renderer = new BenchmarkRenderer(services);

        await renderer.RenderComponent(typeof(RadzenDropDownDataGrid<int>), RadzenParameters());
    }

    [Benchmark]
    public async Task Fast_Closed()
    {
        using var renderer = new BenchmarkRenderer(services);

        await renderer.RenderComponent(typeof(RadzenFastDropDownDataGrid<Person, int>), FastParameters());
    }

    [Benchmark]
    public async Task Radzen_Open()
    {
        using var renderer = new BenchmarkRenderer(services);

        var component = await renderer.Render(typeof(RadzenDropDownDataGrid<int>), RadzenParameters());

        await renderer.Drive(() => ((RadzenDropDownDataGrid<int>)component).OpenPopup());
    }

    [Benchmark]
    public async Task Fast_Open()
    {
        using var renderer = new BenchmarkRenderer(services);

        var component = await renderer.Render(typeof(RadzenFastDropDownDataGrid<Person, int>), FastParameters());

        await renderer.Drive(() => ((RadzenFastDropDownDataGrid<Person, int>)component).OpenPopup());
    }
}

// Why the numbers come out the way they do. A benchmark says how much; this says what of.
static class DropDownProbe
{
    public static async Task Run(int n)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();

        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);

        Console.WriteLine($"\n=== Lookup over {n:N0} rows, ten per page ===\n");

        await Report("RadzenDropDownDataGrid", services, typeof(RadzenDropDownDataGrid<int>),
            DropDownBench.RadzenParametersFor(people),
            component => ((RadzenDropDownDataGrid<int>)component).OpenPopup());

        await Report("RadzenFastDropDownDataGrid", services,
            typeof(RadzenFastDropDownDataGrid<Person, int>), DropDownBench.FastParametersFor(people),
            component => ((RadzenFastDropDownDataGrid<Person, int>)component).OpenPopup());
    }

    static async Task Report(string name, IServiceProvider services, Type type, ParameterView parameters,
        Func<IComponent, Task> open)
    {
        using var renderer = new CountingRenderer(services);

        var component = await renderer.RenderAndReturn(type, parameters);

        var closedFrames = renderer.Frames;
        var closedTd = renderer.Td;
        var closedComponents = renderer.Components;

        await renderer.Drive(() => open(component));

        Console.WriteLine($"  {name}");
        Console.WriteLine($"    closed : frames {closedFrames,7:N0}  td {closedTd,4:N0}  components {closedComponents,4:N0}");
        Console.WriteLine($"    opened : frames {renderer.Frames,7:N0}  td {renderer.Td,4:N0}  components {renderer.Components,4:N0}"
            + $"  (+{renderer.Frames - closedFrames:N0} frames)");
        Console.WriteLine($"    batches: {renderer.Batches}");
    }
}
