# Allocation profiling (finding real hot spots empirically)

BenchmarkDotNet's `[MemoryDiagnoser]` tells you *how much* a benchmark allocates; it does not tell
you *what* or *where*. To attribute allocation to types and call sites, capture a GC trace and read
it back. This is how the "the render is dominated by the framework, not grid code" conclusion was
reached instead of guessed.

## 1. Render loop

`Program.cs` has a `profile` mode that renders a real 500-row grid and reports render-tree-build
bytes vs. bUnit markup-serialization bytes, and a `profile loop` mode that renders continuously so a
profiler can sample it:

```bash
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- profile        # one-shot attribution
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- profile loop    # continuous, for tracing
```

## 2. Capture a GC allocation trace

```bash
dotnet tool install --global dotnet-trace
DOTNET_ROOT=<sdk-dir> dotnet-trace collect --profile gc-verbose --duration 00:00:15 \
  -o render-gc.nettrace -- <path-to>/Radzen.Blazor.Benchmarks.dll profile loop
```

`gc-verbose` emits `GC/AllocationTick` (one event per ~100 KB allocated, carrying `TypeName`,
`AllocationAmount64`, and a managed call stack).

## 3. Analyze it

A ~30-line console tool using `Microsoft.Diagnostics.Tracing.TraceEvent` reads the trace and
aggregates `GC/AllocationTick`:

- **by type** — which types dominate (`System.String`, `RenderTreeFrame[]`, `Dictionary<,>`, …);
- **by call stack** — walk `ev.CallStack()` to the first `Radzen.*` (or first non-`Microsoft.`/`System.`)
  frame to see whether a type's allocation comes from grid code, the Blazor framework, or bUnit.

```csharp
var log = new TraceLog(TraceLog.CreateFromEventPipeDataFile("render-gc.nettrace"));
foreach (var ev in log.Events)
{
    if (ev.EventName != "GC/AllocationTick") continue;
    var type  = ev.PayloadByName("TypeName") as string;
    var bytes = Convert.ToInt64(ev.PayloadByName("AllocationAmount64"));
    var stack = ev.CallStack();          // walk .Caller to the first Radzen.* frame
    // aggregate bytes by type, and by attributed frame
}
```

## What it showed for a 500-row render

- `System.String` (31%) and `RenderTreeFrame[]` (16%) lead; of the string bytes, ~72% carry no
  `Radzen` frame (Blazor/bUnit), and ~150 MB come specifically from bUnit's `Htmlizer` HTML
  serialization — a cost real Blazor (WASM/Server) never pays.
- Grid-attributable allocation (`RenderCell`, `GetValue`, `GetCellCssClass`) is a small slice, and
  the meaningful parts were already optimized.
- Conclusion: the large render cost is the framework render tree; the only big lever for large grids
  is virtualization (rendering fewer rows), not further per-cell library micro-optimization.
