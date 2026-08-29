using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

// Isolates the cost of Blazor's per-row COMPONENT scaffolding, with no Radzen or QuickGrid code
// involved. Every variant emits byte-identical markup: N <tr> of 5 <td>. Only the component
// structure around them differs. This bounds what a slim grid could save architecturally,
// before any feature is removed.

public sealed class RowData { public int Id; public string Name; public int Age; public string Hired; public string Salary; }

// (a) rows built inline in the parent's render tree - QuickGrid's shape
public sealed class InlineRows : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            b.OpenElement(2, "tr"); b.AddAttribute(3, "class", "rz-data-row");
            b.OpenElement(4, "td"); b.AddContent(5, it.Id); b.CloseElement();
            b.OpenElement(6, "td"); b.AddContent(7, it.Name); b.CloseElement();
            b.OpenElement(8, "td"); b.AddContent(9, it.Age); b.CloseElement();
            b.OpenElement(10, "td"); b.AddContent(11, it.Hired); b.CloseElement();
            b.OpenElement(12, "td"); b.AddContent(13, it.Salary); b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement(); b.CloseElement();
    }
}

public sealed class RowComponent : ComponentBase
{
    [Parameter] public RowData Item { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "tr"); b.AddAttribute(1, "class", "rz-data-row");
        b.OpenElement(2, "td"); b.AddContent(3, Item.Id); b.CloseElement();
        b.OpenElement(4, "td"); b.AddContent(5, Item.Name); b.CloseElement();
        b.OpenElement(6, "td"); b.AddContent(7, Item.Age); b.CloseElement();
        b.OpenElement(8, "td"); b.AddContent(9, Item.Hired); b.CloseElement();
        b.OpenElement(10, "td"); b.AddContent(11, Item.Salary); b.CloseElement();
        b.CloseElement();
    }
}

// (b) one child component per row - Radzen's shape, minus the cascades
public sealed class ComponentRows : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            b.OpenComponent<RowComponent>(2);
            b.AddAttribute(3, nameof(RowComponent.Item), it);
            b.CloseComponent();
        }
        b.CloseElement(); b.CloseElement();
    }
}

// (c) child component + 1 CascadingValue per row
public sealed class CascadeRows1 : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            var item = it;
            b.OpenComponent<CascadingValue<RowData>>(2);
            b.AddAttribute(3, "Value", item);
            b.AddAttribute(4, "ChildContent", (RenderFragment)(cb =>
            {
                cb.OpenComponent<RowComponent>(0);
                cb.AddAttribute(1, nameof(RowComponent.Item), item);
                cb.CloseComponent();
            }));
            b.CloseComponent();
        }
        b.CloseElement(); b.CloseElement();
    }
}

// (d) child component + 2 CascadingValues per row - Radzen's actual shape
public sealed class CascadeRows2 : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            var item = it;
            b.OpenComponent<CascadingValue<object>>(2);
            b.AddAttribute(3, "Value", (object)"editcontext-stand-in");
            b.AddAttribute(4, "ChildContent", (RenderFragment)(cb =>
            {
                cb.OpenComponent<CascadingValue<RowData>>(0);
                cb.AddAttribute(1, "Value", item);
                cb.AddAttribute(2, "ChildContent", (RenderFragment)(cb2 =>
                {
                    cb2.OpenComponent<RowComponent>(0);
                    cb2.AddAttribute(1, nameof(RowComponent.Item), item);
                    cb2.CloseComponent();
                }));
                cb.CloseComponent();
            }));
            b.CloseComponent();
        }
        b.CloseElement(); b.CloseElement();
    }
}

[MemoryDiagnoser]
public class ScaffoldBench
{
    [Params(1000)] public int N;
    IServiceProvider services;
    List<RowData> items;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        items = Enumerable.Range(0, N).Select(i => new RowData
        { Id = i, Name = "Person " + i, Age = 20 + i % 45, Hired = "2015-01-01", Salary = "40000" }).ToList();
    }

    async Task Render(Type t)
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(t, ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Items"] = items }));
    }

    [Benchmark(Baseline = true, Description = "a) rows inline (QuickGrid shape)")]
    public Task Inline() => Render(typeof(InlineRows));

    [Benchmark(Description = "b) + component per row")]
    public Task PerRowComponent() => Render(typeof(ComponentRows));

    [Benchmark(Description = "c) + 1 CascadingValue per row")]
    public Task Cascade1() => Render(typeof(CascadeRows1));

    [Benchmark(Description = "d) + 2 CascadingValues per row (Radzen shape)")]
    public Task Cascade2() => Render(typeof(CascadeRows2));
}

// ---- Per-cell cost isolation -------------------------------------------------------------
// Same N x 5 markup again, rows always inline. Only the way each CELL is emitted varies:
// direct AddAttribute calls, versus Radzen's shape (a Dictionary per cell, splatted via
// @attributes, produced by a RenderFragment returned per cell).

public sealed class CellDirect : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            b.OpenElement(2, "tr");
            for (var j = 0; j < 5; j++)
            {
                b.OpenElement(3, "td");
                b.AddAttribute(4, "role", "gridcell");
                b.AddAttribute(5, "class", "rz-cell-data");
                b.AddContent(6, it.Name);
                b.CloseElement();
            }
            b.CloseElement();
        }
        b.CloseElement(); b.CloseElement();
    }
}

// + a Dictionary per cell, splatted
public sealed class CellDictionary : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            b.OpenElement(2, "tr");
            for (var j = 0; j < 5; j++)
            {
                var attrs = new Dictionary<string, object>
                { ["role"] = "gridcell", ["class"] = "rz-cell-data" };
                b.OpenElement(3, "td");
                b.AddMultipleAttributes(4, attrs);
                b.AddContent(5, it.Name);
                b.CloseElement();
            }
            b.CloseElement();
        }
        b.CloseElement(); b.CloseElement();
    }
}

// + a RenderFragment returned per cell (Radzen's RenderCell shape)
public sealed class CellFragment : ComponentBase
{
    [Parameter] public List<RowData> Items { get; set; }

    static RenderFragment Cell(RowData it, Dictionary<string, object> attrs) => b =>
    {
        b.OpenElement(0, "td");
        b.AddMultipleAttributes(1, attrs);
        b.AddContent(2, it.Name);
        b.CloseElement();
    };

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table"); b.OpenElement(1, "tbody");
        foreach (var it in Items)
        {
            b.OpenElement(2, "tr");
            for (var j = 0; j < 5; j++)
            {
                var attrs = new Dictionary<string, object>
                { ["role"] = "gridcell", ["class"] = "rz-cell-data" };
                b.AddContent(3, Cell(it, attrs));
            }
            b.CloseElement();
        }
        b.CloseElement(); b.CloseElement();
    }
}

[MemoryDiagnoser]
public class CellBench
{
    [Params(1000)] public int N;
    IServiceProvider services;
    List<RowData> items;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        items = Enumerable.Range(0, N).Select(i => new RowData
        { Id = i, Name = "Person " + i, Age = 20 + i % 45, Hired = "2015-01-01", Salary = "40000" }).ToList();
    }

    async Task Render(Type t)
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(t, ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Items"] = items }));
    }

    [Benchmark(Baseline = true, Description = "cells written directly")]
    public Task Direct() => Render(typeof(CellDirect));

    [Benchmark(Description = "+ Dictionary per cell, splatted")]
    public Task Dict() => Render(typeof(CellDictionary));

    [Benchmark(Description = "+ RenderFragment per cell (Radzen shape)")]
    public Task Fragment() => Render(typeof(CellFragment));
}
