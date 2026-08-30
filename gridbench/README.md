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
| `VisualDump.cs`, `measure.js` | Ad hoc side-by-side render and Playwright geometry read-back, for looking at by hand |

The styling contract is **not** verified from here. It lives in `../Radzen.Blazor.FastGrid.Tests`
(`dotnet test Radzen.Blazor.FastGrid.Tests`), which runs unattended — see *Styling parity check* below.

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

## Marginal cost of each feature on a slim renderer

Measured by adding exactly one feature to the bare slim renderer, 1000 rows x 5 columns, same rows and
cells emitted every time. This is what decides which features a read-only grid can afford.

| Feature added | Allocated | Marginal | Per unit |
| --- | --- | --- | --- |
| *bare* | 220.93 KB | - | 45 B/cell |
| row style callback | 220.93 KB | **0** | free |
| selection (per-row lookup, aria-selected, class) | 220.93 KB | **0** | free |
| responsive column titles | 220.93 KB | **0** | free on allocation, +18% time |
| cell tooltip (`title="value"`) | 524.76 KB | +304 KB | ~61 B/cell |
| row click (`EventCallback` per row) | 530.79 KB | +310 KB | ~310 B/row |
| cell template (`RenderFragment` per cell) | 689.68 KB | +469 KB | ~94 B/cell |
| cell click (`EventCallback` per cell) | 1,703.39 KB | **+1,482 KB** | ~296 B/cell |
| all of the above | 2,600.78 KB | +2,380 KB | |

Three conclusions:

1. **Most features are free.** Selection, row styling and responsive titles cost no allocation at all -
   they are lookups and constant strings. Nothing about them justifies leaving them out.
2. **Delegates dominate, and per-cell delegates dominate hardest.** A row click costs 310 B per row; a
   cell click costs 296 B per *cell*, which at five columns is roughly five times worse. Any per-cell
   `EventCallback` must be wired only when a handler actually exists - the same lesson as the
   `oncontextmenu` modifiers on the full grid.
3. **Even with everything switched on the slim renderer is far ahead.** 2,601 KB against the optimised
   RadzenDataGrid's 18,189 KB for identical output - about 7x - and that is the worst case, with every
   feature on and every callback wired. A realistic configuration (templates, tooltips, selection, row
   click; cell click only when handled) lands near 1,000 KB, roughly 18x leaner than the full grid.

Note the bare figure is dominated by boxing: the getter returns `object`, so an `int`, `DateTime` or
`decimal` cell allocates a box. A strongly-typed column (QuickGrid's `PropertyColumn<T, TProp>` shape)
would remove most of that 45 B/cell.

## Typed expression columns vs string property names

1000 rows x 5 columns (int, string, int, DateTime, decimal), identical output.

| Column shape | Allocated | vs Radzen's |
| --- | --- | --- |
| `Property="Name"` -> `Func<T,object>` (Radzen today) | 220.47 KB | 1.00x |
| `Expression<Func<T,TProp>>` -> `AddContent(value)` | 165.78 KB | 0.75x |
| `Expression<Func<T,TProp>>` -> `Func<T,string>` -> `AddContent(string)` | **118.91 KB** | **0.54x** |

There is no ergonomics-versus-performance trade here: the strongly-typed, compile-time-checked column is
also the cheapest. `RenderTreeBuilder` has no generic `AddContent<T>`, so handing it a value type binds
the `object` overload, which boxes *and* then stringifies - the naive typed column (row 2) still pays for
the box. Compiling the expression once into a `Func<T,string>`, as QuickGrid's `PropertyColumn` does,
skips the box entirely and only pays for the string. That is where the remaining 46% comes from.

So a slim grid should take expressions rather than string property names - better call sites, compile-time
safety, refactor-safe, and a lower floor. It does mean deliberately *not* mirroring RadzenDataGrid's
`Property="Name"` convention, which is the real cost of the decision.

## Visual pass

`dotnet run --project gridbench -- visual <dir>` renders both grids through bUnit, writes their real
HTML, and builds a side-by-side page linking the actual Radzen theme stylesheet. Screenshot it with
Playwright and look at it.

Styling compatibility is close to free for the **body**: that CSS is class-based
(`.rz-datatable-data, .rz-grid-table { td { ... } .rz-cell-data { ... } }`), so markup carrying the same
class names picks up the whole theme - custom themes and CSS variables included - with no work.

The **header is not**, and an earlier version of this note wrongly said there was no structural coupling
anywhere. The theme gives `th` `padding: 0` and puts the header padding on a *direct child div*:

```scss
th {
  padding: 0;
  > div:not(.rz-cell-filter) { display: flex; align-items: center; padding: var(--rz-grid-header-cell-padding); }
}
```

So `th > div` is load-bearing. Without that wrapper the header row renders shorter than the grid's -
which is exactly what happened, and it took a person looking at the screenshot to notice. With it, the
rendered geometry matches exactly: header cell 37px, body cell 37px, table 332px on both.

`measure.js` reads back the rendered geometry with Playwright rather than relying on the eye, since two
of the four faults below were invisible in a first look at the screenshot.

The pass caught four markup faults in the prototype that no allocation benchmark would ever surface:

1. The wrapper claimed `rz-datatable-scrollable` without the nested structure that variant's CSS
   expects - a class that was simply a lie about the markup.
2. The table was missing `rz-grid-table-striped`, so rows did not stripe.
3. The renderer computed an alternating odd/even class per row - which is both *wrong* (the theme
   stripes with `:nth-child` from the table-level class) and wasted work on every row.

4. The header cell was missing the `th > div` wrapper the theme's padding hangs off, so the header row
   was shorter than RadzenDataGrid's.

Point 3 is the useful one: the correct markup is also the cheaper markup. Point 4 is the cautionary one -
it survived a screenshot being looked at, and was caught only when someone compared the two header rows
deliberately. Render correctness needs eyes *and* measured geometry, not only allocation numbers.

## The component: `RadzenFastGrid`

`Radzen.Blazor.FastGrid` is the prototype turned into a real component: expression columns compiled to
`Func<T,string>`, a `Defer`-based column collection pass, sorting the column applies itself, selection,
row click and an empty template — everything §3 of the spec calls free or conditional.

Same harness, same 5-column data, `--job short`:

| N=1000 | Time | Allocated | vs RadzenDataGrid |
| --- | --- | --- | --- |
| `RadzenDataGrid` (with PR #8) | 17,916 us | 18,189 KB | 1x |
| `SlimGrid` prototype | 1,196 us | 266 KB | 68x leaner |
| **`RadzenFastGrid`** | **1,079 us** | **149 KB** | **122x leaner** |
| QuickGrid | 2,429 us | 370 KB | 49x leaner |

It is leaner than the prototype it came from, because the prototype used Radzen's compiled
`Func<T,object>` getters and paid a box per cell; `PropertyColumn<T,TProp>` compiles to `Func<T,string>`
and does not (§4 of the spec). It renders the same 5,000 cells as the other three.

## The data path: paging, `LoadData` and async execution

Everything in it is behind a test that is false by construction for an in-memory grid -
`LoadData.HasDelegate`, `Data is IQueryable<TItem>`, `Data is ODataEnumerable<TItem>` - so nothing is
materialized, counted or string-formatted unless one of them is true. Re-measured after it was added:

| N=1000, no paging, no LoadData, no executor | Allocated |
| --- | --- |
| before the data path | 149.16 KB |
| after | 149.29 KB |

That is the whole cost of its existence: 0.13 KB, inside the run-to-run noise.

### Filtering

Filters compose through `QueryableExtension.Where(source, descriptors, ...)` - public, and a typed
expression tree rather than a parsed predicate string, so an Entity Framework query still translates.
The grid exposes its filters as `FilterDescriptor`s and accepts them back, which is the currency
`RadzenDataFilter` and `LoadData` already speak. Re-measured with nothing filtered: 149.65 KB, against
149.29 KB before it was added.

The built-in filter UI is a text box per column, in a second header row matching `RadzenDataGrid`'s
`div.rz-cell-filter > div.rz-cell-filter-content > span.rz-cell-filter-label` structure exactly, or -
under `FilterMode.CheckBoxList`, on the grid or per column - a multi-select of the column's distinct
values filtering with `In`. `RadzenDropDown` in `Multiple` mode already draws a check box per item, so
that mode needs no popup, toggle button or apply step of the grid's own; `RadzenDataGrid` spends a
`RadzenPopup`, a `RadzenListBox`, a loading state and two buttons on the same job.

The values come from a composed `Select(...).Distinct()` - a query, not an enumeration, so an Entity
Framework source runs `SELECT DISTINCT` rather than pulling every row across the wire - cached per
column until the data changes. `FilterLookupData` supplies them instead, for a source too large or too
remote to ask.

Still no operator menu, no date popup, no numeric range, no enum picker: those are most of
`RadzenDataGrid`'s filter code and none of its filter engine. `FilterTemplate` replaces the control for
any column that needs more.

Re-measured with none of it in use: 150.13 KB at 1000 x 5, against 149.84 KB before.

### Collections of objects

`CollectionColumn<TItem, TElement>` takes the element type as a parameter, so `DisplayProperty` and
`FilterProperty` are expressions rather than strings, and Razor infers the element type from
`Property` - `AuthoringSample.razor` in the test project is what proves that, since every other test
builds its fragments by hand and so never exercises Razor's inference.

The subtlety worth recording: a selector declared as returning `object` hides its member's real type
**two different ways**. A value type is wrapped in a `Convert` node; a reference type is not wrapped at
all, and the tree just carries a body narrower than the delegate's return type. Stripping `Convert` -
the obvious implementation - handles `a => a.Size` and silently fails on `a => a.Name`, leaving the
member looking like `object`: the wrong default operator, the wrong conversion of what was typed into
the box, and a distinct query projected to `object`. Comparing `body.Type` to `ReturnType` catches
both.

### Collection-valued columns

A column bound to a collection lists its members rather than stringifying the collection, and filters
a row in when any member matches. Re-measured with no collection column present: 149.84 KB, against
149.65 KB before - the element type is resolved once per closed generic type rather than per column,
which is what closed the 1.2 KB the first version cost.

Filtering an **array** turned out to be broken in `QueryableExtension` itself, and is fixed here rather
than worked around: the collection's item type was read only from generic arguments, so an array
property was left with none and the predicate was built against the array - `the binary operator Equal
is not defined for Int32[] and Int32`. `RadzenDataGrid` had the same fault.

### Virtualization

`AllowVirtualization` puts the rows through Blazor's `Virtualize`, with `SpacerElement="tr"` - its
spacers are `div`s by default, and a `div` inside a `tbody` is hoisted out of the table by the HTML
parser, taking the rows' sizing with it. `ItemSize` defaults to 37px, which is the row height
`GeometryParityTests` pins rather than a guess: a wrong one makes the scrollbar lie about how far there
is to scroll.

Virtualization and paging solve the same problem, so virtualization wins: with it on the pager is not
drawn and `AllowPaging` is ignored. One `Paging` property is the single rule for that, because reading
`AllowPaging` in the four places that used to would eventually let them disagree.

Everything funnels through one items provider: a `LoadData` handler is asked for the window, a supported
queryable is counted and materialized with awaited queries, anything else is composed in memory. A sort
or filter has to *refetch* rather than re-render, since `Virtualize` holds its own copy of the window.

Two faults here were wasted work rather than wrong output, and only turned up because the tests count
calls: the grid pre-loaded a page in `OnParametersSetAsync` that the provider then re-fetched, and the
`LoadData` handler was called once with no window at all before the provider asked for one.

### 31 bytes a row for a branch that was never taken

Extracting the row markup into a `RenderRow` method - so the virtualized and non-virtualized paths could
share it - cost **+31 KB at 1000 rows, a 21% regression**, with a byte-identical render tree (28,081
frames either way). The cause is a C# rule rather than anything about Blazor: `RenderRow` contained

```csharp
if (RowClick.HasDelegate)
{
    var captured = item;
    ... _ => RowClick.InvokeAsync(captured) ...
}
```

and a lambda capturing a local makes the compiler allocate that method's display class **on entry**, not
where the local is declared. Every row paid for a closure the branch never built. Moving the lambda into
its own method restored 150 KB exactly.

The frame counter said the tree was identical, so only the benchmark could see this - which is why the
numbers are re-measured after every change rather than at the end.

### The trap it walked into first

The first version called `StateHasChanged()` from the parameter-set path. `ComponentBase` already
renders after `OnParametersSetAsync` returns, but the earlier call had already flushed the queued
render, so the second one queued another - **two full passes over every row, +94% allocation** (149 KB
-> 289 KB at N=1000, 1,079 us -> 2,393 us). Nothing failed. Every test passed, the markup was identical
and the geometry was identical, because rendering the same thing twice produces the same DOM.

Two things caught it, and only because both were run:

- the benchmark, which is why the numbers are re-measured after every change rather than at the end;
- a batch counter added to `CountingRenderer` (`dotnet run --project gridbench -- probe`), which named
  the cause in one line: `RadzenFastGrid: batches 2`.

It is now pinned by `APlainGridRendersExactlyOnce` in the test project, which asserts bUnit's
`RenderCount` rather than anything about the output - the only layer that can see this class of fault.

## Styling parity check (automated)

The pass above found those four faults by hand: run two scripts, read a screenshot, compare two header
rows deliberately. That worked once and is not repeatable - fault 4 already slipped past the first look.
It is now a test project that runs in CI with nobody watching:

```
dotnet test Radzen.Blazor.FastGrid.Tests
```

250 tests, of which eleven compare `RadzenDataGrid<T>` and `RadzenFastGrid<T>` rendered from the same
8 x 5 data in the same run, in two layers:

- **Markup** (`MarkupParityTests`) - the table's `rz-grid-table` / `rz-grid-table-striped`; `rz-data-row`
  on every row with no class that varies row to row; `<td role="gridcell">` wrapping a
  `<span class="rz-cell-data">`; the `th > div > span.rz-column-title > span.rz-column-title-content`
  chain; and no `rz-datatable-scrollable` claimed without the scroll container it implies. Every rule is
  asserted against `RadzenDataGrid` too, in the same run, so the check cannot drift into describing a
  contract Radzen does not keep. The scrollable rule is the one deliberate asymmetry, and says so.
- **Geometry** (`GeometryParityTests`) - both grids laid out by Chromium against the real
  `standard-base.css`, with header cell, body cell and table heights compared to `RadzenDataGrid` within
  0.5px and to the recorded 37 / 37 / 332 baseline within 1px.

Two guards keep the geometry half honest. It never skips: no node, no Playwright or no Chromium fails the
run with a message naming which one, because a check that quietly disappears in CI is the failure this
exists to prevent. And the absolute baseline is asserted alongside the parity comparison, because two
*unstyled* grids agree with each other perfectly - the run also checks that the stylesheet was fetched
and that the theme's custom properties resolve.

### Proving it discriminates

Rule 1 of the verification protocol applies to this check as much as anything else, so each assertion was
confirmed to fail with the component deliberately broken:

| Break | Fails | Reported as |
| --- | --- | --- |
| Remove the `th > div` wrapper | 5 tests | chain `0 matched, out of 5 header cells`; header `37px -> 19px`, table `332px -> 314px` |
| Drop `rz-grid-table-striped` | 1 | `class="rz-grid-table rz-grid-table-fixed"` |
| Add an alternating `rz-datatable-even`/`-odd` | 1 | `found 'rz-datatable-even' in class="rz-data-row rz-datatable-even"` |
| Add an alternating class *not* named odd/even | 1 | `2 distinct class lists: "rz-data-row rz-stripe-a", "rz-data-row rz-stripe-b"` |
| Add `rz-datatable-scrollable` | 1 | claimed `with no scroll container inside` |
| Drop the `<span class="rz-cell-data">` | 5 | `the td has no element children`; body cell `37px -> 35px` |
| Drop `rz-data-row` | 1 | `class="rz-row"` |
| Unreachable stylesheet | 8 | `resources failed to load, so the page is not styled as intended` |

The alternating-class rule is checked two ways on purpose. The named form catches Radzen's own
`rz-datatable-odd`/`-even`; the general form - all rows must carry an identical class list - catches an
alternating class under any name, which is what a keyword match would miss.

### Divergences the check does not fail on

Rendered geometry, text alignment and column widths are identical, but the two grids' markup is not, and
these are the differences the parity rules deliberately do not cover:

| Divergence | Consequence |
| --- | --- |
| No `title="<value>"` on the cell span | **Decided, not overlooked.** `RadzenDataGrid` emits one, so a cell truncated to an ellipsis still reveals its full value on hover; `RadzenFastGrid` truncates identically and shows nothing. A real loss, and invisible to a geometry check. It costs ~61 B/cell - 305 KB at 1000 x 5, against a 149 KB budget - so paying it everywhere would triple the component's allocation for a hover affordance. A `TemplateColumn` can emit it for the one column that needs it. |
| No `rz-text-truncate` on the cell span | Inert: `.rz-grid-table td .rz-cell-data` already sets `overflow/text-overflow/white-space`. Verified: identical computed styles. |
| No `<colgroup>`, no `role="presentation"` on the table | Widths match today only because five equal columns under `table-layout: fixed` distribute evenly with or without it. This diverges the moment column widths are supported. |
| No `rz-text-align-*` class on `th`/`td` | Inert for the default, which the theme resolves to `start` either way. `RadzenFastGrid` has no `TextAlign` concept at all yet. |
| No `rz-datatable-scrollable`, no `rz-data-grid-data[role="grid"]`, no `rz-has-pager` | Deliberate (spec §6). The scroll container is also what carries `RadzenDataGrid`'s keyboard navigation, so that is not free either. |
