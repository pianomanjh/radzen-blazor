# Slim read-only Radzen grid — build spec

Everything here is derived from measurements in `README.md` in this folder. Where a decision was made,
the reason and the number behind it are given, so it can be re-argued rather than merely obeyed.

Read `README.md` first for the raw data. This file is the design that follows from it.

---

## 0. Where this is

Shipped as `Radzen.Blazor.FastGrid` on `tech/radzen-datagrid-slim`, rebased onto `upstream/master`.
The branch is now **purely additive**: it changes nothing in `Radzen.Blazor` at all, and the package
needs **no `InternalsVisibleTo`** - the async executor, the string resolver and the non-rendering event
handler are all mirrored over public surface, so it installs against stock `Radzen.Blazor`.

It got there by sending its one library change up rather than carrying it. Upstream has absorbed the
async IQueryable seam (#2689), the render optimizations (#2684) and now the `QueryableExtension`
array-filter fix (#2696) - which is why `Radzen.Blazor.EntityFrameworkAdapter` no longer exists: the
built-in `AsyncEnumerableQueryExecutor` made it redundant. The theme fix that keyboard navigation needs
is up as #2698 and is the one piece not yet merged.

**Built and measured** (1000 x 5, allocation, modal of several runs):

All from one run at `2e7f756dc`, against that run's own bare - the whole feature table in `README.md`
was re-measured with it, and two runs of it agreed to within 0.71 KB.

| | Costs |
| --- | ---: |
| bare | 154.0 KB |
| sorting, filtering, paging, virtualization, column picking, settings, templates, `ItemKey` | see `README.md` |
| row click, cell click, cell context menu, row detail - **all four together** | **+0.7 KB** |
| column resize | +4.9 KB |
| column reorder | +6.7 KB |
| two frozen columns | +1.1 KB |
| keyboard navigation | **+1.2 KB, 1.00x** |
| range selection, on top of navigation | **+0.3 KB** |
| positional ARIA, row numbers | **+0 KB** |
| positional ARIA, column numbers on every cell | **+0.1 KB, ~1.1x** |
| responsive titles | **+0 KB, 1.40x** |
| column auto-fit, off | **0 KB** - 154.04 against a 154.09 bare |
| column auto-fit, on demand | **+0.2 KB**, and ~1.7ms + 0.03ms a rendered row in the browser (§13) |
| the scroll container and `role="grid"` | 0 |

Resize and reorder are measured together as well as apart: 158.9 KB and 160.8 KB on their own, 162.9 KB
at once. They are additive because they are the same kind of cost - a handle and a pair of callbacks
per *header*. Against `RadzenDataGrid` with reorder on both sides, which allocates 13,184 KB for it,
that is **82x**.

Frozen columns cost +1.1 KB - the inset belongs to the column rather than the cell, so what is paid is
one memoized string for the whole grid plus a class and a style frame on the cells of a frozen column.
`RadzenDataGrid` allocates 19,785 KB for the same two frozen columns, which is **128x**.

Every figure above is allocation. `--job short` does not measure time, so the ratios quoted here and in
`README.md` are the ones settled by full-length runs; a feature added since carries no ratio rather
than a short-run guess.

Frozen is the one feature here that costs measurably more *time* than it does memory: a full-length run
puts it at **1.10x** (478.0us to 525.0us, error 3.7 and 10.5), which is those two frames on two
thousand cells. Two frames per cell being visible while a kilobyte is not is the pooled-frame-array
question in §11 again, from the other side.

Against `RadzenDataGrid` with the same feature on both sides, the narrowest row is cell click at
**132x** and row detail is **109x**. Nothing in the grid charges a delegate per row any more.

Keyboard navigation measured 155.2 KB against a 153.85 KB bare grid over three full-length runs, inside
the +2 KB and 1.02x gate §12 set for it, and the re-measurement above puts it at +1.2 KB against a
154.0 KB bare - the same answer from a different baseline, which is what a marginal is for. It cost eight times that until an assumption in §12 was
measured rather than believed: `data-r` on every row read **+16 KB at a thousand rows**. That number
has since been taken apart and it was never the frame - the table of cached index strings held 512
entries, so 488 rows of every render called `ToString`. Growing it to fit took row click, cell click
and row detail from +16 KB each to under a kilobyte, and an attribute per row with it. §12 records
both the design that came out of the wrong reading and the measurement that corrected it.

Range selection measured as nothing at all, which is the answer its shape predicts: it has no
parameter, binds nothing and emits nothing, because a Shift key is its whole surface. The row that
proves it reads 0.23 KB above the navigation row, and setting `SelectionMode` to its default rather than
`Multiple` - the feature off, the parameter still passed - reads the same 0.23 KB. **A benchmark row
that differs by a parameter is measuring the parameter too**, and at this resolution that is visible.

Positional ARIA landed on the other side of the budget from where §12 put it, and for a reason that
took the `data-r` number down with it: **the row attribute is free and the cell attribute is free in
bytes and not in time**. §12 had them the other way round, on a frame-count argument that turned out to
be about string values instead.

**Not built**: editing, grouping, composite headers. Keyboard navigation is built in full - the cursor,
the keys, range selection and positional ARIA, and so is column auto-fit (§13). §10 has what is still
open.

## 1. Why a separate component

`RadzenDataGrid` renders 1000 rows x 5 columns in 28,708 KB on master. Optimising it in place got that
to 18,189 KB (-36.6%, shipped in PR #8). The remaining ~55% is structural:

- the `RadzenDataGridRow` component instantiated per row
- the `RenderFragment` returned per cell by `RenderCell`
- the per-row attribute machinery (`RowAttributes`, `RowStyle`, `RowAriaSelected`, the `<tr>` splat)

**Ruled out, with reasons — do not retry these:**

| Idea | Why not |
| --- | --- |
| Move the cell markup into `RadzenDataGridRow`'s loop so no per-cell fragment is needed | `RenderCell` is **self-recursive** — it calls itself for child columns of a composite header (`RadzenDataGrid.razor`, the `else` branch over `childColumns`). Markup in a loop cannot recurse. Flattening it restructures composite-column rendering, which is live (see the `DataGridCompositeColumns` demo). |
| Pass the parent's builder as a `__builder` parameter so a `@code` method can emit markup | Razor only does this for a component's `BuildRenderTree`. For a method in a `@code` block it parses the generic signature as markup — tried it, 124 compile errors. |
| Render rows inline on a fast path, keeping `RadzenDataGridRow` as fallback | Two row-rendering paths to keep in sync forever. Same drift hazard that `/simplify` flagged in `RenderCell`, at much larger scale. |

A separate component is the low-risk route, not the only one: it touches nothing that already works.

## 2. Budget

At 1000 rows x 5 columns, for identical output:

| | Allocated |
| --- | --- |
| `RadzenDataGrid` on master | 28,708 KB |
| `RadzenDataGrid` after PR #8 | 18,189 KB |
| QuickGrid | 370 KB |
| Slim, bare | 220 KB (119 KB with typed columns, §4) |
| Slim, every feature on and every callback wired | 2,601 KB |

Target: under ~1,000 KB for a realistic configuration. Anything above that means a rule in §3 was broken.

## 3. Architecture rules

1. **Rows and cells are written inline** into the grid's own render tree. No component per row, no
   `CascadingValue` per row, no `RenderFragment` returned per cell.
2. **No callback is allocated unless a handler exists.** This is where the budget goes:
   a row click costs ~310 B/row; a cell click ~296 B/**cell**, which at five columns is five times worse.
   Bind `onclick` only when the corresponding `EventCallback.HasDelegate`.
3. **Nothing is paid for when switched off.** Feature costs are conditional, never unconditional. The
   `oncontextmenu` modifiers in `RadzenDataGrid` cost 10.6% of its entire allocation while evaluating to
   `false` on every cell — that is the failure mode to avoid. The same trap in C# rather than Razor: a
   lambda capturing a local makes the compiler allocate that method's display class **on entry**, not at
   the declaration, so a per-row method with an unused closure inside a branch costs a closure per row.
   That was 21% of this component's allocation until it was moved into its own method.
4. **Free features are in, not out.** Selection, row-style callbacks and responsive column titles measured
   at *zero* marginal allocation. There is no performance argument for omitting them.
5. **A generic value must never be widened to reach an interface.** `((IFormattable)(object)value)` boxes
   every value type it touches — 32 B per cell for a `decimal`, on every row of every formatted column.
   The formatter is built once per column by a generic method constrained to `struct, IFormattable`, so
   the interface call is made under a constraint and the struct stays on the stack. Same rule, same
   reason, as compiling the cell to `Func<TItem, string>` rather than reading the value as an object.
6. **Deriving a string to compare two things allocates; comparing the things does not.** Razor rebuilds
   every column's expression trees on every render, so each one is compared against the last to avoid
   recompiling. Deriving both property paths to compare them cost a list and a joined string per
   expression per column per render; walking the two member chains together costs nothing.

## 4. Column model

Expressions, not string property names. This is both the better ergonomics and the cheaper option:

| Column shape | Allocated (1000x5) |
| --- | --- |
| `Property="Name"` -> `Func<T,object>` | 220.47 KB |
| `Expression<Func<T,TProp>>` -> `AddContent(value)` | 165.78 KB |
| `Expression<Func<T,TProp>>` -> `Func<T,string>` -> `AddContent(string)` | **118.91 KB** |

`RenderTreeBuilder` has no generic `AddContent<T>`, so handing it a value type binds the `object`
overload, which boxes **and then** stringifies. Compile the expression once into a `Func<T,string>` and
only the string is paid for. The naive typed column still pays the box — implement row 3, not row 2.

Shape:

```
abstract class ColumnBase<TItem>
    string Title, CssClass, FormatString        // genuinely strings, leave them alone
    abstract void RenderCell(RenderTreeBuilder b, int seq, TItem item)
    virtual IOrderedQueryable<TItem> ApplySort(IQueryable<TItem> source, bool descending)
    string PropertyPath { get; }                // derived, see below

sealed class PropertyColumn<TItem, TProp> : ColumnBase<TItem>
    Expression<Func<TItem, TProp>> Property
    Expression<Func<TItem, TProp>> SortBy       // optional, defaults to Property
    Expression<Func<TItem, TProp>> GroupBy      // optional, defaults to Property
    Expression<Func<TItem, TProp>> FilterBy     // optional, defaults to Property

sealed class TemplateColumn<TItem> : ColumnBase<TItem>
    RenderFragment<TItem> Template              // ~94 B/cell, see README
```

### Collection-valued properties

A property that is a collection is **listed**, not stringified: `List<string>.ToString()` is the type
name, which is why such a column otherwise needs a template doing nothing but `string.Join`. The
members are joined with `Separator` (default `", "`), `Format` applies to each member, and the filter
matches a row when **any** member matches - `Contains` for a collection of strings, `Equals` for a
collection of value types, because the *element* type decides the operator, not the property type.

Such a column is not sortable: no provider can order rows by a list. An explicit `SortBy` re-enables
it, naming something that can be ordered.

For a collection of **objects**, `CollectionColumn<TItem, TElement>` puts the element type in the
signature, so the member to show and the member to filter on stay expressions:

```razor
<CollectionColumn Property="@(r => r.Accounts)" DisplayProperty="@(a => a.Name)" />
```

Razor infers `TElement` from `Property` - output type inference reaches `IEnumerable<TElement>` from a
lambda returning `List<Company>` - so neither type parameter is named at the call site.
`FilterProperty` defaults to `DisplayProperty`, since filtering on what the reader can see is almost
always what is meant, and the check-box list offers the same member.

**A selector declared as returning `object` hides its member's real type two different ways**, and both
have to be unwrapped or everything derived from that type is wrong: a value type is wrapped in a
`Convert` node, and a reference type is *not wrapped at all* - the tree simply carries a body narrower
than the delegate's return type. Comparing `body.Type` to `ReturnType` catches both; checking only for
a `Convert` catches the first.

A column typed as `object` cannot be recognised statically, so its value decides per cell - one type
test. A typed collection column takes the same path; the element type itself is resolved once per
closed generic type, not per column.

The column applies its own sort (`ApplySort`), since only it knows `TProp`. Strongly typed, translates to
SQL, and skips the dynamic-LINQ string parse entirely. This is QuickGrid's `GridSort<T>` shape.

### Property path derivation — do not skip this

Four things in the Radzen ecosystem consume property **name strings**, and an `Expression` serves none
of them:

| Consumer | Why |
| --- | --- |
| `LoadDataArgs.OrderBy` | it is a `string` — the whole `LoadData` contract |
| OData | `$orderby=Customer/Name` goes over the wire |
| Settings persistence | `DataGridColumnSettings` keys state by property name across reloads |
| `FilterDescriptor.Property` | a string, and it is what `RadzenDataFilter` emits |

So the expression is the *authored* form and the path is *derived* from it once at init and cached.
Walk `MemberExpression`, stripping any `Convert`/`ConvertChecked` wrapper (the boxed
`Expression<Func<T,object>>` form). Verified working for `p => p.Id`, `p => p.Customer.Name`, and
`p => (object)p.Id`. Radzen has `PropertyAccess.GetProperty(string)` but nothing expression->path;
it is about 20 lines.

**Sharp edge:** a computed expression has no path. `p => p.First + " " + p.Last` renders fine but cannot
sort server-side, round-trip through `LoadData`, or persist. Such a column is **not sortable unless an
explicit `SortBy` (or sort key) is supplied** — make that visible at the call site, as QuickGrid does by
requiring an explicit `GridSort<T>`. Do not silently disable sorting, and do not throw for a
display-only column.

## 5. Data path

- **`Data` (`IEnumerable<T>`/`IQueryable<T>`) is the primary path.** Compose filter/sort/page onto it.
  With `IAsyncQueryExecutor` registered (PR #7) an EF queryable is counted and paged asynchronously.
- **`LoadData` stays**, as the escape hatch for sources that are not composable queryables — REST, OData,
  gRPC, stored procedures. Async `Data` does not replace it; it only removes the need for it with EF.
- **Gate the cost.** Build the `OrderBy` string only when `LoadData.HasDelegate || IsOData`. A grid using
  neither must pay nothing for their existence. (Rule 3.) Measured after the data path landed: 0.13 KB
  at 1000 x 5, inside the noise.
- **Never render from the parameter-set path.** `ComponentBase` renders after `OnParametersSetAsync`
  returns; a `StateHasChanged()` inside it flushes the queued render early and the one that follows is a
  second full pass over every row. That cost +94% allocation and no test noticed, because the second
  pass produces identical DOM. `APlainGridRendersExactlyOnce` pins it.
- **No dynamic LINQ.** Sorting uses typed expressions; filtering builds predicates with `Expression.Call`.
  Note `Radzen.Blazor` has **no** `System.Linq.Dynamic.Core` package reference — it ships its own
  161-line `DynamicExtensions.cs`. So there is no dependency to avoid, but there is a string-parse cost
  to skip.

## 6. Markup and styling contract

Emit Radzen's class names and the theme applies for free — including custom themes and CSS variables.
Verified against the real stylesheet: rendered geometry matches exactly (header cell 37px, body cell
37px, table 332px).

- Wrapper: `rz-data-grid rz-datatable`
- Table: `rz-grid-table rz-grid-table-fixed rz-grid-table-striped`
- Row: `rz-data-row` — **no alternating class.** Striping is `:nth-child` off the table-level class;
  computing odd/even per row is both wrong and wasted work.
- Cell: `<td role="gridcell"><span class="rz-cell-data">…</span></td>`. The span is what carries the
  cell's colour, font size, line height and ellipsis truncation, via `.rz-grid-table td .rz-cell-data`.
  `RadzenDataGrid` puts the class on the span **only**, and so does `RadzenFastGrid`. An earlier version
  of this line also put it on the `td`. Harmless under the shipped themes — every `.rz-cell-data` rule is
  a descendant selector — but it is not what Radzen emits, and a custom theme writing a bare
  `.rz-cell-data` rule would have applied it twice.
- **`title="<value>"` on the cell span is opt-in, not absent.** `RadzenDataGrid` always emits one, so a
  cell truncated to an ellipsis reveals its full value on hover. This spec predicted ~61 B/cell from the
  prototype — 305 KB at 1000 x 5, a tripling — and said not to pay it. The shipped component measures
  **+116 KB**, about 23 B/cell, so it is +77% rather than 3x: the prediction was pessimistic because the
  real column's text path is cheaper than the prototype's. It is still off by default, being an attribute
  per cell plus a second derivation of the cell's text, and a `TemplateColumn` is still the way to have
  it on one column rather than all of them.
- **Header cell is structurally coupled:** the theme gives `th` `padding: 0` and hangs the header padding
  off a *direct child div*. `th > div > span.rz-column-title > span.rz-column-title-content` is required.
  Without the div the header row renders shorter. Per column, not per row, so it costs nothing.
- Do **not** emit `rz-datatable-scrollable` unless the full nested scrollable structure is there.

## 7. Reuse from Radzen.Blazor

All public and callable from a dependent package:

| Reuse | For |
| --- | --- |
| `QueryableExtension` | filter/sort composition |
| `RadzenPager` | paging UI, drop in as-is |
| `FilterDescriptor`, `SortDescriptor`, `LoadDataArgs`, `DataGridColumnSortEventArgs<T>` | the descriptor/event model, so `LoadData` handlers port unchanged |
| `RadzenComponent` (base) | `Visible`/`Style`/`Attributes`, mouse + context-menu callbacks, culture, localization |
| `ContextMenuService`, `TooltipService`, `DialogService` | ambient services, consumed the normal way |
| `PropertyAccess` | fallback for genuinely dynamic columns |
| themes | shipped as static web assets under `_content/Radzen.Blazor/` |

**Not** reusable: `ClassList` is internal — write a small class-composition helper.

Will not compose: anything typed to `RadzenDataGrid<T>` specifically (`RadzenDropDownDataGrid` embeds a
real one; the column picker and `RadzenDataGridColumn` are grid-specific).

## 8. Packaging

A separate NuGet package depending on `Radzen.Blazor`, in the shape of
`Radzen.Blazor.EntityFrameworkAdapter` (PR #7). Radzen need not adopt anything; offering costs them
nothing. Package name is still **open** — it lands in the namespace, so decide before writing code.

## 9. Verification protocol

Each layer below caught real faults the previous one missed. Use all of them.

1. **Tests** — and check each one *discriminates*: break the thing deliberately and confirm the test
   fails. Several tests written during this work passed whether or not the code was correct.
2. **Styling parity check** — `dotnet test Radzen.Blazor.FastGrid.Tests`. Layers 3 and 4 below, and the
   structural half of layer 2, run automatically and fail with a non-zero exit: it renders both grids
   over the same data, asserts the markup contract in §6 against `RadzenDataGrid` in the same run, and
   compares rendered header/body/table heights through Chromium against the real stylesheet. Every one
   of its assertions was confirmed to fail with the component deliberately broken — see
   *Proving it discriminates* in `README.md`. It never skips: a missing node, Playwright or Chromium
   fails the run rather than quietly passing.

   **It reads paint as well as geometry**, because three faults got past every markup assertion by
   emitting correct classes the theme did nothing with. Over seven panes it now also asserts that a
   selected row's computed background differs from an unselected one of the same stripe parity and
   matches `RadzenDataGrid`'s; that declared widths land on the columns that declared them; that a
   frozen column does not move when its container is scrolled; and that nothing is drawn over a frozen
   column in *any* of the four sections that stack independently — title row, filter row, body, footer.
   Each of those panes exists because a check without it passed while the grid was wrong.
3. **Markup diff against `RadzenDataGrid`** — `dotnet run --project gridbench -- visual <dir>` writes
   both grids' real HTML. Diff them. This caught a bug where a cell's `style` vanished entirely while
   every test still passed. Still worth doing by hand for anything the parity check does not assert;
   `README.md` lists the divergences it deliberately allows.
4. **Visual pass** — screenshot `compare.html` against the real theme. Caught missing striping and a
   `rz-datatable-scrollable` class that lied about the markup. Both are now assertions in step 2; keep
   the eye for what no assertion has been written for yet.
5. **Geometry** — `node measure.js` reads rendered sizes back through Playwright, ad hoc. Caught a short
   header row that survived a screenshot being looked at, which is why step 2 exists at all.
6. **Drive it in a browser** — `dotnet run --project Radzen.Blazor.FastGrid.Playground`, then
   http://localhost:5399 (it takes ~25s to bind, and a stale instance from an earlier session will hold
   the port while you drive the *old* build - check the toolbar matches your change). Toggles for every
   feature, an Entity Framework / in-memory switch, an adjustable row count, and a metrics strip on the
   page.

   **This layer is not optional, and it is not last.** Layers 1-5 all assert on markup; none of them
   can see what a browser does with it. Nine bugs got through every one of them and were found here,
   most within a minute of the first click, and three of them by a person looking at the screen rather
   than by anything that could have been automated first:

   Two of the nine turned out to be testable after all, once the browser had shown what to look for -
   which is the layer's other use. A fault found here is worth a minute asking what the test would have
   had to do; sometimes the answer is "have a viewport", and sometimes it is a parameter nobody thought
   to set.

   | Fault | Why nothing above caught it |
   | --- | --- |
   | Resize ran, raised its callback, and moved nothing | The script takes a base id and appends `-col`; it was handed the already-suffixed one, found no col, and wrote the width to the `th`, which `table-layout: fixed` discards. The id test agreed with the markup rather than with the script. |
   | The row-detail toggle counted as a row click | Whether a click was a toggle was decided by a flag settled when the listener attached, so a grid that gained a `Template` later drew a toggle the listener had never heard of. |
   | Unhandled `JSDisconnectedException` on every teardown | It derives from `Exception`, not `JSException`. Nothing failed; the only trace was a line in a server log. |
   | A render loop at ~3,600 renders/sec | Nothing on screen changed while the circuit spun, so it read as "the grid is slow". |
   | A selected row was never painted | The theme nests its selected-row rule inside `.rz-selectable`, which the grid did not emit. `rz-state-highlight` sat on exactly the right `tr` and matched nothing. |
   | Scrolled columns drawn over the frozen ones, in the header only | The theme stacks every header cell at the same z-index, frozen or not, so a frozen one tied with its neighbours and document order let the column to its right win. The body was correct, which made it look like a rendering glitch rather than a rule. |
   | The filter row not pinned with its column | It is a second `tr` inside `thead` rather than part of the title row, so it never received the class or the inset - and the check written for the previous fault skipped it, because it searched for cells already carrying the frozen class. |
   | A virtualized grid over an asynchronous source refreshing itself forever | `RefreshAsync` announced a data change as a settings change; the application stored it, re-rendered, and its `Data` property answered with a new queryable - which is what `AsNoTracking()` does on every read. 880,000 renders in 2.5s, no exception, nothing in the log. Every existing test passed: bUnit's `Virtualize` fetches once, and nothing tested a parent that hands the settings back. |
   | The keyboard cursor vanishing on `PageDown` under virtualization | The jump lands on a row outside the rendered window, so the script has nothing to focus; it scrolls to where the row will be and the re-assert after the next render was to catch it. There is no next render - `Virtualize` re-renders *itself* when the window arrives, and the grid's `OnAfterRenderAsync` never runs. The fix waits for the row in the script instead, bounded and superseded by the next keystroke. Every bUnit test passed throughout: there the window is the whole data set, so the row is always already there. |

   **The browser pass on the review fixes (Sep 3 2026) found nothing new broken and confirmed two
   things nothing above could see.** `Responsive` renders correctly for the first time: above the
   breakpoint a cell reads `0` where it used to read `Id 0`, and below it the rows stack into cards
   with the headers hidden - the card layout had never once rendered, because the class the theme
   scopes it under was never emitted. And the column picker's renumbering was proved with a control
   rather than argued: tag the scroller and `tbody` with an expando, toggle `AllowColumnPicking`, and
   at the old sequence both come back **undefined** - the whole table was destroyed and rebuilt to
   show a drop-down - while at the new one both survive. That is the discrimination check §9 asks for,
   applied to a browser rather than a test.

   The playground had no `Responsive` toggle until this pass, which is why the feature could ship
   broken and stay broken: **a feature the playground cannot drive is a feature nobody looks at.**

   Watch **renders/sec** on the metrics strip: a grid at rest is 0, and the panel turns it red above
   five. That reading alone names a render loop in a glance - **while the circuit is answering.** Once
   it is not, the strip freezes at its last value and a stopped counter reads exactly like a quiet one.
   The reading that tells them apart is whether the page still *responds*: click a toggle and see
   whether the DOM changes. A render loop killed a circuit here and was twice read as "clean" from a
   counter that had simply stopped being updated.

   The playground is also where a feature is *discoverable*: selection is driven by clicking a row and
   nothing on the page said so, and its toggle was wired to discard the grid's answer rather than to
   `AllowRowSelectOnRowClick`, so "off" measured a grid that selected into a bin. A control that does
   not drive the grid teaches the wrong thing about it.

7. **Benchmarks** — `--job short --filter "*FastGridFeatureBench*"`. Numbers last: they say nothing
   about correctness.

   **Take the modal value of several runs.** The `RadzenDataGrid` reference rows are bimodal between
   two values about 990 KB apart - every low run records gen1 and gen2 collections and the high one
   records neither, which points at `RenderTreeBuilder`'s pooled frame arrays. One pass of the table
   reported a 507 KB regression that was an artefact of that.

   **`--job short` measures allocation, not time.** Allocation repeats to two decimals across runs;
   the time column does not. Reorder came out at 1.76x, 1.86x and 0.97x on three passes of it, frozen
   at 1.01x and then 2.68x, every one with an error bar wider than the difference being claimed. Both
   settled under a full-length run - reorder 0.93x, frozen 1.10x, errors under 3%. Quote a time ratio
   from a full-length run or do not quote one. And run it on a quiet machine: one of those passes had
   the playground serving a circuit alongside it.

   **Read the numbers against what the feature does per row.** Frozen columns cost +0.9 KB and 1.10x
   time, which looks contradictory until you count what changed: two attribute frames on the cells of
   a frozen column. Frames are pooled, so the work shows up in time and not in bytes - the same
   observation as the bimodal rows, from the other side.

   **Sequence numbers ascend per run, not per element.** `RenderTreeDiffBuilder` finds where an
   element's attributes end and diffs that range on its own, then diffs the children on their own - so
   an attribute numbered above a child costs nothing, and two attributes out of order drop the fast
   attribute path. The comment in `RenderHead` said the two shared one space and was the stated reason
   for a region; the region is still right, for the other reason (a conditional first child and a
   loop's first child would claim the same number), but the rule it cited was wider than the truth. A
   review found seven violations against the wide rule; four were real.

   **Markup is paid in the values, not the frames.** Three large costs on this branch were attributed
   to render-tree frames and all three were strings: `data-r`'s 16 KB was uncached `ToString`, the cell
   tooltip's 116 KB is text derived per cell with a *free* attribute, and `ItemKey`'s 23.5 KB is
   boxing - the one of the three whose stated cause survived a control, at +0.04 KB for a
   reference-typed key. What frames actually cost is time: `aria-colindex` 1.1x, frozen columns 1.10x,
   responsive titles 1.40x, each for under a kilobyte. **A large allocation attributed to a frame has
   not been measured yet.**

   **A control that separates a feature from an attribute does not separate an attribute from its
   value.** `data-r` read +16 KB, and the control that established it - a row-click grid writing the
   same attribute and binding nothing else - was sound and landed within half a kilobyte. It proved the
   cost belonged to the attribute, which was true, and every reading of it after that said "the frame",
   which was not: the table of cached index strings stopped at 512 and the benchmark renders a
   thousand. **When a number is attributed to a mechanism, check that the other half of the thing was
   actually free rather than assumed to be.** What settled it was a second attribute per row measuring
   the same, and one per *cell* - six times the frames - measuring a twentieth of it.

   **Keep the harness's fakes honest.** `gridbench`'s fake `IJSObjectReference` answered `default(bool)`,
   so the grid's click listener never confirmed, the fallback rendered, and the benchmark measured the
   cost the browser no longer pays. A fake standing in for a browser has to answer like one.

### Rules this protocol has cost us

- **Do not narrow a `catch` around an optional path.** Three times in one session, narrowing to the
  precise-looking exception types broke the exact case the catch was written for: bUnit's strict mode
  throws a type this package cannot name, and `JSDisconnectedException` is not a `JSException`. Where
  the fallback is correct, catch everything and say why.
- **A test that agrees with the markup is not a test.** Both the resize id test and the toggle flag
  were self-consistent and wrong about the contract they were meant to pin. Pin the contract, not the
  output.
- **A class the theme scopes under a parent does nothing until that parent is emitted, and every
  markup assertion passes meanwhile.** Selection put `rz-state-highlight` on exactly the right `<tr>`
  and painted nothing for the life of the feature, because the theme nests that rule inside
  `.rz-selectable`, which the grid never emitted. Frozen columns did the same twice over: the theme
  makes a `.rz-frozen-cell` sticky and supplies no inset, and it stacks header and footer cells at a
  fixed z-index whether or not they are frozen. All three were found by a person looking at the screen.
  **Before trusting a mirrored Radzen class, read what the theme nests it under** - `grep` it in
  `themes/components/blazor/_grid.scss` and follow the nesting, not just the rule.
- **A check that looks for the thing being present can only see it once it works.** The frozen-overlap
  probe searched each row for a cell *carrying* the frozen class, so the filter row - which never got
  the class at all - was skipped in silence and the grid reported clean with the bug in place. Ask
  instead what is drawn at the position the feature claims to own. The same trap as the two above,
  one level up: the check agreed with the markup rather than with the contract.
- **A probe that can report a false positive will eventually be deleted rather than fixed.** The same
  overlap check hit-tests rows clipped by the scroller as "covered", because `elementFromPoint` returns
  whatever is painted there. Bound the rows to the scroller before asking.
- **Any browser-facing optimization needs a fallback, and the fallback is what keeps it testable.**
  The click listener leaves the per-cell delegates in place unless the script confirms it attached, so
  `cut.Find("td").Click()` still reaches `CellClick` under bUnit. Without that a test written the
  obvious way would pass while asserting nothing - worse than a slow grid. Cost: a grid whose listener
  cannot attach renders twice, so render hooks run twice there.
- **Order the optimistic render first.** Render the cheap shape and fall back on failure, never the
  reverse: starting with the handlers and dropping them on success makes every browser grid pay the
  cost once and then re-render to undo it.

## 10. Open decisions

- Package and namespace name.
- ~~Column resize~~ - **done**, and it settled the question that gated three features. Resize does not
  need the scrollable variant's structure; it needs a `colgroup`, which the grid already emitted. What
  it did need was the ordinary `.rz-data-grid-data` scroll container, so a widened column has somewhere
  to overflow rather than pushing the page sideways. That container is now emitted always, costs
  nothing measurable, and carries the `role="grid"` the grid had never emitted - the `row`, `rowgroup`
  and `gridcell` roles below it had no grid ancestor. **Column reorder and frozen columns were gated on
  the same decision**; reorder is now built, frozen columns are not.
- ~~Column reorder~~ - **done**, and it needed nothing the scroll container had not already settled.
  A drag writes a `reorderedIndex` beside the column's declared `OrderIndex`, and the placement pass
  that `OrderIndex` already drove does the rest: the feature is a way to *set* an order the grid could
  always draw. The one thing it could not copy from `RadzenDataGrid` is how a move is recorded -
  upstream removes the column from its own list and re-inserts it, which cannot work here because that
  list is rebuilt from column registration. Every visible column is given its index outright instead,
  which survives a re-registration and a round trip through the settings. Costs +6.7 KB and no
  measurable time.
- ~~Frozen columns~~ - **done**, and the theme turned out to supply less than it looked. A
  `.rz-frozen-cell` is made `position: sticky` and given a background, a z-index and the seam shadow -
  but no inset, and sticky without an inset does not stick. `RadzenDataGrid` supplies it from
  `updateFrozenColumnPositions`, which measures the header and writes an inline style to every frozen
  cell in every row - and which is called from exactly one place, inside the resize drag, so upstream
  does not pin anything until a column is resized.

  Here the inset is a property of the *column*: the table is `table-layout: fixed` with a colgroup, so
  a column's distance from its edge is the sum of the declared widths between it and that edge. It is
  composed once, folded into the cell style that was already memoized and already emitted, and correct
  on the first paint with no script and no interop - and nothing to redo on a scroll, a page or a
  virtualized window. The widths are summed with `calc()` rather than parsed, so a column may be sized
  in any unit or a mixture of them. **A run ends at the first frozen column that declares no width**:
  its own position is still known, but nothing after it is, so those are drawn unfrozen rather than
  pinned to a guess.

  Left and right edge runs only. A frozen column stranded in the middle is what `RadzenDataGrid`'s
  `-inner` classes are for; it is drawn as an ordinary column here.

  **Every section stacks differently, and a frozen column has to win in each.** The theme makes header
  cells sticky at `z-index: 1` and footer cells at `2`, frozen or not, so a frozen cell there ties with
  the ordinary ones beside it and document order settles it - the column to its right paints straight
  over the pinned one while every position and inset stays correct. Each is raised one above its own
  siblings, inside the stacking context its section already creates, so neither can climb out over the
  rows. The body needs none of it: an unfrozen cell there is `static`, so being positioned at all is
  enough.

  There are **four** such sections - the title row, the filter row, the body and the footer - and the
  filter row is a second row of the header rather than a thing of its own, which is how it was missed
  after the title row was fixed. The check that catches this reads which columns are pinned off the
  title row and then asks every row what is drawn at that column's x. An earlier version looked for
  cells *carrying* the frozen class instead, and passed with the filter row's pinning deliberately
  removed: a row that never got the class has nothing to find, so it was skipped in silence.

  Costs +0.9 KB and 1.10x the render time at 1000 x 5 with two columns frozen - the only feature on
  this list whose time cost is larger than its allocation, because what it adds is two attribute frames
  per cell of a frozen column and frames are pooled.
- **Delegated clicks are off under virtualization**,
- **Delegated clicks are off under virtualization**, and that is a scope choice rather than a gap. A
  virtualized grid renders a window of some tens of rows, so the per-cell delegates cost tens of
  kilobytes there rather than 1,483, and `Virtualize` hands its `ChildContent` an item with no position,
  so there is no row index for the listener to resolve. Revisit only if virtualized windows get large.
- **Whether turning `AllowSorting` off should clear an applied sort.** It currently does not - the data
  stays ordered, because reordering it would be the surprise - but the icon, the multi-sort badge and
  `aria-sort` now follow `AllowSorting`, so the grid no longer advertises a control that is not there.
- **`ShowExpandColumn="false"` is now a placement choice, not a saving.** It used to avoid 404 KB; row
  detail costs under a kilobyte, so the parameter is about where the control lives.
- ~~Whether virtualization is in scope for v1~~ - **done.** `AllowVirtualization` puts the rows through
  `Virtualize` with `SpacerElement="tr"`, and one items provider serves every source. It is exclusive
  with paging: the two solve the same problem, so `Paging` is a single property both the pager and the
  view read.
- ~~Whether to support `RadzenDataFilter` interop in v1~~ - **resolved.** The grid speaks
  `FilterDescriptor` in both directions, which is what `RadzenDataFilter` emits. The path derivation of
  §4 is what makes that possible.
- **A column's settings identity is not unique, and a column may have none.** Both are the same gap:
  settings key a column by its property path. Two columns over one property are restored onto the
  first of them, so hiding the second and reloading hides the first; a column with no path - a
  `TemplateColumn`, or a `CollectionColumn` with no `SortBy` - cannot be stored at all, so its
  position in a dragged order never survives. `RadzenDataGrid` answers both with `UniqueID`, matched
  ahead of `Property`. Adopting that here is a new public parameter and a settings-format addition,
  which is why it is recorded rather than done. §10b has the failure in full.
- **Row expansion is keyed on the item instance, which both leaks and loses state.** `expandedRows`
  is a `HashSet<TItem>` added to by `ToggleRow` and emptied only by an explicit collapse or
  `ExpandMode.Single`. Over a source that re-materialises - `AsNoTracking()` read per render, or a
  `LoadData` handler assigning a fresh page - every entity ever expanded is pinned for the life of
  the circuit, and because the set compares by reference those entries can never match a new instance
  again: the row draws collapsed while the old one is held. Both halves are the same cause.

  **The obvious fix is wrong.** Clearing it beside `lookups.Clear()` looks right and is not:
  `dataChanged` is `!ReferenceEquals(lastData, Data)`, which for exactly those re-materialising
  sources is true on *every* parameter set - so it would collapse every expanded row on every render,
  for precisely the grids the leak affects. `lookups` tolerates that because rebuilding a check-box
  list costs nothing; user state does not. The grid already has `ItemKey`, and keying expansion by it
  would answer the leak and the lost state together. Recorded rather than done, for that reason.
- ~~**A sortable header that is not currently sorted draws no sort icon.**~~ - **done.** The glyph is
  now reserved the way upstream reserves it, so hovering signals something and the first click no
  longer inserts an element into the flex line and re-truncates the title. It became urgent rather
  than cosmetic once §13 needed to measure a header: a header measured around a missing glyph fits a
  glyph too narrow, which makes the jump permanent instead of momentary.
- **Column auto-fit for `RadzenFastDropDownDataGrid`**, deferred out of §13 with the question it is
  waiting on stated: does the popup grow to the fitted content, or does the grid fit within the width
  the popup already has? Both are defensible and they are different features.
- The built-in filter UI is a text box or a check-box list, and nothing else: no operator menu, no date
  popup, no numeric range, no enum picker. `RadzenDataGrid` has all four and they are most of its filter
  code. `FilterTemplate` is the escape hatch; whether any of them should be built in is open.

## 10b. Review status

What has been read by a reviewer other than its author, what that found, and what has not. Recorded
because the branch is 106 commits long and "has this been reviewed" is not answerable from the log -
its first general pass sits a long way back, and the slices below were read at very different points.

Every pass below ran as a sub-agent against a written brief, reported CONFIRMED or PLAUSIBLE per
finding, and had its fixes mutation-checked. The count is what each pass found that a green suite did
not - the whole suite passed before and after every one of them.

| Slice | State | Found |
| --- | --- | --- |
| Early core + column faults | reviewed at `a95a32e04` | 7, fixed then |
| The drop-down | reviewed at `fbc6e9516`; 3 commits since | 15, fixed then |
| Keyboard, range selection, positional ARIA | reviewed | 4 |
| `RadzenFastGrid.Data.cs` - lifecycle, async, invalidation | reviewed | 4 |
| `RadzenFastGrid.Data.cs` - query semantics | reviewed | 5 |
| Delegated clicks and `fastgrid.js` | reviewed | 4 |
| Frozen columns, resize, reorder | reviewed | 6 |
| The drop-down, re-reviewed | reviewed | 6 |
| Today's own fixes, re-reviewed | reviewed | 3 |
| Attribute-run ordering, all render files | mechanically checked | 1 |
| `ColumnBase.cs` and the column types | reviewed | 5: 4 fixed, 1 open |
| `RadzenFastGrid.cs`, the core render path | reviewed | 5: 4 fixed, 2 open |

**Every slice has now been read by someone other than its author**, which was not true until the two
passes that closed the rows above. Between them they found ten, of which eight are fixed and three are
recorded as open because each is a design decision rather than a fix - one finding was two symptoms of
a single cause, fixed once.

Both passes independently reported the same two non-ascending attribute runs, which is also what a
script walking every run in the package found: the footer splat and the header title cell. **Both are
now fixed, and every attribute run in the package ascends.**

The header cell is the more interesting of the two. It had been left alone twice on the strength of a
comment saying the element *could not* be fixed, only declined to add to - a claim about the framework
rather than about the schedule, and false: the reorder pair drops into the gap the class and
`aria-sort` leave by moving up. **A recorded decision is only as good as the reason recorded with it**,
and "we chose not to" and "it cannot be done" are worth checking apart before either is inherited.

From the core render path, all three fixed ones were a rule applied in one place and not in its
neighbour:

- **`Responsive` never emitted `rz-datatable-reflow`**, which is the class the theme scopes the entire
  feature under - both the rule hiding the per-cell title above the breakpoint and the media block
  that stacks rows into cards below it. So the titles showed beside every value at every width,
  nothing stacked, and the grid paid 1.40x the render time to be worse than with the feature off.
  The sixth instance of this failure mode, and its test asserted the span count and the title text -
  the implementation restated.
- The footer cell's render hook was numbered below the attributes above it, while the body cell's -
  the same hook, one method away - was numbered past them and says why.
- The column picker was written first among the root's children and numbered 700, after everything
  else. To be written first it needed a number *below* the top pager's 10; the comment had the rule
  backwards.

From the column model, likewise:

- A declared `SortOrder` was the only route into the sort list that never asked `CanSort`, and
  `PropertyColumn` was the only column whose `ApplySort` overrode the nullable "cannot order by"
  contract its own base declares. Together they ordered a grid by a `List<string>`, which has no
  comparer: the render threw and drew nothing.
- A settings reset cleared every column's filter and the whole sort list, but the restore that
  follows can only name a column by `PropertyPath` - so a column without one lost what its markup
  declared. A `CollectionColumn` has no `PropertyPath` when it has no `SortBy`, and none when its
  `SortBy` is over a computed key, while filtering perfectly well by `FilterPropertyPath` throughout.
- A computed column borrowed its sort key as a filter path. `ApplyFilter` composes from the display
  expression and the reflective route filters by the path, so the column filtered two different
  members depending on which route ran - and which one runs is decided by whether some *other* column
  declined. It declines to filter now, as it already declines to sort.
- `In` and `NotIn` read a null string as itself in the delegate builder and as the empty string in the
  expression builder, so one grid over a `List` and the same grid over a queryable answered one
  check-box-list filter differently - and the list was the side disagreeing with `QueryableExtension`.
  Every other operator in that builder already coalesced; `In` was the one missed.

**Open, and a design decision rather than a fix: a column's settings identity is not unique.**
`ColumnForPath` answers with the first column matching a stored path, and `CaptureSettings` writes
every column under that same key - so two columns over one property are both restored onto the first.
Hiding the second and reloading hides the *first* instead, which is a wrong answer on screen and not
merely lost state.

**It does not take a duplicated property to collide.** A `PropertyColumn`'s path is its *sort* path
when `SortBy` is set, so a column displaying `Last` and sorting by `First` shares an identity with the
column displaying `First` - two ordinary columns, nothing declared twice. A filter stored for one is
restored onto the other, and the grid answers with rows neither column asked for. **`RadzenDataGrid` does not have this problem**: it matches on `UniqueID` first and
falls back to `Property` only when there is none. Adopting the same idea would close this *and* the
`TemplateColumn` limitation below, which is the same missing concept seen from the other side - a
column with no property path has no settings identity at all, so its position in a dragged order is
never stored. Both are open; neither should be closed by guessing at the identity model.

**What the passes have taught about where to look**, which is worth more than the counts:

- **Its faults are silent.** A render loop that took 880,000 renders in 2.5s logged nothing. A load
  that overwrote its successor rendered the wrong table with no exception. A grid rendered zero rows
  above a pager still counting. Assume a wrong answer or a hang, not a throw.
- **A class the theme scopes under a parent does nothing until that parent is emitted**, and every
  markup assertion passes meanwhile. Five instances now. Grep the class in `_grid.scss` and read what
  it is nested *under*.
- **A check that looks for the thing being present can only see it once it works.** The frozen-inset
  test took the first cell carrying `rz-frozen-cell` and asserted about it - correct while one kind of
  cell could carry it, wrong the moment another did.
- **Two features sharing one mechanism is where the branch breaks.** A declared `OrderIndex` and a
  drag shared a placement rule; `View()` and `TotalCount()` asked the same two questions in opposite
  orders; the click listener and the keyboard cursor share one `locate()`.
- **A number attributed to a mechanism without a control has not been measured.** §9 has the rule and
  what it cost to learn.
- **A comment that states a constraint is a claim to be checked, not a fact to be inherited.** Two
  separate ones on this branch were wrong in the same direction - both said something was impossible
  when it was merely undone, and both were believed twice. See the header cell above and the sequence
  rule below.
- **A rule stated in a comment is only as good as the comment.** `d9992eaaf` corrected the sequence
  rule where it was argued and left one instance of the old, wider claim standing - ten lines above a
  comment stating the true one, and directly above numbering that is only correct under the new rule.
  A reviewer citing it would have read working code as a fault. Fix the rule everywhere it is written
  down, not only where it was being argued.
- **A fix is right for the case that motivated it and has to be checked against the neighbouring
  one.** Reviewing a day of fixes found three faults in them: a listener that let go without
  forgetting it had attached, so the grid could not take it up again; `default(TItem) is not null`,
  which answers null for a `Nullable<T>` as well as for a class; and three attribute runs left
  descending by the commits that documented the rule against it. Every one was the other half of a
  conditional the fix had only read one way.
- **"It was reviewed once" and "little has changed since" predict nothing.** The drop-down had its own
  15-fault pass and 3 commits after it, and was ranked last for that reason; re-reading it found six,
  including a validator that never fired and a multiple selection that lost a tick when the user
  turned the page.

## 11. What is next, in the order it was argued

Nothing here is committed to; this is the list as it stood, so it can be picked up cold.

**Not built:**

- ~~**Keyboard navigation**~~ - **built, all four steps of §12.** It is the last of the three the scroll
  container unblocked; resize, reorder and frozen columns are all built. The roving-focus model turned
  out not to be the obstacle it looked like, because `RadzenDataGrid` does not use one either: focus
  stays on `.rz-data-grid-data` and the active cell is named by `aria-activedescendant`. What the design
  had to settle instead was where the algorithm lives, what paints a focused cell when the theme has no
  rule for one, and what a keystroke costs on a server-rendered circuit.
- ~~**Column auto-fit**~~ - **built**, as §13 designed it, and the sort glyph it needed went up first
  on its own. Two of that section's decisions did not survive being measured; both are marked there.
- **Editing, grouping, composite headers.** Unchanged, and for the reasons in §1 and §10.

**Measurement debt:**

- **The bimodal reference rows.** Two stable values ~990 KB apart, correlated with whether gen1/gen2
  collections happened, hypothesised as `RenderTreeBuilder`'s pooled frame arrays. It has now shown up
  twice and been reproduced on demand, and it is still inferred from a correlation. Measuring the pool
  directly would close the oldest open question in `README.md` - and it is a question about
  `RadzenDataGrid`, not about this grid.

  Frozen columns are the same question from the other side: two attribute frames per cell of a frozen
  column cost **1.10x** the render time and under a kilobyte of allocation. If frames are pooled, that
  is exactly the shape to expect - the work is real and the bytes are not new.

- **`--job short` cannot answer a question about time.** Reorder measured 1.76x, 1.86x and 0.97x across
  three runs of it; frozen measured 1.01x and then 2.68x, with error bars wider than the means. Both
  were settled by one full-length run, which put reorder at 0.93x and frozen at 1.10x with errors under
  3%. Allocation is stable to two decimals at `--job short` and is what that job length is for. **Take
  a time ratio from a full-length run or do not quote one.**

**Upstream, separable from everything else:**

- **`updateFrozenColumnPositions` is not scoped to its own grid.** A resize drag calls the shared
  `Radzen.startColumnResize`, whose move handler runs that routine on every frame once any
  `.rz-frozen-cell` exists. It measures the header's frozen cells and then writes an inline inset to
  every frozen cell of `gridElement.querySelectorAll('tr')` - which reaches the rows of a grid rendered
  inside a row-detail template, and pins them to the *outer* grid's offsets. It affects
  `RadzenDataGrid` identically, and fixing it means changing `Radzen.Blazor.js`, which this branch
  deliberately does not touch. So it goes up on its own, the way the array-filter fix did. Since the
  toggle cell is pinned the offsets it computes for this grid's own rows now agree with the server's,
  so what is left is the nested case and some wasted DOM writes during a drag.

- ~~The `QueryableExtension` array-filter fix~~ - **sent up on its own** as radzenhq/radzen-blazor#2696,
  from a branch off `upstream/master` rather than from here. An array property is enumerable but not
  generic, so the filter was built against the array itself and threw ("the binary operator Equal is not
  defined for Int32[] and Int32"). Upstream had no array coverage on that path at all - the two tests
  named `Where_FiltersArrayProperty_*` filter a string - so the PR carries two of its own, written to
  fail on master first.

**Still open from before, unchanged:**

- Package and namespace name.
- Whether any of `RadzenDataGrid`'s four richer filter UIs - operator menu, date popup, numeric range,
  enum picker - should be built in, or whether `FilterTemplate` stays the whole answer.

---

## 12. Keyboard navigation - the design

All four steps of the order below are built: the theme fix upstream, the cursor itself - the C#
algorithm, the JavaScript effect layer, the re-assert after every render, and the package's interim
stylesheet - range selection, and positional ARIA. **Measured at +1.4 KB and 1.00x** for the cursor,
**+0 KB** for range selection and **+0.1 KB** for the ARIA. Three of the four are inside the gate; the
fourth is not, and deliberately. **`aria-colindex` on every cell runs at ~1.1x against a gate of
1.02x** - brought back as a number to decide on, as the budget section requires, and kept: the tiers
below confine it to a grid whose user has hidden a column that is not at the end, and the alternative
was gating a screen reader's correctness behind a switch aimed at sighted keyboard users. Everything below carries the reason it was decided that way, so it can be re-argued rather
than merely obeyed; where it diverges from `RadzenDataGrid` the divergence is deliberate and the reason
is given. Two of the decisions did not survive contact with a measurement, and both are marked where
they stand rather than quietly rewritten.

### What it is for

**Power-user navigation on a large business grid**, not an accessibility checkbox. The target shape is
`Cartons.razor` in the consuming application: eight-plus columns, ~11,700 rows, `RowClick` navigating to
a detail page, `SelectionMode.Multiple`. Accessibility follows from doing it properly, but it is not
what sets the scope.

**It is judged against the WAI-ARIA grid pattern, not against `RadzenDataGrid`.** Every other feature
here mirrors Radzen because the *theme* keys off the class names; keyboard behaviour is not a markup
contract, so that argument does not carry. Where upstream matches the pattern, matching upstream is
free. Where it does not, this grid follows the pattern and the difference is recorded in the README's
divergence table rather than inherited.

The bar is an upstream pull request, because that bar subsumes the consuming application's.

### The model

**Cell level.** Up and Down move a row; Left and Right move a cell within the row. The grid already
emits `role="grid"` on the scroll container, and a `role="grid"` whose cells cannot be reached is
mis-roled - that content is a `table`, or the container is a `listbox`. Cell movement is also the only
keyboard route to a column that horizontal scrolling has pushed off screen, which a ten-column grid
has and a five-column one does not.

**The header is row 0**, as upstream has it: Left and Right cross the `<th>`s and Enter or Space sorts
the focused column. Sorting is the most common thing anyone does to a business grid and without the
header there is no keyboard route to it at all.

**The filter row is not in the arrow space.** It holds real `<input>`s that `Tab` already reaches, and
putting them in the arrow space would make every keystroke decide whether it is navigation or typing.
It swallows keydown instead, which is what upstream does and costs nothing to copy.

**One tab stop**, on `.rz-data-grid-data`, which restores its last position when re-entered - tabbing
out to a filter box and back is a constant gesture, and starting over each time is the difference
between keyboard support existing and anyone using it. `aria-activedescendant` is cleared on blur;
upstream never resets `hasActiveRow`, so its grid claims an active descendant while unfocused.

**The active cell is named by `aria-activedescendant`, not by roving tabindex.** Roving focus would put
a `tabindex` attribute on every cell, which is an attribute frame per cell - the shape that costs frozen
columns 1.10x - and it is over budget for a feature that does not need it.

### Where the work happens

**The algorithm is C#. The effect is JavaScript. JavaScript holds no rules.**

Upstream splits it the other way: ~156 lines of `focusTableRow` own the index arithmetic, the clamping,
the highlight and the `aria-activedescendant` bookkeeping, and C# caches two integers. Two of upstream's
four keyboard bugs come from that split - JavaScript mutating state Blazor also owns. Its focus ring is
wiped whenever selection changes `RowStyle` and Blazor rewrites the row's `class`; and passing
`UniqueID` where the rendered element carries `GetId()` means setting `id=` on a grid kills navigation
silently, swallowed by a bare `catch`.

Here C# computes the new `(row, cell)` and calls down with it. The script swaps a class, moves the id
and scrolls into view. It decides nothing, so it cannot disagree.

**No render per keystroke.** The handler is wrapped in `NonRenderingHandler`, which already exists for
exactly this. Note that upstream re-renders on every arrow key too - its keydown is an ordinary Blazor
handler, so `ComponentBase` wraps it in `StateHasChanged` and the JavaScript then patches a class on top
of a render that already happened. Skipping the render is not a divergence in behaviour, only in cost.

That has one consequence worth naming, because it is what forces a listener into the script that the
design did not otherwise want. Blazor's `preventDefault` for an event is an attribute written by a
render - upstream drives it from a `preventKeyDown` field, which only reaches the DOM on the render its
keystroke caused. A grid that does not render per keystroke can never update it, and blanket
`preventDefault` on keydown would swallow `Tab` and trap focus in the grid. So the script attaches one
native listener whose only job is to call `preventDefault` for the keys **C# names in the call** - the
browser scrolls a line for an arrow key and a page for `Space`, and the grid scrolls the focused cell
into view itself, so both run and the container jitters. Suppressing a key the grid handles is not a
rule about navigation; which keys those are is still decided where they are handled.

Two exceptions the same listener has to make, both about the browser rather than about the grid: it
ignores a key typed inside an `input`, `textarea`, `select` or `contenteditable`, where the arrows move
a caret and are not ours to take. The filter row separately stops keydown propagating, which is what
keeps the *grid's* handler from seeing it - two mechanisms because there are two listeners, and only
Blazor's honours Blazor's flag.

**`OnAfterRenderAsync` re-asserts focus.** The grid re-renders constantly for other reasons - sort,
filter, page, resize, a parent's `StateHasChanged` - and each one rewrites the row's `class`. Rather
than defend the class, the grid tells the script where focus is again after any render while focus is
live. One interop call on renders that were happening anyway, and C# is unambiguously the authority.
This is what upstream cannot do, because its `focusedIndex` is a cache rather than the source of truth.

**Rows are addressed by `data-r`**, whose emit condition widens from "clicks are delegated" to "clicks
are delegated or navigation is live". Addressing by `tbody.rows[i]` would cost no markup and be wrong:
`Virtualize` emits a spacer `tr` and row detail emits a second `tr` per expanded row, so DOM order is
already not model order. The index strings are pre-cached, so the attribute costs a frame and no
allocation.

> **Measured, and wrong on the last sentence - then measured again, and wrong about why.** An attribute
> per row read **+16 KB at 1000 rows**, eight times this feature's whole budget, and that was put down
> to the frame: the value being a pre-cached string, the frame was what was left, and
> `RenderTreeBuilder` renting its frame array from a pool made a plausible mechanism for it.
>
> The premise was the part that was false. **The table of index strings held 512 entries**, so a
> thousand-row grid called `ToString` on 488 rows of every render - the values were pre-cached for the
> first 512 rows and for no others, and the benchmark renders a thousand. Grown to fit, `data-r` costs
> **+0.78 KB** and the frame is nearly free after all.
>
> What made it findable was positional ARIA writing two more index attributes: one per row, which
> measured the same +15.5 KB, and one per *cell* - six times the frames - which measured +0.09 KB. Six
> times the frames for a twentieth of the cost is not a frame-count story, and the only thing the two
> do not share is how large their values get.
>
> The addressing decision below stands on its own even so, and is now a preference rather than a
> saving. DOM order is not model order - but the
> rendered *data rows* are, because the two things that break it are distinguishable: a detail row is a
> sibling carrying `rz-expanded-row-content`, and `Virtualize`'s spacer carries no class at all. So the
> nth `tr.rz-data-row` is the nth row, and the inline path needs no attribute. Under virtualization it
> still does, because there the index is a position in the whole data set rather than in the DOM - and
> there it is tens of rows rather than a thousand, which is the same argument delegated clicks make in
> reverse. `RowsAreAddressed` is `ClicksAreDelegated || (navigation && virtualizing)`, and the script
> takes `data-r` where it is offered and counts where it is not. Writing it on every row would now
> cost 0.78 KB rather than 16, so the case for counting is that it needs no attribute rather than that
> the attribute is expensive.

### The keys

Arrows; `Home` and `End` for the first and last cell **in the row**; `Ctrl+Home` and `Ctrl+End` for the
first and last cell **in the grid**; `PageUp` and `PageDown` for a viewport of rows.

`Home` and `End` are a deliberate divergence: upstream binds them to the first and last *row*, which is
the pattern's `Ctrl+Home` and `Ctrl+End`. On a ten-column grid the row meaning is the more useful of the
two and the one fingers expect.

**`Enter` activates and raises `RowClick`. `Space` selects.** Upstream binds both to selection and
offers no keyboard route to a row click at all, which on the target page means a keyboard user can
multi-select cartons but can never open one. Splitting the two keys is the pattern's own answer and it
resolves that outright. `Shift+Space` and `Shift+Arrow` extend a range from the selection anchor.

**In RTL the arrows flip**, because the pattern specifies visual direction and this grid is already
direction-aware through logical properties and edge-relative freezing. `Home` and `End` do not flip -
"first cell in the row" is already logical. Direction is read once when the listener attaches, not per
keystroke.

**Not built: typeahead, `F2`, `Escape` in the body.** Typeahead on a grid sorted by an arbitrary column
is ambiguous about which column it should match, and that ambiguity is a design question rather than a
key binding. `F2` and `Escape` belong to editing, which is out of scope for the reasons in §1.

### Range selection, and the anchor it reaches from

Built, and it cost nothing: no parameter, no binding, no markup. A Shift key is the whole of its
surface, which is why there is no `AllowRangeSelection` - a switch would name a cost that does not
exist. It is live when `SelectionMode` is `Multiple`, which is the only mode where a range means
anything.

**The anchor is a selection anchor, not a cursor.** This is the one thing the design above got wrong,
and `Shift+Space` is what exposed it: that gesture does not move, so a run anchored where the cursor
stands would reach from a row to itself. The anchor is the last row `Space` or `Enter` acted on, and a
grid whose cursor has only ever moved has none - there the first Shift key sets one where the cursor
is, which is what makes `Shift+Arrow` work on a grid nobody has selected anything in yet.

It follows that a plain arrow key moves the cursor without moving the anchor. That is a divergence from
a desktop list, where an unmodified arrow moves the selection too - and it is forced rather than
chosen: `Space` selects here and the arrows do not, so the last row *selected* and the last row
*reached* are different facts, and the anchor is about the first.

**The range is recomputed from the anchor on every keystroke rather than accumulated**, so shrinking it
gives back exactly the rows it covered: the answer is a function of where the two ends are now, not of
the path taken between them. What that costs is the selection as it stood when the run opened, kept for
as long as the run lasts. It has to be kept, and this is the reason: the grid does not own `Selection`,
so once the range has covered a row there is nothing left to read to find out whether the user had
chosen it beforehand.

**Which rows changed is the difference against the selection as it stands**, not against the previous
range - so growing and shrinking are one code path, and a caller that changed the selection underneath
the grid is still told the truth. Both sides go through sets: a range is thousands of rows on the grid
this was written for, and `ICollection.Contains` per row would make it quadratic.

**`Shift` with Left or Right does nothing.** The pattern extends a *cell* selection with them; what
this grid selects is rows, so a sideways move has nothing to extend. The pattern's `Shift+Space` -
"selects the row that contains the focus" - is likewise written for a cell-selecting grid, where it
widens a cell selection to its row. Where selection is already rows that gesture is just `Space`, so
`Shift+Space` takes the meaning it has in every desktop list instead, which is the one users arrive
with.

**A run ends at the next key without `Shift`, at a sort, a filter or a page, and at leaving the grid.**
The middle three end the anchor with it, because both ends of a range are positions in the view and all
three are ways a row arrives at an index that used to belong to another one. That is one call in
`RefreshAsync`, which is where every state change a user can make already funnels.

Leaving is different and ends only the run. A run also carries the selection as it stood when it
opened, and blur is exactly where that stops being trustworthy - the user can select elsewhere and come
back, and a surviving run would restore rows they had since dropped. The anchor stays, on the same
reasoning the cursor's position does: it says where the next range reaches from rather than what is
selected now. The visible consequence is that a range you tabbed away from is committed - the next
Shift can extend past it but not take it back - which is the honest reading of a gesture that ended.

**Off under virtualization**, which is the same scope choice delegated clicks make and for a related
reason. A range is the rows between two positions, and the rows come from `View()` - what the render
walked. A virtualized view can only hand over the window it drew, so a range reaching past it would
select the rows it could see and call that the answer. `Shift` there moves the cursor, which is what
the grid did before this existed.

**The anchor does not move on a mouse click.** The inline click path is handed the row rather than its
index - only the delegated listener knows the position - and an anchor that moved on the grids whose
script attached and not on the others would be worse than one that does not move at all.

### Boundaries

**Paging: arrowing past the last row advances the page** and lands on the first row of the next, and
likewise backwards. Upstream simply stops - nothing calls `ChangePage` - which on 11,700 cartons makes
the keyboard useless past row 100. Paging state and the item count are already in C# here, so the
boundary is a comparison rather than a DOM measurement.

**Virtualization: focus follows the data, not the rendered window.** Upstream clamps `focusedIndex` at
the window edge while the viewport scrolls, so the index and the row drift apart with nothing to
re-sync them. The window edge is an implementation detail the user should never feel.

### Cells, columns and rows that move

**Every rendered cell is navigable, including the row-detail toggle**, and `Enter` activates whatever
is in it. The toggle is already a `<td role="gridcell">`; making it unreachable would be the markup
lying again. One rule beats two, and it avoids upstream's trap of having `ArrowRight` mean expand rather
than move whenever a `Template` is supplied - which costs upstream horizontal navigation entirely on
exactly the grids that have the most columns.

**Scroll-into-view carries an inset equal to the frozen run's width.** A frozen column is sticky and
pinned from the first paint, so `scrollIntoViewIfNeeded` - which reads the viewport rect - considers a
cell scrolled underneath one to be visible, and the focused cell sits occluded with no indication.
`RadzenFastGrid.Frozen.cs` already sums those widths with `calc()` for the pinning; the scroll margin is
the same number reused - *almost*. That sum is a CSS length, `calc(80px + 220px)`, and nothing in C#
can turn it into the pixels a scroll needs. What is reused is the run rather than the number: C# says
how many cells of the row are pinned to each edge, which it knows, and the browser measures how wide
they came out, which only it does. Same division of labour as everywhere else here. Upstream never meets this because it pins nothing until a column is resized.

**Focus tracks a column's position; it tracks a row's item.** These point different ways on purpose.
Reordering or hiding a column is a deliberate act on the columns, and having focus stay where it is on
screen is less startling than having it chase a column across the table. A sort or a filter is an act on
the *rows* whose entire purpose is to move the one being looked for, so focus follows the item through
`ItemKey` - which already exists and already backs selection membership - and falls back to the position
where no `ItemKey` is supplied.

**Nothing to focus.** Keys are inert while `IsLoading`. Focus clears when the row set empties and
returns to the first row when data arrives; retaining an index against an empty set means holding a
position that may not exist later, for a benefit nobody would notice. The empty-message row is not
navigable. **Focus never enters `FastGridSettings`** - column order, widths, sorts and filters are the
user's configuration; where the cursor was is about the current moment.

### What paints the focused cell

**The theme cannot draw one, and this is why upstream's cell navigation is invisible.**
`_grid.scss` has exactly two focus rules: `.rz-grid-table thead th.rz-state-focused` gives an outline,
and `tr.rz-state-focused > td` gives a row background. There is **no `td.rz-state-focused` rule**.
`focusTableRow` adds that class to a body `<td>` on Left and Right, and it lands under neither parent;
the row's own class is not cleared either, since the query only finds descendants. So upstream moves
`aria-activedescendant` correctly - a screen reader announces the new cell - while a sighted keyboard
user pressing `ArrowRight` sees nothing change at all.

This is the third instance on this branch of the same failure: **a class the theme scopes under a parent
does nothing until that parent is emitted, and every markup assertion passes meanwhile.**

**And the row highlight is scoped to selection, which is the same bug one level up.** The
`tr.rz-state-focused > td` rule lives inside `.rz-selectable`, a class `RadzenDataGrid` adds only when
`RowSelect`, `ValueChanged` or `SelectionMode.Multiple` is set - and which this grid adds only when
`SelectsOnRowClick`. So on a **read-only grid, keyboard focus paints nothing at all**: not the cell,
not the row. That is not upstream trivia, it is a hole in this design, since the grid this section is
written for is read-only by definition.

The fix went upstream as **radzenhq/radzen-blazor#2698**: it adds the missing `td.rz-state-focused`
rule using the `--rz-grid-cell-focus-outline` variables every theme already defines, and moves the
focus block out of `.rz-selectable`, placed directly after it so a focused row still beats a selected
one at equal specificity. Measured in Chromium against the compiled themes rather than read off the
source - on a selectable grid, selected, focused and selected-and-focused are byte-identical before and
after; on a grid without selection, the row background goes from nothing to `rgba(53,160,215,.2)` and
the cell from no outline to `solid 2px` inset by `-2px`.

**Both halves have now landed and the stand-in is gone.** #2698 merged, and a follow-up moved the
frozen-cell pseudo-element block out of `.rz-selectable` as well - without it a read-only grid tinted
the focused row everywhere except its frozen cells, which stayed white. `Radzen.Blazor` 11.3.1 carries
both, so the package no longer ships `fastgrid.css`, the playground no longer links it, and the parity
fixture measures against the theme alone. The suite passes unchanged, which is what says the theme's
rules and the stand-in's were the same rules.

A read-only grid is the *only* configuration this component promises, so before that landed the
feature had no visible cursor at all. That made the upstream fix a prerequisite rather than a courtesy,
which is why it was first in the order below.

### The ARIA that costs something, and when it is paid

**It is designed here and gated nowhere near here.** This is step 4 of the keyboard work because that
is the order it was argued in, but the emission conditions are `Paging || AllowVirtualization` for the
rows and "a column is hidden" for the columns - `AllowKeyboardNavigation` appears in neither. A screen
reader on a paged grid needs to know where the window sits whether or not a sighted user can arrow
around it, and gating that behind a switch aimed at sighted keyboard users is how accessibility ends up
off everywhere.

The consequence is that the per-cell tier is a cost a grid can reach without opting into anything - a
user hides a middle column and the render goes to ~1.1x. **That is the decision, taken knowingly**, and
it is what makes the three tiers load-bearing rather than a refinement: they keep the bill on the one
configuration that earns it, and leave the baseline and the two cheaper cases at zero.

With paging or virtualization the DOM holds a window - a hundred rows of 11,700 - and nothing tells a
screen reader which. The pattern's answer is `aria-rowcount` and `aria-rowindex`; for hidden columns it
is `aria-colcount` and `aria-colindex`.

These were predicted to land on opposite sides of the budget: `aria-rowindex` a frame **per row**,
roughly a tenth of what frozen columns cost and comfortably inside; `aria-colindex` a frame **per cell**,
about half of frozen's cost, which is outside.

> **Both halves of that were wrong, and finding out why corrected a number this branch had been
> quoting for weeks.** Measured, `aria-rowindex` on every row costs **nothing** and `aria-colindex` on
> every cell costs **+0.09 KB** - six times the frames for a twentieth of the cost, which cannot be a
> frame-count story at all. What the per-row attribute had actually been paying for was its *value*:
> the table of cached index strings held 512 entries, a thousand-row grid called `ToString` on 488 rows
> of every render, and that was the +16 KB `data-r` had been charged for and attributed to the frame.
> Grown to fit, both attributes are free in bytes.
>
> The cell attribute is not free in **time**: one frame on every cell of a thousand-row grid runs at
> **about 1.1x**, which is exactly the shape frozen columns already had at 1.10x for two frames on the
> cells of one column. So the conclusion §12 reached - that the per-cell attribute is the expensive one
> - survives; only the currency and the mechanism were wrong.

So **each is emitted only where it is needed**, which is §3 rule 3 applied literally and, as it turns
out, the specification's own rule quoted back: "if all of the columns are present in the DOM, including
`aria-colindex` is not necessary as user agents can calculate the column index". `aria-rowindex` and
`aria-rowcount` only when paging or virtualization makes the DOM a window; `aria-colcount` only when the
picker has hidden a column. An unpaged grid showing every column pays nothing, which is the
configuration the 153 KB baseline measures.

**And `aria-colindex` in three tiers rather than one**, because the specification has three cases and
the 1.1x is what makes the difference between them worth having:

| What the picker has hidden | What is written |
| --- | --- |
| nothing | nothing |
| the trailing columns | `aria-colcount` alone - what is left is still columns one upward |
| the leading columns | one index per row, on the first cell, naming where the run starts |
| a column in the middle | an index on every cell, because the run has a hole in it |

A row-detail toggle pins the first cell to column one, so any run starting later already has a hole
before it and falls into the last case. **The frame is the declared column order**, which is the only
ordering a hidden column has a place in: a reorder index is a position among the *visible* columns and a
column nobody can see was never given one. A grid that both hides and reorders therefore numbers cells
by where they were declared rather than where they are drawn - which is the case the specification
already requires every cell to be numbered for, so it is the honest answer rather than a drawn position
invented for a column that has none.

**Row numbers include the header rows**, which are rows of the grid: the title row is 1, the filter row
is 2 where there is one, and the data rows follow. A detail row repeats its parent's number instead of
taking one of its own - numbering it separately would push every row below it out of step with the data
set, which is the one thing the attribute exists to keep true. A total not yet known reads `-1`, the
value defined for it, rather than a zero that would be a claim.

### The drop-down

`RadzenFastDropDownDataGrid` opens on `Enter`, `Space` and `ArrowDown` and closes on `Escape`, but has
no navigation inside the popup - the README lists it as a limitation. It gets row-level navigation from
the same code with cell movement switched off, and the drop-down owns `Enter` as select-and-close. Full
cell navigation in a picker is motion without a purpose: the user is choosing a row, not reading a
table.

### Allocation is a design constraint here, not an afterthought

This component exists because `RadzenDataGrid` allocates 13,172 KB where it allocates 153. A feature
that quietly gives some of that back has taken the argument away. §3 rules 2 and 3 apply in full, and
for this feature specifically they mean:

- **Nothing per cell.** No `tabindex`, no `id`, no unconditional `aria-colindex`. This is what rules out
  roving focus, and what makes the conditional ARIA above the design rather than a refinement of it.
- **Nothing per row that is not already there.** `data-r` is reused rather than joined by a second
  attribute, and its index strings are already pre-cached.
- **No delegate per row or per cell.** One keydown listener for the whole grid.
- **No render per keystroke**, via `NonRenderingHandler`.
- **Nothing at all when the feature is off.** A grid with navigation disabled must measure as the bare
  grid does, to the two decimals `--job short` is stable to.

### Budget, and the measurements that have to be recorded

**The gate: under +2 KB and under 1.02x at 1000 x 5.** That is strict enough to rule out anything
per-cell and loose enough not to micro-argue. A model that lands at 1.05x gets brought back as a number
to decide on, not quietly accepted.

**Measuring is part of the work, not a follow-up.** A commit here is not done until its number exists
and is written down. Concretely:

- `gridbench` gains cases in the established naming: `+ keyboard navigation`,
  `+ keyboard navigation and range selection`, `+ positional ARIA`, and
  `= RadzenDataGrid + keyboard navigation` for the like-for-like ratio, which is the only comparison
  that means anything once a feature is paid for on both sides. **That last row does not exist**, and
  the reason is worth keeping: `RadzenDataGrid`'s navigation has no switch - the tab stop and the
  keydown handler are unconditional - so its baseline row is already the navigation-on measurement and
  a second identical row would say nothing. The like-for-like comparison is `+ keyboard navigation`
  against that baseline: 155.2 KB against 12.86 MB, **85x**, and it costs that grid nothing marginal
  because it never had the choice.
- **Navigation is measured alone, before range selection**, so the gate judges one thing rather than a
  bundle.
- **Time ratios come from a full-length run or are not quoted.** `--job short` measures allocation;
  it settled reorder at 1.76x, 1.86x and 0.97x across three runs before one full-length run put it at
  0.93x. Allocation is stable to two decimals at `--job short` and that is what that job length is for.
- Run it on a quiet machine. One earlier pass had the playground serving a circuit alongside it.
- The numbers land in **three places**: the cost table in §0, the "what each of these costs" and
  "where that leaves it against `RadzenDataGrid`" tables in `README.md`, and `gridbench/README.md` for
  the raw data. Take the modal value of several runs before trusting the `RadzenDataGrid` column, which
  is bimodal between two values about 990 KB apart.

### How it is verified

**bUnit for the algorithm, Chromium for the paint.** bUnit drives keydowns and asserts the computed
`(row, cell)`, the boundary behaviour, the key set and the ARIA - which is the whole reason the
algorithm is in C#. `GeometryParityTests` gains a pane for what is drawn.

**The probe must ask the question the wrong way round.** Not "does the focused cell carry
`rz-state-focused`" - that is the check that passed while the filter row's pinning was deliberately
removed, because a row that never got the class has nothing to find and is skipped in silence. It must
ask *what is painted at the focused cell's rect*, and assert it differs from its neighbours. The same
probe covers the frozen-column occlusion case, by asserting the focused cell is not painted over by a
pinned one.

**No parity pane against `RadzenDataGrid` for focus.** It would assert that this grid matches a grid
that paints nothing.

### The order it lands in

1. ~~The `_grid.scss` focus rule, upstream, on its own~~ - **done, radzenhq/radzen-blazor#2698.** It
   turned out to be two rules rather than one, for the reason above. The interim package rule now
   matches what went up, in `wwwroot/fastgrid.css`, and the README's styling section says so.
2. ~~Navigation. Then measure, and record.~~ - **done: 155.2 KB against 153.85 KB bare, 1.01x over
   three full-length runs.** Allocation is `--job short`, stable to two decimals across three runs; the
   time ratio came out 1.03, 1.01 and 0.90 at full length, every one with an error bar wider than the
   difference, which is the answer "not measurably slower" rather than a number to quote to two places.
3. ~~Range selection - `Shift+Space` and `Shift+Arrow`.~~ - **done, and it measured as nothing.** It
   turned out to need one thing the design had not named: a *selection* anchor rather than a cursor
   one. `Shift+Space` is aimed at the row the cursor is already on, so a run anchored where the cursor
   stands reaches nowhere - the anchor has to be the last row `Space` or `Enter` acted on, and only
   falls back to the cursor on a grid that has not selected anything yet. See below.
4. ~~Positional ARIA.~~ - **done, and it measured on the opposite side of the budget from where this
   section put it.** The per-row attribute is free, the per-cell one is free in bytes and about 1.1x in
   time, and chasing the difference is what took `data-r`'s +16 KB apart. It also gained a tier the
   design had not: the specification asks for the index on every cell only where the drawn columns have
   a hole in them, and the 1.1x is what makes the cheaper cases worth telling apart.

One commit each, which is what every other feature on this branch did, and the only reason resize,
reorder and frozen columns have separate numbers at all.

### Where this could still be wrong

- **The row/column asymmetry** of "focus follows the item, focus follows the position" will read as an
  inconsistency to anyone who meets it without the reason. The reason is above; it needs to survive
  review rather than be smoothed away.
- **The measured `--job short` time column was noise, and it nearly took a decision with it.** Three
  short runs of this feature returned 1.00, 1.04 and 1.19; three full-length runs returned 1.03, 1.01
  and 0.90. Had the gate been read off the first set it would have failed on a number that does not
  exist. §9 already says this; it is recorded again here because the temptation to read the column that
  is already printed is what makes the rule necessary.
- **The round trip is accepted, not solved.** Every keystroke costs one server round trip, which in the
  consuming application is a ~157ms floor from Hong Kong that is the speed of light rather than
  anything fixable in code. A single press is fine; holding an arrow to scan may not be. The escape
  hatch is moving the algorithm into JavaScript, which reverses the decision that made it testable -
  a rewrite of the tested part, not a tweak, and one that should be taken on a measurement rather than
  a guess.

---

## 13. Column auto-fit - the design

**Built.** Everything below carries the reason it was decided that way, so it can be re-argued rather
than merely obeyed. Three of the decisions come from facts about the shipped theme rather than from
preference, and those are marked, because a theme change invalidates them and nothing else here.

Two of the things written here before the code did not survive being measured, and both are marked
where they stand rather than quietly rewritten: the header's `max-content` flip needed its flex growth
turned off as well, and the one-frame gate was a guess that the pass does not meet at a thousand
rendered rows and comfortably meets at every size a paged or virtualized grid reaches.

### What it is for

**A column as wide as what is in it.** The table is `table-layout: fixed`, so a column that declares no
width gets an equal share and every value longer than that share truncates to an ellipsis. The consuming
application's grids are the shape this is aimed at - `Cartons.razor` and its neighbours, eight-plus
columns of very unevenly sized text, where an equal share is wrong for every column at once.

**It is not a fill-the-width feature**, though it ends up doing that too. Fitting to content is the part
that needs a measurement; distributing what is left over is arithmetic on the answer, and under this
theme it turns out to need no arithmetic at all (see *Who absorbs the slack*).

### The surface

`AutoFitColumns`, typed `AutoFitMode { None, Once, OnDemand }`, `None` by default. `AutoFit` on the
column, a `bool` defaulting to `true`, opts one column out.

**`Once` fires on the first render that has rows in the DOM, and never again.** It is deliberately not
keyed on the data changing. `dataChanged` here is `!ReferenceEquals(lastData, Data)`, which for the
sources this matters to - `context.Rows.AsNoTracking()` read per render, a `LoadData` handler assigning a
fresh page - is true on *every* parameter set, so a re-fit keyed on it is the continuous mode arrived at
by accident. §10b records the same trap taking down row expansion. A consumer who wants a re-fit when
their data changes calls the API from their own handler, where they know something actually changed and
the grid cannot.

**`OnDemand` is a double-click on the resize handle**, which fits that column and requires
`AllowColumnResize` because there is otherwise no handle to double-click. That requirement is
documented rather than worked around: the alternatives were a modifier key, which is undiscoverable and
untestable, and an item in the column picker, which is a menu about visibility.

**`AutoFitOverflow`, `AutoFitPriority`, `AutoFitAsync()` and `AutoFitAsync(column)` are the whole rest
of the surface.** The first two arrived with the fit-to-container work below and belong here rather than
only in the section that argued for them - a surface section that lists two of four parameters is worse
than one that lists none, because it reads as complete. No event: a fit is
awaitable, and `Once` has no audience for a notification. Recorded as a decision so it is not later read
as an oversight.

### Where the work happens

One round trip per trigger, and **the script both measures and writes**. C# storing the widths and
re-rendering would cost a full pass over every row to change N `col` elements, and would show a reflow
between the measure and the paint. Resize already has this shape - the drag writes widths in the browser
and calls back afterwards - so this is the existing mechanism, not a second one.

1. **C# to the script**, with the target column indices, each column's `MinWidth` and `MaxWidth` strings
   as authored, which columns are frozen, and the current load generation.
2. **The header row**, N elements: set `width: max-content`, force one reflow, read, revert.
3. **The body**, the maximum `scrollWidth` over the rendered `.rz-cell-data` spans of each column.
4. **The chrome**: `getComputedStyle` on each column's own first cell for horizontal padding and
   borders. Per column rather than per grid, because `CssClass` can change them.
5. **Compose and write**, then hand the widths back with the generation they were measured under.
6. **C#** discards a stale generation, stores into `autoFitWidth`, and renders only under the condition
   in *What it collides with*.

`EffectiveWidth` becomes `resizedWidth ?? autoFitWidth ?? Width`. **A fitted width is not captured into
the settings**: a drag is a choice a user made, a fit is derived from the data, and restoring a fit
computed against a different result set is worse than recomputing it. It also keeps the
settings-identity collision of §10b from acquiring another participant.

**Which of a drag and a fit wins depends on who asked, and the first version of this got it wrong in a
way that destroyed user state.** This section originally said only "a drag beats a fit". The code then
did the opposite - a fit cleared `resizedWidth` outright, so that fitting a column somebody had already
dragged would do something visible - and the section was never amended to say so. What that missed is
that **`resizedWidth` is also where a width restored from the settings lands**, a restored width being
a drag from a previous visit. So `AutoFitMode.Once` wiped every saved width on first render, and
because `CaptureSettings` reads that same slot, the next sort or page turn persisted the absence. The
width was not overridden; it was deleted.

The rule now distinguishes the two callers, which is the distinction that was missing:

- **The automatic fit** (`Once`) does not measure a column that already carries a width the user chose.
  It is not a target at all, so nothing is written and nothing is cleared.
- **A fit somebody asked for** (`AutoFitAsync`, the double-click) does take that column, and clears the
  drag with it - because a fit that visibly did nothing to the column under the pointer is worse.

Two tests hold it, both confirmed to fail against the original code.

### Measuring a cell is free; measuring a header is not

**The body needs no clone and no offscreen probe**, because of a theme rule: `.rz-cell-data` is
`display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap`, so a truncated cell's
`scrollWidth` already *is* its untruncated content width. The ellipsis this feature exists to remove is
what makes it measurable.

**But "the body is free" was written here before it was tried, and it is wrong.** That same
`display: block` means the span fills its column, so its `scrollWidth` is never *less* than the column
it sits in - a column wider than its content measures as itself, and a fit built on that could only
ever grow a column, never shrink one. The body gets the `max-content` flip too. The shipped rule covers
both elements:

```css
.rz-fastgrid-measuring .rz-cell-data { width: max-content !important }
.rz-fastgrid-measuring .rz-column-title { width: max-content !important; flex: 0 0 max-content !important }
```

**The consequence is the performance one**, and it is why the gate below is what it is rather than what
this section first guessed: both forced layouts are over N x rows, not over N header elements. One
class toggle still buys one layout each way - that part holds - but they are whole-table layouts.

**The header is not, and reading it the same way returns a plausible wrong answer.**
`.rz-column-title` is an `inline-flex` at `width: 100%` with `overflow: hidden`, and its content child
carries `overflow: hidden` too - which zeroes that child's automatic minimum size. A flex container
whose items shrink to nothing has `scrollWidth == clientWidth`, so the header measures as *the width it
currently has*. Every column fits to itself, the numbers move a pixel or two, and nothing about the
result says it is wrong.

**And `width: max-content` on its own does not fix it - that was written here before it was tried, and
the probe caught it.** `.rz-column-title` is `flex: auto` inside the header's flex line, and a flex
item's used main size comes from its flex properties rather than from `width`, so setting the width
changes nothing at all. The measuring rule has to turn the growth off with it. The first run measured
every column at 226px against a 224.5px starting width - five columns agreeing to within a pixel, which
is what a fit that has measured nothing looks like from the outside.

**A specificity argument was the wrong diagnosis and would have survived review.** `!important` is
needed, but it was needed against `flex`, not against `width: 100%`, and a fix aimed at the second one
passes every markup assertion while measuring the same wrong number.

The header therefore needs a flip of its own, with the flex growth turned off. It is one rule beside the
body's rather than a pass of its own - the class goes on once and both elements answer it. The
alternative - reading the content child and adding
the glyph width, the flex `gap` and the title's `padding-inline` back from computed styles - is
arithmetic against the theme's current internals, and it is the shape of thing that has now been wrong
six times on this branch without any test noticing.

**The header must reserve its sort glyph**, which is why that fix lands first and on its own. A header
measured without it fits one glyph too narrow and jumps on the first sort - the same jump §10 recorded,
made permanent by a width computed around its absence.

### Turning a measurement into a width

`ceil(max(header, cells)) + padding + border + 1`.

The `+1` is for `scrollWidth` rounding to an integer. A fitted column one pixel short shows an ellipsis,
which is the single outcome that makes the whole feature look broken.

**No over-fit percentage.** `RadzenSpreadsheet` adds 3% because it measures on a canvas and the result
has to survive being drawn by something else, including Excel after an export. Here the measurement
comes from the renderer that will draw it, so the same 3% on a 400px description column is twelve
pixels of visible slack bought against a mismatch that does not exist.

**`MinWidth` and `MaxWidth` are applied by the browser, not by us.** The fitted number is pixels and
those parameters are arbitrary CSS - `10rem`, `30%`, `4em`. So the `col` is written as
`clamp(<MinWidth>, <fitted>px, <MaxWidth>)` when either bound is set, and as the bare pixel width when
neither is. This is the argument frozen insets already won: they are summed with `calc()` rather than
parsed, precisely so a column may be sized in any unit or a mixture of them. Parsing CSS is the option
that works for pixels and is quietly wrong for everything else.

`MaxWidth` matters more than it looks. Without an upper bound one four-hundred-character value takes the
whole table and every other column truncates to nothing - the state the feature was meant to leave.

### Who absorbs the slack

**The last non-frozen column *being fitted* is left with no width at all.** The last *visible* one is
what this section first said, and the two differ when the trailing column declares its own `Width` - a
column the markup has sized cannot also be the one with no width, so the code takes the last of the
columns it is actually fitting. The consequence is that slack can land mid-table on a grid whose last
column is declared, which is a real cost of "a declared width wins" rather than a separate decision. Under `table-layout: fixed` a `col`
with no width absorbs the remainder, so the browser does the distribution: no slack arithmetic, no
container measurement, and it stays right through a window resize with no observer and no second round
trip. The whole distribution pass deletes itself.

Two constraints come with it. It must never be a frozen column, because §10's rule is that a frozen run
ends at the first frozen column declaring no width. And if every column is frozen there is no candidate,
so the fallback is pixels everywhere and a table that scrolls.

**The last column rather than the widest one**, though the widest is what a distribution pass would pick.
"Widest" is a property of the data, so a filter changes which column stretches and the layout rearranges
itself under a reader for no visible reason. The trailing edge is where slack is expected to be, it is
stable across re-fits, and it needs no parameter.

**A fitted total wider than the container scrolls; it does not shrink.** The `.rz-data-grid-data`
container that resize needed is already there. Shrinking columns back to fit a viewport truncates every
one of them, which is the state this feature exists to escape - "always fill the width, never scroll" is
a different mode, not a fallback inside this one.

### What it collides with

**Frozen columns, and this one is not optional.** The frozen inset is composed *on the server*:
`PinLeftRun` and `PinRightRun` build a `calc()` sum from `EffectiveWidth` and hand it to `SetFrozen`,
which feeds the memoized style emitted on every frozen cell. So a script that writes new `col` widths
while C# records them without rendering leaves every frozen cell carrying an inset computed from the
*old* widths - wrong on screen, and invisible to any test that asserts markup. **An auto-fit renders
when the grid has a frozen column and skips the render when it does not.** The cost belongs to frozen,
not to auto-fit, and resize pays it unconditionally today.

That collision has an upside worth banking in the same commit: because a run stops at the first frozen
column with no declared width, fitting one *extends* runs that currently give up and draw unfrozen.

**The toggle column.** The colgroup emits a bare `col` standing in for it, with no column of its own in
`visibleColumns`. Any mapping between a measured cell and a `col` has to account for that offset - it is
the off-by-one that once drew every column one position left.

**`Responsive` below its breakpoint** stacks rows into cards and the colgroup means nothing, so no fit
runs there. It is asked as **"is this still a table"** - `getComputedStyle(table).display !== 'table'` -
rather than by comparing a width against 768px: the breakpoint is the theme's number to change, and
every other reason a table might stop being one has the same consequence for a colgroup. Its test
applies the `display: block` that media query applies and asserts the fit answers null **and leaves
every `col` exactly as it found it** - declining after writing would be the worst of both. This guard
was specified here, left unbuilt in the first version, and caught by review. A fit taken above the breakpoint stays stored and is correct again when the window widens.
This needs its own test rather than an argument: `Responsive` shipped broken on this branch for exactly
the neighbouring reason.

**Virtualization.** The script sees the current window and nothing else, so `Once` fits the first window
and `OnDemand` fits the one on screen. That is documented rather than hidden, and it is what the user can
see. Refusing to run under `AllowVirtualization` was considered and rejected: those are the grids that
want this most.

### What invalidates a fit, and what supersedes one

**Nothing invalidates it.** A filter that removes the one long value leaves the column wide, and that is
the wanted behaviour: narrowing a column while a reader is looking at it is the jumping this design has
now rejected in three separate places. Widths are stored per column, so a reorder carries them and a
hide-then-show restores them for free. The README says that after a filter the columns may be wider than
they need to be, and that the answer is to re-fit.

**A newer trigger supersedes an older one, and a fit is stamped with the grid's load generation.** A
result whose generation is stale is discarded - it was measured against rows that have since been
filtered, sorted or paged away. This deliberately reuses the coordinator that `RadzenFastGrid.Data.cs`
already owns rather than adding a second notion of "is this still current" beside it; §10b's standing
lesson is that two features sharing one mechanism is where this branch breaks, and two mechanisms
answering one question is how `View()` and `TotalCount()` came to ask theirs in opposite orders.

### Budget, and which harness reports which number

**The cost of this feature is in a place gridbench cannot see.** gridbench is BenchmarkDotNet over
bUnit: it renders and measures allocation, and it cannot execute `fastgrid.js`, cannot reflow and cannot
measure a node. It will report the reflow, the `scrollWidth` pass and the `getComputedStyle` calls as
zero.

So two channels, and **a browser millisecond must not be written into the allocation table**, where
every other figure is a KB from a bUnit render. §9 already has "a number attributed to a mechanism
without a control has not been measured"; this is its sibling, and the failure is quieter.

| Channel | What it answers | Gate | Measured |
| --- | --- | --- | --- |
| gridbench, `AutoFitColumns = None` | that the feature off costs nothing | identical to bare | **154.04 KB against a 154.09 KB bare - free** |
| gridbench, `OnDemand` on | the render-side delta - an element id and a colgroup | under 0.5 KB | **154.33 KB, +0.24 KB** |
| Chromium harness, `performance.now()` around the pass | the actual cost | see below | **~1.7ms + ~0.03ms a rendered row** |

Allocation from `--job short`, so no time ratio is quoted from it.

**The one-frame gate this section first wrote down was a guess, and the measurement replaced it.** The
pass reads 3.2ms at 50 rendered rows, 7.1ms at 200 and ~32ms at 1000 - a straight line through a ~1.7ms
fixed cost and ~0.03ms a row. So it is inside a frame for any paged or virtualized grid, which is every
grid this feature is aimed at, and two frames for one rendering a thousand rows at once. That last
configuration has neither paging nor virtualization, and it is the one where the pass is also doing the
most work it will ever be asked to do.

Replacing the per-cell `querySelector` with a sibling walk changed nothing measurable, which says where
the time actually is: not the read loop but the **two forced layouts** of the whole table, one when the
measuring class goes on and one when it comes off. That is the cost of measuring a table that sizes
nothing to its content, and it does not go away by reading faster.

The browser figure carries the machine it was taken on. Unlike an allocation number it does not travel,
and quoting it without one invites the comparison it cannot support. These were taken on an M-series
Mac with the suite running alongside.

### How it is verified

**There is no `RadzenDataGrid` parity pane available** - upstream has no auto-fit at all, so there is
nothing to compare against and a pane asserting agreement would be asserting agreement with a grid that
does nothing. That is the same reasoning that left the keyboard cursor without one.

The Chromium probe carries it alone: a fitted column's rendered width equals its widest cell's content
plus the cell padding; a column of short values comes out narrower than one of long values; `MaxWidth`
clamps; the bare trailing column absorbs the remainder; a grid below the responsive breakpoint fits
nothing. bUnit covers the decision half with the measurement stubbed - eligibility, the precedence of the
three width layers, which column is left bare, and the all-frozen fallback.

**Prove it discriminates by making the measurement return a constant.** If the probe still passes with
every column measured identically, it was asserting the implementation rather than the behaviour, which
is what §9 exists for.

**As built: 24 bUnit facts on auto-fit alone and one many-scenario Chromium pane.** The probe runs the shipped `fastgrid.js` itself
rather than a transcription of it - the export keywords come off so a `file://` page can call it, and
nothing else about the source is touched. Its pane holds two columns with identical values and
different titles, which is what separates the header half of the measurement from the body half: a
width difference between those two can only have come from the header, and that is the assertion the
flex fault failed. It carries its own control - the pane starts with every column equally wide, so a
fit that does nothing fails rather than passes.

The recorded answer at 1000 x 5, for the record and so a change to it is visible:
`[43, 281, 159, 40, 375]` from a starting `[179.6 x 5]`, with `min(81px,40px)` written for the clamped
column, 1000 cells truncated in that column and none in any other, and the table the same 898px before
and after.

### The order it lands in

Landed as three commits, in this order.

1. **The sort glyph**, on its own and first. It is a prerequisite of measuring a header, but it also
   changes header geometry for every grid and fixes a jump that exists today - so it must not hide
   inside a feature commit for a feature that is off by default.
2. **This section**, before the code.
3. **The feature**: the mode and the API, the script's measure-and-write, the third width layer, the
   frozen render condition.
4. **README, the cost rows, and §10.**

### Recorded open

- **`RadzenFastDropDownDataGrid` does not get this in v1, and the question it is waiting on is: does
  the popup grow to the fitted content, or does the grid fit within the width the popup already has?**
  Both are defensible and they are different features. It is also the slice with the worst review
  history on this branch - fifteen findings, then six more on a re-read three commits later - so a
  change to its layout is the wrong place to spend that risk before the question is answered.
- **No event.** Above, with its reason.

**A fit somebody asked for animates.** `transition: width` on a `col` works - worth stating because it
reads like it should not, and was measured twice before being believed. A transition declared on a `th`
or `td` animates nothing: under `table-layout: fixed` the column decides the width, so the cells have
nothing of their own to interpolate.

- **It costs less than not animating.** Over a thousand rendered rows and five columns: 60fps held,
  worst frame **17.5ms against 29.4ms for the instant jump**. The per-frame relayout only moves boxes -
  the cells are `nowrap` with `overflow: hidden`, so no text re-wraps, and only visible rows paint.
- **The bare column glides without being animated.** It is the remainder, so the browser recomputes it
  from its neighbours on every frame of theirs. Worth recording because the obvious test of this - to
  transition the bare column's own width - answers a question a re-fit never asks, and says it jumps.
- **`auto` does not interpolate**, so a first fit would land in one frame while every later one glided.
  Each column being sized is pinned to the width it already has and the style flushed, giving the
  transition somewhere to leave from. **2.5ms over a thousand rows**, because the pin writes what is
  already there and the layout it forces has nothing to move.
- **Only a fit the user asked for is animated.** The one `Once` runs is the grid settling into its first
  layout; animating that reads as a page still loading rather than as an answer to anything.
- **The transition is scoped to a class the fit adds and removes.** A permanent rule on `col` would put
  200ms between a resize drag and the pointer.
- `prefers-reduced-motion: reduce` turns it off - which is also why the headless probe must ask for
  `no-preference` before it can observe the feature at all.

The browser test counts transitions rather than sampling a width part-way through one: headless
Chromium runs the animation clock free of wall time, and all four transitions start *and finish* inside
90ms of a 200ms run. An intermediate width is correct in a real browser and not observable in that one,
so the test asserts what is - that a transition ran, and for which caller.

**When the columns cannot fit, the table overflows and the wrapper scrolls.** Sizing a column to its
content is the point; compressing it again would undo the measurement just taken. But a `col` with no
width in a table that has overflowed its parent is given *nothing* - the bare column renders zero
pixels wide and its content is simply not there, which is not part of that answer. So when the fitted
columns already fill the container, the bare column is sized like the rest: bareness exists to absorb
slack, and there is none to absorb.

Decided by arithmetic rather than by writing the widths and looking, because looking costs a second
whole-table layout. **Both figures it needs are read inside the measuring pass, with every other read.**
Taking them from after the class comes off instead put the pass at 111ms against its own 100ms gate -
the first thing that gate has caught, and it caught a claim made in a comment that the reads "cost
nothing".

**`AutoFitOverflow.Fit` keeps the table inside its container and follows it.** Columns marked
`AutoFitPriority.Required` keep their measured width; the rest give way in proportion to what they hold
above their `MinWidth`, iteratively, so a column that reaches its floor hands its share to the ones
still above theirs.

**Required-ness is a floor, not a flag.** A required column is given a floor equal to its content
width, so it arrives at the distribution with nothing to take and the ordinary arithmetic leaves it
alone. The first version also tested a `required` flag inside the loop, and a mutation deleting that
flag passed every test - because the floor was already doing the work. Two mechanisms for one rule is
how §10b says this branch breaks; the flag is gone.

**A pure-CSS version of this does not exist, and the first design said it did.** `<col>` width takes a
pure length or a pure percentage. Anything mixing the two - `min()`, `max()`, `clamp()`, or
`calc((100% - 120px) * 0.6)` - parses, survives in the style attribute, and then **falls back to `auto`
at layout**. A sweep that appeared to show proportional shrinking was the browser splitting leftover
space between columns it was treating as `auto`, with the required ones holding only because they were
plain px. Read the numbers against the expression before believing them: a `min(300px, ...)` reporting
490px is the tell.

So it needs JS, and what that costs was measured rather than argued (1000 rows x 8 columns, six
resizes):

| | wall | callbacks | in callbacks |
| --- | --- | --- | --- |
| no observer | 327ms | - | - |
| observer redistributing | 464ms | 7 | **0.3ms** |

The observer and the arithmetic are free. The cost is the second table layout each write forces -
~23ms per step at a thousand rendered rows, ~1ms under paging - which is why the callback is throttled
to one redistribution per animation frame.

**Allocation is the part that matters on Blazor Server, and it is nothing.** 2000 redistributions and
16,008 column writes moved the JS heap by 0 bytes. Per-column arrays are typed and built once at fit
time; a column is written only when its value changed; and the observer never calls back into .NET, so
no interop, no render and no managed allocation - the cost does not multiply by the number of circuits.

Two things the design has to keep holding:

- **The surplus still goes to one column.** Handing every column its content width leaves the table
  narrower than its container, and `table-layout: fixed` then shares the difference across *every*
  column in proportion - including the required ones, which is the one thing the mode promises not to
  do. So while there is surplus the bare column absorbs it, exactly as under `Scroll`.
- **Changing `AutoFitOverflow` re-arms a `Once` fit.** The two modes produce different widths, and the
  fit that already ran produced the other ones.

**`MinWidth` and `MaxWidth` mean the same thing in both modes, and neither is parsed.** Under `Scroll`
they go to the browser inside a `clamp()` and it resolves them, so any unit works. Fitting to a
container is arithmetic and needs a number - and the first version got one by reading the string, which
is exactly what this section rules out elsewhere: *"Parsing CSS is the option that works for pixels and
is quietly wrong for everything else."* `MinWidth="10rem"` was silently ignored under `Fit` alone.

The number now comes from the browser too: each bound is written to a probe element in the table's own
wrapper - so a percentage resolves against the width it was written against - and measured back. All
of them are written and then all of them read, one layout for the set, once per fit and never on a
resize.

The bounds are applied in `clamp()`'s order, the minimum last, so a `MinWidth` above a `MaxWidth` wins
the way CSS has `min-width` beat `max-width` - and so a `MinWidth` wider than the content *widens* the
column, which the first version only did under `Scroll`. Getting that order wrong is not cosmetic: it
leaves a floor above the width it is a floor for, the table's `min-width` then overstates what the
columns can sum to, and the browser scales them back up to reach it - so columns promised they would
not move, moved.

**There are two floors, and they are spent in order.** A column headed "Manufacturing Code" over
six-character codes carries width its values never needed, and holding that width while the grid
scrolls is the wrong trade. So the distribution runs twice:

1. Down to the **soft floor** - the width at which a column still shows its own heading. Everything
   with ordinary slack gives here first.
2. Then, only if the table still does not fit, down to the **hard floor** - what the values themselves
   need. This is the round that spends the gap between a heading and its content, and a column whose
   heading is about as wide as its values has almost nothing here and gives almost nothing.

The order is the point: a heading is learned once and a value is read every time, so the heading is the
cheaper thing to spend - but only after the columns with real slack have given theirs. Measured on the
probe pane at 120px: `[38/38/158/80/30]` with two rounds against `[42/280/158/80/51]` with one, where
the long-headed column goes on holding 280px it does not need.

**A best-effort column with no `MinWidth` gets both its floors from its own measurement**: the soft one
from its heading, the hard one from its values. The heading is the point below which it stops saying
what it is, and both are numbers the grid already has - the two halves of the measurement it just took -
rather than any invented for the purpose. The first version floored such a column at zero and a narrow
enough container took it there: still in the table, no longer on the screen.

Under the hard floor sits a 5px backstop, for a column whose values measure nothing. It is not a
readable column and is not meant to be; it is the difference between a column the eye can find and one
that is simply gone. **It has no test of its own** - the probe page cannot produce a header with no
title to measure - so it is a guard, not a guarantee.

Its test asks whether the heading is *truncated* rather than re-deriving what the heading needs, and
**the element that clips is `.rz-column-title-content`, not `.rz-column-title`**. The title is
`flex: auto; width: 100%`, so it never overflows and always reports its column's width - measured on it
the answer is "nothing is clipped" at every width, 38px included. The same `flex: auto` also makes
`scrollWidth` useless for deriving what a heading needs: it answers 600px for every column on a wide
pane. Two separate ways to measure the wrong thing, both of which this branch has now paid for.

**What the tiers are tested for, and what they are not.** The test asserts that the hardest squeeze
leaves every column on its hard floor - within a pixel per column of the table's own `min-width`, which
is the sum of them - and that catches a second round that never runs. It does **not** catch the two
rounds being run in the wrong order: at every pressure this probe pane can produce, spending the
headings first and the slack first land in the same place, and separating them would need a pane built
for that alone. The order is a claim the code makes and the spec explains; it is not one a test holds.

### Known consequences, recorded rather than designed around

- **A fit of the bare column itself is discarded.** The colgroup skips that column by reference so the
  grid's `ColumnWidth` cannot come back and size it, which also drops a width an `AutoFitAsync(column)`
  on it had just measured. Staying bare is the right answer for the layout and the wrong one for the
  user who double-clicked its handle and watched nothing happen. Recorded rather than fixed: the two
  wants are genuinely opposed, and the layout's is the one the rest of the design depends on.
- **A grid whose every column is `Required` cannot be fitted.** Nothing may give way, so a container
  narrower than their total scrolls, and one wider leaves the browser to share the surplus across them
  in proportion. Where at least one column is best-effort the surplus goes to it instead - the last one
  if there is no bare column to take it - so this is only the all-required case.

- **A one-column fit does not move the bare column**, and the first version cleared it - the trailing
  column silently regained the grid's `ColumnWidth` on some later unrelated render. Both review axes
  found it independently. The test that missed it asserted what was *sent* to the browser rather than
  what the grid recorded.
- **Hiding the bare column leaves the table narrower until the next fit.** The reference is kept, so
  showing the column again restores it. Re-picking on every render was considered and refused: the
  fit's other widths are stale the moment a column is hidden anyway, so the honest answer is to fit
  again rather than keep one number fresh among several that are not.
- **A reorder does not bump the view generation.** `RadzenFastGrid.Reorder.cs` deliberately skips
  `RefreshAsync`, so a fit in flight during a drag can land against positions that have moved. Narrow -
  it needs a reorder to complete inside one round trip - but recorded, because the generation is
  documented above as covering "a sort, a filter or a page turn", and a reorder is none of those.

### Where this could still be wrong

- **"Fits what is rendered" is a caveat, not a guarantee, and a paged or virtualized grid is the common
  case.** A user fits a column on page 1 and meets a longer value on page 7. The honest alternatives are
  both worse - re-fitting on every page turn is the jumping already rejected, and asking the server for
  the longest value per column is a query per column that only works for a property column and cannot
  rank a template at all. Recorded so that the first complaint about it is met with the decision rather
  than a fix.
- **The measuring class is a write between two reads**, which is the shape that produces layout thrash.
  It is one toggle rather than one per element, so the pass costs two whole-table layouts rather than
  thousands - but two is where the ~32ms at a thousand rendered rows goes, and reading faster does not
  touch it. If that ever needs to come down, this is the only place with anything in it.
- **The three theme facts this rests on.** The body being free rests on `.rz-cell-data` truncating; the
  header needing a flip rests on `.rz-column-title` being an `inline-flex` with a shrinkable child; the
  slack column rests on `table-layout: fixed`. All three are read out of the shipped theme rather than
  assumed, and all three would change silently under a custom one. The probe is what would catch it,
  and only if it is run against that theme.
