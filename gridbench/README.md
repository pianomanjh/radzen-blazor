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

## Recording a measurement

A feature's marginal cost answers "what did this cost". It does not answer "is this grid still worth
using", which is the question anyone reading a commit is actually asking - and the two can point
different ways. Row detail costs 403 KB, which leaves the grid 33x leaner than `RadzenDataGrid` and,
for the first time, *heavier* than QuickGrid. Neither of those facts was in the commit that added it.

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
| row detail | 557.03 KB | 18,467 KB | **33x** | +5,295 KB |
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
Both click rows have since stopped charging for the delegate: one listener on the tbody answers for
every row and cell, so a cell click costs 16 KB rather than 1,483 and the narrowest row in the table
went from 14x to 132x. What is left is the shape of the second half of that sentence only. Row detail
is the last feature here that costs a delegate per row, and it is the last row where the gap closes.

Which is the argument for the reference rows either way: the direction of the error was not guessable,
and half these numbers had never been measured at all.

### The reference rows are bimodal, and one run of them proves nothing

`= RadzenDataGrid, same columns` does not settle on a value. Run it on its own, same binary, same
machine, and it returns **12.86 MB about three times in four and 13.83 MB the rest of the time** - a
990 KB step between two stable values, with nothing in between. Two full passes of this table
disagreed by exactly that step on that row while every other row reproduced to within half a kilobyte,
which is how it was noticed at all.

The correlate is visible in the diagnoser's own columns: every 12.86 MB run records gen1 and gen2
collections, and the 13.83 MB run records none. That points at `RenderTreeBuilder`'s pooled frame
arrays - whether they survive between iterations decides whether the next one re-grows from scratch -
and it is the same mechanism guessed at when responsive titles jumped from +0.4 KB against the pre-#8
grid to +4,682 KB after it. That guess was recorded here as a hypothesis fitted to one measurement.
It now has a second instance that can be reproduced on demand, so it is no longer fitted to one - but
the mechanism is still inferred from a correlation rather than demonstrated, and it stays a hypothesis
until something measures the pool directly.

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
| `title="<value>"` on the cell span is opt-in | `RadzenDataGrid` always emits one, so a cell truncated to an ellipsis reveals its full value on hover. `RadzenFastGrid` does it behind `ShowCellDataAsTooltip`. Measured on the component at **+116 KB** at 1000 x 5 - about 23 B/cell, against the ~61 B/cell this table predicted from the prototype, so +77% rather than the tripling it forecast. Still off by default, since it is an attribute per cell plus deriving each cell's text a second time; a `TemplateColumn` remains the way to have it on one column only. |
| No `rz-text-truncate` on the cell span | Inert: `.rz-grid-table td .rz-cell-data` already sets `overflow/text-overflow/white-space`. Verified: identical computed styles. |
| No `<colgroup>`, no `role="presentation"` on the table | Widths match today only because five equal columns under `table-layout: fixed` distribute evenly with or without it. This diverges the moment column widths are supported. |
| No `rz-text-align-*` class on `th`/`td` | Inert for the default, which the theme resolves to `start` either way. `RadzenFastGrid` has no `TextAlign` concept at all yet. |
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
