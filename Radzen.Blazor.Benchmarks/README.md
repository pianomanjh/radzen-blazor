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

## Strongly-typed `PropertyExpression` (opt-in, QuickGrid-style)

`RadzenDataGridColumn` gains an optional `PropertyExpression` parameter as a type-safe alternative to
the string `Property`:

```razor
<RadzenDataGridColumn TItem="Person" PropertyExpression="@(p => p.Address.City)" Title="City" />
```

The member path (`"Address.City"`) is derived from the expression and fed into the existing
string-based sort/filter/group pipeline, so it composes with every other column feature. The value
getter is compiled directly from the supplied expression instead of being built by reflecting over a
string path. The string `Property` still wins when both are set. Benefits are compile-time checking
and refactor-safe renames; per-render value-access cost is the same as the string form (which already
uses a cached compiled getter — see below).

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

### `CellAttributes` — 3 allocations per cell in the common case

For every cell the grid built a `DataGridCellRenderEventArgs` (which eagerly allocates a backing
`Dictionary`) and wrapped it in a `ReadOnlyDictionary`, even with no `CellRender` handler — three
allocations per cell just to return an empty attribute set. It now returns a shared empty dictionary
when `CellRender` is null (the result is only read, never mutated).

### Wasted (row-independent) string allocation

Two hot strings were rebuilt identically for work that never varies by row:

- **`GetStyle`** returns the same style string for every cell of a column within a render, yet rebuilt
  it (list + interpolation + `string.Join`) per cell. It now memoizes the data-cell style with a
  single-entry cache keyed by the inputs that actually affect it (width, alignment, min/max, column
  groups), so the string is built once per column and reused until an input changes. Output is
  identical; the memo self-invalidates when any keyed input changes (covered by tests).
- **`RowStyle`** only ever yields one of four constant strings but interpolated one per row; it now
  returns cached constants.

`GetStyle` over 10 columns (alignment set), all data cells:

| Rows | Baseline | Optimized | Time | Allocation |
|-----:|--------------------:|------------------:|-----:|-----------:|
| 1,000  | 327.9 µs / 343.8 KB | 157.9 µs / **0 B** | −52% | −100% |
| 10,000 | 3,405 µs / 3.44 MB  | 1,523 µs / **0 B** | −55% | −100% |

Columns that carry width (with column groups), alignment, or min/max styles previously allocated a
fresh identical string for every cell; that is now zero.

### Filtering / searching — the per-column filter UI (not just initial render)

Beyond the row body, every filterable column carries a filter UI. Two things dominate here:

**The default eager filter popups.** `FilterPopupRenderMode` defaults to `PopupRenderMode.Initial`,
which renders every column's filter popup — operator dropdowns, value editors, and for date columns a
full date-picker calendar — eagerly on **every** render, even though it stays hidden until the user
opens it. `PopupRenderMode.OnDemand` renders it on first open instead.

| Filter UI (100 rows, 10 cols) | Time | Allocated |
|---|---:|---:|
| Filtering off | 41 ms | 12.2 MB |
| **Eager popups (`Initial`, the default)** | 76 ms | **19.2 MB** |
| On-demand popups (`OnDemand`) | 49 ms | 12.3 MB |

The default adds **~7 MB (+57%) and ~34 ms per render** for hidden popups; on-demand returns to
roughly the filtering-off cost. This is a per-grid setting, not a code change — `OnDemand` is the
performance choice for grids with many filterable columns (especially date/numeric columns). Changing
the library default is a maintainer decision, surfaced here with numbers.

**`GetFilterOperators()` was recomputed per menu item.** In `SimpleWithMenu` mode the operator menu
called `GetFilterOperators()` ~16× per column per render, each running `Enum.GetValues<FilterOperator>()`
plus a LINQ filter (a lazy query re-evaluated on every enumeration). The result is constant per column,
so it is now materialized once and reused. Render time in that mode: 54.7 ms → 48.2 ms (~12% faster);
allocation is essentially unchanged (the operator arrays are small). A modest CPU cleanup, not a big
win — the eager-popup default above is the real lever.

### Aggregate — full grid render (bUnit `RenderComponent`, all changes vs. master)

End-to-end render of the whole grid. This total is dominated by Blazor's own render-tree and markup
allocation; the grid optimizations remove a consistent slice on top of that. This is the *worst
case* for these changes — no selected rows, no `CellRender`, only 2 nested columns; grids with
selection, nested paths, or frozen columns gain more (see the isolated benchmarks above).

| Rows | Baseline (master) | Optimized | Time | Allocation |
|-----:|------------------:|----------:|-----:|-----------:|
| 100 | 14.87 MB / 51.9 ms | 13.45 MB / 51.4 ms | −1% | −10% |
| 500 | 46.53 MB / 145 ms  | 39.92 MB / 129 ms  | −11% | −14% |

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
