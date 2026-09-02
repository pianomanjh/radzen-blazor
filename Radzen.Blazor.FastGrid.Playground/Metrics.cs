using System.Diagnostics;

namespace Radzen.Blazor.FastGrid.Playground;

/// <summary>
/// What the last render cost and what the process is holding, for looking at while driving the grid.
/// </summary>
/// <remarks>
/// Allocation is process-wide rather than per-thread. The per-thread counter is the exact one, but a
/// render does not begin and end on the same thread here - ShouldRender and OnAfterRender can be
/// dispatched separately - and reading it across the two produced negative figures. Process-wide is
/// monotonic, so it is honest, at the cost of counting anything else the server did meanwhile.
/// Time is a stopwatch around the same span.
/// <para>
/// The renders-per-second figure is the one worth watching. A grid at rest should sit at zero; anything
/// else is a render loop, and one of those cost several thousand renders a second here without changing
/// anything on screen.
/// </para>
/// </remarks>
public sealed class Metrics
{
    readonly Stopwatch stopwatch = new();
    long allocatedAtStart;
    DateTime windowStart = DateTime.UtcNow;
    int rendersInWindow;

    public int Renders { get; private set; }

    public double LastRenderMs { get; private set; }

    public long LastRenderBytes { get; private set; }

    public double RendersPerSecond { get; private set; }

    public long ManagedHeapBytes => GC.GetTotalMemory(false);

    public int Gen0 => GC.CollectionCount(0);

    public int Gen1 => GC.CollectionCount(1);

    public int Gen2 => GC.CollectionCount(2);

    public void BeginRender()
    {
        allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
        stopwatch.Restart();
    }

    public void EndRender()
    {
        stopwatch.Stop();

        LastRenderMs = stopwatch.Elapsed.TotalMilliseconds;
        LastRenderBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedAtStart);
        Renders++;
        rendersInWindow++;

        var elapsed = (DateTime.UtcNow - windowStart).TotalSeconds;

        if (elapsed >= 1)
        {
            RendersPerSecond = rendersInWindow / elapsed;
            rendersInWindow = 0;
            windowStart = DateTime.UtcNow;
        }
    }

    public void Reset()
    {
        Renders = 0;
        rendersInWindow = 0;
        RendersPerSecond = 0;
        LastRenderMs = 0;
        LastRenderBytes = 0;
        windowStart = DateTime.UtcNow;
    }
}
