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
