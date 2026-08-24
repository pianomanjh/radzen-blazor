# Radzen.Blazor DataGrid performance benchmarks

A [BenchmarkDotNet](https://benchmarkdotnet.org/) harness that measures the allocation and CPU
cost of the `RadzenDataGrid` value-access hot path — the work the grid performs to produce the
display value for every visible cell on every render.

## Running

```bash
# property-access primitive (reflection vs. cached compiled getter)
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- --filter '*PropertyAccessBenchmarks*'

# real grid: GetValue for every cell on a page
dotnet run -c Release --project Radzen.Blazor.Benchmarks -- --filter '*CellValueBenchmarks*'
```

Add `--job short` for a faster (slightly noisier) run.

## Background

`RadzenDataGridColumn.GetValue(item)` is called for every visible column of every rendered row on
every render. Internally the grid already builds a **compiled, cached** property getter
(`PropertyAccess.Getter` — an `Expression`-tree `Func<TItem, object>` cached in a
`ConcurrentDictionary`), the same design QuickGrid uses. However:

- `GetValue` **bypassed** that getter for any dotted/nested property (`"Address.City"`) and fell
  back to per-cell reflection (`PropertyAccess.GetValue`, which does `Type.GetProperty` +
  `PropertyInfo.GetValue` and boxes, per segment, per cell).
- `GetSortValue` always used reflection.
- `GetCellClass` rebuilt a constant-per-column CSS string (plus `Enum.GetName` + `ToLower`) for
  every cell.

The optimization routes all property access through the existing compiled+cached getter and
memoizes the constant per-column string. **No public API change** — `Property` is still a string.

> Note: the `System.Linq.Dynamic.Core` namespace in this repo is Radzen's own bundled
> reimplementation (`DynamicExtensions.cs` / `ExpressionParser.cs`), not the NuGet package. The
> common sort and filter paths already build hand-written expression trees; the string parser is
> only used for the opt-in `CustomFilterExpression`. Reflection, not dynamic LINQ, dominated the
> per-cell cost.

## Results

Measured on .NET 10, `--job short`. (Allocated is exact regardless of job.)

### Property-access primitive — value getter invoked over N items (one column)

| N items | Reflection (current) | Compiled cached getter | Speedup | Alloc removed |
|--------:|---------------------:|-----------------------:|--------:|--------------:|
| 1,000 (flat)   | 51.7 µs / 32 KB    | 3.1 µs / **0 B**   | 17× | 32 KB |
| 1,000 (nested) | 115.4 µs / 112 KB  | 3.7 µs / **0 B**   | 31× | 112 KB |
| 100,000 (flat)   | 5,209 µs / 3.2 MB  | 804 µs / **0 B**  | 6.5× | 3.2 MB |
| 100,000 (nested) | 12,877 µs / 11.2 MB | 1,185 µs / **0 B** | 11× | 11.2 MB |

Reflection allocates 32 B (flat) / 112 B (nested) **per item, per column, per render**; the compiled
getter allocates nothing.

### Real grid — `GetValue` for every cell (10 columns, 2 nested)

Value access across N rows using real, fully-initialized `RadzenDataGridColumn` instances.

| Rows | Baseline | Optimized | Time | Allocation |
|-------:|---------------------:|--------------------:|-----:|-----------:|
| 1,000   | 1.06 ms / 498 KB   | 0.69 ms / 272 KB   | −35% | −45% |
| 10,000  | 9.99 ms / 5.07 MB  | 6.87 ms / 2.80 MB  | −31% | −45% |
| 100,000 | 100.6 ms / 50.8 MB | 68.5 ms / 28.1 MB  | −32% | −45% |

Consistent **~32% less time and ~45% less allocation** at every scale. At 100k rows the grid
allocates 22.7 MB less per pass. Only 2 of the 10 columns are nested; those two accounted for
almost all of the removed allocation. A grid whose columns are mostly nested paths benefits
proportionally more. The remaining allocation is `Convert.ToString` / `string.Format` producing the
actual display strings, which is inherent to rendering text.

(An earlier smaller-page run measured 500 rows/page at 519.8 µs / 244.5 KB → 334.4 µs / 131.3 KB,
the same −36% / −46%.)

### `GetStyle` for every data cell

`RadzenDataGridColumn.GetStyle` is invoked for every data cell on every render even though its result
does not depend on the row. It allocated a `List<string>` per cell and ran a LINQ scan over all
columns per cell. The optimization allocates the list lazily (cells with no explicit style allocate
nothing) and only performs the column scan when a width is set. Output is byte-for-byte identical.

| Rows (x10 cols) | Baseline | Optimized | Time | Allocation |
|----------------:|---------------------:|-------------------:|-----:|-----------:|
| 1,000  | 268.6 µs / 312.5 KB  | 55.8 µs / **0 B** | −79% | −100% |
| 10,000 | 2,661 µs / 3,125 KB  | 568 µs / **0 B**  | −79% | −100% |

Cells that *do* carry width/alignment/frozen styles still allocate the list (as before); only the
common empty-style cell became allocation-free.

### `getFrozenColumnClass` — dead per-cell allocation

`getFrozenColumnClass` never read its `visibleColumns` argument (it walks `Grid.ColumnsCollection`
internally), yet every call site materialized `columns.Where(c => c.GetVisible()).ToList()` per cell
to pass it in. The parameter and those list expressions were removed — one list allocation per cell
gone, for every grid, whether or not it has frozen columns.

### Aggregate — full grid render (bUnit `RenderComponent`, all three changes vs. master)

End-to-end render of the whole grid. This total is dominated by Blazor's own render-tree and markup
allocation; the grid optimizations remove a consistent slice on top of that. (No frozen columns, 2
nested columns — grids with more nested/frozen columns benefit more.)

| Rows | Baseline (master) | Optimized | Time | Allocation |
|-----:|------------------:|----------:|-----:|-----------:|
| 100 | 14.87 MB / 51.9 ms | 13.91 MB / 48.1 ms | −7% | −6% |
| 500 | 46.53 MB / 145 ms  | 41.88 MB / 138 ms  | −5% | −10% |

The isolated benchmarks above show the magnitude of each individual fix; this table shows what
survives once Blazor's fixed rendering overhead is included.

### Row membership tests (selected / edited / expanded)

For every row on every render the grid tested membership in `selectedItems`, `editedItems` and
`expandedItems` via `items.Keys.Any(i => ItemEquals(i, item))` — an O(selected) scan plus a LINQ
closure, and it runs 2-3 times per row (row style, aria-selected, edit mode). When no `KeyProperty`
is set (the default) the dictionary's own equality already matches, so this is an equivalent O(1)
`ContainsKey`. A single helper now does the O(1) test in that case and a closure-free loop otherwise.

| Rows | Selected | Baseline | Optimized | Speedup | Alloc removed |
|-----:|---------:|---------------------:|------------------:|--------:|--------------:|
| 1,000  | 50 | 178.8 µs / 128 KB   | 6.7 µs / **0 B**   | 27× | 128 KB |
| 10,000 | 50 | 1,841 µs / 1.28 MB  | 163 µs / **0 B**   | 11× | 1.28 MB |

Per render this ran 2-3 times per row, and the old cost grew with the number of selected rows; the
`ContainsKey` form is O(1) per row regardless.
