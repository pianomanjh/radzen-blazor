using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen.Blazor;
using QG = Microsoft.AspNetCore.Components.QuickGrid;

// Validity check + structural probe. Counts what each grid actually emits per render:
// render-tree frames, child components instantiated, and <td> elements. If the two grids
// do not emit the same number of cells, the render face-off is not comparing like with like.
sealed class CountingRenderer : Renderer
{
    public CountingRenderer(IServiceProvider services)
        : base(services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance) { }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    protected override void HandleException(Exception e) => throw e;

    public int Frames, Elements, Components, Attributes, Text, Markup, Td, Tr, Batches;
    public readonly Dictionary<string, int> ComponentTypes = new();

    protected override Task UpdateDisplayAsync(in RenderBatch batch)
    {
        Batches++;
        var frames = batch.ReferenceFrames;
        for (var i = 0; i < frames.Count; i++)
        {
            ref readonly var f = ref frames.Array[i];
            Frames++;
            switch (f.FrameType)
            {
                case RenderTreeFrameType.Element:
                    Elements++;
                    if (f.ElementName == "td") Td++;
                    else if (f.ElementName == "tr") Tr++;
                    break;
                case RenderTreeFrameType.Component:
                    Components++;
                    var n = f.ComponentType?.Name ?? "?";
                    ComponentTypes[n] = ComponentTypes.TryGetValue(n, out var c) ? c + 1 : 1;
                    break;
                case RenderTreeFrameType.Attribute: Attributes++; break;
                case RenderTreeFrameType.Text: Text++; break;
                case RenderTreeFrameType.Markup: Markup++; break;
            }
        }
        return Task.CompletedTask;
    }

    public Task Render(Type type, ParameterView parameters) => Dispatcher.InvokeAsync(async () =>
    {
        var component = InstantiateComponent(type);
        var id = AssignRootComponentId(component);
        await RenderRootComponentAsync(id, parameters);
    });
}

static class Probe
{
    public static async Task Run(int n)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        var services = sc.BuildServiceProvider();
        var people = Person.Make(n);
        var queryable = people.AsQueryable();

        RenderFragment radzenCols = builder =>
        {
            int s = 0;
            foreach (var (prop, title) in new[] { ("Id", "Id"), ("Name", "Name"), ("Age", "Age"), ("Hired", "Hired"), ("Salary", "Salary") })
            {
                builder.OpenComponent<RadzenDataGridColumn<Person>>(s++);
                builder.AddAttribute(s++, "Property", prop);
                builder.AddAttribute(s++, "Title", title);
                builder.CloseComponent();
            }
        };

        RenderFragment qgCols = builder =>
        {
            int s = 0;
            builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
            builder.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(p => p.Id));
            builder.AddAttribute(s++, "Title", "Id"); builder.CloseComponent();
            builder.OpenComponent<QG.PropertyColumn<Person, string>>(s++);
            builder.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(p => p.Name));
            builder.AddAttribute(s++, "Title", "Name"); builder.CloseComponent();
            builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
            builder.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(p => p.Age));
            builder.AddAttribute(s++, "Title", "Age"); builder.CloseComponent();
            builder.OpenComponent<QG.PropertyColumn<Person, DateTime>>(s++);
            builder.AddAttribute(s++, "Property", (Expression<Func<Person, DateTime>>)(p => p.Hired));
            builder.AddAttribute(s++, "Title", "Hired"); builder.CloseComponent();
            builder.OpenComponent<QG.PropertyColumn<Person, decimal>>(s++);
            builder.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(p => p.Salary));
            builder.AddAttribute(s++, "Title", "Salary"); builder.CloseComponent();
        };

        var rz = new CountingRenderer(services);
        await rz.Render(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["Columns"] = radzenCols }));

        var fg = new CountingRenderer(services);
        await fg.Render(typeof(Radzen.FastGrid.RadzenFastGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["ChildContent"] = SlimBench.FastCols }));

        var qg = new CountingRenderer(services);
        await qg.Render(typeof(QG.QuickGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Items"] = queryable, ["ChildContent"] = qgCols }));

        Console.WriteLine($"== Render structure, N={n}, 5 columns ==\n");
        Console.WriteLine($"{"",-22}{"Radzen",12}{"QuickGrid",12}");
        void Row(string label, int a, int b) => Console.WriteLine($"{label,-22}{a,12:N0}{b,12:N0}");
        Row("render-tree frames", rz.Frames, qg.Frames);
        Row("  elements", rz.Elements, qg.Elements);
        Row("  attributes", rz.Attributes, qg.Attributes);
        Row("  components", rz.Components, qg.Components);
        Row("  text", rz.Text, qg.Text);
        Row("  markup", rz.Markup, qg.Markup);
        Row("<tr> emitted", rz.Tr, qg.Tr);
        Row("<td> emitted", rz.Td, qg.Td);
        Console.WriteLine($"\n  RadzenFastGrid: batches {fg.Batches}, frames {fg.Frames:N0}, td {fg.Td:N0}, tr {fg.Tr:N0}");
        Console.WriteLine($"  RadzenDataGrid: batches {rz.Batches}    QuickGrid: batches {qg.Batches}");
        Console.WriteLine();
        Console.WriteLine($"  frames per row : Radzen {(double)rz.Frames / n,8:F1}   QuickGrid {(double)qg.Frames / n,8:F1}");
        Console.WriteLine($"  components/row : Radzen {(double)rz.Components / n,8:F1}   QuickGrid {(double)qg.Components / n,8:F1}");
        Console.WriteLine();
        Console.WriteLine("  Radzen child components instantiated:");
        foreach (var kv in rz.ComponentTypes.OrderByDescending(k => k.Value).Take(10))
            Console.WriteLine($"    {kv.Key,-42}{kv.Value,8:N0}");
        Console.WriteLine("  QuickGrid child components instantiated:");
        foreach (var kv in qg.ComponentTypes.OrderByDescending(k => k.Value).Take(10))
            Console.WriteLine($"    {kv.Key,-42}{kv.Value,8:N0}");
        Console.WriteLine();
    }
}
