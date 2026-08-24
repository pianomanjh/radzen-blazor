# Radzen.Blazor DropDown performance benchmarks

A [BenchmarkDotNet](https://benchmarkdotnet.org/) harness plus an allocation-profiling entry point for
the `RadzenDropDown` family, using the same measure-first method as the DataGrid investigation:
map hot paths → benchmark → trace with `dotnet-trace` → fix only what is real.

## Running

```bash
# multiselect render cost as the selected count grows
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- --filter '*DropDownSelectionBenchmarks*'

# continuous render loop for a profiler (items, selected)
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- dropdown 500 250
```

To attribute allocation, capture a GC trace of the loop and aggregate `GC/AllocationTick` by type and
call stack (see the DataGrid `PROFILING.md` for the method):

```bash
dotnet-trace collect --profile gc-verbose --duration 00:00:15 -o dd.nettrace \
  -- <path>/Radzen.Blazor.Benchmarks.dll dropdown 500 250
```

## The finding: multiselect selection was O(items × selected)

A multiselect dropdown bound by `ValueProperty` degraded badly as more items were selected. Two causes,
both per the trace:

1. **`IsItemSelectedByValue`** (called 3–4× per rendered item) did `selectedValues.Cast<object>().Contains(v)`
   — a linear scan of the selected values *per item*. Now backed by a `HashSet` rebuilt only when the
   value collection changes: O(1) per item, and the per-call `Cast` iterator allocation is gone.
2. **`SelectItemFromValue`** (the real bottleneck — 45% of all render allocation in the trace) resolved
   each bound value by scanning the whole view *and* re-scanning `selectedItems` with a LINQ query
   allocated **per value** — O(items × selected) with an expression per value. For in-memory data it now
   builds a value→item lookup once (O(items + selected)); the per-value query is kept only for a
   non-in-memory (e.g. EF) view so server-side lookups stay server-side.

### Result — render a 500-item multiselect dropdown

| Selected | Baseline | Optimized | Speedup | Allocation |
|---------:|---------:|----------:|--------:|-----------:|
| 10  | 23.9 ms / 6.4 MB  | 10.2 ms / 5.7 MB | 2.3× | −11% |
| 100 | 147 ms / 11.7 MB  | 10.5 ms / 5.8 MB | 14×  | −50% |
| 250 | 380 ms / 19.8 MB  | 13.6 ms / 6.0 MB | 28×  | −70% |

Render time is now **flat** in the selected count instead of exploding — the O(items × selected) work
became O(items + selected). All 4931 tests pass; builds clean on net8/9/10.

> The trace was essential: fixing `IsItemSelectedByValue` alone cut allocation but barely moved the
> (noisy) time, because `SelectItemFromValue` dominated. The allocation trace pointed straight at it.

## What was checked and found NOT to be a further win (RadzenDropDown)

After the fix, a re-trace of the render and separate measurements settled the rest:

- **Item text access is already cached.** `type = Data.AsQueryable().ElementType` matches the item type, so
  the compiled getter is used per item — no per-item reflection. (The `String.Split` seen in the trace has
  no Radzen frame in its stack; it is bUnit's HTML-attribute serialization, which real Blazor never runs.)
- **The remaining render allocation is the Blazor render tree** (`RadzenDropDownItem.BuildRenderTree` — one
  component per item) plus bUnit serialization. For large lists the lever is virtualization
  (`AllowVirtualization`, default off), not library micro-optimization — same conclusion as the grid.
- **Search/filter execution is O(items) and cheap.** `Query.Where(TextProperty, searchText, op, cs)` over
  100k items is ~5.2 ms / 4.9 MB for one search (mostly materializing the matched subset); 10k is
  ~1.5 ms / 0.45 MB. No O(n²), one expression build per search. The cost of a search is re-rendering the
  matched items (framework), not the filter execution.
- `ItemAttributes` allocates one small args object per item, but the item genuinely reads
  `Visible`/`Disabled`/`Attributes` from it, and it is tiny next to the render tree — not worth the risk.

Net: the one big RadzenDropDown win (the O(n² ) multiselect selection) is fixed; the rest is framework-bound.
The remaining family wins are in **RadzenAutoComplete** (below) and **RadzenDropDownDataGrid**.

## RadzenAutoComplete — cache the item-text getter

`RadzenAutoComplete` does not derive from `DropDownBase`, so it had no getter cache: each rendered
suggestion read its `TextProperty` via uncached reflection (`PropertyAccess.GetItemOrValueFromProperty`
→ `GetProperty` + `PropertyInfo.GetValue` + `path.Split('.')`), per item per render. It now compiles a
getter once per `(item type, TextProperty)` and reuses it, with a reflection fallback for anything it
cannot compile.

### Result — render the suggestion list

| Items | Baseline | Optimized | Speedup |
|------:|---------:|----------:|--------:|
| 200  | 1.01 ms / 834 KB  | 1.35 ms / 828 KB | ~flat (noise) |
| 1000 | 7.42 ms / 3.24 MB | 3.00 ms / 3.21 MB | **2.5× faster** |

The win here is CPU, not allocation: the per-item reflection was expensive to run, but its allocation is
small next to the render tree (allocation is framework-bound, as elsewhere). At 1000 suggestions the list
renders ~2.5× faster.

## RadzenDropDownDataGrid — same O(items × selected) multiselect selection

`RadzenDropDownDataGrid` overrides `SelectItemFromValue` with the same shape as the base dropdown had:
each bound value was resolved with a `Query.Where(FilterDescriptor…)` **per value** plus a per-value
`selectedItems` scan — O(items × selected), with a filter expression built per value. Fixed the same way
(value→item lookup once for in-memory data; per-value query kept for a non-in-memory/EF view).

*This is the dropdown's own selection logic, not the embedded grid — the inner `RadzenDataGrid`'s per-cell
costs are out of scope here and come from the DataGrid work.*

### Result — render a 500-item multiselect DropDownDataGrid

| Selected | Baseline | Optimized | Speedup | Allocation |
|---------:|---------:|----------:|--------:|-----------:|
| 10  | 26.3 ms / 763 KB  | 4.0 ms / 512 KB | 6.5× | −33% |
| 100 | 239 ms / 3.81 MB  | 4.9 ms / 537 KB | 48×  | −86% |
| 250 | 557 ms / 9.26 MB  | 5.1 ms / 608 KB | 110× | −93% |

Flat in the selected count instead of exploding. All 4935 tests pass; builds clean on net8/9/10.

## Unnecessary re-renders

None of the dropdown/grid item/row/column components override `ShouldRender`, so a parent re-render
(e.g. the containing page re-rendering for an unrelated reason) cascades into every child. Measured with
a forced no-op re-render (`dotnet run -- rerender <items>`): the cost scales linearly with item count
(~8 KB/item: 100→846 KB, 500→4.1 MB, 2000→16.3 MB), i.e. every item re-renders even though nothing changed.

`RadzenDropDownItem` now overrides `ShouldRender` to skip an item whose `Item`/selected/disabled/multiple
state is unchanged (only the plain path — a `Template` or `ItemRender` can produce dynamic content, so
those always render). This is the idiomatic Blazor fix and is correct/tested, but it is a **modest** win
here (500 items: 4.1 MB → 3.8 MB, ~7%): the dominant re-render cost is the *parent* rendering all N item
frames plus its own O(n) work (`IsAllSelected` ×4, `Data.Cast().Any()` ×3 per the map), which item-level
`ShouldRender` cannot remove. For large lists the real lever for re-render cost — as for first-render — is
**virtualization** (`AllowVirtualization`, default off), so a closed dropdown does not render N items at all.
