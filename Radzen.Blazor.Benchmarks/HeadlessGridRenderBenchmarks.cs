using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Renders the same <see cref="GridHost"/> as FullRenderBenchmarks, but through a bare Blazor
/// <see cref="Renderer"/> that discards the render batch - no bUnit, no AngleSharp DOM, no HTML
/// string. The measured allocation is the component render-tree build+diff itself (the work Radzen
/// actually does), not the test harness.
/// </summary>
[MemoryDiagnoser]
public class HeadlessGridRenderBenchmarks
{
    [Params(500)]
    public int Rows { get; set; }

    // 0 = non-interactive, 1 = RowClick (per-cell onclick on the unoptimized path).
    [Params(0, 1)]
    public int Interactive { get; set; }

    private List<Person> data;
    private IServiceProvider services;
    private ParameterView parameters;

    [GlobalSetup]
    public void Setup()
    {
        data = Person.Generate(Rows);
        services = BenchmarkRenderer.BuildServices();
        parameters = HeadlessBench.HostParameters(data, Rows, Interactive);
    }

    [Benchmark(Description = "Headless initial render (Rows x 10 columns)")]
    public int RenderGrid()
    {
        using var renderer = new BenchmarkRenderer(services);
        renderer.RenderAsync<GridHost>(parameters).GetAwaiter().GetResult();
        if (renderer.UnhandledException != null) throw renderer.UnhandledException;
        return renderer.RenderCount;
    }
}

/// <summary>
/// Headless re-render: the grid is rendered once in setup, then re-rendered per op against a fresh
/// data reference (a data refresh). This is where the per-render optimizations live - the cell-class
/// memo (warm) and the per-cell vs per-row onclick - which an initial render does not exercise.
/// </summary>
[MemoryDiagnoser]
public class HeadlessGridRerenderBenchmarks
{
    [Params(500)]
    public int Rows { get; set; }

    [Params(0, 1)]
    public int Interactive { get; set; }

    private BenchmarkRenderer renderer;
    private GridHost host;
    private ParameterView parametersA, parametersB;
    private bool toggle;

    [GlobalSetup]
    public void Setup()
    {
        var dataA = Person.Generate(Rows);
        var dataB = Person.Generate(Rows);
        parametersA = HeadlessBench.HostParameters(dataA, Rows, Interactive);
        parametersB = HeadlessBench.HostParameters(dataB, Rows, Interactive);
        renderer = new BenchmarkRenderer(BenchmarkRenderer.BuildServices());
        host = renderer.RenderAsync<GridHost>(parametersA).GetAwaiter().GetResult();
        if (renderer.UnhandledException != null) throw renderer.UnhandledException;
    }

    [Benchmark(Description = "Headless re-render (data refresh)")]
    public int ReRender()
    {
        toggle = !toggle;
        var next = toggle ? parametersB : parametersA;
        renderer.Dispatcher.InvokeAsync(() => host.SetParametersAsync(next)).GetAwaiter().GetResult();
        if (renderer.UnhandledException != null) throw renderer.UnhandledException;
        return renderer.RenderCount;
    }

    [GlobalCleanup]
    public void Cleanup() => renderer?.Dispose();
}

internal static class HeadlessBench
{
    public static ParameterView HostParameters(List<Person> data, int rows, int interactive) =>
        ParameterView.FromDictionary(new Dictionary<string, object>
        {
            [nameof(GridHost.Data)] = data,
            [nameof(GridHost.PageSize)] = rows,
            [nameof(GridHost.Interactive)] = interactive,
        });
}

// Minimal renderer after egil/Benchmark.Blazor (https://github.com/egil/Benchmark.Blazor):
// discards the render batch so only the render-tree build+diff is measured - no markup, DOM, or
// HTML serialization. Radzen needs its component services plus an IJSRuntime; JS interop is a
// no-op since a headless render produces no DOM to talk to.
internal sealed class BenchmarkRenderer : Renderer
{
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    public Exception UnhandledException { get; private set; }
    public int RenderCount { get; private set; }

    public BenchmarkRenderer(IServiceProvider services)
        : base(services, NullLoggerFactory.Instance) { }

    public static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRadzenComponents();
        services.AddSingleton<IJSRuntime, NoopJSRuntime>();
        return services.BuildServiceProvider();
    }

    public Task<TComponent> RenderAsync<TComponent>(ParameterView parameters) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            var component = (TComponent)InstantiateComponent(typeof(TComponent));
            var id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, parameters);
            return component;
        });

    protected override void HandleException(Exception exception) => UnhandledException = exception;

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        RenderCount++;
        return Task.CompletedTask;
    }
}

internal sealed class NoopJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object[] args) => new(default(TValue));
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object[] args) => new(default(TValue));
}
