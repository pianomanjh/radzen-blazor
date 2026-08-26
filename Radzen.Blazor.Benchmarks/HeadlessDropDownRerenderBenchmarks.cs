using System;
using System.Collections.Generic;
using System.Linq;
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
/// Headless re-render of a multiselect <see cref="DropDownHost"/> bound by ValueProperty with many
/// values selected - the case AddSelectedItemsByValue's value->item lookup addresses. Rendered
/// through a bare Blazor <see cref="Renderer"/> that discards the render batch (no bUnit, no
/// AngleSharp DOM), so the measured allocation is the render-tree build+diff itself. The selection
/// is toggled each op (same Data reference) so the value resolution re-runs while the cached lookup
/// stays valid.
/// </summary>
[MemoryDiagnoser]
public class HeadlessDropDownRerenderBenchmarks
{
    [Params(1000)]
    public int Items { get; set; }

    [Params(250)]
    public int Selected { get; set; }

    private BenchmarkRenderer renderer;
    private DropDownHost host;
    private ParameterView parametersA, parametersB;
    private bool toggle;

    [GlobalSetup]
    public void Setup()
    {
        var data = Item.Generate(Items);
        var selectedA = data.Take(Selected).Select(i => i.Id).ToList();
        var selectedB = data.Skip(1).Take(Selected).Select(i => i.Id).ToList();
        parametersA = HostParameters(data, selectedA);
        parametersB = HostParameters(data, selectedB);
        renderer = new BenchmarkRenderer(BenchmarkRenderer.BuildServices());
        host = renderer.RenderAsync<DropDownHost>(parametersA).GetAwaiter().GetResult();
        if (renderer.UnhandledException != null) throw renderer.UnhandledException;
    }

    private static ParameterView HostParameters(List<Item> data, List<int> selected) =>
        ParameterView.FromDictionary(new Dictionary<string, object>
        {
            [nameof(DropDownHost.Data)] = data,
            [nameof(DropDownHost.Selected)] = selected,
        });

    [Benchmark(Description = "Headless re-render multiselect dropdown (selection change)")]
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
