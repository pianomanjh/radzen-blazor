# Grid render benchmarks: RadzenDataGrid vs QuickGrid vs a slim prototype

Exploratory harness, not shipped code and not in any solution — CI builds only
`Radzen.Blazor.csproj` and the test project, so this is never compiled by CI.

Run it with:

    dotnet run --project gridbench/Radzen.Blazor.GridBench.csproj -c Release -- --job short --filter "*SlimBench*"
    dotnet run --project gridbench/Radzen.Blazor.GridBench.csproj -c Release -- probe

## What's here

| File | Purpose |
| --- | --- |
| `Program.cs` | `RenderBench` (Radzen vs QuickGrid), `PipelineBench` (dynamic-LINQ vs typed ordering), `EfBench` |
| `Probe.cs` | Structural probe — counts render-tree frames, elements, attributes and child components per render |
| `Scaffold.cs` | Isolates the cost of Blazor's per-row *component* scaffolding, with no grid code involved |
| `Slim.cs` | `SlimGrid<T>` prototype — Radzen's markup, QuickGrid's architecture |

All render benchmarks use a minimal in-memory `Renderer` (the Benchmark.Blazor technique) so no
browser or JS interop is involved.

## Findings

Baseline, 5 columns, all optional features at their defaults (sorting, filtering, column
resize/reorder/picking, grouping, virtualization and responsive are all **off**):

| N | RadzenDataGrid | QuickGrid | Alloc ratio |
| --- | --- | --- | --- |
| 50 | 1.79 ms / 1,123 KB | 1.51 ms / 85 KB | 13x |
| 200 | 5.18 ms / 5,414 KB | 1.51 ms / 126 KB | 43x |
| 1000 | 32.0 ms / 28,708 KB | 2.48 ms / 370 KB | 78x |

Both emit the same visible output (5,000 `<td>`, ~1,000 `<tr>` at N=1000), so this is like for like.
Since every optional feature is off, the gap is **not** explained by Radzen being more full-featured.
It is structural, and splits in two.

### 1. Per-row component scaffolding — ~20% of the cost

`Scaffold.cs` renders byte-identical markup under four component shapes:

| Shape | Time | Allocated |
| --- | --- | --- |
| rows inline (QuickGrid shape) | 266 us | 72 KB |
| + one component per row | 1,137 us | 1,881 KB |
| + 1 `CascadingValue` per row | 2,433 us | 3,978 KB |
| + 2 `CascadingValue` per row (Radzen shape) | 3,554 us | 5,837 KB |

`RadzenDataGridRow.razor` wraps every row in two cascading values — one for `EditContext`, one for
the row — so the grid instantiates 3 components per row (3,011 at N=1000) where QuickGrid
instantiates 8 in total. Each cascade costs more than the row component itself.

### 2. Per-cell work — the other ~80%

`RadzenDataGrid.RenderCell` allocates a `Dictionary` per cell, builds style and class strings per
cell, and returns a `RenderFragment` — a delegate plus closure per cell — which Blazor then invokes
as a nested fragment and splats. QuickGrid writes cells directly into the parent's render tree.

### The prototype

`SlimGrid<T>` keeps Radzen's markup shape and CSS classes and uses Radzen's own compiled property
getters, but renders rows and cells inline with no per-row component, no cascading values, no
per-cell dictionary and no per-cell render fragment:

| N=1000 | Time | Allocated |
| --- | --- | --- |
| RadzenDataGrid | 26,769 us | 28,709 KB |
| SlimGrid prototype | 1,273 us | 266 KB |
| QuickGrid | 2,563 us | 370 KB |

21x faster and 108x leaner than RadzenDataGrid, and ahead of QuickGrid.

**This is a ceiling, not a product.** The prototype has no sorting, paging, filtering, selection or
templates. The open question is the marginal render cost of adding each of those back.

## Ablation ladder on the real grid

Each step applied to `RadzenDataGrid` itself and measured, N=1000 x 5 columns, then reverted.
Effects proved additive (the combined run matched the sum exactly).

| Step | Allocated | Delta | Share of baseline |
| --- | --- | --- | --- |
| baseline | 28,708 KB | | |
| E1: `<td>` attributes written directly (no `Dictionary` + `@attributes` splat, no `@oncontextmenu`/`@onkeydown` modifiers) | 23,887 KB | -4,821 KB | 17% |
| E2: + cell body branches removed (responsive, expand, edit-mode, template) | 22,833 KB | -1,054 KB | 4% |
| E3: + both per-row `CascadingValue`s removed | 15,783 KB | -7,050 KB | 25% |

Time over the same ladder: 26,769 us -> 14,607 us.

So **~45% is recoverable inside the existing component**, without dropping any feature. Notably the
cell body's conditional branches are nearly free (4%) - the cost is the plumbing around them, not the
features they implement.

The remaining 15,783 KB is structural: the `RadzenDataGridRow` component instantiated per row, the
`RenderFragment` returned per cell by `RenderCell`, and the per-row attribute machinery
(`RowAttributes`, `RowStyle`, `RowAriaSelected`, the `<tr>` splat). `SlimGrid` renders the same
output in 266 KB, so that last 55% is reachable only by changing the row/cell rendering architecture.

### Caveat on E3

The two per-row cascades feed `FormComponent.EditContext` and `IRadzenForm` to editors inside
templates. Removing them unconditionally breaks any `RadzenTextBox` (or similar) placed in a plain
`Template`, and would also un-shadow an outer `EditContext` when the grid sits inside an `EditForm`.
A shippable version has to cascade conditionally, so the 25% is not free.
