# Grid render benchmarks: RadzenDataGrid vs QuickGrid vs a slim prototype

Exploratory harness, not shipped code and not in any solution — CI builds only
`Radzen.Blazor.csproj` and the test project, so this is never compiled by CI.

Run it with:

    dotnet run --project gridbench/Radzen.Blazor.GridBench.csproj -c Release -- --job short --filter "*SlimBench*" --buildTimeout 900
    dotnet run --project gridbench/Radzen.Blazor.GridBench.csproj -c Release -- probe
    dotnet run --project gridbench/Radzen.Blazor.GridBench.csproj -c Release -- pool-probe 1000 20

**`--buildTimeout 900` is not optional on a slow or busy machine, and leaving it off does not look like
an error.** BenchmarkDotNet builds a generated project per run and gives it 120s; when that expires it
reports `NA` in every column, prints "There are not any results runs" among a hundred other lines, and
exits 0. A six-run loop was collected off this harness and tabulated before anyone noticed that the
number of benchmarks executed was zero. **Check `executed benchmarks:` at the end of the run before
believing a row** - the same rule the mutation sweeps learned, arriving from a different direction.

## What's here

| File | Purpose |
| --- | --- |
| `Program.cs` | `RenderBench` (Radzen vs QuickGrid), `PipelineBench` (dynamic-LINQ vs typed ordering), `EfBench` |
| `Probe.cs` | Structural probe — counts render-tree frames, elements, attributes and child components per render |
| `Scaffold.cs` | Isolates the cost of Blazor's per-row *component* scaffolding, with no grid code involved |
| `Slim.cs` | `SlimGrid<T>` prototype — Radzen's markup, QuickGrid's architecture |
| `VisualDump.cs`, `measure.js` | Ad hoc side-by-side render and Playwright geometry read-back, for looking at by hand |
| `PoolProbe.cs` | Reads `ArrayPool`'s own EventSource during a render — what the frame arrays cost and what they only appear to |

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

## Recording a measurement

A feature's marginal cost answers "what did this cost". It does not answer "is this grid still worth
using", which is the question anyone reading a commit is actually asking - and the two can point
different ways. When row detail cost 403 KB it left the grid 33x leaner than `RadzenDataGrid` and, for
the first time, *heavier* than QuickGrid. Neither of those facts was in the commit that added it. (It
now costs under a kilobyte and is 119x leaner, which is the other half of the same argument: the ratio
moved because the grid changed under it, and only a table carrying both numbers shows that.)

So `FastGridFeatureBench` carries the two reference points as rows of its own, in the same table as the
features rather than in a document beside it, and a commit that changes what the grid costs records
three things:

- what the feature costs against the bare grid,
- where that leaves the grid against `RadzenDataGrid` with the same feature on,
- and against QuickGrid, noting where QuickGrid has no such feature to compare - which is most of them,
  and is itself the answer to "why is this grid heavier than QuickGrid now".

The second is the one that decides. `RadzenDataGrid`'s baseline already includes the cost of features
it cannot switch off, so comparing a switched-on FastGrid against a switched-off RadzenDataGrid is not
like for like; comparing both with the feature on is the honest test, and is what the reference rows
exist for.

Worth recording that the honest test did not go the way it was expected to. The assumption when these
rows were added was that comparing a switched-on FastGrid against a switched-off RadzenDataGrid
*flattered* FastGrid. It does the opposite. Every feature, both grids, same data and same five columns:

| Feature on both, N=1000 | `RadzenFastGrid` | `RadzenDataGrid` | Gap | Costs RadzenDataGrid |
| --- | ---: | ---: | ---: | ---: |
| *nothing* | 152.92 KB | 13,172 KB | 86x | - |
| cell tooltip | 269.62 KB | 13,172 KB | **49x** | +0 KB |
| row class | 153.17 KB | 14,087 KB | 92x | +914 KB |
| row click | 169.17 KB | 14,834 KB | **88x** | +1,662 KB |
| a filter row | 157.14 KB | 16,098 KB | **102x** | +2,926 KB |
| a column picker | 175.77 KB | 15,618 KB | **89x** | +2,446 KB |
| responsive titles | 153.01 KB | 17,374 KB | **114x** | +4,202 KB |
| row detail | 169.27 KB | 18,467 KB | **109x** | +5,295 KB |
| cell click | 169.17 KB | 22,352 KB | **132x** | +9,180 KB |

Measured against `RadzenDataGrid` as it now stands on master, which has absorbed the render and async
work these rows were first measured against as PRs. That is the point: the comparison is against the best
version of the thing being compared to, and it has moved twice now.

The interesting part is what did *not* move. The baseline fell 480 KB, and four of the marginal costs -
row class, row click, the column picker, cell click - came back within a kilobyte of what they were, so
their totals fell by the baseline shift and nothing else. Three fell further on their own: the filter row
by 260 KB, responsive titles and row detail by about 480 KB each. A feature's marginal cost is the durable
number here; the totals move whenever the grid underneath them does.

The gap used to narrow wherever this grid charged for something `RadzenDataGrid` charges for anyway -
a delegate per row or per cell - and widen wherever the feature is markup the other grid pays per row.
That has stopped being true, because the delegates have gone. One listener on the tbody answers for
every row and cell and for the row-detail toggle, so a cell click costs 0.78 KB rather than 1,483, row
detail 0.88 KB rather than 404, and the three of them together cost that once rather than each. Those
three were 16 KB apiece until the index-string table was grown to fit the rows being rendered; the
section below on `data-r` has what that was.
The narrowest row in the table went from 14x to 132x, and no feature here charges a delegate per row
any more. What is left is the shape of the second half of that sentence only: the gap now widens
everywhere, because every remaining difference is markup the other grid pays for per row.

Which is the argument for the reference rows either way: the direction of the error was not guessable,
and half these numbers had never been measured at all.

### The reference rows are bimodal, and one run of them proves nothing

`= RadzenDataGrid, same columns` does not settle on a value. Run it on its own, same binary, same
machine, and it returns **12.86 MB about three times in four and 13.83 MB the rest of the time** - a
990 KB step between two stable values, with nothing in between. Two full passes of this table
disagreed by exactly that step on that row while every other row reproduced to within half a kilobyte,
which is how it was noticed at all.

The correlate is visible in the diagnoser's own columns: every 12.86 MB run records gen1 and gen2
collections, and the 13.83 MB run records none. That was read here for three sessions as pointing at
`RenderTreeBuilder`'s pooled frame arrays - whether they survive between iterations decides whether the
next one re-grows from scratch - and recorded as a hypothesis that would stand "until something measures
the pool directly".

**Something has now measured the pool directly, and it is not the pool.** `dotnet run --project
gridbench -- pool-probe` listens to `ArrayPool`'s own EventSource while rendering, so a rental is a
bucket with a size and a reason rather than an inference from a GC column. §26 of the spec has the whole
measurement; the short form:

- The large frame arrays *are* pooled, and that half is confirmed rather than refuted. Every render
  rents 65536, 32768 and 16384-element buffers from `ArrayPool<RenderTreeFrame>.Shared` - identified by
  renting from that pool and filtering on the id it reports, not assumed - and returns them, allocating
  them only on the first render. Three forced gen2 collections did not make the next render re-allocate.
  **The small buckets are a different story**: the 64-element bucket misses 1,680 times in *every*
  render, 4,200 KB of it, dropped again on return. It is a constant on both sides of the step, so it is
  not the step - but "only the first render allocates" was written here first and was wrong.
- **A real pool miss is far too big to be this step.** Hold the buffers so the pool cannot satisfy the
  rental and the render allocates **5,120 KB more**, which the pool's own events account for exactly:
  9,320 KB allocated, less the 4,200 KB the 64-bucket costs every render.
- **The step is the JIT.** With the probe's listener disarmed - it costs 4,420 KB a render, a third of
  the workload - allocation per render steps **14,157.4 KB (13.83 MB) → 13,219.9 (12.91) → 13,172.1
  (12.86)**, by 937.5 KB and then 47.8 KB, 985.3 KB in total. `DOTNET_TieredCompilation=0` removes the
  big step entirely; so does `DOTNET_JitObjectStackAllocation=0`, and so does `DOTNET_TieredPGO=0`. What
  moves is objects that tier-1 with dynamic PGO stack-allocates and tier-0 puts on the heap.

**Those three plateaus are the three values this row reports**, and the two ends are the historical pair.
Which one a benchmark process reports is whether tier-1 arrived before its measured window - and that is
demonstrable rather than inferred: `DOTNET_TC_CallCountingDelayMs=2000`, which delays promotion and
changes nothing else, pins the row at **13.83 MB three runs out of three** against 12.86 MB in the
default runs beside them.

**The 1-in-4 rate is gone.** 190 fresh default-configuration processes produced the high mode zero times.
Tier-1 now lands well before the measured window unless something delays it, and the old correlate has
gone with it - a full-table pass reported this row in the low mode with no gen1 collections at all.

**The operational consequence is the part that matters: take the modal value of several runs before
recording anything from a reference row.** The 13,172 KB baseline in the table above is the modal value
of four. A single run of this table produced 14,159 KB for it, which reads as a 507 KB regression and
is an artefact.

### The reference rows found something in RadzenDataGrid

`ShowCellDataAsTooltip` defaults to **true**, so the baseline row above already has it on, and the
tooltip reference row - which set it to `true` - measured nothing. That is the exact shape of a
benchmark that proves nothing, so the row was turned around to measure it *off*: **12,948 KB**, against
18,191 KB with it on.

**That one default was 5,243 KB, 29% of everything `RadzenDataGrid` allocated rendering a thousand
rows**, paid by every grid whose author had never heard of the parameter. `RadzenDataGrid.razor:684-690`
is why: per cell it formats the value into a string it has already rendered once, then allocates a whole
`Dictionary<string, object>` to carry the single `title` attribute. Five thousand dictionaries and five
thousand strings at 1000 x 5.

**Both of those have since been fixed in PR #8**, which is why the table above shows the tooltip costing
`RadzenDataGrid` nothing:

- the per-cell `Dictionary` and its splat are replaced by a `title` attribute the builder omits when the
  value is null;
- the value is derived once and shared with the cell body instead of twice, and only when something
  actually wants it - `GetValue` is `public virtual`, so how often it is called is observable behaviour
  and not merely an allocation.

`ShowCellDataAsTooltip="false"` is no longer a 29% cut, because there is no longer 29% to cut. The
parameter now changes one attribute per cell and nothing else.

It has since gone all the way to nothing. On current master the baseline and the tooltip-turned-off row
are **both 13,172 KB** - the same number, not merely a close one. Turning the tooltip off used to save
704 KB even after PR #8 (13,652 against 12,948), and that residue is now gone. Worth stating plainly
because this section previously reported the tooltip costing "+0 KB" in the table while the off-row sat
704 KB below the baseline, which cannot both be true; the table was comparing two tooltip-on rows. Now
they agree.

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
| `RadzenDataGrid` (with PR #8) | 17,790 us | 18,189 KB | 1x |
| `SlimGrid` prototype | 1,280 us | 266 KB | 68x leaner |
| **`RadzenFastGrid`** | **1,178 us** | **151 KB** | **120x leaner** |
| QuickGrid | 2,342 us | 370 KB | 49x leaner |

Re-measured after the layout, chrome, row and selection features landed. The component's own cost
against the commit before them, on the same machine in the same session: **+0.72 KB at 1000 rows and
+0.84 KB at 200**. Constant rather than per-row, which is what it should be - it is the two lists the
grid now keeps to hold its drawn columns and to order them, allocated once per component and not
touched per row. Time is unchanged: 1,129 us to 1,178 us at 1000 rows against a 60 us standard
deviation, and 348.9 us to 348.5 us at 200.

The 149 KB this table used to carry came from an earlier session; the same build measured 150.44 KB
here. Sessions drift, so a regression is only worth reading against a baseline taken beside it.

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

`FilterAsYouType` (default on) and `FilterDelay` (default 500 ms) match `RadzenDataGrid`'s names and
defaults, and the shape of the binding is worth recording because the first attempt got it wrong.
Typing adds `oninput` to the filter box; it does not *replace* `onchange`, which is what a blur and an
Enter raise. Swapping one for the other looks equivalent and is not: it silently removes the only
event that commits a box the user abandons mid-pause.

Both events then fire for the same typing, so the two meet at one apply point that skips text already
applied - which is why the column records the *text* that produced its filter rather than trusting the
value. `"3.0"` and `"3"` are one filter value and two different things to have typed, and an
unparseable `"3-"` filters by null exactly as an empty box does. Anything that filters by another route
- descriptors, the clear button, a declared `FilterValue` changing - drops the recorded text, so
re-typing what was there before still applies. Both of those were mutants that survived the first
version of the tests and are now covered.

The debounce is a generation counter rather than a `CancellationTokenSource` per keystroke: a
superseded delay still wakes up, finds itself out of date and returns, and there is no token source to
own, cancel or dispose. Committing the box supersedes any pending delay, so the abandoned wait cannot
wake up afterwards and re-apply stale text. It checks disposal at the same point, because typing and
navigating away inside half a second is ordinary use and leaves a delay running against a component
that is gone.

The pause saves the query. What saves the *render* is a separate thing, and skipping it would have left
most of the feature undone: `ComponentBase` re-renders after every event it handles, so a bound
`oninput` redraws the whole grid on each keystroke to show exactly what is already on screen. Measured
at three keystrokes with the pause not yet elapsed: **three full renders, nothing applied**. Binding it
through a non-rendering receiver instead takes that to **zero**, and the render that matters still
happens, because it comes from the reload the filter triggers rather than from the keystroke.
`EventCallback.Factory.CreateBinder` cannot carry the receiver - it wraps the delegate, so
`callback.Target` is a compiler-generated closure rather than the `IHandleEvent` - which is why this one
box has a binder on `onchange` and a plain callback on `oninput`.

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

Four faults here were wasted work rather than wrong output, and only turned up because the tests count
calls: the grid pre-loaded a page in `OnParametersSetAsync` that the provider then re-fetched; the
`LoadData` handler was called once with no window at all before the provider asked for one; new data
assigned to a virtualized grid left `Virtualize` holding the window it fetched from the old source; and
**the total was counted on every window**, so an endless scroll against Entity Framework ran a
`COUNT(*)` per scroll. Scrolling does not change how many rows there are - only a sort, a filter, a
reload or new data does - so it is counted once per query now.

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

### The visual pass, second time

The component grew a filter row, a check-box list and a pager after the first visual pass, so
`dotnet run --project gridbench -- visual <dir>` now dumps a fully featured grid as a fourth pane, sorted
so the sort icon is in the output at all - both grids draw it only on the column actually sorted.

It found one divergence: `RadzenDataGrid` emits `rzi-sort` alongside the direction class and this did
not. Inert under the shipped themes, because the direction rule wins for both glyph and colour either
way, but a custom theme's `.rzi-sort` rule would apply to one grid and not the other. Now matched.

It also confirmed what the geometry check cannot: that the pager, the filter boxes and the check-box
list are laid out and drawn correctly against the real theme. Worth noting the first screenshot showed
every icon as raw ligature text - `first_page`, `arrow_drop_down` - which was the harness not copying the
font files next to the stylesheet, not the component. Look twice before believing a visual fault.

### What a code review found that 254 tests did not

Seven faults, each invisible for a specific reason worth naming - the reasons generalise further than
the faults do.

| Fault | Why nothing caught it |
| --- | --- |
| The unloaded query was enumerated **twice** from the render thread - once for the rows, once for the pager's total - pulling an entire unpaged table synchronously while the awaited load was in flight | Every fake executor in the suite returned `Task.FromResult`, so no test ever rendered with the load still outstanding |
| A column typed as `object` left what was typed in the filter box as a string, and the predicate builder put a string constant where an int belongs: *argument types do not match* | Nothing filtered an `object`-typed or template column |
| Sorting the check-box-list values threw for a type that is not `IComparable`, taking the grid's whole first render down | Every lookup in the suite happened to be strings or ints |
| **Every column recompiled its expression on every render** | Razor rebuilds the expression tree per render, so reference equality never holds in markup - and every test builds its fragments by hand, reusing one instance |
| The check-box-list lookup cache was never cleared on the `LoadData` path, so page one's values were offered on every page | No test combined `LoadData` with a check-box list |
| `Dispose` disposed the cancellation source without cancelling it, leaving an in-flight query running against a component that is gone | Nothing disposed a grid mid-load |
| Sequence numbers descended between regions, so the table was torn down and rebuilt whenever the pager appeared | Output is correct either way |

The recompile is the one worth remembering. It is not visible in the markup, not visible in the frame
count, and not visible to any test that reuses an expression instance - only to one that authors columns
the way Razor does and weighs what a re-render costs. Measured at **6,207 B per re-render against
14,511 B**, for five rows and two columns; the gap widens with every column.

The fix leans on a property of the path derivation rather than on comparing trees: a path is only
derived for a plain member chain, which is exactly the shape that cannot capture anything, so two
expressions with the same non-null path are interchangeable. Anything computed has no path, is never
treated as equivalent, and is recompiled.

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

267 tests, of which eleven compare `RadzenDataGrid<T>` and `RadzenFastGrid<T>` rendered from the same
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

Two of the panes have no `RadzenDataGrid` beside them, and deliberately: the keyboard cursor is drawn
on a read-only grid and on a frozen cell, and a parity assertion there would assert that this grid
matches a grid that paints nothing. They are checked against their own neighbours instead - the focused
cell's outline against the cell next to it, and the focused row's colour against a row **two** rows
away, since striping is `:nth-child` and the row next door differs whatever focus does. The package's
`fastgrid.css` is linked into the page after the theme, so what those panes measure is what an
application gets.

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
| Drop `rz-selectable` from the grid | 1 | `selected 'rgb(255,255,255)' vs unselected 'rgb(255,255,255)'` |
| Drop the toggle column's `<col>` | 3 | widths shift one column left; toggle cell `47.19px -> 90px` |
| Give a frozen column no inset | 2 | `frozen moved -200px, unfrozen moved -200px`, and every row of every section reported covered |
| Drop the frozen header's `z-index` | 1 | `covered in: thead row 0 col 1, thead row 1 col 1` - both header rows, and only the column with something to its left to be covered by |
| Unpin the filter row | 1 | `covered in: thead row 1 col 0, thead row 1 col 1` |
| Unpin the footer | 1 | `covered in: tfoot row 0 col 0, tfoot row 0 col 1` |

The last six rows are all the same fault in different clothes: a class the theme scopes under a parent
the grid never emitted, or an inset the theme never supplies. Each passed every markup assertion, and
each needed the browser to be asked what it actually drew. The filter-row row is the sharpest of them -
the check written for the header fault still passed with the filter row broken, because it searched for
cells already carrying the frozen class and a row that never got one has nothing to find.

The alternating-class rule is checked two ways on purpose. The named form catches Radzen's own
`rz-datatable-odd`/`-even`; the general form - all rows must carry an identical class list - catches an
alternating class under any name, which is what a keyword match would miss.

### Divergences the check does not fail on

Rendered geometry, text alignment and column widths are identical, but the two grids' markup is not, and
these are the differences the parity rules deliberately do not cover:

| Divergence | Consequence |
| --- | --- |
| `title="<value>"` on the cell span is opt-in | `RadzenDataGrid` always emits one, so a cell truncated to an ellipsis reveals its full value on hover. `RadzenFastGrid` does it behind `ShowCellDataAsTooltip`. Measured on the component at **+116 KB** at 1000 x 5 - about 23 B/cell, against the ~61 B/cell this table predicted from the prototype, so +77% rather than the tripling it forecast. Still off by default, since it is an attribute per cell plus deriving each cell's text a second time; a `TemplateColumn` remains the way to have it on one column only. |
| No `rz-text-truncate` on the cell span | Inert: `.rz-grid-table td .rz-cell-data` already sets `overflow/text-overflow/white-space`. Verified: identical computed styles. |
| No `role="presentation"` on the table | Inert. The `<colgroup>` this row once said was missing is emitted - widths, resize and the frozen columns' insets all depend on it, and it carries a bare `col` for the toggle column so the widths below it do not shift by one. |
| No `rz-text-align-*` class on `th`/`td` | `RadzenFastGrid` has `TextAlign`, and applies it as `text-align` in the memoized cell style rather than as a class. Same rendered alignment; a custom theme hanging rules off the class name would not see it. |
| No `rz-datatable-scrollable`, no `rz-data-grid-data[role="grid"]`, no `rz-has-pager` | Deliberate (spec §6). The scroll container is also what carries `RadzenDataGrid`'s keyboard navigation, so that is not free either. |

## Lookup face-off: RadzenDropDownDataGrid vs RadzenFastDropDownDataGrid

`dotnet run -c Release -- --filter '*DropDownBench*'`, and `dotnet run -c Release -- dropdown-probe`
for the frame counts behind it. Both bound to the same rows, three columns, ten per page, sorting on,
filtering off - see below for why off.

Over 1,000 rows (net10.0, `--job` default):

| Method | Mean | Allocated | vs baseline |
| --- | ---: | ---: | ---: |
| `Radzen_Closed` | 4,275 us | 177.3 KB | 1.00 |
| `Fast_Closed` | **4.3 us** | **6.3 KB** | 0.001 / 0.04 |
| `Radzen_Open` | 4,273 us | 178.4 KB | 1.00 |
| `Fast_Open` | **151 us** | **39.4 KB** | 0.035 / 0.22 |

At fifty rows the figures are the same to within noise (4,152 / 3.9 / 4,262 / 154 us). They are flat in
N because paging draws ten rows either way: what this compares is the shape of the render, not the size
of the source.

### Why

`dropdown-probe`, same configuration:

```
  RadzenDropDownDataGrid       closed  716 frames  30 td  19 components
                               opened  716 frames  30 td  19 components  (+0)

  RadzenFastDropDownDataGrid   closed   26 frames   0 td   0 components
                               opened  419 frames  30 td   7 components  (+393)
```

Both emit the same thirty cells when open, which is what makes the timing comparison like with like.

The `+0` is the result. `RadzenDropDownDataGrid` renders its popup grid whether or not anyone opens it,
so a form with twenty lookups has drawn twenty grids before the user touches one. That is also why its
open and closed timings are identical: opening is free because the work was already done, at load.
`RadzenFastDropDownDataGrid` builds nothing until the first open, and keeps what it builds afterwards -
which is not only cheaper but preserves the sort, filter and page the user left the popup on.

### Two corrections to this benchmark's own method

Both were caught before the numbers were quoted, and both would have made the result untrustworthy:

- **Filtering was on for both in the first cut.** It should not have been: `RadzenDropDownDataGrid`
  never passes `AllowFiltering` to its popup grid - it has a single search box above it instead - while
  `RadzenFastDropDownDataGrid` filters through the grid's own per-column filter row. That measured a
  filter row against nothing. It biased *against* the new component, but a comparison that is not like
  with like is not worth quoting in either direction.
- **`--job short` produced error bars larger than its means** (+/-5,589 us on a 4,672 us mean). Those
  numbers were discarded rather than reported. The table above is the default job, whose error is
  around 2% of the mean.

## Keyboard navigation

`--job short --filter "*FastGridFeatureBench.Bare*" "*FastGridFeatureBench.KeyboardNavigation*"`, three
runs, playground stopped:

| | Allocated |
| --- | ---: |
| bare | 153.82 / 153.93 / 153.96 KB |
| `+ keyboard navigation` | 155.27 / 155.16 / 155.24 KB |

**+1.34 KB**, against a gate of +2 KB. Full-length runs for the time, which is the only job length the
ratio means anything at:

| Run | bare | `+ keyboard navigation` | Ratio |
| --- | ---: | ---: | ---: |
| 1 | 524.8 us +/- 9.87 | 541.5 us +/- 10.77 | 1.03x |
| 2 | 519.7 us +/- 10.08 | 522.5 us +/- 10.42 | 1.01x |
| 3 | 524.8 us +/- 10.49 | 471.1 us +/- 5.47 | 0.90x |

Every one of those has an error bar wider than the difference it claims, which is the answer: **not
measurably slower**. The middle run is the one to quote if a single number is wanted, and the honest
reading of all three is 1.01x. The `--job short` time column for the same pair returned 1.00, 1.04 and
1.19 - it is not a time measurement and reading it as one would have failed the 1.02x gate on a number
that does not exist.

There is no `= RadzenDataGrid + keyboard navigation` row to set beside this, and the absence is the
finding rather than an omission: that grid's tab stop and keydown handler are unconditional, so its
`= RadzenDataGrid, same columns` row **is** the navigation-on measurement. Against it: 155.2 KB against
12.86 MB, **85x**, costing that grid nothing marginal because it never had the choice. Five runs of the
reference on its own returned 12.86 MB every time, so this one is not sitting on the bimodal step.

### Range selection measures as nothing, and the row that says so is 0.23 KB off

`--job short`, two runs, plus one full-length run for the time:

| | Allocated |
| --- | ---: |
| bare | 153.83 KB |
| `+ keyboard navigation` | 155.25 / 155.18 KB |
| `+ keyboard navigation and range selection` | 155.48 / 155.48 KB |

Full length, one run: bare 439.2 us +/- 5.72, navigation 437.4 +/- 4.72 (**1.00x**), range selection
436.0 +/- 3.34 (**0.99x**). Three means inside one error bar of each other, which is the answer.

The allocation row is the interesting one, because it is **0.23 KB above the navigation row for a
feature that renders nothing**. The two rows differ by one parameter - `SelectionMode` - and the
control settles which of the two that 0.23 KB belongs to: set the same parameter to its **default**
value, so the feature is off and the parameter is still passed, and it reads 155.48 KB as well. So the
cost is the harness handing the component one more parameter, and range selection itself is **+0 KB**.
That is what its shape predicts - it has no parameter of its own, binds nothing and emits nothing,
because a Shift key is the whole of its surface - but the number had to be asked for rather than
assumed, which is the rule the row above this one exists to enforce.

**A benchmark row that differs by a parameter is measuring the parameter too.** At a hundred kilobytes
that is invisible; at one and a half it is a fifth of the reading.

### Positional ARIA, and where its two halves land

`--filter "*FastGridFeatureBench.Bare*" "*FastGridFeatureBench.PositionalAria*"`, full length, quiet
machine:

| | Allocated | Time |
| --- | ---: | ---: |
| bare | 153.88 KB | 1.00x |
| `+ a pager and row numbers over one page` | 155.81 KB | 1.01x |
| `+ six columns with the middle one hidden, and column numbers` | 159.87 KB | 1.20x |

Neither marginal is the attribute, and both rows need a control to say so.

**Row numbers cost nothing.** The pager row reads 155.81 KB; with the emission taken out and everything
else the same it reads 155.80. All 1.93 KB of the marginal is the pager component. The two cannot be
told apart by a parameter - the grid emits the numbers exactly when the DOM stops being the whole table,
which is what drawing a pager means - so the control is a build rather than a row.

**Column numbers cost nothing in bytes either.** The column row's 5.99 KB is the sixth declared column,
which registers whether or not it is drawn: with the emission out, the same row reads 159.82 KB. Forcing
the attribute onto every cell of the ordinary five-column bare grid isolates it from the other
direction and moves 153.88 to **153.97 KB**.

**What they cost is time, and only the per-cell one.** The column row runs 1.20x; with the emission out
it runs about 1.09x, so the attribute is roughly **1.1x** on its own - one frame on each of five
thousand cells. The two builds were measured in separate runs, so treat that as one significant figure.
It is the same shape frozen columns have at 1.10x for two frames on the cells of one column, and it is
the measurement that earned `aria-colindex` its three tiers: a grid hiding its last column writes
nothing, one hiding its first writes one index per row, and only a hole in the middle pays per cell.

Row numbers under a hundred: the grid still emits them per row, and they are still free, because the
index-string table now covers whatever is being rendered. Before it was grown, this same row read
171.29 KB - which is how the table's size came to be looked at at all.

### The measurement audit: two claims checked with controls, one of them wrong

Prompted by the `data-r` correction below. The question asked of every large recorded cost was the one
that would have caught it: **is the stated cause isolated by a control, or inferred?** Two claims were
attributed to a mechanism by reasoning alone.

**`ItemKey`'s 23.5 KB is the boxing — confirmed.** The claim was that a `Func<TItem, object>` over an
`int` boxes once per row, 24 bytes a thousand times, and therefore "a reference-typed key costs nothing
here". That second half is a testable prediction and it had never been run. It is now a permanent row:

| | Allocated |
| --- | ---: |
| bare | 153.89 KB |
| `+ ItemKey` (an `int` key) | 177.37 KB |
| `+ ItemKey over a reference-typed key` | **153.93 KB** |

+0.04 KB. The attribution holds, and the claim is now measured rather than reasoned.

**The cell tooltip's 116 KB is the text, not the attribute — corrected.** The write-up read "the `title`
attribute plus deriving each cell's text a second time", crediting the attribute with a share. Writing a
constant `title` on every cell and skipping the derivation entirely:

| | Allocated |
| --- | ---: |
| bare | 154.03 KB |
| `+ cell tooltip`, constant title, nothing derived | **154.03 KB** |
| `+ cell tooltip`, as shipped | 270.59 KB |

The attribute frame is **free**. All 116.7 KB is `CellTextOf` allocating a string per cell. Same shape
as the `data-r` error, found the same way.

**The rule that comes out of all of it:** *markup is paid in the values, not the frames.* Every large
per-row or per-cell allocation on this branch has turned out to be a string once a control was put
behind it, and every frame-shaped cost has turned out to be time — `aria-colindex` 1.1x, frozen columns
1.10x, responsive titles 1.40x, each under a kilobyte. **A large allocation attributed to a frame has
not been measured yet.**

Also checked and clear: no other per-row or per-cell string is built in the render path. `RowClassFor`
returns constants in the common case and memoizes otherwise, and the only concatenations left are per
column, in the header and footer.

### `data-r` cost 16 KB, and it was the string table running out

The design had the cursor address rows by the `data-r` attribute that delegated clicks already write,
on the reasoning that its values are pre-cached strings and so the attribute "costs a frame and no
allocation". Measured, that came out wrong by eight times the feature's whole budget:

| | Allocated |
| --- | ---: |
| bare | 153.82 KB |
| `+ keyboard navigation`, writing `data-r` per row | 170.41 KB |
| `+ keyboard navigation`, addressing rows by position | 155.16 KB |
| `+ row click` (writes `data-r`, binds no delegates) | 169.85 KB |

The conclusion drawn at the time was that the strings are free and the **frame** is not -
`RenderTreeBuilder` rents its frame array from a pool, and a thousand more frames push that rental
into the next bucket. It was the wrong half.

**The table of index strings held 512 entries.** A thousand-row grid therefore called
`int.ToString()` on 488 rows of every render, which is 488 strings a render and about 16 KB of them.
The premise - "its values are pre-cached strings" - was true of the first 512 rows and of no others,
and the benchmark renders a thousand. Growing the table to fit settles it:

| | Allocated |
| --- | ---: |
| bare | 153.88 KB |
| `+ row click`, table of 512 | 169.17 KB |
| `+ row click`, table grown to fit | **154.66 KB** |
| `+ cell click`, table grown to fit | 154.66 KB |
| `+ row detail available`, table grown to fit | 154.76 KB |

So an attribute per row costs **+0.78 KB**, not +16, and the frame is nearly free after all. Three of
the most expensive rows in this document fell by 14 KB each for a change that touches one array.

**How it hid for so long.** Every check that was run pointed at the attribute, and correctly: the
row-click control writes `data-r` and binds nothing else, and it landed within half a kilobyte of the
keyboard row. Both features paid the same 16 KB and it really did belong to the attribute - to the
*value* it wrote rather than the frame that carried it. A control that separates a feature from an
attribute does not separate an attribute from its value, and nothing here was asking it to.

**What made it findable** was measuring a second attribute per row and getting a different answer.
`aria-rowindex` writes one attribute per row exactly as `data-r` does, and measured +15.5 KB - the
same number, which is confirmation. `aria-colindex` writes one per *cell*, six times as many frames,
and measured **+0.09 KB**. Six times the frames for a twentieth of the cost is not a frame-count
story, and the one thing the two attributes do not share is the range of their values: row indexes run
to a thousand and column indexes run to six.

The pooled frame array is still real - and it is now measured rather than supposed, by `pool-probe`
above: the same buckets are rented and returned on every render and only the first one allocates. What
that section takes away is its role in the bimodal step, not its existence. The two sightings that
remain are exactly what a pooled rental that does not change bucket looks like, and the probe confirms
the buckets do not change:

- **Frozen columns**: two attribute frames on the cells of a frozen column. **1.10x time and +0.9 KB**.
- **`aria-colindex`**: one attribute frame on every cell. **~1.2x time and +0.09 KB**.

Both are work with no bytes, which is what a pooled rental that does not change bucket looks like.
What is gone from this list is the case that looked like bytes with no work - that one was never the
frame array at all. **Markup is paid in render time; the bytes beside it are usually something else,
and it is worth finding out what before naming a mechanism.**

The addressing decision stands even so, because it is no longer about the cost. DOM order is not model
order - `Virtualize` emits a spacer `tr` and every expanded row emits a second `tr` beneath itself -
but the rendered *data rows* are model order, because both intruders are distinguishable: the detail
row carries `rz-expanded-row-content` and the spacer carries no class. So the script takes the nth
`tr.rz-data-row`, and virtualization keeps the attribute because there the index is a position in the
whole data set rather than in the DOM. What has changed is the price of the alternative: writing the
attribute on every row would now cost 0.78 KB rather than 16, so this is a preference rather than a
saving, and it is recorded as one.

### The playground grew a Virtualize toggle, and it found a circuit-killing loop

Keyboard navigation needed one - the cursor moves through the whole data set under virtualization
rather than through the rendered window, and there was no way to drive that in a browser. The toggle
took a minute to add and immediately turned up a fault no test in this repository could reach:

**A virtualized grid over an asynchronous source refreshed itself forever.** Roughly 880,000 renders in
two and a half seconds at 200% CPU, the trace cycling
`Home -> RadzenFastGrid -> Defer -> Home -> CascadingValue -> Virtualize`. The server logged
**nothing**: no exception, the process stayed up, and the only trace was the WebSocket closing 1006 and
the metrics strip freezing at whatever it last managed to flush.

The cycle, once instrumented, was four steps and every one of them looked reasonable on its own:

1. `OnParametersSetAsync` compares `Data` by reference and finds it changed.
2. Virtualizing, it calls `RefreshAsync`, which raises `SettingsChanged` - the grid announcing its
   state so an application can persist it.
3. The application stores what it was handed and re-renders, which is the entire point of the
   parameter.
4. Its `Data` property answers with **a new queryable object**, because that is what
   `context.Rows.AsNoTracking()` does every time it is read. Back to 1.

The settings guard was not the fault and held perfectly throughout: `raisedSettings` is compared by
reference and every returning instance was correctly ignored. The loop ran entirely on the *data*
comparison, and the announcement was what fed the parent the render it needed to close the circle.

**The fix is that a data change is not a settings change.** Being handed a new source is not something
the user chose, so there is nothing to persist and nothing to announce; `RefreshAsync` gained an
`announce` flag and the parameter-set path passes `false`. Nothing else changes, and the paged branch
never had the fault because `BeginAsyncLoad` announces nothing - it was bounded by luck rather than by
design. `Data` is also now read once rather than twice, since an unstable source was being compared
against one instance and remembered as another.

`EntityFrameworkTests` pins both halves: a fresh-but-equivalent queryable raises no settings, and a
genuinely different query still reloads. The playground's `EfSource.Rows` is deliberately left
unstable, because that is what ordinary application code looks like and it is what caught this.

Three things about finding it are worth keeping:

- **A frozen counter reads exactly like a stable one.** The first pass concluded "keyboard navigation
  off, renders steady at 2, clean" - and 2 was simply the last number that reached the browser before
  the circuit stopped answering. The reading that discriminates is not the counter but whether the
  circuit *responds*: click a toggle and see whether the DOM changes. Several conclusions drawn from
  the counter before that check were wrong, in both directions.
- **Bisecting the suspect first wasted the most time.** The feature being built was assumed guilty
  because it was new, and two builds went into removing its markup and then its lifecycle hooks before
  anyone reverted the *library* and reproduced it with none of it present. Reverting to the last known
  good commit is the cheaper first move and it was available from the start.
- **The log said nothing, and that was the clue.** An unhandled exception in a circuit logs. Silence
  plus a 1006 close plus 200% CPU is not a crash, it is a spin - and it points at the render loop
  rather than at the data path, which is where the two builds of bisecting went looking.

`FastGridVirtualizationTests` and `EntityFrameworkTests` both covered a virtualized grid over a DbSet
before this and both passed, because bUnit has no viewport: `Virtualize` asks its provider for
everything once. What was missing was not a browser but a parent that stores what the grid announces
and hands its `Data` back - which is now a test, and did not need a browser after all.
