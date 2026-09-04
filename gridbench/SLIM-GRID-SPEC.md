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
the keys, range selection and positional ARIA - and so are column auto-fit (§13) and lookup columns
(§14, shapes 1 to 3). §10 has what is still open.

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
- **A declared `FilterValue` is not applied to the first asynchronous load.** A column's declared
  filter becomes its current one in the column's own `OnParametersSet`, which runs as the table is
  drawn - and the asynchronous load that fetches the first page is started from the grid's
  *parameter-set* path, before any column has registered. So a grid over an executor-backed queryable
  draws its first page unfiltered, with the filter row showing a filter that is not in the query, and
  no reload follows to put it right. The in-memory path does not have this: it composes during the
  render, by which time every column has registered.

  Found while building §15's candidate 2, which needed the asynchronous route because it is the only
  one that composes without asking about `AllowFiltering` first. Recorded rather than fixed, because
  the fix is a reload triggered by the first registration and that is the same "when may the grid
  reload itself" question the settings entry above turns on - with the same `!ReferenceEquals` hazard
  underneath it.
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

- ~~**A check-box list's distinct scan is dropped on every parameter set, not on every data change.**~~ -
  **found, measured and fixed.** `lookups` was cleared on `!ReferenceEquals(lastData, Data)`, which for
  the sources this grid is built for - `context.Rows.AsNoTracking()` read per render, a `Where` written
  in a property - is true whenever the parent renders. So the filter row drew empty, `pendingLookups`
  refilled, and one `SELECT DISTINCT` per check-box-list column ran again behind a second render. Not a
  loop, since `StateHasChanged` does not re-set parameters: N queries and one extra render per *parent*
  render.

  **Measured at 3 scans for one render and two parameter sets** - exactly one per set - by the control
  the file did not have. `CheckBoxListFilterTests` asserted what is offered and never how often it is
  asked for, so a scan re-running on every render passed all sixteen of its tests.

  The fix is to ask what a new source *instance* means, which differs by source kind. A materialized
  collection is rows, so a new one is new values and the scan must run again. A queryable the grid
  composes over is a *query*, and application code answers with a new instance every time it is read -
  so that identity is not a data change, and `Reload()` is what drops the values, exactly as its own
  comment always claimed. Held between two tests: the control above, and
  `TheLookupIsRebuiltWhenTheDataChanges`, which fails if nothing clears.

  **The consequence, accepted:** markup that swaps one query for a genuinely different one goes on
  offering the first one's values until `Reload()`. That is the same lifetime rule §14 gives its
  lookups, chosen there for the same reason.

  The `!ReferenceEquals` trap now has **four** recorded participants - row expansion above, the `Once`
  fit in §13 which dodges it deliberately, this, and the drop-down's `Adopt` found in §19 - and this is
  the only one whose cost was a database round trip. §14 never inherits it: a lookup column runs no
  distinct scan at all.
- **A multiple-select drop-down over a re-materialising source loses its ticks and doubles its value.**
  Found in §19 and left there, because fixing it is the identity question rather than a patch. The grid
  draws a tick by asking a `HashSet<TItem>` whether it holds the row being drawn, and that set compares
  by reference - so a source read again per render ticks nothing, and a click on an apparently unticked
  row `Remove`s the new instance, misses, and `Add`s it beside the old one: two objects, one id, and a
  value that publishes the id twice. Measured at two ticks before and none after. This is row expansion
  above from the other end, it wants `ItemKey` for the same reason, and it is the first instance in this
  list whose symptom is a wrong value rather than a wasted query.

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
| Lookup columns, §14 | reviewed twice, two axes each | 23: 6 wrong answers, the rest tests, names and claims |
| Architecture, whole library | reviewed for shape, not correctness | 1 fault, 8 deepening candidates - §15 |

**Every slice has now been read by someone other than its author**, and the whole has now been read
once for shape rather than for faults - §15 has what that found and the one fault it turned up.

The lookup columns were read on two axes - does it follow the repo's standards, does it implement
§14 - and then read again on both at greater depth, which is where most of it came from: the second
round found more than the first, and the whole suite passed before and after every one.

Four of it are worth carrying forward.

**A nit is worth chasing.** The first standards pass ended on "typing 'bl' matches the blank entry
too", filed as an aside. It was a real fault - the entry is labelled in the reader's own language, so
what a typed filter found depended on the page's culture - and fixing it exposed a second underneath,
where text matching no name showed every row rather than none.

**Reviewing a day of fixes finds faults in them, and this is the third time.** The fix for a non-key
value read as `default(TKey)` went into the method that finds the ticked entry and not the one beside
it that composes the predicate, so the grid filtered to the id-zero rows while the list showed nothing
ticked. Both axes of the second round found it independently.

**A test written for an exit path can fail to reach it.** The first cancellation test disposed the grid
and asserted nothing threw; removing the catch it was written for did not fail it, because the wide
catch below took the exception and a disposed grid renders either answer identically. It is a direct
test of the column now, and the mutation fails it. The parity test between the two filter routes had
the same shape one step removed - it compared the two answers, and two empty grids agree, so a
predicate that went always-false on both sides was invisible. It asserts the answer now.

**A claim in a comment is a claim whether or not its author wrote it as one.** Two of this section's
own sentences were overstated and a reviewer had to trace the code to find out: that the settings
identity "could not have been" the id path, and that the empty-answer bound "has its own test now".
Both conclusions held; the reasoning under the first and the coverage under the second did not. Between them they found ten, of which eight are fixed and three are
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
  conditional the fix had only read one way. **A fifth instance came out of §16**, and as a gap rather
  than a fault: `AllowFiltering` is asked in exactly one place and `ComposeInMemory` never re-asks, so
  a grid with filtering switched off and a column still carrying a value would be filtered - and no
  test said otherwise. Nothing was wrong; nothing was holding it right either, and the two halves only
  became visible in one file once the composition moved into one.
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
- ~~**Lookup columns**~~ - **built**, shapes 1 to 3 of §14, with the auto-fit deferral it needed as a
  prerequisite rather than a follow-up. Six of that section's decisions did not survive the build and
  two rounds of review; all six are marked there. The playground draws both cardinalities, all three
  provenances and both filter modes, which is the fastest way to see what the section describes.
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
`ItemKey` - which already exists, as the key the render tree diffs rows by - and falls back to the
position where no `ItemKey` is supplied.

**This paragraph used to say `ItemKey` "already backs selection membership", and it does not.** Selection
membership is `selection.Contains(item)` over the collection the caller supplied
(`RadzenFastGrid.cs:1723`, `:2042`), which compares however that collection compares - by reference for
the `HashSet<TItem>` the grid's own keyboard range builds. `ItemKey` has exactly two readers, `SetKey`
and this. Found by §20's review; it makes focus the *only* place item identity is keyed rather than
compared, which strengthens §12's argument and weakens the claim that the precedent was already set.

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

The number now comes from the browser too: each bound is written to a probe element and measured back.
All of them are written and then all read, one layout for the set, once per fit and never on a resize -
and on a grid that declares no bounds there is nothing to write, so it costs nothing at all.

**The probe's holder is given the container's width explicitly.** A percentage resolves against its
containing block, and an absolutely positioned box with no width of its own is shrink-to-fit: a probe
asking for 20% of that gets 20% of nothing, measures zero, and is discarded as a length the browser
could not resolve. The first version left the holder to size itself and this section claimed the
percentage resolved "against the width it was written against", which it did not. `rem` was unaffected,
which is why the test written for units passed - it asked only for `rem`. It asks for both now.

**One bounded measurement, not two.** The bounds are applied where the column is measured, so the total
that decides whether any slack is left is the same number the fitting arithmetic uses. Applying them
only to the string handed to the browser overstated a `MaxWidth`-capped column by whatever the cap
removed - enough to conclude there was no slack when there was.

The bounds are applied in `clamp()`'s order, the minimum last, so a `MinWidth` above a `MaxWidth` wins
the way CSS has `min-width` beat `max-width` - and so a `MinWidth` wider than the content *widens* the
column, which the first version only did under `Scroll`. Getting that order wrong is not cosmetic: it
leaves a floor above the width it is a floor for, the table's `min-width` then overstates what the
columns can sum to, and the browser scales them back up to reach it - so columns promised they would
not move, moved.

**Fitting one column is not leaving the mode, and the grid has to be able to say which.** A
double-click on a resize handle fits that column alone; it cannot rebuild a distribution, because one
column is not a layout. But the first version said so with the same `false` that means "this grid is no
longer fitting", and the script took the teardown branch: floor cleared, observer released, the grid
stopped following its container until some later whole-grid fit. What travels is now `'fit'`, `'keep'`
or `'scroll'` - rebuild, leave alone, take down - because the two facts a boolean was carrying were
never one fact.

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

**Waiting for rows is bounded by a clock that can actually be read.** Under virtualization the rows
arrive after the render, so the script waits for them rather than measuring an empty table. Waiting on
frames alone waits as long as a backgrounded tab stays hidden, since `requestAnimationFrame` does not
fire there - and the server has disarmed by then, so nothing would ask again.

The first attempt at a bound tested the deadline at the top of the loop, *after* awaiting a frame. That
reads as a timeout and is not one: the tab that needs it is exactly the tab where the await never
returns, so the deadline was only ever consulted when it was not needed. The frame and a timer race,
and the timer has to be able to win.

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

---

## 14. Lookup columns - the design

**Built**, shapes 1 to 3. Argued before the code, the way §12 and §13 were, so what follows can be
re-argued rather than merely obeyed. Every decision carries its reason; the two that rest on facts about
a dependency rather than on preference are marked, because a change there invalidates them and nothing
else here. *What the build changed* at the end records the four decisions that did not survive contact
with the code, and the numbers that replaced the predictions in *Budget*.

### What it is for

**A column that displays a name and carries an id.** A grid over `Product` wants to show a category,
a brand list, an owner - and the row holds `CategoryId`, `BrandIds`, `OwnerId`. Today the only routes
are a join the source may not have, a navigation property that drags whole entities across the wire per
row, or a `TemplateColumn` that resolves each cell by hand and cannot filter or sort.

The efficiency argument is the point rather than a side effect: **the row carries integers and the names
are held once for the grid.** A thousand rows with a category each is a thousand ints and one lookup of
however many categories exist, against a thousand materialized `Category` instances. What a cell renders
is a string already in that lookup, so the cell itself allocates nothing.

### The surface

Two column types, split on the cardinality of the key the row carries:

```razor
<LookupColumn Property="@(p => p.CategoryId)" Lookup="@categories" Title="Category" />
<LookupCollectionColumn Property="@(p => p.BrandIds)" Lookup="@brands" Title="Brands" />
```

`LookupColumn<TItem, TKey>` takes `Expression<Func<TItem, TKey>>`; `LookupCollectionColumn<TItem, TKey>`
takes `Expression<Func<TItem, IEnumerable<TKey>>>`. Razor infers both type parameters from the property,
as it already does for `CollectionColumn`.

**The split is on cardinality and not on provenance, and that is the whole shape of this section.** The
two axes are orthogonal - one id or many, against three ways of saying where the names come from - and
folding both into one type gives a component with six mutually exclusive parameters, five of them null
at any call site, and a run-time check of which were set that no compiler can help with. Cardinality is
knowable at compile time from the property's type and it changes how the column renders, filters and
sorts, so it earns a type. Provenance does not: every case ends as the same `TKey -> string` map.

So provenance is **one** parameter of a closed type:

```csharp
abstract record FastGridLookup<TKey>
    sealed record Map<TKey>            : IReadOnlyDictionary<TKey, string>
    sealed record Items<TKey, TEntity> : IEnumerable<TEntity>,
                                         Func<TEntity, TKey>, Func<TEntity, string>
    sealed record Query<TKey, TEntity> : IQueryable<TEntity>,
                                         Expression<Func<TEntity, TKey>>,
                                         Expression<Func<TEntity, string>>

// TEntity is per case and inferred at the call site, so it never reaches the column's own signature:
// the column is LookupColumn<TItem, TKey> whichever case supplies its names.
```

This is the `FulfillmentTrait` pattern the consuming application's own guidance names for a closed set of
cases. Illegal combinations are unrepresentable rather than validated, and a fourth source later is a new
case rather than a fourth nullable parameter.

**`Items` takes delegates and `Query` takes expressions, deliberately.** Only `Query` composes into a
database query; `Map` and `Items` are resolved in memory, where an `Expression` buys nothing and costs a
`Compile()` per grid. §4's rule is that the authored form should be the one the consumer needs, and
uniformity for its own sake would make the common case pay for the rare one.

### Where the lookup lives

**Declared on the column, deduplicated by the grid.** Two columns over the same table is the ordinary
case, not the exotic one - `CreatedByUserId` and `ApprovedByUserId` both resolve against users - and
per-column ownership would fetch it twice and hold it twice.

A grid-level registry would share it at the cost of a second thing to declare and a name to get wrong,
and it makes a column's meaning non-local. Instead the grid caches on the `FastGridLookup<TKey>` value
itself: `record` equality gives `Map` and `Items` deduplication for nothing, and the sharing is an
optimization nobody has to name or think about.

**Measured, and the answer splits the cases.** Two instances built the way Razor markup builds them -
the whole expression re-evaluated on every render:

| case | equal? | why |
| --- | --- | --- |
| `Map` | yes | a dictionary reference held in a field |
| `Items` | **yes** | non-capturing lambdas are cached in static fields, so the *same delegate instances* come back every render, and the collection reference is stable |
| `Query` | **no, ever** | its `Expression<Func<>>` members are a fresh object graph on every evaluation and `Expression` does not override `Equals` |

`Query` fails **even over a stable root queryable**, so this is not about the `IQueryable` member at all -
it is the expressions beside it, and no call site can hold it right by being careful with the query.

**That is harmless, and only because the lifetime rule in *Loading and lifetime* is "once".** A failed
dedup costs one extra fetch at startup, not a fetch per render. What it does make load-bearing is the
other half of that rule: **once a column has resolved its lookup it does not re-resolve because the
parameter arrived as a different instance.** A cache keyed on the lookup's identity per render would
refetch a `Query` on every render - which is precisely the defect §10 records against the check-box
list's distinct scan, in a feature designed to avoid it. Resolve once per column; `Reload()` is what
drops it.

So the dedup is a bonus that `Map` and `Items` get and `Query` does not, rather than a mechanism
anything depends on. Two columns over one `Query` fetch twice, once, at startup.

### The row carries ids, so the filter compares ids

**Everywhere: the predicate, the persisted settings, the descriptor's value.** A user picks "Toys"; the
grid translates that to `3` against the lookup it already holds and emits `CategoryId In [3]`.

- **No join is needed**, which is the premise - the source has an id and no navigation.
- **It translates on any provider**, and it is the operator the check-box list already uses.
- **Persisted settings survive a rename.** A filter stored as a name breaks the day someone edits the
  lookup row; stored as an id it does not.

Accepted with it: a saved filter goes stale when a row is *deleted* from the lookup, and a human reading
persisted settings sees ids rather than names. Both are better than filters that break on a rename.

The collection case appends to the authored expression rather than rewriting it:

```csharp
p => p.BrandIds.Any(id => selected.Contains(id))
```

**Every generic argument there is `TKey`, which is a type parameter**, so there is no
`MakeGenericMethod` over a type known only at run time and nothing to guard with `DynamicCode`. A
provider translates it as a subquery.

### The check-box list is the lookup, not a distinct scan

**No `SELECT DISTINCT` runs for a lookup column.** The names are already held, and the ids come with
them, so the list is the lookup's own entries.

The alternative - `DistinctValues`, the way an ordinary check-box-list column works - would offer only
ids actually present in the data, which is a real advantage on a grid narrowed to a handful of them.
It was rejected for three reasons, in increasing order of weight:

1. It is a query per column where this design has none.
2. The list would then **change as the data changes**, so a filter control's options move under the
   user. A stable list is worth more than a shorter one.
3. **Its cache is the one §10 records as having been invalidated wrongly** - dropped on
   `!ReferenceEquals(lastData, Data)`, so the scan re-ran per column per parent render, measured at one
   per parameter set. That is fixed, so (3) is history rather than a live cost; it is kept here because
   it is the reason to be careful about *what a cache is keyed on*, which the dedup finding above then
   ran into from the other side.

A `FilterLookupInUseOnly` opt-in was considered and **refused**: it buys back a query per column to
shorten a list, and `FilterTemplate` is already the escape hatch for a grid that needs it. Build it when
something asks, and key its cache on the source kind rather than a reference when you do.

### What `Simple` mode does on a lookup column

**The typed text is matched against the names, in memory, and the ids it hits are emitted as `In`.** The
lookup is already there, so this is a `Where` over a dictionary's values and the query is unchanged from
the check-box path.

The two alternatives are both worse. Filtering the ids as text is useless - nobody types `47` looking for
Toys. Refusing `Simple` outright leaves `FilterMode` with a value that throws or silently does nothing on
one column type, which is the sort of hole §10b keeps finding.

**Documented consequence:** text matching two hundred names emits two hundred ids, and providers have
parameter limits. A cap belongs in the code with the number stated, rather than a surprise at run time.

**And text matching *no* name is a filter that matches no row, not an absent one.** That is the
opposite of what the same empty list means on the check-box list beside it, where nothing ticked is no
filter - so `HasFilter` asks the one thing that tells them apart, which is whether the box recorded
what was typed. It survives a settings round trip because that text is stored with the filter; see
*What the build changed*.

### The descriptor the collection case reports is portable, and no sentinel is invented

An earlier draft of this section had the collection descriptor as a grid-local encoding, on the
assumption that a bare `BrandIds In [3, 7]` would read as a scalar comparison to any other consumer.
**Reading `QueryableExtension` rather than assuming shows upstream already has exactly this convention**,
and it is the one to emit:

```
Property       = "BrandIds"     the collection property
FilterProperty = null/empty     meaning: the element itself
FilterOperator = In
FilterValue    = [3, 7]
```

Given a collection property with no `FilterProperty`, upstream sets the compared expression to the
element parameter itself rather than to a member of it, rewrites `In` to `Contains`, and wraps the whole
thing with `EnumerableAnyOrAll`. What comes out is
`BrandIds != null && BrandIds.Any(x => new[]{3,7}.Contains(x))` - **the predicate this section
specifies**, arrived at independently.

So the descriptor round-trips through `RadzenDataFilter` and this grid alike, and nothing here needs a
sentinel of its own. Emit the convention that exists.

**Available and not used in v1:** `CollectionFilterMode` on the descriptor already chooses `Any` against
`All` at that same call. "Rows carrying *every* selected brand" is therefore a parameter away rather than
a redesign, and is left out only because nothing has asked for it.

### Sorting

**Not sortable unless an explicit `SortBy` names something the provider can order by.** Sorting by the id
puts categories in insertion order under a column showing names alphabetically - a wrong answer that
looks like a working feature. This is §4's existing rule for a column whose display cannot be ordered by,
applied unchanged, and made visible at the call site rather than silently disabled.

Where a navigation property does exist, `SortBy="@(p => p.Category.Name)"` is the honest answer, and the
author is the one who knows whether it is there.

**Ruled out, so it is not built later:** translating the lookup into the query as an ordered
`CASE WHEN CategoryId = 3 THEN 'Toys' ...`. It works and it translates, and it is a query whose size is
the lookup's size, which is the opposite of this feature's premise.

### Loading and lifetime

**The whole lookup, once, held for the life of the grid.** For the shapes this is aimed at - categories,
brands, statuses, owners - that is tens or hundreds of rows fetched once, after which every cell resolves
and the filter list is complete. Fetching only the ids in play would be smaller and would refetch on
every page turn, sort and filter, which trades one small query for a stream of them.

An author with a genuinely large lookup already has the answer: hand over a narrowed `IQueryable`
(`db.Users.Where(u => u.Active)`), which is more honest than the grid guessing at a limit.

**A `Query` lookup is fetched through `IFastGridQueryExecutor` even when `Data` is in memory.** What
decides is the lookup's own queryable, not `AsyncOwnsData` - a grid over a `List<Product>` with an EF
lookup must still not block the circuit on database I/O. The fetch happens after the render, the way
`LoadLookupsAsync` already does, so it cannot overlap a page load on the same context.

**Nothing invalidates it automatically. `Reload()` drops it.** That is the only escape hatch, and a
lookup with no way to refresh is a cache with no invalidation at all - which produces an "I added a
category and it never appeared" report that has no answer. The cost is bounded and only paid when
somebody asks.

**No `ReloadLookups()`.** A second refresh verb with subtly different scope is how the
`RefreshAsync(announce:)` confusion in §0 happened, and nobody will remember which one they wanted.

### What a cell draws before the lookup arrives

**Blank, and the rows are not gated on it.** `Map` and `Items` have no gap; `Query` has one fetch between
the first render and the names, so the first paint has ids and no names.

- **Not the raw id.** It is a number the reader did not ask for, in a column titled "Category", and it
  makes the settle look like a bug rather than a load.
- **Not a gate on the rows.** Making every lookup column a blocking dependency of the first paint trades
  a small ugliness for a large latency, and this grid's round trip has been measured at ~157ms from Hong
  Kong (§12). The existing loading indicator already says something is in flight.

One extra render, cells fill in - the shape the check-box list already has.

### A null key and a missing key are different failures

**A null key renders an empty cell and is offered in the filter as a "(none)" entry.** "Which products
have no category" is a reasonable question and `In` over a nullable key answers it. Note that this
differs from the current check-box list, which drops nulls outright in `FilterLookup`.

**A missing key - an id with no lookup entry - renders the raw id and is never offered.** It is the one
case where showing the id is right: a deleted row, a lookup narrowed by a `Where`, or a stale cache is a
*fault*, and the id is the only thing that lets someone diagnose it. It is not a choice a user can make,
so it does not go in the filter.

Two different failures should not look the same. §10b's first lesson is that this grid's faults are
silent; these two are the cheapest possible place to stop being.

### What it collides with

**Auto-fit, and it will get this wrong unless §13 is changed.** `autoFitPending` is disarmed by the
*attempt* rather than the answer, and the script waits for **rows** rather than for cell content. So an
`AutoFitMode.Once` grid fits on the render where lookup cells are still blank, the column settles at its
soft floor - the header width - and the names then arrive into a column too narrow for them.
Permanently, because §13 settled that nothing invalidates a fit.

**The fix belongs on the server, not in the script**, and an earlier draft of this section said the
opposite - "extend what the script waits for", plus an instruction to make that wait race a timer.
Reading `fastgrid.js` rather than assuming shows `ready()` already races a `setTimeout` against
`requestAnimationFrame` against a 1000ms deadline, carrying the fix from the second auto-fit review. Its
`wait` argument asks one question, "are there rows yet", and rows are not what is missing.

What is missing is on the C# side, where `AutoFitOnFirstRenderAsync` decides whether to fire at all. So:
**do not attempt the fit while a `Query` lookup is still outstanding, and leave `autoFitPending`
armed.** The resolve already calls `StateHasChanged`, and the next render fires the fit that was owed.
Nothing in the script changes, and nothing re-arms.

**Not by re-arming on the lookup's arrival**: that makes columns jump after the grid looked settled,
which §13 rejected when it decided `Once` stays instant and only an asked-for fit animates.

**The bound is the thing to get right, and it is not the script's.** §13 disarms on the *attempt* rather
than the answer, precisely so that a grid whose script never loads does not ask again on every render.
Deferring the attempt gives that property back temporarily, so a lookup that never resolves is a fit that
never fires. **Every exit path out of the fetch must clear the outstanding state** - the success, the
throw, and the cancelled-and-superseded return that `LoadLookupsAsync` already has. A test per exit path,
not one for the happy one.

**`FilterLookupData` has nothing left to do**, since the list comes from the lookup. It is inherited from
`ColumnBase` and therefore still settable, so setting both is a **dev-time error naming both
parameters**, not a silent loss. `DynamicCode.Unavailable` is the precedent: say what was asked for and
what to use instead. Silently ignoring a parameter somebody deliberately set is a failure mode this
branch has already paid for more than once.

### Budget

The predictions are kept above what was measured, so being wrong is visible.

- **A scalar lookup cell should allocate nothing.** It is `AddContent(string)` over a string already held
  in the lookup - cheaper than a `PropertyColumn` carrying a FormatString, which builds one.
  **Measured: nothing**, on the same harness `PropertyColumnTests` weighs a string cell with.
- **A collection lookup cell allocates one joined string per cell per render**, through `CellText.Join`,
  whose `[ThreadStatic]` `StringBuilder` is already reused across cells. **Measured: 112 B a cell at
  three ids** - and the prediction missed something. `CellText.Join` was non-generic, so every id was
  boxed on the way to a `Func<object, string>`: the same cell through that route measures **184 B**, and
  the 72 B between them is exactly three boxed integers. A typed overload went in beside it, and the
  control that says which is which is that same cell listed the untyped way.
- **The lookup itself is one dictionary per distinct lookup**, not per column and not per row.
- **The filter costs what the check-box list already costs**, minus the distinct query it does not run.

**A control is required before any of these is quoted.** §9's rule - a number attributed to a mechanism
without a control has not been measured - is what the `data-r` claim cost when it went unchecked.

### Native AOT

**Nothing declines**, and this is checked rather than argued. Every selector is typed, both filter shapes
compose from the columns' own expressions, and no member is reached by name, so no path here sits behind
`DynamicCode.Supported` - so unlike the four features `Radzen.Blazor.FastGrid.TrimTest` deliberately
leaves out, both columns belong in it. They are on its reachable path now: it publishes trimmed with
warnings as errors and no trim warning, and the browser check that follows resolves a cell of each
cardinality and filters a column by a name it typed, which is what a trimmed member would be missing
from.

**All three provenances, because provenance is where the risk actually is.** A review pointed out that
the first version of this used a `Map` for both columns, which exercises none of the only code here
that builds an expression tree at run time - `Query`'s projection, an `Expression.New` over a captured
constructor with a body rebound onto another lambda's parameter. That application carries a `Query`
column now, over a `List<T>.AsQueryable()` that needs no database, and the check waits for its cells to
fill rather than asserting about the render they are blank in.

Not covered *there*: the collection column's *expression* filter route. That application's data is an
array, so the in-memory predicate is what runs. It is driven elsewhere - the playground's Entity
Framework source maps `TagIds` as a primitive collection, so ticking a tag composes
`Any(id => selected.Contains(id))` into SQLite - and it is composed from `MethodInfo`s captured by
ldtoken rather than found by name. What is untested is that combination *under a trimmer*, which is a
narrower gap than it was.

That is a stronger position than `CollectionColumn` manages, and it is a reason to keep the selectors
typed even where an `object`-returning one would read more simply at the call site: §4 records that a
selector declared as returning `object` hides its member's real type two different ways, and this branch
has paid for that once already.

### Shape 4, and why it is not built

An earlier form of this design had a fourth shape: `Property="@(p => p.Brands.Select(b => b.Id))"`, the
ids projected out of a navigation collection, with the grid loading them on demand.

**It is buildable and it is not built.** Two facts settled it, and both are recorded because the first
one was got wrong here before it was checked:

- **`Include` is not required.** Projecting a collection navigation alongside the entity has been
  first-class since EF Core 6.0 ("split queries for non-navigation collections"), so
  `source.Select(p => new { p, Ids = p.Brands.Select(b => b.Id) })` translates. An earlier draft of this
  section required `.Include()` and proposed a guard for its absence; that guard would have fired on
  correct code.
- **`ItemKey` is `Func<TItem, object>`, not an expression**, so it cannot be composed into a query. A
  side query projecting `(key, ids)` per page therefore needs a key the grid does not have, and getting
  one means either widening `ItemKey` to an expression - whose `Convert`-to-object node is its own
  translation question - or a third type parameter on the column.

The remaining route, projecting the item and its ids together in one query, needs a projection type with
a member per lookup column, which means **building a type at run time** and giving back the "nothing
declines under AOT" property above, in exchange for one round trip.

**The supported answer is a DTO.** `db.Products.Select(p => new ProductRow { ..., BrandIds =
p.Brands.Select(b => b.Id).ToList() })` makes `TItem` the projection, `BrandIds` an ordinary collection
of ids, and the column an ordinary `LookupCollectionColumn` needing none of the above. It is one query,
no key, no data-path change, and it drops the columns nobody renders - which is the same efficiency
argument this whole section is built on, applied one level up.

**Filtering never needed any of it.** `.Any(id => selected.Contains(id))` composes into the main query
and materializes nothing client-side, so only the *cell* ever wanted the ids. That is worth keeping in
mind if this is revisited: the feature at stake is display, not filtering.

### What the build changed

Six decisions above did not survive contact with the code - four found while building it and two more
that two rounds of review turned up. Each is recorded here rather than edited away, because what a
decision was before it was checked is the part worth inheriting.

**The fetch is cancelled by the grid going away, and by nothing else.** *Loading and lifetime* said it
had the "cancelled-and-superseded return that `LoadLookupsAsync` already has", meaning the page load's
token. That is wrong twice over. The check-box list's scan is about the data and is stale the moment a
newer load replaces it; a lookup column's names are not, so a sort landing mid-fetch would throw away an
answer that was still correct. And *nothing would ask again*: the render that superseded it has already
happened, so "a newer load will ask on its own render" is a render that is already behind. The question
actually being asked is "was this dropped while it ran", and that is a generation stamped on the column
and moved by `Reload`. The token is now a lifetime one, cancelled in `Dispose`.

**A fetch that throws resolves the column to no names.** *What a cell draws before the lookup arrives*
never said what a *failed* fetch draws, and the auto-fit rule needs an answer: a throw that propagates
out of `OnAfterRenderAsync` takes the circuit down, which makes "clear the outstanding state on the
throw path" pointless. The rows are drawn and correct and only the names are missing, so it resolves to
an empty lookup - and every cell then draws its id, which is what a missing entry already draws and for
the same reason. Two silent blanks would have been one fault nobody can see.

**A column that comes back with nothing asks again itself, and an empty answer is an answer.** The
column cannot wait for a parameter set, because the renderer skips `SetParametersAsync` for a retained
component whose parameters have not changed - which is exactly a column whose lookup is held in a field.
So it re-queues itself, and that is a render feeding a fetch feeding a render. The bound is that an
answer counts even when it is empty: a mutation that left the column outstanding after a *successful*
fetch did not fail a test, it aborted the run with a stack overflow. A review caught the first version
of that claim overstating itself - the test named for the bound fetched a *non-empty* lookup, so the
empty case it was cited for was the one it did not cover. There is a test for a lookup that resolves
to nothing now.

**The sharing is narrower than the table above suggests, and the table is still right.** Two `Items`
built the same way are equal - but only from *one call site evaluated twice*, which is what markup does
with an expression on every render. Two separate call sites are two compiler-cached delegates and are
never equal, so two columns each writing `FastGridLookup.Items(...)` in their own markup share nothing.
What the sharing actually pays for is the ordinary shape - one lookup held in a field and handed to both
columns - where it is the same instance and the second column skips building the map at all.

**`HasFilter` is virtual, and an empty selection means two opposite things.** Not a departure this
section foresaw at all - it came out of *What `Simple` mode does on a lookup column* meeting the rule
that a check-box list with nothing ticked is no filter. On the box, a name nothing answers to *is* an
answer and the grid should show no rows; on the list, nothing ticked is the absence of a filter. Both
are `In` over an empty list of ids, so the value cannot tell them apart. What can is that only the box
records what was typed, so that is what the override asks.

The consequence reaches further than the column: **the typed text is part of the stored settings now**,
because a filter captured and restored through the value alone comes back as the other one - a grid
showing nothing restored as a grid showing everything. And recording that text had to move: it was
being written by the caller after `Filter` returned, and `Filter` reloads, and the reload is what
announces the settings, so it was recorded after the thing that stores it.

Two smaller things, recorded because they will look arbitrary otherwise:

- **The blank entry is `Spreadsheet_Blank`, so it reads "(Blank)" rather than "(none)".** It is the only
  string in Radzen's resources that already means "the rows with nothing here", and it is translated
  into every culture Radzen ships, which a key of this component's own would not be. The grid's
  `BlankFilterText` overrides it.
- **A nullable key types the lookup at the nullable key**, and `FastGridLookup.Map` is the awkward one
  there: `Dictionary<int?, string>` is a CS8714 warning in the *consumer's* own nullable-enabled code,
  because `Dictionary` asks for a key that cannot be null. `Items` and `Query` take a selector, so a cast
  in the lambda is the whole answer, and that is what the README documents. Inside the library the
  suppression is stated once, beside the one factory that builds the map.

**Two seams on `ColumnBase` replaced a branch the grid could not have written.** A review read the
filter plumbing moving onto the column as a refactor riding along, and it is worth saying why it is
not optional: the grid had to ask a column what typed text means and what a ticked list means, and it
cannot ask a lookup column anything specific because it is not generic over `TKey`. A virtual needs a
default, and a default lives on the base - so the two methods moved rather than being added beside the
ones they replace. What came with it is that the selection seam absorbed the `MakeGenericType` the grid
was doing to type that list, so a lookup column reaches nothing by name on that path either.

The collection case turned up a fault of the kind §10b keeps finding, before it shipped rather than
after: **the null guard has to sit inside the negation**. Written outside it, `NotIn` keeps a row
carrying no ids at all when composed as an expression and drops it when composed as a delegate - which
is the same shape as the `In`-over-a-null-string disagreement a review found in the shared builder. It
has that fault's test: the same data as a `List` and as a queryable, and the two answers compared.

### Recorded open

Both of the questions this section originally left for a spike have been answered before the build, and
both changed what is written above rather than confirming it: `Query` never deduplicates by value and it
is the expressions rather than the queryable that prevent it, and the collection descriptor needs no
sentinel because upstream already has the convention. What is left:

- **A lookup column's settings identity is its *sort* path, not its id path**, which is not what this
  section said before it was built. `PropertyPath` is two things at once - the settings key *and* the
  name a `LoadData` or OData sort travels under - so it cannot simply carry the id: a remote grid would
  order by `CategoryId` under a column sorting by `Category.Name`.

  Separating them was available and was **not taken**, and the reason is not that it could not be done -
  a `SettingsKey` defaulting to `PropertyPath` is four call sites. It is that the separation makes
  things worse rather than better here: an id-path settings key gives a `LookupColumn` over
  `p.CategoryId` **the same identity as a `PropertyColumn` over `p.CategoryId`**, which is §10b's
  collision newly created rather than avoided. §10b's own instruction is not to close that by guessing
  at the identity model, and this would have been a guess.

  So the consequence stands and is `CollectionColumn`'s exactly: **a lookup column with no `SortBy` has
  no settings identity at all**, and its width, order, visibility and filter are never captured. One
  *with* a `SortBy` stores its filter as ids and survives the rename this section argued for, and that
  round trip has a test. This joins §10b's open collision as another participant rather than settling
  it.
- **Not in `RadzenFastDropDownDataGrid`**, for the same reason §13's auto-fit is not: that slice has the
  worst review history on the branch, and its open layout question should be answered before anything
  else is added to it.

### Where this could still be wrong

- **"The lookup is small" is an assumption about the caller's domain, not a fact.** Everything here -
  fetching it whole, holding it for the grid's life, offering all of it in the filter, reverse-mapping
  text against it - is right for hundreds of entries and wrong for hundreds of thousands. The design
  offers a narrowed `IQueryable` as the answer and does not enforce a limit. The first grid to point a
  lookup at a large table will find this, and the honest response is a documented ceiling rather than a
  silent degradation.
- **A stable filter list is asserted to be worth more than an accurate one.** That is a judgement about
  users, not a measurement, and it is the one decision here most likely to be reversed by somebody
  actually using it.
- **The blank-cell interval is invisible in every test that renders synchronously.** A bUnit test with a
  `Map` lookup never sees it, so the case that needs covering is specifically the `Query` one, and it has
  to assert about the *first* render rather than the settled one. This is the same shape as the frozen
  filter row in §10b: a check that looks for the resolved state can only see it once it works.

---

## 15. Architecture review — the deepening candidates

Every pass in §10b asked whether the code is *correct*. This one asks whether it is the right *shape*:
where a module is shallow, where a seam is missing, and where a rule that lives in a comment should
live in an interface instead. It found two wrong answers, recorded below, and building the first two
candidates turned up a third that is recorded in §10; the rest of what follows is shape. None of the
three was visible until the shape was written down, which is the argument for this kind of pass rather
than a summary of it.

Read by four sub-agents against written briefs, over the two grid partials, the column model, the
browser module with its six calling partials, and the test suite read as a consumer of the interfaces
rather than as coverage.

**The constraint every candidate is scored against is §3.** Rules 3 and 5 make allocation a design
rule, so any deepening that puts a new allocation on the per-row or per-cell path fails on arrival.
Everything below moves work that already happens once per render or once per column.

**The vocabulary is deliberate** and is `codebase-design`'s: *module* (an interface and an
implementation, at any scale), *interface* (everything a caller must know — signature, invariants,
ordering constraints, error modes, cost), *deep* and *shallow* (behaviour per unit of interface), *seam*
(where behaviour can be altered without editing in that place), *leverage* (what callers gain), and
*locality* (what maintainers gain). Not "component", "service", "layer" or "boundary".

### The two faults - **both fixed**, with `Attachment` (candidate 4, built)

**The key guard was recorded before the call that earned it, and never let go.** `navigationAttached`
was set *before* `attachNavigation` was invoked, so a throw still recorded success - and there was no
`DetachNavigationAsync` at all, though `detachNavigation` is exported and dispose called it. Switching
`AllowKeyboardNavigation` off at runtime stops `RenderNavigation` emitting the view id, so
`getElementById` answered null and the guard stayed bound to a live element: a grid that no longer
navigates went on calling `preventDefault` for every key in `HandledKeys` while nothing acted on them.

The pointer listener does the opposite and says why - "recorded once it is true of the DOM rather than
before the call". **This is the fourth instance of §10b's rule that a fix is right for the case that
motivated it and has to be checked against its neighbour**, and the first where the neighbour had
already been fixed and the lesson was not carried across.

**And then the neighbour turned out to have the same fault.** `DetachClicksAsync` existed, ran on the
right condition, and could not work: the tbody's id was emitted under `ClicksAreLive &&
!AllowVirtualization`, which is the very condition that stops the grid delegating - so switching
virtualization on dropped the id on the render *before* the detach that needed it, `detach(bodyId)`
found nothing, and the listener stayed bound beside the per-cell handlers that had just replaced it.
Every click raised twice. **That is exactly what the comment above `AttachClicksAsync` has always said
must not happen**, written by an author who had seen the hazard, fixed the half they could see, and had
no way to notice that the markup undid it.

Neither fault is reachable from a bUnit test on its own: the C# call is made in both cases and only the
browser knows it removed nothing. What is testable is the cause, and it is one rule -

> **An element a listener is bound to keeps its name past the switch that bound it.** Letting go means
> naming the element, and the switch that stops the feature is the switch that would stop it being
> named. So both ids are *latched*: never emitted for a grid that has never used the feature, never
> withdrawn from one that has.

Making them unconditional instead was tried and rejected: three tests assert those ids are absent when
the features are off, which is §3's rule 3, and a fourth compares two grids' markup, which per-grid ids
break. Keeping an id only while a listener is *currently* bound was also tried, and is worse than
either - correct only while nothing re-renders between the switch and the release, which the component
does not control, `Virtualize` violates on its own, and no test can pin.

**A mutation check caught one of this section's own tests not discriminating.** The test for "the
attempt is forgotten with the listener" passed with that rule deleted, because releasing also clears
the remembered payload - so for any payload but its type's default the guard sees a change and
re-attaches regardless. It uses a default payload now, and fails when the rule is removed. Eight
mutations, eight caught; §9's first layer earning its place again.

### The candidates

Ranked as found. Nothing here is committed to; each is an argument to be taken or refused, and a refusal
with a load-bearing reason should be recorded here beside it.

| # | Candidate | Strength |
| --- | --- | --- |
| 1 | Compose the view behind one interface | ~~Strong~~ **built**, §16 |
| 2 | `drawing` is a mode, not a field | ~~Strong~~ **built** |
| 3 | The browser seam has no interface | ~~Strong~~ **built**, §18 - and three of this row's claims corrected there |
| 4 | Attachment is a pattern copied twice, one copy missing its half | ~~Strong~~ **built** |
| 5 | Four methods of one shape, four meanings of `null` | ~~Strong~~ **built**, §17 |
| 6 | `ColumnBase`'s internal half is a field-by-field protocol | ~~Worth exploring~~ **built**, §20 - and it is four sections, not seventeen members |
| 7 | A column's identity is a concept with no name | Worth exploring |
| 8 | The drop-down forwards twelve parameters, then hands out the grid | ~~Worth exploring~~ **built**, §19 - it was the scan, not the forwarding |

**1. Compose the view behind one interface.** **Built**, as `Composition`; §16 has the design it was
built from and, at the end, the three of its decisions that did not survive the building.
`RadzenFastGrid.Data.cs:1117-1246` and `:1832-2007` were
about 300 lines that are already a function of `(columns, sorts, source, config)` — `BuildFilters`,
`ApplyFilters`, `Reflective`, `ComposeInMemory`, `ApplySorts`, `Compose`, `Page`, `Total`, `OrderBy`,
`FilterString`. They are private instance methods over `columns` and `sorts`, both of which are declared
in the *other* partial, so the partial-class split is a text split rather than a module boundary and
the pipeline's only interface is "render a grid and read the DOM". `InMemoryCompositionTests.cs:48-64`
stands up two `TestContext`s and diffs rendered rows to check the two routes agree.
`FilterExpressionParityTests` is the proof this is avoidable: it calls
`FilterExpression<Person, TProp>.For` and `.PredicateFor` directly, covers 84 operator x route
combinations, and needs no bUnit at all.

**2. `drawing` is a mode, not a field.** **Built**, as `DrawPass<TItem>`. The flag and the four fields
beside it are one value: what the render in progress has already worked out. `Composed` and `Compose`
are one method, and `TotalCount` is one line rather than a memo dance around `CountAll`, which keeps
the source selection it was tangled with.

**One claim in this section was too strong, and is corrected here.** It said `ApplyFilters` "filters
differently inside a render than outside one". That divergence is not reachable: `Filtered` and
`Compose` both guarded on `AllowFiltering` before calling, and `LoadPageAsync` - the one caller that
did not - only ever runs outside a render. So the `!drawing` term in that guard was dead. It sharpens
the candidate rather than weakening it: a guard written against an ambient that *cannot* vary is worse
than one that does, because nobody can see the rule and the next caller inherits it. The guard is
`!AllowFiltering` now, asked in one place, and its two callers have dropped the term they duplicated.

**What the build changed.** Two of this candidate's own decisions did not survive it.

- **The readers were going to test `Drawing`, and now do not.** A mutation check found the test for
  "the total is remembered only for the pass" passing with that condition deleted - `Keep` already
  gates on `Drawing`, so nothing can have been remembered outside a pass for a reader to find. Two
  redundant branches, both unreachable, both looking like the rule was enforced twice. `Keep` is the
  single gate now.
- **Which moved the risk somewhere a test could not follow it**, and that is the more interesting half.
  With the readers ungated, correctness depends on a pass being closed *entirely* rather than by
  clearing its flag - otherwise a later caller holding the same source instance is handed a stale
  composition. No test could pin that: the obvious one asserts the assignment it makes itself, not the
  one the grid makes. So `Drawing` and `Filters` are settable only by `Begin`, and the mutation that
  closes a pass by clearing the flag **no longer compiles**. A fault made unrepresentable is worth more
  than a test that it has not happened.

**Measured**, `--job short` at 1000 rows, one run before and two after: bare 154.53 KB -> 154.54 and
154.65, one sort 175.89 -> 175.79 twice, a filter row 158.90 -> 158.77 twice. Allocation-neutral,
which is what was claimed; the bare spread of 0.11 KB across the two after-runs is the noise floor
rather than a cost, and the two composing paths are a shade cheaper in both. **No time ratio is
quoted**: at that job length the errors on all three were wider than the differences, and §9 has the
rule about that.

The pass is a field on the grid, and is passed by `ref` only where it crosses a module edge - which is
candidate 1, not this. What this does *not* do is remove the ambient from the grid's own helpers:
`ActiveFilters` and `Compose` still read the field. The claim is only that it no longer crosses a seam.
The alternative - threading `ref pass` through `RenderGrid`, `RenderPager`, `RenderHead`, `RenderBody`
and `RenderRow` - is a large diff through the hottest code on the branch, to buy something this already
has.

**3. The browser seam has no interface.** **§18 has the design, and disagrees with this entry: the
coverage count below is wrong and the reason given for it is wrong.** Sixteen named entry points
carrying about forty-five positional arguments — and that is the narrow half. The wide half is undeclared: element ids, `data-r`,
`data-toggle`, `rz-data-row`, `rz-cell-data`, and `:scope > table > tbody > tr`, none of which appears
in a signature, so a rename breaks the script and no C# test notices. `autoFit` takes ten positional
arguments; `FastGridAutoFitTests.cs:39-49` has already written the type that wants to exist, as
`record Ask(...)` plus a hand-rolled positional decoder. Seven of the nine exports have zero coverage
of any kind, and because the doubles answer `null` the RTL arrow flip at `Keyboard.cs:313, 318` is
never executed by any test. `NavigationMetrics` is already a value crossing the seam with no in-process
way to supply one.

The ordering constraints are part of the interface and are written only as prose, in a different file
from the calls they govern (`Data.cs:952-992`): attach after the pagers sync, fit before focus, fit
after the lookup names land, reassert focus last, detach before release before dispose. §13 already
recorded that swapping two of them would measure blank cells "and every test would still pass".

**4. Attachment is a pattern copied twice.** ~~See the fault above.~~ **Built.** Two features with
identical lifetime, one of which had grown re-attach, `attachedKinds`, `DetachClicksAsync`, a fallback
and record-after-the-call, and the other of which had grown none of them. Both are now one
`Attachment<TPayload>` - `SyncAsync(wanted, payload)` answering what it did, and `ReleaseAsync()` - with
the tbody listener and the view listener as its two adapters, which is what makes the seam real rather
than hypothetical.

It calls interop through two delegates rather than reaching for the module itself, so a fake is the
second adapter and the module's rules are testable in-process: six of the nine tests written for it
need no browser and no `TestContext`. That is candidate 3 in miniature, scoped deliberately to attach
and detach over one payload and nothing about geometry, focus or fitting - so that if candidate 3
disagrees with it, what is thrown away is twenty lines.

Three things moved out of the callers and into it, and each was a place the two disagreed: what
"attached" means (the pointer listener asked what `attach` reported, the key guard asked nothing at
all - it now asks whether the script found the element to measure); when the binding is recorded; and
what dispose should release, where the two features had been reading *different* flags on adjacent
lines, neither matching the condition its own detach used. What stayed with the callers is the
fallback, because it is click-specific and ends in a re-render: `SyncAsync` reports, the caller
decides.

**5. Four methods of one shape, four meanings of `null`.** **Built.** §17 has the design it was built
from, the three findings a probe turned up, and the four of its own claims that did not survive. `ApplyFilter` returning null means "fall
back for *me*" (`Data.cs:1195`); `ApplyFilterInMemory` means "abandon the route for *everyone*"
(`:1940`); `ApplySort` means "skip me, keep the rest" (`:2002`); `ApplySortInMemory` means "abandon for
everyone, but only if `i == 0` and nothing is ordered yet" (`:1977`). Four contracts, one return shape,
none of them stated where an implementer would read it — and the decision is contagious: on a mixed
`Or`, one declining column sends every typed column through the reflective route (`:1211-1218`). §10b's
computed-column fault was this reading the wrong one of the four.

**6. `ColumnBase`'s internal half is a field-by-field protocol.** **Built**; §20 has the design it was built from, corrects two of this entry's three counts and the diagnosis behind them, and records at the end the three of its own claims that did not survive. The public half is deep — 28
parameters, `RenderCell`, and four `Apply*` methods behind which the whole typed-expression story
sits. The internal half is not: seventeen members each answering exactly one grid call site
(`CellClass`, `CellStyle`, `CellElementClass`, `ColStyle`, `FrozenCellStyle`, `FrozenFooterStyle`,
`IsFrozen`, `ElementIds`, `CanAutoFit`, `SetAutoFitWidth`, `ResizedWidth`, `FilterValues`,
`FilterSelection`, `FilterValueFromText`, `FilterValueFromSelection`, `FilterMemberPath`,
`FilterPropertyType`), plus `AutoFitWidth`, which has no reader anywhere in the library or the tests.
The ordering rules between them are enforced by comment: derive before `base.OnParametersSet`, hand the
same string instance back or the frozen memo misses, re-write `AppliedFilterText` after `SetFilter`
clears it (two call sites, one rule).

The class is public and abstract with a public abstract member, so it advertises itself as an extension
point — but nine of its twenty-one virtuals are `internal virtual`, so an out-of-assembly column can
render and sort and cannot participate in the filter row at all. The sibling duplication is the same
gap from the other side: the six-member sort-forwarding block is copied verbatim into `TemplateColumn`,
`CollectionColumn` and `LookupColumnBase`, the "Derive" ceremony four times, and
`RenderCell => AddContent(CellTextOf(item))` four times.

**Constrained by §3, and possibly refused by it.** Any consolidated answer must be a readonly struct
over strings the column already memoizes, handed back by reference identity — `ColumnBase.cs:367-383`
already keys its memo on `ReferenceEquals`. If it cannot be done at zero marginal allocation it should
not be done, and `gridbench` answers that in one run.

**One of its complaints is refused, and this is the reason.** Opening the eight `internal virtual`
members - which are not scattered, but are exactly the filter row's protocol - would publish that
protocol at its current shape while two things that would change it are open: §10's question of whether
an operator menu, a date popup, a numeric range or an enum picker is built in, and candidate 7 below,
which would give a column an identity the filter lookup is currently keyed by. Publishing eight members
now and revising them after either lands is worse than publishing them once. §20 records it the same
way.

**7. A column's identity is a concept with no name.** Settings, reorder and the picker all need to name
a column and all three borrow a *query* path to do it. `PropertyPath` is the settings key and the name
a remote sort travels under; `FilterPropertyPath` keys the filter lookup; `FilterMemberPath` builds the
reflective descriptor. §10b's collision and the `TemplateColumn` limitation and §14's lookup-identity
consequence are three symptoms of one missing module. **This section does not re-open them** — §10b's
instruction not to guess at the identity model stands. The only claim here is that they are one
question and should be designed once rather than three times.

**8. The drop-down forwards twelve parameters, then hands out the grid.** It is not a shallow
pass-through overall: `Adopt`, `Chosen`/`ElementOf`, the popup lifetime and the form participation are
about 380 of its 668 lines and none is reconstructible from `RadzenFastGrid`. But twelve of its
thirty-three parameters are one-line forwards, so a thirteenth is four places; and `Grid => grid`
(`:202`) then exposes all 81 of the grid's parameters, which its own test already reaches through
(`FastDropDownDataGridTests.cs:414-428` asserts `Assert.Same` and then reads `Grid!.CurrentPage`). Its
second id-to-name path is the more interesting half: `Adopt` scans `Data` linearly with no cache,
guarded by `ReferenceEquals(lastData, Data)` — **which §10 has already recorded as false on every
render for exactly the sources this library targets**. That makes it a fourth participant in the
`!ReferenceEquals` trap, and the only one whose cost is a full scan per render.

### Deliberately not proposed

- **`FilterExpression`'s two implementations of sixteen operators.** The duplication is between an
  expression tree and a delegate, which is the point of it, and `FilterExpressionParityTests` is a real
  check rather than a hope. Its own comment already names the risk.
- **Lifting `ApplyFilter` out of the columns.** `TProp`, `TKey` and `TElement` are type parameters only
  there. Any move erases the type and puts `MakeGenericMethod` back, which is what `DynamicCode` exists
  to fence off. The available deepening is to move *shape* into a helper already closed over the type —
  which is what `FilterExpression<TItem, TProp>` and `FastGridSort<TItem>.By<TKey>` already are.
- **A seam for localization, `Defer`, or `NonRenderingHandler`.** One adapter each, so each is a
  hypothetical seam and fine as it stands.
- Anything §1 rules out, or §10 has already settled with a reason.

---

## 16. Composing the view behind one interface - the design

§15's first candidate, argued before it is built. Nothing here has been written yet; the numbers are
measurements of the code as it stands, and the decisions are the ones that came out of grilling the
candidate rather than assumptions to be rediscovered.

### What it is for

`RadzenFastGrid.Data.cs` holds about 250 lines that are already a function of their arguments -
`BuildFilters`, `DescriptorFor`, `ApplyFilters`, `Reflective`, `Compose`, `ComposeInMemory`,
`ApplySorts`. They are private instance methods over `columns` and `sorts`, both of which are declared
in the *other* partial, so the partial-class split is a text split rather than a module boundary and
the pipeline's only interface is "render a grid and read the DOM".

What that costs is visible in the suite. `InMemoryCompositionTests` stands up **two** `TestContext`s
and two grids and diffs the rendered rows, because there is no function to call twice with the same
arguments. `FilterCompositionTests` types into the DOM to reach the mixed `And`/`Or` branch - the one
place in the composition carrying a written correctness argument. And `ComposedInMemory` exists as a
property whose own doc comment says it is "exposed for the tests, and only to them", because which
route ran is invisible in the rows.

**The proof that this is avoidable is already in the repo.** `FilterExpressionParityTests` calls
`FilterExpression<Person, TProp>.For` and `.PredicateFor` directly, covers 84 operator x route
combinations, and needs no bUnit at all. That interface sits at the right seam. The composition above
it does not.

### The surface

As designed, and **as built** - the build widened it, and §16's addendum has the argument:

```csharp
internal static class Composition
{
    internal static Composed<TItem> Compose<TItem>(
        IReadOnlyList<ColumnBase<TItem>> columns,
        IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)> sorts,
        IEnumerable<TItem> source,
        CompositionOptions options,
        ref DrawPass<TItem> pass);

    // Filtering and ordering on their own, for the callers that have to count between them.
    internal static IQueryable<TItem> Filter<TItem>(
        IReadOnlyList<ColumnBase<TItem>> columns, IQueryable<TItem> source, CompositionOptions options);

    internal static IQueryable<TItem> Sort<TItem>(
        IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)> sorts, IQueryable<TItem> source);

    // What the columns are asking for: as descriptors, gated on AllowFiltering, and in force now.
    internal static List<FilterDescriptor>? Filters<TItem>(IReadOnlyList<ColumnBase<TItem>> columns);

    internal static List<FilterDescriptor>? DeclaredFilters<TItem>(
        IReadOnlyList<ColumnBase<TItem>> columns, CompositionOptions options);

    internal static List<FilterDescriptor>? ActiveFilters<TItem>(
        IReadOnlyList<ColumnBase<TItem>> columns, CompositionOptions options,
        in DrawPass<TItem> pass);
}

internal readonly struct Composed<TItem>
{
    internal IEnumerable<TItem> Rows { get; }

    /// Whether the delegate route ran rather than the expression one.
    internal bool InMemory { get; }
}

internal readonly struct CompositionOptions
{
    internal bool AllowFiltering { get; }
    internal FilterCaseSensitivity FilterCaseSensitivity { get; }
    internal LogicalFilterOperator LogicalFilterOperator { get; }
}
```

Three parameters that are really parameters, one options value, and the pass. That is the whole
argument list, and it is not a guess: the moving code reaches for exactly nine things on `this`, and
they are `columns` (6 references), `LogicalFilterOperator` (6), `sorts` (4), `pass` (4),
`FilterCaseSensitivity` (3), `ComposedInMemory` (3), `AllowFiltering` (1), `SortColumn` (1) and
`DynamicCode.Supported` (1). Six collapse into `CompositionOptions`, three are the real parameters,
`SortColumn` is answerable from `sorts`, `DynamicCode.Supported` travels with `Reflective`, and
`ComposedInMemory` stops being a reference at all.

**`ComposedInMemory` becomes part of the answer.** That is the single cleanest win here: a return value
currently smuggled out through a field becomes something a caller can read - and act on - rather than
merely observe afterwards. The property whose reason for existing is "for the tests, and only to them"
stops needing to exist.

> **That last sentence is wrong, and the addendum below has why.** The property still exists, because
> this section's own verification item 3 needs it: it is the only thing a test outside the grid can see
> that says the grid asked the module and used what it was told. What is true is the first half - the
> value stops being written from inside the composition and becomes an answer the caller is handed.

### What moves and what stays

**Moves - 253 lines:** `ApplyFilters(IQueryable)` 73, `ComposeInMemory` 63, `Compose` 50,
`BuildFilters` 22, `ApplySorts` 20, `Reflective` 14, `DescriptorFor` 11.

`Reflective` takes the `DynamicCode` policy with it. "This route needs dynamic code" belongs to the
route, not to the grid.

`BuildFilters` and `DescriptorFor` move even though descriptors are not purely a composition concern -
they also feed the **public** `Filters` property and `LoadDataArgs.Filters`. That is the argument for
moving them rather than against it: three places answer "what are the columns asking for" today, and
§10b's recurring finding is a rule applied in one place and not in its neighbour. One place means they
cannot disagree, and the two outside callers call the module.

**Stays - 76 lines:** `View` 33, `CountAll` 30, `Page` 10, `Total` 3.

All four are about *which source owns this* - `LoadData.HasDelegate`, `loadedCount`, `AsyncOwnsData`,
`Paging` - which is a different question from what to do to it, and the module gets shallower the
moment it has to know both. Keeping source selection on the grid is also what keeps the module a pure
function of its arguments, which is the whole reason it becomes testable.

**A side effect worth having:** the private `ApplyFilters(IQueryable<TItem>)` moving out ends its name
collision with the public `ApplyFilters(IEnumerable<FilterDescriptor>)`, which today are kept apart by
overload resolution and nothing else.

### What the module sees of a column

`IReadOnlyList<ColumnBase<TItem>>` - the whole column type, not a narrowed interface.

A narrow interface exposing only `HasFilter`, the four `Apply*`, `FilterPropertyPath`,
`FilterMemberPath`, `CurrentFilterValue`, `CurrentFilterOperator` and `FilterPropertyType` would be a
seam with **one** adapter, since nothing but `ColumnBase` would ever satisfy it. Worse, it would decide
§15's candidate 6 - what a column exposes - as a side effect of moving the pipeline. That decision
should be taken deliberately, and this module is one of the call sites that will tell us whether a
narrowing is right.

Pre-projecting the columns into a value is ruled out by §3: it allocates per column per composition and
buys nothing.

**This is where the piece is most likely to grow.** If the moving code turns out to reach the grid
through a virtual call on a column that is not in the nine above, the argument list stops being small
and the answer is the registry moving too - which is a materially larger change. That is a stop-and-
re-decide point, not something to push through.

### Why it is internal

`internal static`, reached by the tests through the `InternalsVisibleTo` the project already grants.

Making it public commits a NuGet package to supporting the composition's shape forever, for a seam
whose whole justification is internal testability, and §8 treats the package surface as a deliberately
narrow thing. The distinction that matters is between reaching *at* a module's interface and reaching
*past* it: internal plus `InternalsVisibleTo` is the former. What is being fixed is the present
situation, where tests reach past the grid into `columns` and `sorts` because there is no interface at
all.

### The pass crosses by ref, and does not travel further

`ref DrawPass<TItem>` - §15's candidate 2 built it as a plain mutable struct for exactly this.

It stays a field on the grid and is passed explicitly **only** across this seam. The render tree is
untouched and `TotalCount()` keeps one signature, which matters because it has callers on both sides of
the render - the pager and `aria-rowcount` inside it, the keyboard cursor outside. Threading `ref pass`
through `RenderGrid`, `RenderPager`, `RenderHead`, `RenderBody` and `RenderRow` would be a large diff
through the hottest code on the branch to buy what this already has.

So the honest claim after this lands is that the memo no longer crosses a module edge - not that the
grid has no ambient state left. `ActiveFilters` still reads the field.

### How it is verified

§9's four layers, and specifically:

1. **`InMemoryCompositionTests` is rewritten at the seam** and its two-`TestContext` diff deleted. Two
   calls with the same arguments and different options, compared directly.
2. **`FilterCompositionTests` stays as it is.** It is integration coverage and catches a different class
   of fault; it should stop *growing*, not be replaced.
3. **One new DOM-level test that the grid actually calls the module.** Without it the module can be
   correct and unused and everything stays green - and this branch's recorded failure mode is silent
   wrong answers, which is exactly that shape.
4. **Every new test mutation-checked**, and the mutation must compile: piece 1 and piece 2 each found a
   test that passed with the rule it named deleted, and piece 2 found two branches no caller could
   reach with a different answer.
5. **A `gridbench --job short` control before and after**, on `*FastGridFeatureBench.Bare`,
   `*SingleSort`, `*Filtering`. The claim is allocation-neutral. Take no time ratio from that job
   length - the errors are wider than the differences.

Expect **31 of the 32 test files that touch filtering, sorting or composition to be untouched** (it was
30 - the addendum has why, and the boundary held). If
they are not, the boundary is wrong.

### The order it lands in

One commit, with the spec updated inside it rather than trailing - which is what the rest of this
branch does, and a trailing docs commit is how a section ends up describing something that changed
under it.

**Candidate 5 follows this, not the other way round.** The four `Apply*` methods and their four
different meanings of `null` are precisely what `Composition` consumes, so changing their return shape
once the calls live in one module is a change to one caller instead of scattered ones.

### Where this could still be wrong

- **The module may end up shallow.** Five parameters is not a small interface, and if `Compose` turns
  out to be the only thing anyone calls, `Composition` is a namespace with one function in it rather
  than a module. The test for that is whether `Composed<TItem>` earns its place: if callers only ever
  read `Rows`, the route flag should have stayed a field and this was a rename.
- **`sorts` has no type yet.** It is a list on the grid whose element type this section has deliberately
  not named, because naming it is the first thing the build will have to decide and guessing here would
  be the kind of recorded decision §10b warns about.
- **The memo and the module may not want the same lifetime.** `DrawPass` is a render pass; the
  composition is asked for outside a render too - by the click resolver, by the keyboard cursor, by the
  virtualized items provider. Passing `default` there is correct and cheap, but if it turns out most
  calls pass `default`, the pass is a parameter three callers carry for one caller's benefit.

### What the build changed

Built as `Composition.cs`, 434 lines. 285 lines left `RadzenFastGrid.Data.cs` and 24 came back - the
forward that records the route, the options value, and the call sites that now name the module. Five of
this section's decisions did not survive the building, one of its own tests did not discriminate, and
the mutation that caught that one went on to find a gap in the suite older than this piece.

**The surface is six entry points, not one.** `Compose`, `Filter`, `Sort`, `Filters`, `DeclaredFilters`
and `ActiveFilters`, and each has a caller: `LoadPageAsync` filters and sorts a queryable in two steps
because it counts between them, `ProvideRowsAsync` filters without ordering because an ordering inside
a count aggregate is not translatable, and three places ask what the columns are filtering by. That was
visible in the code and not in the move list above, which itemised seven methods and no callers. It
settles this section's first "where this could still be wrong" in the module's favour: `Compose` is not
the only thing anyone calls, so `Composition` is a module and not a namespace with one function in it.

**`ComposedInMemory` did not stop existing**, as marked above. The alternative was to delete it and move
`FastGridSortByTests`' three route assertions to the seam - which would have moved a second test file,
and this section's own boundary check is that one moves.

**`ActiveFilters` moved with it, and the pass crosses at two entry points rather than one.** This
section said "`ActiveFilters` still reads the field", and the grid's does - it reads `pass` in order to
hand it over. But the *rule* it encodes, that the filters in force are the pass's while drawing and the
declared ones otherwise, is a composition rule and now lives with the composition. Both crossings are
the same seam; what this section ruled out was threading the pass through the render tree, and that is
still ruled out. `DeclaredFilters` exists because opening a pass and asking outside one were one
question written twice, as two spellings of `AllowFiltering ? ... : null` in two files.

**The pass memo carries the route, not just the rows.** `DrawPass<TItem>` memoizes `Composed<TItem>`
now. It has to: `Reuses` hands the second caller of a render the first caller's answer, and an answer
that dropped the route would give the grid the right rows beside a wrong account of how it got them.
Which caller composes first does not matter - the first composes and every one after is answered from
the memo, so the memoized route is always the last thing written. That is reachable in a plain grid, a
filtered and paged list where the body enumerates and the pager counts the same instance, and it is
pinned at all three levels: the memo, the module and a rendered grid. This is the second existing test
file to move, `DrawPassTests`, against the one this section budgeted for - so the count is **30 of 32
untouched**, and the boundary is right even though the number was not.

**`sorts` is the tuple it already was**, `IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)>`.
Naming that pair would be candidate 7 decided as a side effect of moving the pipeline, which is the
argument this section already makes about narrowing what the module sees of a column.

**`Filtered` is gone.** It appeared in neither list above, being two lines. Once both of them were calls
to the module it was a forward with one caller whose guard could be read for the first time - and the
guard was worse than nothing: it built and discarded a descriptor list to decide whether to call a
function that returns its argument untouched when there is nothing to filter. Its one caller calls
`Filter`.

### A mutation caught a test of this section's own, and then a gap older than it

The first pair of `CompositionSeamTests` compared the grid's route against the module's over a list and
over a queryable, and **passed with the grid answering the route from the shape of its source** -
`data is not IQueryable<TItem>`, which is what the flag looks like it means and agrees with the module
in both of those cases. What separates them is a list whose column *cannot* compose in memory: the
source is a list and the route is not the in-memory one. With that third case the mutation fails, and
fails only there. §9's first layer earning its place for the third piece running.

**And then the gap.** Removing the `AllowFiltering` gate from `DeclaredFilters` also passed the whole
suite - and that is not a cost bug. `ComposeInMemory` builds its predicate from whatever the columns
report and never re-asks, so a grid with filtering switched off and a column still carrying a filter
value would have been filtered. One gate asks, nothing downstream re-asks, and **nothing said so**:
§10b's recurring shape, found here only because moving the code put the gate and its neighbour in one
file. `WithFilteringOffAColumnCarryingAFilterDoesNotFilter` is what says it now.

**Eight mutations, six caught**, each compiling: the in-memory route reporting the wrong flag (10
tests), the memo dropping the route (3), the grid answering the route itself (1), a declining column no
longer handing the composition back (1), the nothing-to-do path claiming a route it did not take (1),
and the filtering gate removed (1). The two that survive are recorded rather than counted:

- `ActiveFilters` dropping its `pass.Drawing` term passes, and should. Within a pass the descriptors
  cannot change, so that memo is a cost and not a correctness rule; `DrawPassTests` pins the mechanism
  instead of the outcome.
- `Reuses` dropping `reused.Rows is not null` passes, because `Keep` writes both fields together. That
  redundancy is older than this piece and is left exactly as it was found, rather than removed on the
  strength of an argument about callers that a future `Keep` would not be bound by.

**Measured**, `--job short` at 1000 rows, one run before and two after: bare 154.55 KB -> 154.55 and
154.66, one sort 175.83 -> 175.93 and 175.79, a filter row 158.76 -> 158.77 twice. Allocation-neutral,
which is what was claimed; the sort row straddles its own before-value, and the bare spread of 0.11 KB
is the same noise floor piece 2 recorded. **No time ratio is quoted**, per §9.

**One cost is carried through rather than introduced, and is worth naming now it is in one place.**
`Compose` derives `filtering` from `ActiveFilters(...) is not null`, which outside a render builds and
discards a `List<FilterDescriptor>` per call. Inside a render it reads the pass and allocates nothing,
which covers every per-row path; the calls that pay are the asynchronous ones. Candidate 5 is where the
four `Apply*` return shapes get decided, and this is the same question from the other end.

---

## 17. Four methods of one shape, four meanings of `null` - the design

§15's fifth candidate, argued before it is built, and in the order §16 set: the four `Apply*` methods
are precisely what `Composition` consumes, so changing what they mean is now a change to one caller
rather than to scattered ones.

Everything below about the present code was checked by running it, not by reading it. Three of the
findings are recorded here because a probe answered them, and two of those three are the reason this
section is worth building at all.

### What it is for

Six methods on `ColumnBase<TItem>` return something-or-`null`: `ApplySort` and `ApplyThenBy`,
`ApplyFilter`, `ApplyFilterInMemory`, `ApplySortInMemory` and `ApplyThenByInMemory`. `null` means "I
cannot" in all six, which is one contract. What is *done* about it is four different things, and all
four live in the caller:

| The column declines | What `Composition` does | Where |
| --- | --- | --- |
| `ApplyFilter` | this column alone falls back to the reflective builder | `Composition.cs:149` |
| `ApplyFilterInMemory` | the whole composition goes back to the expression route | `:290` |
| `ApplySort` | the column is left out of the ordering, the rest of it stands | `:196` |
| `ApplySortInMemory` | back to the expression route, but only for the first column | `:320` |

**Those are not four contracts. They are two rules, one per route, and the routes are what differ.**
The expression route can absorb a decline, because it has somewhere to put it - reflection for a
filter, omission for a sort. The delegate route has nowhere, so it hands the whole composition over -
while handing over is still possible. For filtering it always is: the predicate has been built and not
yet applied. For ordering it stops being possible the moment an ordering has begun, because a
half-applied `IOrderedEnumerable` cannot be given to the other route - so the first column can send it
back and a later one is left out, which is what the expression route would have done anyway.

Said once each, that is two sentences. Said four times inline and nowhere at the declarations, it is
what the next three findings are.

### Three findings, each from a probe rather than a reading

**1. One of the four doc comments states the wrong one of the four contracts.** `ColumnBase.cs:948-951`
says of `ApplySortInMemory`:

> Orders an in-memory sequence by this column, or returns null when it cannot order - **the same
> contract as `ApplySort`, which the grid already skips over.**

It is not the same contract. A first column that declines does not get skipped over; it sends the whole
composition to the other route. And the first column is not an edge case - it is the only column a
single-column sort has, which is most grids. An author of a new column type reading that sentence would
be wrong about the common case. §10b already has the rule this breaks: *a rule stated in a comment is
only as good as the comment*, and this is the second instance of it on this branch.

Behaviourally this is currently harmless, and the reason is worth recording because it is what hid it:
**no column can decline in memory and succeed on the queryable route.** Every column guards both of its
sort methods on the same thing - `PropertyColumn` on `!CanSort || (SortBy ?? Property) is not { }`,
and `TemplateColumn`, `CollectionColumn` and `LookupColumnBase` on `SortBy?.` - so abandoning the route
produces the same rows more slowly rather than different rows. The four contracts differ; the columns
that would make the difference visible do not exist. That is the definition of a fault waiting for its
first caller.

**2. The guard has a conjunct that can never discriminate.** `Composition.cs:326` reads:

```csharp
if (next is null && ordered is null && i == 0)
```

`ordered` is assigned only at the foot of the loop and starts null, and this very `return` is what stops
the loop reaching `i == 1` with `ordered` still null. So `ordered is null` and `i == 0` are the same
condition, and the guard reads as three tests where there are two. **Both halves were removed
separately and the suite passed both times.** It is the shape piece 2 found twice - a rule that looks
like it is enforced twice and is enforced once - and it is a shape a reader cannot tell apart from the
queryable loop twenty lines above, where `ordered is null` is *not* redundant, because there a declining
first column leaves `ordered` null and the loop carries on.

**3. Two of the four contracts have no test at all.** Removing the decline rule entirely - so that any
declining column, not only the first, abandons the delegate route - **passes the whole suite**. So does
making a declining column abandon the *expression* route's sort rather than being skipped. Two of the
four rows in the table above are unpinned. The other two are well covered: the in-memory filter decline
fails five tests when it is broken, and §16's own work pinned the route flag.

### What changes

**The rule moves to where it is enforced, once per route, and the declarations point at it.** Four
restatements of a policy the declarations do not control is how one of them came to be wrong; the fix is
not to correct the wrong one and leave four, it is to have one. `ColumnBase`'s six methods say what
`null` means *for the column* - "I cannot" - which is the part a column author owns and the part that is
identical in all six. What is done about it is `Composition`'s, is stated there per route, and is
referred to rather than reproduced.

**The dead conjunct goes**, with the invariant that made it dead stated in its place - including that
the invariant is created by the `return` beside it, so that removing the return does not silently make
the remaining test wrong.

**The two unpinned contracts get a test each**, mutation-checked, and the mutation must compile:

- a delegate-route sort where a *later* column declines, which must be left out while the composition
  stays on the delegate route;
- an expression-route sort where a column declines, which must be left out while the other columns'
  ordering stands.

Both need a column that can sort on one route and not the other, and no such column exists today - see
finding 1. So both tests need a column type that does not ship, which is the same shape as
`Attachment`'s fake adapter in candidate 4: a test double that exists to make a rule reachable. That is
the piece's one real risk and it is in "where this could still be wrong" below.

**No column changes.** Not one of the six overrides gains or loses a line.

### Deliberately not proposed

- **A stated capability - `ComposesFilter`, `ComposesSort` - replacing the nullable returns.** It reads
  well and does not survive contact. The guards are null tests the compiler needs for flow analysis
  (`(FilterBy ?? Property) is not { } selector`), so a capability property makes the method re-assert
  what it just asked with a `!`, trading a check for a suppression. It also turns one call into a
  two-call protocol, which is §15 candidate 6's complaint about `ColumnBase`'s internal half arriving in
  its public half. And it does not fix the fault: the four caller policies would remain, keyed on a bool
  instead of on a null.
- **One sort loop over both routes.** The two loops are the same loop apart from their types and their
  decline rule, but `IOrderedQueryable<T>` and `IOrderedEnumerable<T>` share no interface, so unifying
  them means a route abstraction - a constrained generic struct, to stay inside §3 - threaded through
  the hottest composition path to save about twelve lines. It would make the code harder to read to
  remove a duplication that is two loops long. Refused, with that as the reason.
- **Changing what any of the four rules *is*.** Each was argued where it stands and §15 and §16 record
  the arguments; this section moves where they are said and pins two of them, and changes none of them.

### How it is verified

§9's four layers, and specifically:

1. **The two new tests must fail without the rule they name**, checked by a mutation that compiles.
   Both rules are currently unpinned, so the mutation is simply the code as it stands today with the
   rule deleted - which is the strongest form of this check available, because the "before" is known to
   pass.
2. **The doc fault gets no test**, and that should be said plainly rather than worked around: a comment
   cannot be pinned by a test. What can be pinned is the behaviour it misdescribes, which is what the
   two new tests do. The comment is fixed by having one of it rather than four.
3. **A `gridbench --job short` control before and after**, on `*Bare`, `*SingleSort`, `*Filtering`. The
   claim is allocation-neutral, and it should be trivially so: no column changes and no allocation is
   added or removed. Control at `afb05de33` is bare 154.55 KB, one sort 175.79 KB, a filter row
   158.78 KB. No time ratio from that job length, per §9.
4. **Expect the existing test files to be untouched.** This piece adds tests and moves no boundary, so
   unlike §16 there is no file that has to move. If one does, something bigger happened than was
   designed.

### Where this could still be wrong

- **The test double may be the whole piece.** Both new tests need a column that sorts on one route and
  not the other, and none exists. If writing that double turns out to be most of the work, the honest
  reading is that these two rules are unpinnable *because nothing can reach them* - and the better
  answer is the one piece 2 reached: a rule no caller can reach with a different answer should stop
  being a branch. That is a stop-and-re-decide point. It would turn this piece into a deletion, and a
  deletion with a measurement behind it is a better outcome than two tests over a double.
- **"State it once" is still a comment.** The fix for a comment that was wrong is a comment that is
  right, in one place instead of four. That is better and it is not structural, and §10b's rule says
  comments rot. The structural version is the capability property, which the section above refuses on
  three grounds - so this piece is deliberately choosing the weaker mechanism, and should say so rather
  than claim more.
- **Finding 1 may deserve to be resolved the other way.** If `ApplySortInMemory`'s doc is what the
  design *should* say - a declining column is skipped, on both routes - then the code is what is wrong,
  and the delegate route should skip a declining first column rather than hand the composition over.
  That is a smaller diff than this section proposes and a real behaviour change: it would keep a grid on
  the fast route where it currently leaves it. It is not proposed here because handing over is what
  §16's `AColumnThatCannotComposeSendsItBackToTheOtherRoute` pins and what the in-memory filter rule
  does two loops earlier, so changing it would split one route's behaviour in two. But it is the
  argument this section is least sure of.

### What the build changed

All three findings landed. Four of this section's own claims did not survive the building, and two of
its new tests had to be corrected before they tested anything - one caught by a mutation and one by
review.

**The stop-and-re-decide point did not fire, and the reasoning behind it was wrong.** This section
expected both new tests to need "a column that can sort on one route and not the other", said none
exists, and set that as the point to stop at. It was the wrong question. The tests are about what the
*caller* does with a decline, so a column that declines on **both** routes is all they need - and one
ships: a `TemplateColumn` told a `SortProperty` and no `SortBy` has `CanSort` true and returns null from
all four sort methods. Probed before building. That settles the first "where this could still be wrong"
in the good direction: these rules were untested rather than untestable, and the deletion this section
held open as the better outcome is not the outcome.

**A mutation caught the first of the two new tests not discriminating**, which is the third piece
running that this has happened on. The expression-route test asserted the rows came back ordered
*ascending* by the composing column - which is the order `People.Many` builds them in, so it was an
assertion an unsorted source also satisfies, and the mutation that abandons the whole ordering passed
it. Both tests sort descending now, and the helper that builds their sort list carries the reason,
because the trap belongs to the fixture rather than to either test.

**And review caught a third rule this section had asserted and not pinned.** The expression route's
loop asks whether an ordering has *begun*, which is deliberately not the same question as "is this the
first column" - and it is the difference between the two loops that this piece exists to make legible.
Both new tests put the declining column second, which only ever reaches the other branch. So a
*first*-column decline on the expression route, where the second column must start the ordering rather
than append to one, is a third test; the mutation that collapses that test to `i == 0` fails it and
nothing else.

**Four contracts, and now the branch that separates them - five tests, each failing alone.**

**The dead conjunct is gone**, and what replaced it is longer than what it removed: `i == 0`, the
invariant that makes it sufficient, and the warning that the invariant is manufactured by the `return`
on the next line. A conjunct that cannot discriminate is worth removing; the reason it could not is
worth keeping, because the next reader's instinct will be to restore it after reading the loop above,
where the same test is not redundant.

### Three corrections to what this section proposed for the documentation

**"Stated once" was written three times.** The first attempt put the rule in `Composition`'s class
remarks, restated it in `Sort`'s remarks and restated it again at the delegate route's guard - a
document whose thesis is that a consequence written where it is not enforced is a consequence nothing
keeps honest. The two loops now carry only what is local to them, and the four decline sites carry a
one-line pointer each.

**The public half and the internal half own different halves of the rule.** `ColumnBase` is shipped API,
and the first attempt told its readers the consequence "is stated once in `Composition`" - a type they
cannot see, in a package that does not expose it. `ColumnBase` now states what declining *means* and
what it costs, which is what an implementer needs; `Composition` states what follows per route, which is
what a maintainer needs. Neither restates the other. The repo history and the §-references came out of
the shipped docs at the same time: they resolve to nothing for a consumer.

**And the replacement was nearly wrong in the same way as the original.** A draft of the new remark told
column authors that "both routes produce the same rows whichever way a decline falls out; what differs
is cost" - which is true of today's columns and is not the contract, and which deleted the sentence at
the guard that carries the actual reason for the rule: **the other route may not decline where this one
did, and that is a different answer rather than a slower one.** That is why the delegate route hands the
composition over rather than simply leaving the column out. It is restored, and the symmetry that makes
declining currently free is stated as a property of these columns rather than of the arrangement. A
section whose finding is a wrong doc comment came within one commit of shipping a wrong doc comment.

**Measured**, and this is the one piece whose claim was that the measurement should be uninteresting.
Control at `afb05de33`: bare 154.55 KB, one sort 175.79 KB, a filter row 158.78 KB. Two runs after:
154.55 and 154.55, 175.79 and 175.79, 158.77 and 158.91. No column changed and nothing was added to any
path. **No time ratio is quoted**, per §9.

That filter row is worth a sentence, because it measures the instrument rather than the change. The two
after-runs are of **the same executable code** - everything that changed between them is comment text -
and they differ by 0.14 KB. So a 0.14 KB reading on that row is the noise floor and not a cost, which
until now had only been inferred from piece 2's 0.11 KB spread on the bare row. A run pair that is
identical by construction is the cheapest way to measure that, and is worth doing deliberately the next
time a piece needs to defend a small number rather than only when one falls out.

**No existing test file moved**, as designed: three tests were added to `InMemoryCompositionTests` and
no other suite was touched.

### What this piece is, honestly

The second bullet under "where this could still be wrong" stands as written, and reads better as a
summary of the piece than as a caveat to it: **the fix for a comment that was wrong is a comment that is
right, in one place instead of four.** That is better, and it is not structural, and the structural
alternative is still refused above on three grounds.

What is structural is the rest: a conjunct that could never discriminate is gone, and three rules that
nothing held now have a test each. Those survive a comment rotting. §15 rated this candidate Strong for
the four-meanings observation; what it was worth was one wrong statement, one dead branch and three
unpinned rules - a smaller thing than the ranking implied, and worth saying in case the ranking is used
to choose what comes next.

---

## 18. The browser seam has no interface - the design

§15's third candidate, argued before it is built. Four things in this section were checked by running
them, and three of those four contradict what §15 said about this candidate. That is the most useful
part of the section and it is first.

### What §15 got wrong about it, and how that changes the piece

**§15: "Seven of the nine exports have zero coverage of any kind."** Four do, not seven. Counting test
references per export: `autoFit` 41, `attach` 5, `detach` 2, `attachNavigation` 2, `detachNavigation` 2,
and then `measureNavigation`, `focusCell`, `blurCell` and `releaseFit` at zero. Two of the covered five
are covered thoroughly.

**§15: "because the doubles answer `null` the RTL arrow flip at `Keyboard.cs:313, 318` is never executed
by any test."** The first half is a fact about the tests that exist and the second half is a diagnosis,
and the diagnosis is wrong. The flip is reachable now, with machinery already in the suite:
`FastGridAttachmentTests.cs:38` already stages a `NavigationMetrics` through bUnit's module double.
Staging one with `Rtl = true` and pressing an arrow was run while writing this section, and the flip
executes - ArrowRight moves the cursor from cell 0 to cell 1 under LTR and leaves it at 0 under RTL,
ArrowLeft does the reverse. `NavigationMetrics` is `internal` precisely so a test can do that, and its
own doc comment says so. **Nobody wrote the test.**

**Which means the strongest plank under this candidate does not hold.** "There is no way to reach this
in process" was the argument for an abstraction with a fake behind it, the way `Attachment` has one.
There is a way, it is the one bUnit already provides, and it reaches every export. What is missing is
tests, and tests do not need a new seam to be written.

**What §15 got right, and it is the half no test can reach:** sixteen named entry points carrying
forty-two positional arguments - nine module exports with twenty-six, two stock-`Radzen` calls with
seven, five `[JSInvokable]` callbacks with nine - and an undeclared half that appears in no signature at
all.

### What is actually wrong, then

**1. Ten positional arguments, decoded by position in three places.** `autoFit(tableId, indices,
minWidths, maxWidths, toggleOffset, bare, wait, animate, overflow, required)`. C# writes them in order,
the script reads them in order, and `FastGridAutoFitTests.cs:39-49` reads them in order a third time -
it has already written the type that wants to exist, as `record Ask(string Table, int[] Indices,
string[] Min, string[] Max, int ToggleOffset, int Bare, bool Wait, bool Animate, string Overflow,
bool[] Required)` plus a hand-rolled decoder over `invocation.Arguments`. **A test that decodes by
position has the caller's bug in it**, so this is the one hazard here that writing more tests cannot
touch: swapping `minWidths` and `maxWidths` would be silent in all three.

**2. The export names are strings on three sides.** The C# call site, the JS export, and the test's
`module.Setup("autoFit")`. Renaming two of the three leaves the third passing, because a module double
in loose mode answers a name it was not set up for with a default.

**3. The undeclared half.** `tr[data-r]`, `[data-toggle]`, `tr.rz-data-row`, `.rz-cell-data`,
`.rz-state-focused`, `.rz-column-title`, `:scope > table > tbody > tr`, `:scope > colgroup`,
`:scope > thead > tr`. The script selects on them; `RadzenFastGrid.cs` emits them as string literals
several hundred lines away; nothing in either file mentions the other. This is the real content of "the
seam has no interface", and it is what a rename breaks silently.

**4. The ordering constraints are prose, in a different file from the calls they govern.**
`Data.cs:952-992`: pagers before clicks, fit before focus, fit after the names, focus last, and in
teardown detach before release before dispose. §13 already recorded that swapping two of them would
measure blank cells and every test would still pass.

### The surface

A concrete façade, not an abstraction - the distinction matters and the probes above are why.

```csharp
internal readonly struct Browser
{
    internal Browser(IJSObjectReference module);

    internal ValueTask<bool> AttachAsync(string bodyId, DotNetObjectReference<...> handler, string[] kinds);
    internal ValueTask DetachAsync(string bodyId);
    internal ValueTask<NavigationMetrics?> AttachNavigationAsync(string viewId, string[] keys);
    internal ValueTask DetachNavigationAsync(string viewId);
    internal ValueTask<NavigationMetrics?> MeasureNavigationAsync(string viewId);
    internal ValueTask FocusCellAsync(string viewId, int row, int cell, int pinnedStart, int pinnedEnd, int itemSize);
    internal ValueTask BlurCellAsync(string viewId);
    internal ValueTask ReleaseFitAsync(string tableId);
    internal ValueTask<string?[]?> AutoFitAsync(AutoFitAsk ask);
}
```

A `readonly struct` over the one module reference, so §3 is satisfied by construction: it is a wrapper
around a field, not an object per call, and every one of these is called once per attach, per fit or per
focus rather than per row.

**No interface and no fake.** An `IBrowser` with a test double would buy reach the suite already has,
and would put a second implementation of nine methods in the test project to be kept in step with the
script by hand - which is the thing that goes wrong here, done twice. `Attachment` earns its two
delegates because it has rules of its own to test; this has none. It forwards.

**`autoFit`'s ten arguments become one value**, on both sides:

```csharp
internal readonly record struct AutoFitAsk(string Table, int[] Indices, string[] Min, string[] Max,
    int ToggleOffset, int Bare, bool Wait, bool Animate, string Overflow, bool[] Required);
```

which is `FastGridAutoFitTests`' own `Ask`, promoted out of the test and into the thing it describes.
The script destructures one object instead of counting ten places, and the test reads
`invocation.Arguments[0]` as the record instead of casting ten elements - so its decoder is deleted
rather than rewritten. That is the whole of hazard 1, and the only change to the JS file's own logic.

**The DOM contract gets named once and pinned.** A `BrowserContract` of the eight selectors and
attribute names the script depends on, and a test asserting that a rendered grid carries each of them -
so a rename in `RadzenFastGrid.cs` fails a C# test instead of a browser. The script cannot import the
constants, so the two sides still agree by hand; what changes is that there is one list to check against
rather than a search through a thousand lines of JS.

### What changes and what does not

**Changes:** `Browser` and `AutoFitAsk` are new. Six call sites stop naming exports and counting
arguments. `fastgrid.js` changes in one function, `autoFit`, and only its parameter list.
`FastGridAutoFitTests`' `Ask` and `Read` are deleted in favour of the real type. Four tests are added
for the four uncovered exports, and one for the RTL flip that §15 said was unreachable.

**Does not change:** the two stock-`Radzen` calls, which are upstream's interface and not this
package's to name. Every `[JSInvokable]` callback. `Attachment`, which keeps its two delegates - it is
constructed with them by its callers, and those callers can hand it `Browser`'s methods without
`Attachment` knowing what a module is. No behaviour anywhere.

### Deliberately not proposed

- **An `IBrowser` and a fake.** Refused on the evidence above: the reach it would buy exists, and the
  cost is a second nine-method implementation that has to track a script it cannot see. If a rule ever
  lands *in* this seam rather than passing through it, that is when it earns an abstraction, and
  `Attachment` is the precedent for how.
- **Structuring the ordering constraints.** They are real and §13's finding stands, but every way of
  making them structural - a named sequence, a phase enum, a builder - moves five awaits in
  `OnAfterRenderAsync` behind something that has to be read to know what it does, which is worse than
  five awaits with a comment. What would actually pin them is a test that observes the order, and that
  needs the browser rather than a double. Left for candidate 3's second half or for §13 to answer.
- **Generating the DOM contract from one source.** The two sides are C# and JavaScript, and there is no
  build step here to generate either from the other. Adding one to a package whose whole claim is that
  it is a plain library is a bigger price than the hazard.

### How it is verified

§9's four layers, and specifically:

1. **The four uncovered exports get a test each**, and the RTL flip gets the one §15 said could not be
   written. Each mutation-checked, and the mutation must compile.
2. **The DOM contract test must fail when the markup drifts**, which is checked by renaming each emitted
   literal in turn - eight mutations, and any that passes means the contract lists something the test
   does not really assert.
3. **`autoFit` must still be asked for exactly what it was asked for before.** Its 41 existing test
   references are the regression suite for the argument change, and they should need only the decoder
   swapped, not their expectations.
4. **`GeometryParityTests` is the one layer that runs the real script** - 38 tests against Chromium - so
   the `autoFit` parameter change is not a C#-only claim.
5. **A `gridbench --job short` control before and after.** Allocation-neutral: one struct over a
   reference, one record per fit, nothing per row. Control at `7e05bc199` is bare 154.81 KB, one sort
   175.79 KB, a filter row 158.78 KB - and note that bare has read 154.55 and 154.81 on runs of
   identical code, so the floor on that row is at least 0.26 KB and a difference smaller than that is
   not a difference.

### Where this could still be wrong

- **This may be two pieces.** The façade and `AutoFitAsk` are one argument; the DOM contract is another,
  and it is the one with an unknown size - eight names is the count today, and finding the ninth is what
  the work consists of. If the contract list grows past what one test can honestly assert, it should
  land on its own and the façade should go first.
- **The façade may read as ceremony.** Nine methods that forward to nine `InvokeAsync` calls is a thin
  thing, and thin wrappers are how a codebase acquires a layer nobody wants. The defence is that it
  makes each export name and each argument list exist exactly once, which is the hazard - but if the
  built version does not visibly reduce what a call site has to know, it is a rename and should be
  called one.
- **The correction to §15 may be too kind to the tests.** Reach and coverage are not the same thing:
  every export being reachable through a string-keyed double is a weaker property than a typed seam,
  because the double answers a misspelled name with a default rather than an error. `JSRuntimeMode.Loose`
  is what makes the suite tolerant, and it is set in almost every test file here. The piece does not
  change that, and probably something should.

### What the build changed

The facade landed as designed and is not a rename - the verdict this section asked for is below. Four of
its own claims were wrong, one of its constants was the mistake it warns other people about, and typing
the seam turned up something no reader had noticed.

**The quoted surface is not the built surface.** `Browser<TItem>` is generic, not `Browser`: the answer
crossing the seam is `RadzenFastGrid<TItem>.NavigationMetrics`, so the type parameter follows it in.
`AutoFitAsk`'s sequences are `IReadOnlyList<>` rather than the array-and-`List` mixture the test's own
`Ask` had, which is why that test said `Length` for three of them and `Count` for the fourth. And a
third type is new that this section did not name, `ClickKinds` - the click attach was already passing an
object rather than three arguments, hand-written camelCase at the call site; naming it is the same
change as the others and it removes the hand-written casing.

**Typing the seam found a `float` nobody had noticed.** `focusCell` takes six numbers and one of them is
`Virtualize`'s row height, which is a `float` and is multiplied by a row index to get a scroll offset.
The untyped call took it without comment and a reader counting six numbers had nothing to say one of
them was not a count. It is `float` in the signature and the reason is written beside it. Nothing was
broken; it was unsayable.

**One constant was exactly the mistake this section is about.** `BrowserContract` shipped a
`FocusedClass` for `rz-state-focused` - which the grid does not emit at all. The script *writes* it, so
no rendered-grid assertion was possible and none existed, and renaming it would have been silent in a
list whose entire purpose is that renaming is not silent. Review caught it. `ViewClass` went the same
way for a milder reason: the script is handed the view's **id** and never selects it by class, so the
class is how a *test* finds the view and belongs in the test. What is left is names the grid emits and
the script selects, and every one has an assertion. `:scope > thead > tr` and `:scope > tbody` were
added, having been in this section's own list of the undeclared half and left out of the built one.

**The list is still not the whole list, and should be read that way.** `closest('td')`,
`closest('tr[data-r]')` and `:scope > table > tbody > tr` are structure the script depends on that no
constant names. What `BrowserContract` is for is the names a rename could quietly change; what it is not
is a complete description of the DOM the script walks.

**The two sides of the ask are checked from both directions, and the C# side is not checked at all -
deliberately.** `autoFit` taking one object trades a positional coupling for a naming one: the record
serializes camelCase and the script destructures by name. So one test reads the script off disk, parses
what it destructures, and compares that to what the record serializes to - as sets, both ways, because a
field C# sends that the script ignores travels on every fit and a name the script takes out that C# does
not send is `undefined` inside a measurement. Renaming on the script side fails it. Renaming on the C#
side **does not compile**, because the tests read the properties, which is the better of the two and is
why nothing here tests it.

**And the harness had a positional call left in it.** Converting the nine `autoFit` calls in
`measure-geometry.js` missed a tenth, which then received a string where an object goes, destructured
nothing out of it, and returned `null` - **and the parity suite passed anyway.** Which was worth
following: `Fitting_the_container_leaves_room_for_the_columns_it_is_not_fitting` passes with no fit
performed at all. Its container was 700px and the table measured 698 unfitted, so it was already
fitting; the reserved column is 220px because the harness sets it there; and both of its assertions are
satisfied by a table the browser laid out on its own. The scenario's own comment names the band it
needed to sit in and 700 was outside it. That is a fault in a §13 test rather than in this seam, and it
lands in the commit after this one.

**Measured**, control at `bc202edc8` bare 154.81 KB, one sort 175.79 KB, a filter row 158.78 KB; after,
154.66, 175.90 and 158.77. Inside the floor this branch has now measured directly - the bare row has
read 154.55, 154.66 and 154.81 on runs of identical code. **No time ratio is quoted**, per §9.

### The verdict this section asked for

> "if the built version does not visibly reduce what a call site has to know, it is a rename and should
> be called one."

It is not a rename. What the call sites no longer have to know: nine export-name strings, the order of
`autoFit`'s ten arguments, the `InvokeAsync<NavigationMetrics?>` type argument written out three times,
and the camelCase spelling of an anonymous object. Each also lost a two-step - `var script = await
ModuleAsync(); if (script is null) return;` became `if (await BrowserAsync() is not { } browser)`.

**And the count is honest about what is left.** `ModuleAsync` still exists, because something has to do
the import and the disposer holds the module rather than reaching for it. Two ways in, one of which is
the facade; this section's first draft claimed there was one, and the disposer forty lines below said
otherwise.

### Where it is still weak

- **Reach is not coverage, and the piece did not change that.** Every export is reachable through a
  string-keyed double that answers a misspelled name with a default, and `JSRuntimeMode.Loose` is set in
  almost every test file here. Four exports have tests now that had none, and the RTL flip has the test
  §15 said could not be written - but a typo in a setup string still passes silently. That is the same
  hazard the facade fixed on the call side, unfixed on the test side, and it is the next thing to look
  at if this seam is opened again.
- **The ordering constraints are untouched**, as designed. §13's finding stands.

---

## 19. The drop-down adopts its value again on every render - the design

§15's eighth candidate, argued before it is built. That entry ranks it "worth exploring" and makes three
claims; one is a measured fault, one is wrong about which sources reach it, and one is not a fault at
all. The measurement is first, because it is what decides the piece.

### The fault, measured

`Adopt` finds the row a bound value names, so a closed drop-down renders text rather than a placeholder.
It runs on a value change **and on a data change**, deliberately - "a value is routinely bound before its
rows arrive". The data-change test is `!ReferenceEquals(lastData, Data)`, which §10 has already recorded
as true on every render for a source written in markup.

What it does then is `Data.FirstOrDefault(item => Equals(ValueOf(item), value))`, and `ValueOf` returns
`object?`. **So every element boxes**, which is §3's rule 5 - "a generic value must never be widened to
reach an interface" - on a path that runs once per parent render for a drop-down nobody has opened.

Measured over twenty renders of a closed drop-down whose `Data` is re-materialised each time, against
the same drop-down holding one instance:

| rows | re-materialising | stable source | the re-adopt |
| ---: | ---: | ---: | ---: |
| 50 | 4,843 B/render | 3,475 B/render | **+1,368 B** |
| 1000 | 27,646 B/render | 3,475 B/render | **+24,171 B** |

24,171 B over a thousand rows is 24 B an element, which is a boxed `int` exactly, and it is **seven
times the entire rest of the render**. The scan stops at the match, so this is the worst case - a value
whose row is last, or is not there at all, which is also what a `LoadData` page that does not contain
the value produces.

### What §15 got wrong about it

**"for exactly the sources this library targets"** - no. `Adopt` returns before scanning when
`Data is IQueryable && Data is not ICollection<TItem>`, with a comment saying why: walking a queryable
here would run an unfiltered, unpaged query on the render thread. So the Entity Framework source §10's
sibling findings are about is the one case that **cannot** reach this. What reaches it is an in-memory
sequence that is a new instance each render - `Data="@people.Where(p => p.Active)"` written in markup,
or a `ToList()` in a property. That is a real and ordinary way to write a lookup, and it is a narrower
claim than §15's.

**"twelve of its thirty-three parameters are one-line forwards, so a thirteenth is four places"** - the
count is twelve of thirty-two, and it is not a fault. Each is a documented, typed parameter of a
component in a shipped package; the alternative is `@attributes` splatting, which makes the drop-down's
surface undiscoverable to anyone reading its API, and this branch's §8 argument for a narrow deliberate
surface cuts the same way. Four places for a thirteenth is the price of saying what the component
supports. **Refused, with that as the reason.**

**`Grid => grid` exposing all 81 of the grid's parameters** is a real leak and is not this piece. Its
one caller is a test asserting the grid instance survives the popup closing, which wants "is this the
same grid" and not "here is the grid" - but narrowing it is an API change to a public member for the
benefit of one assertion, and it should be argued on its own rather than as a side effect of fixing a
scan.

### What changes

**Once the value is explained, a data change does not need to explain it again.**

```csharp
// today
if (!valueChanged && !dataChanged) return;

// designed
if (!valueChanged && !dataChanged) return;
if (!valueChanged && StillExplains(value)) { lastData = Data; return; }
```

where `StillExplains` asks whether what is already held answers the value - one `ValueOf` call for the
single case, and for `Multiple` whether every wanted value is already in `SelectedItems`. That turns N
boxes into one, and turns 24,171 B into about 24.

**It keeps the reason `Adopt` runs on a data change at all.** A value bound before its rows arrive is
not explained, so `StillExplains` is false and the scan runs - every render, until the row shows up,
which is exactly the behaviour that test exists for. What stops is re-explaining an answer already
found.

**The trade, stated:** the held row is an instance from the previous source. A source swapped for a
genuinely different one goes on showing the old row's text until the value changes or something calls
`Reload()`. That is the same lifetime rule §10 chose for the check-box lists and §14 for its lookups,
and it is chosen here for the same reason - the alternative is paying a scan per render to notice a
change that almost never happens.

### Deliberately not proposed

- **Typing `ValueProperty` as `Expression<Func<TItem, TValue>>`.** This is the real fix for the boxing:
  `TValue` is already a type parameter of the component, so the value's type is known, and §4's whole
  argument - that a typed expression beats a widened one, measured at 220 KB against 119 KB - applies
  here unchanged. It is refused *for now* because it is a breaking change to a public parameter on a
  shipped component, and because the piece above takes the same 24 KB to nothing without one. Recorded
  here so that it is a decision rather than an oversight: if the drop-down's surface is ever revised,
  this is the change to make, and it would also fix the `Multiple` path's boxing, which the piece above
  only avoids rather than removes.
- **Caching the adoption by key.** §10's answer for its sibling was "key it by `ItemKey`", recorded and
  not done. The drop-down has no `ItemKey`, and giving it one to avoid a scan it can already skip is a
  larger surface for a smaller gain.

### How it is verified

§9's four layers, and specifically:

1. **The measurement above, repeated after.** The claim is that the re-adopt column goes to roughly the
   cost of one `ValueOf` at both row counts. It is measured with
   `GC.GetAllocatedBytesForCurrentThread` around twenty renders rather than in `gridbench`, because
   what is being measured is a *re*-render of a closed drop-down and `DropDownBench` measures first
   renders. That measurement becomes a test, since a number nothing re-checks is a number that drifts.
2. **A test that a value bound before its rows arrive is still adopted when they do**, which is the
   behaviour the skip must not break, and which is the one thing this change could plausibly get wrong.
3. **A test that a genuinely changed source is not re-explained**, asserting the trade above rather than
   leaving it as prose - a stated consequence nothing checks is how §17's wrong comment happened.
4. **Every new test mutation-checked**, and the mutation must compile.
5. **A `gridbench --job short` control before and after** on the grid rows, which should not move at
   all: nothing here is on the grid's path. And `DropDownBench` itself, which measures the first render
   the skip does not affect - quoted to show it did not.

### Where this could still be wrong

- **`StillExplains` may cost more than it saves for `Multiple`.** Checking that every wanted value is
  held is a walk of `SelectedItems` and a set build, which for a large multi-selection is not obviously
  cheaper than the scan it replaces. If it is not, the honest answer is to skip only the single case,
  which is the common one and the one measured above.
- **The trade may be the wrong one.** "The source changed and the text did not follow" is a real bug for
  anyone who swaps `Data` for a different query and expects the label to follow. §10 and §14 both made
  this choice, so the branch is at least consistent - but three consistent choices are not evidence, and
  a user hitting it will not care which section it was recorded in.
- **The fault may be rarer than the measurement makes it look.** It needs a re-materialising in-memory
  source *and* a bound value *and* a parent that re-renders often. Each is ordinary; all three together
  may not be. The number is real; how often anyone pays it is not something this section can measure.

### What the build changed

The skip landed and the measured fault is gone, at a third of the reach this section designed for. Three
of its claims were wrong, one of them badly enough to change the shape of the piece, and two things it
went looking through turned out to be broken already.

**It skips a single value only.** This section designed the skip for both and named the escape hatch -
"if it is not [cheaper], the honest answer is to skip only the single case". Two things closed it. The
measurement: `Adopt`'s multiple path already `break`s once every wanted value is found, so over a
thousand rows it walks one element, and skipping it saves **136 B a render** rather than 24 KB. And the
correctness: the grid draws its ticks by asking a `HashSet<TItem>` whether it holds the row it is
drawing, and that set compares by reference - so rows carried over from a re-materialised source are
ticks that do not appear. Skipping would have made that permanent, because a selection that has gone
wrong still explains the value and so is never looked at again.

**And that fault is already there, without any of this.** Bound to two rows over a source that
re-materialises, the popup ticks 2 rows before and **0** after - and clicking an already-chosen row then
publishes **3** ids for two rows, because `OnRowClick` removes the new instance, misses, and adds it
beside the old one. Measured with the skip and without it: identical. So it is a fourth participant in
§10's `!ReferenceEquals` trap and the first whose symptom is a wrong value rather than a wasted query,
it is nothing to do with this piece, and it wants `ItemKey` - the same answer §10 recorded for row
expansion and did not do. **Recorded, not fixed here**, and the reason is that fixing it means deciding
the identity question §15's candidate 7 exists for.

**"Until the value changes or something calls `Reload()`" was false.** This component has no `Reload`.
The stale row is dislodged by a value change and by nothing else, and the comment says that now.

**The number is 96 B, not 24.** "Turns 24,171 B into about 24" was arithmetic about one `ValueOf` call
rather than a measurement; measured, the re-adopt column goes from 24,171 B to 96 B at a thousand rows
and from 1,368 B to 96 B at fifty. The claim - that it stops being a function of the row count - holds.

### Two mutations of this section's own, and what they found

**The allocation test discriminated at one of the two row counts it runs at.** It asserted the
re-materialising case costs less than 1.5x the held one, and at fifty rows the *unfixed* code is 1.4x -
which this section's own table already said, and neither the table nor the test noticed. The threshold
is 1.15 now, which fails at both, and there is a second test that has no ratio in it at all: a source
that counts how many times it is walked, asserting zero.

**A third redundant conjunct in three pieces.** `!valueChanged &&` came out of the guard because no
value that changed can be explained by the row that explained the previous one. `Multiple ||` stayed in
despite being redundant for the same kind of reason - `selected` is only ever written by the scalar
branch - and that one is kept deliberately, with the reason written beside it: the exclusion is a
correctness decision and leaving it to be inferred from which field happens to be set is how it would
quietly stop being true. Two redundant terms, opposite answers, both argued.

### Two measurements that had stopped measuring

**`DropDownBench` has been reporting `NA` for every `Fast_` row.** It passes `TextProperty` as the
string `"Name"`, which is what `RadzenDropDownDataGrid` takes and is not what this one takes, so every
run since that parameter became an expression has thrown a cast exception behind a printed table. Fixed
here because this section's verification asks for that bench to be quoted and a bench that throws cannot
be. What it says now, at fifty rows and at a thousand: **15.63 KB against `RadzenDropDownDataGrid`'s
168.6 KB closed, and 49.06 KB against 169.6 KB open** - 0.09x and 0.29x, and flat in the row count
because a closed lookup builds nothing.

**A test in the suite is flaky.** `ReviewRegressionTests.ACheckBoxListLookupIsNeverRunFromTheRenderThread`
fails about one full run in three, and fails every time it is run alone - at this commit and at the one
before it. Not this piece's, and recorded because "the suite is green" is a claim this branch makes
often and that test makes it conditional.

**Measured**, control at `6317cd150` sorted 175.79 KB and a filter row 158.77 KB; after, bare 154.66,
sorted 175.79 and a filter row 158.81. Nothing here is on the grid's path and the numbers say so.
**No time ratio is quoted**, per §9.

### What is left of the candidate

Of §15's three complaints about this component, one was a measured fault and is fixed for the case that
carried the cost, one is refused with its reason above, and one - handing out the grid - is untouched.
What this piece adds to the list is that the multiple path is *wrong* over a re-materialising source,
which none of the three noticed, and that the benchmark meant to catch component-level regressions here
has not run for some time.

---

## 20. `ColumnBase` asks the grid to know its recipes - the design

§15's sixth candidate, argued before it is built. That entry calls it "worth exploring", constrains it
by §3, and describes it as "seventeen members each answering exactly one grid call site". Two of its
three counts are wrong, and the diagnosis behind them is wrong in a way that changes what the piece is.
The corrections are first, because they decide it.

### What §15 got wrong about it

**`CellStyle` has no grid call site.** It is read three times inside `ColumnBase` - by
`FrozenCellStyle`, `FrozenHeaderStyle` and `FrozenFooterStyle`, each as the basis they fold an inset
into - and once by a test. Naming it in a list of members that answer the grid is what a count taken by
reading declarations rather than callers produces.

**"Nine of its twenty-one virtuals are `internal virtual`" is 8 of 28.** Nineteen are public virtual or
abstract, eight are internal virtual, one is protected virtual. The *substance* survives the correction
and is sharper for it: the eight are not scattered, they are exactly one feature - `NamesOutstanding`,
`FetchNamesAsync`, `DropNames`, `DefaultFilterOperator`, `FilterValueFromText`, `FilterValues`,
`FilterSelection`, `FilterValueFromSelection`. An out-of-assembly column can render, sort and compose a
filter *predicate*; what it cannot do is take part in the filter **row**. That is one closed door rather
than nine.

**`AutoFitWidth` having no reader is right**, and is the one claim in the entry that checks out exactly:
`internal string? AutoFitWidth => autoFitWidth;` is matched by nothing in the library, the tests or the
bench. It is the getter half of a field whose only readers are `EffectiveWidth` and `CanAutoFit`, both
of which read the field.

**And the diagnosis is wrong.** Seventeen members are not shallow because there are seventeen of them.
Counting members is how a shallow module and a wide one are confused. What is actually shallow here is
narrower and worse: at four call sites the grid holds a *recipe* rather than asking a question.

### The fault, stated as one sentence

**A column is drawn in four sections, and three of the four make the grid fold the frozen decoration in
itself.**

| section | the class the grid writes | the style |
| --- | --- | --- |
| header (`:1340`) | `column.FrozenClass is { } f ? headerClass + " " + f : headerClass` | `FrozenHeaderStyle` |
| filter (`:1538`) | `column.FrozenClass is { } f ? "rz-unselectable-text " + f : "rz-unselectable-text"` | `FrozenHeaderStyle` |
| body (`:1862`) | `column.CellElementClass` - the column folds it, memoized | `FrozenCellStyle` |
| footer (`:1169`) | `column.FrozenClass is { } f ? ... FooterCssClass + " " + f ...` | `FrozenFooterStyle` |

Four rows, and every column of the table is a rule written nowhere. That a frozen column contributes a
class *and* an inset is the grid's knowledge in three rows and the column's in one. That the filter row
uses the **header's** style rather than one of its own is the grid's knowledge in all four, and it is
the only place that fact is recorded - §10 has already paid for that: "there are **four** such sections
- the title row, the filter row, the body and the footer - and the filter row is a second row of the
header rather than a thing of its own, **which is how it was missed** after the title row was fixed."
The rule that was got wrong is the one this table's third column holds, and it is held at a call site
rather than in a type.

`CellElementClass` is the shape the other three want. It already exists, already memoizes on the pair it
folds, and already means "the class of this column's cell in this section". It is one row of a table
whose other three rows were written by hand at the point of use.

### What changes

Six changes, ranked by what each removes. None is on the per-row path and none adds an allocation to it;
the fourth and fifth remove members from four subclasses and add none.

**1. The four sections are four pairs, asked the same way.** `HeaderCellClass(string headerClass)`,
`FilterCellClass`, `FooterCellClass` join `CellElementClass`, each memoized against what it folds
exactly as `CellElementClass` is, and `FilterCellStyle => FrozenHeaderStyle` is where "the filter row is
a second row of the header" is finally written as code rather than as a comment two files away. The grid
asks each section for a class and a style and stops knowing that a frozen column is a class plus an
inset.

The memo is the existing one and not a new mechanism: the base classes it folds are interned literals in
three of four rows and `FooterCssClass` - a parameter - in the fourth, so a hit returns the same string
instance and a frozen column costs one string per section per grid rather than one per render. Today's
three inline folds allocate a string per frozen column per render; that is once per column and not per
row, so the change is not sold as a saving and the bench is expected to say so.

**2. `SetFilter` carries the text that produced the value.** Today it clears `AppliedFilterText`, and
two of its six call sites put the text back on the next line under a comment explaining that they must
(`Data.cs:608-610`, `:1214-1217`). One rule, written twice, in the places most likely to be copied from.
`SetFilter(value, filterOperator, text = null)` puts it in the signature; the four sites that want the
clear say nothing and get it.

**3. `OnParametersSet` is sealed, and derivation is a hook that runs before it.** Five classes override
it and every one of them is "do my own derivation, then call base" - with a comment in two of them
explaining that the order matters, because the base picks the default filter operator from
`FilterElementType` and a column that has not read its member selector yet answers `object`. That rule
is currently enforced by five authors remembering it, and **the test suite already contains a column
that gets it backwards**: `ReviewRegressionTests.CompileCountingColumn` calls `base.OnParametersSet()`
first and derives afterwards. It happens not to matter for that column, which is exactly why nobody
noticed. A sealed `OnParametersSet` calling `protected virtual void OnDerive()` first makes the order
not the subclass's to choose, and the mutation that gets it wrong stops compiling.

`ColumnBase` is public and this narrows it: a third-party column overrides `OnDerive` where it used to
override `OnParametersSet`. §8's packaging question is open and nothing has shipped, so this is the
cheapest it will ever be.

**4. The four `Apply*` methods default to a sort the column supplies.** `TemplateColumn`,
`CollectionColumn` and `LookupColumnBase` each carry the same four one-line forwards to `SortBy` -
twelve methods, verbatim across three classes, and two of the three also carry the same
`PropertyPath => SortBy?.Path`. An `internal virtual FastGridSort<TItem>? SortSource => null` on the
base, with the four `Apply*` and `PropertyPath` defaulting through it, turns twelve methods and two
properties into three overrides of one member.

Nothing public changes behaviour: the default `SortSource` is null, so every `Apply*` still answers null
for a column that supplies no sort - which is what an out-of-assembly column inherits today.
`PropertyColumn` overrides all four with typed expressions and does not participate. `CanSort` stays
overridden where it differs, because it genuinely does: `TemplateColumn` can sort on a bare
`SortProperty` with no `FastGridSort` at all.

**5. `RenderCell` defaults to the cell's own text.** Four of the five columns implement `RenderCell` and
`CellTextOf` as the same expression written twice -

```
LookupColumn            AddContent(sequence, CellTextOf(item))     CellTextOf => key is null ? null : NameOf(key(item))
LookupCollectionColumn  AddContent(sequence, CellTextOf(item))     CellTextOf => ...Join(...)
CollectionColumn        AddContent(sequence, Text(item))           CellTextOf => Text(item)
PropertyColumn          AddContent(sequence, cellText?.Invoke(item))  CellTextOf => cellText?.Invoke(item)
```

- and nothing checks that the two agree. They must: `CellTextOf` is what the truncation tooltip shows
(`RadzenFastGrid.cs:1935`) and what a column's text is read through elsewhere, so a column whose two
halves drift shows one thing in the cell and another on hover. Making `RenderCell` virtual with
`AddContent(sequence, CellTextOf(item))` as its body removes four overrides and makes the divergence
unrepresentable for a text column.

The cost is that `RenderCell` stops being `abstract`, so a column that overrides neither draws an empty
cell instead of failing to compile. That is a real loss and it is small: `CellTextOf` returning null is
already the base's answer, and the compiler was enforcing "say how a cell is drawn" over a class whose
other twenty-seven members all have defaults. `TemplateColumn` keeps its own `RenderCell`, because a
template is content and not text.

**6. `AutoFitWidth` is deleted.** No reader anywhere.

### Deliberately not proposed

- **Opening the eight `internal virtual` filter members.** It is the candidate's most interesting
  complaint and it is not a shape question, it is a decision about what a third-party column may do -
  and taking it would freeze the filter-row protocol at its current shape while two things that would
  change it are open: §10's question of whether an operator menu, a date popup, a numeric range or an
  enum picker is built in, and §15's candidate 7, which would give a column an identity the filter
  lookup is currently keyed by. Publishing eight members now and revising them after either lands is
  worse than publishing them once. **Refused, with that as the reason**, and recorded beside the
  candidate in §15.
- **Composing the four sections eagerly at the two points that can change them.** It is available -
  `OnParametersSet` and `SetFrozen` are the only writers of every input - and it would turn the body's
  two per-cell getters into two field reads, removing the four-term comparison `CellStyle`'s memo runs
  per cell today. It is refused because it trades a mechanism that **cannot** go stale for one that can:
  a lazy memo guarded on its inputs is self-correcting, and compose-on-write is correct only while both
  writers remember to recompose. That is this branch's most-recorded fault class, and the speed it would
  buy is not measurable at `--job short`, so it would be bought on an argument rather than a number.
- **Anything that makes the grid hoist a per-column value out of the row loop.** §15's candidate 2
  refused that shape - "a large diff through the hottest code on the branch" - and nothing here is worth
  reopening it for.

### How it is verified

§9's four layers, and specifically:

1. **A `gridbench --job short` control before and after**, on all three rows. The control at
   `9530a37a8` is bare **154.55 KB**, one sort **175.79 KB**, a filter row **158.78 KB**, and the noise
   floor on the bare row is ~0.3 KB. Nothing here is on the per-row path, so all three should hold. A
   fourth run with two columns frozen, because change 1 is the only one that touches a frozen column's
   strings and §10 measured frozen at +0.9 KB.
2. **A test that the four sections agree**, asserting that a frozen column's filter cell carries the
   header's style and not the body's - the fact §10 records being got wrong once, and which currently
   nothing checks directly.
3. **A test that the memo hands back the same instance per section**, as
   `FastGridColumnLayoutTests:122` already does for `CellStyle` - `Assert.Same`, which is the only
   assertion that distinguishes a memo that engages from one that does not.
4. **A test that a column's cell and its tooltip agree**, which is what change 5 makes
   unrepresentable and what nothing asserts today.
5. **Every new test mutation-checked, and the mutation must compile.** Changes 3 and 5 claim to make a
   fault unrepresentable; for those the evidence is a mutation that *fails to build*, which is worth
   more than one that fails a test.
6. **`GeometryParityTests` in a real browser**, because change 1 rewrites what every frozen cell in
   three of four sections is classed and styled with, and the geometry layer is the only thing that
   reads a pinned column's actual position.

### Where this could still be wrong

- **Change 1 may be a rename rather than a deepening.** It adds three members and removes three
  concatenations, which is close to flat, and if the four pairs do not end up looking like one table
  when they are written down, the honest answer is that `CellElementClass` was already the whole of the
  idea and the other three sections were fine as they were. The test in verification step 2 is what
  decides it: if the rule it asserts cannot be stated without naming a section, the sections are real.
- **Change 3's seal may cost more than the rule is worth.** Sealing a `ComponentBase` override on a
  public class is the most aggressive thing here, and the argument for it rests on one test column
  getting the order backwards *without consequence*. A rule whose violation has never cost anything may
  not need enforcing at all.
- **Change 5 removes a compiler error.** "Say how your cell is drawn" is currently checked at build
  time for every column anyone writes, and after this it is not. The trade is one class of mistake for
  another, and the claim that the drift it prevents is the likelier one is a judgement rather than a
  measurement.
- **Change 4 puts a sort on the base that only three of five columns have.** `SortSource` is a member
  every column inherits and most cannot use, which is the same shallowness this section is complaining
  about, one level up. The defence is that it replaces four such members with one; the defence is not
  that it is free.
### What the build changed

All six changes landed. Three of this section's own claims did not survive, one of them measured to the
byte, and the refactor turned up two methods that no test in the suite had ever executed.

**The memo design was wrong, and gridbench said so in an exact number.** This section said the four
sections would each be "memoized against what it folds exactly as `CellElementClass` is", and called that
"the existing one and not a new mechanism". Built that way it cost **8 reference fields per column** -
three for the header's fold, two for the filter's, three for the footer's - which is 64 bytes a column
and **320 bytes on a five-column grid**, paid by every grid whether or not anything is frozen, to save
three concatenations per frozen column per render.

The bench read it as +0.31 KB on three rows that had no business moving: bare **154.86** against a
control of **154.55**, one sort **176.10** against **175.79**, a filter row **159.34** and **159.09**
against **158.78**. 320 B is 0.3125 KB, and the two identical readings of 154.86 are what said it was
not noise. Only the body's pair is memoized now - it is the one read once per *cell* - and the other
three compose on read, which is exactly what the grid was already paying when it folded them itself.
After: bare **154.69** then **154.55**, one sort **175.87**, a filter row **158.77**, two frozen columns
**155.74** against **156.05** with the fields. Every row back inside the noise floor, and the frozen row
down by the same 320 bytes.

**The bisect that found it is worth more than the fix.** Reverting each change on its own - the sections,
`RenderCell`, `OnDerive` - left the number at 154.86 every time, which reads like "none of them did it"
and is the opposite of true. The probes renamed members back and restored the grid's inline folds while
leaving the *fields* on the class, and the cost was never in the code that ran; it was in the size of the
object. **A bisect has to remove the thing rather than rename it**, and a per-render cost that does not
scale with the row count is a hint that what grew is an object rather than a loop.

**Two methods had never been executed by any test.** Change 4 removed twelve verbatim `Apply*` forwards
from three columns. Mutating the base's `ApplyThenBy` and `ApplyThenByInMemory` to answer `null` left all
798 tests green - so the second-sort half of that block, six methods as they were written, was covered by
nothing at all. `ApplySort` and `ApplySortInMemory` were both caught. There is a test now over both
routes, and both mutations fail it. This is §9's first layer finding a gap that only appeared because the
duplication was collapsed: three copies of an untested method look like coverage from a distance.

**"Unrepresentable" is too strong for change 5, and right for change 3.** Sealing `OnParametersSet` does
make the derivation order not the subclass's to choose - the mutation that moves `OnDerive()` after the
base's own work fails ten tests, and there is no way to write the old mistake. `RenderCell` defaulting to
`CellTextOf` is weaker than that: it removes the duplication from the four columns that had it, so those
two halves cannot drift, but a subclass can override `RenderCell` again and reintroduce exactly the
divergence. What guards it now is a test across all four column types, not the type system. Said plainly
here because this section claimed otherwise.

**`AutoFitWidth` was dead, and so was `FrozenClass` by the end.** The first was already recorded. The
second became unreferenced once the three folds moved into the column, which is the small proof that the
sections really did take the recipe: the member the grid needed to hold one no longer has a caller.

**The same four-section table exists a second time and this section did not notice it.** The expand
toggle is not a `ColumnBase` and carries its own `ToggleFrozenClass`, `ToggleFrozenCellStyle`,
`ToggleFrozenHeaderStyle` and `ToggleFrozenFooterStyle` (`RadzenFastGrid.Frozen.cs:46-59`) - the same
three stackings for the same four sections, with the same rule that the filter row shares the header's.
It is composed in one place rather than at four call sites, so it is not shallow the way the column's
half was, and it is left alone. But a change to how a pinned cell stacks now has to be made in two files,
and neither said so; both do now.

### What the review found that the build had not

Two read-only passes over the diff, one against §3 and `CONTRIBUTING.md` and one against this section's
own claims. Between them they corrected a sentence in the code, closed a door the design had left open,
found a missing control, and produced two refusals worth recording.

**"Only the body's fold is memoized" was half wrong, in the comment that is this file's own summary of
the design.** It is true of the four *classes* and false of the three *styles*: `ComposeFrozenStyles`
memoizes the body's, the header's and the footer's behind one pair of keys, because they are one
composition with a z-index appended. Five fields, untouched by this piece and predating it. The comment
now says which half is which. A policy statement that is wrong about half its members is worse than none,
and this one sat at the top of the block a future author would read before adding a field.

**Sealing `OnParametersSet` closed one door and left the next one open.** `SetParametersAsync` is what
runs it, was `public override`, and was not sealed - so a column could override *that*, call the base,
and derive afterwards, which is the exact fault `OnDerive` exists to make unwritable, one method over.
It is sealed now. Registration happens there too, so a subclass that overrode it and forgot to chain
would have left itself out of the grid entirely.

**The frozen row had no control, and now has one.** §20's verification asked for four rows measured
before and after; the frozen row was only added to the filter partway through, so its "before" was a
cross-commit inference rather than a control. Measured at `567dfb237`, whose library is `9530a37a8`'s:
**155.74 KB**, against **155.82** after. Unmoved, and now on the same terms as the other three - which
read **154.64** against 154.55, **175.79** against 175.79, and **158.77** against 158.78.

**Two of this section's six verification items ask for evidence the design cannot produce**, and that is
a finding about the design rather than about the build.

- Item 3 wants "the memo hands back the same instance **per section**". Three sections have no memo, on
  purpose and for a measured reason, so the strongest available test is the one that exists: the body's
  pair, plus the three styles that share `ComposeFrozenStyles`.
- Item 5 wants change 5's fault to be proved unrepresentable by a mutation that **fails to build**. No
  such mutation exists: `RenderCell` is public virtual on a public class, so a column can override it
  and disagree with `CellTextOf`, and the mutation that does so compiles. Change 3's equivalent does
  work - `protected override void OnParametersSet` on a column is now CS0239. **One of the two claims of
  unrepresentability is real and the other is not**, and writing both in the same sentence is how the
  weaker one would have been believed.

**`SortSource` is `internal`, which is worse than the risk this section named.** The bullet said it puts
"a sort on the base that only three of five columns have". It is narrower than that: no out-of-assembly
column can override it at all, so for anyone outside this library it is a member that exists and can
never be used, and the twelve forwards it replaced were the only way in. Kept as it is, because widening
it means publishing part of the column protocol, which is exactly what this section refused to do for the
filter row's eight members and for the same reason.

**§20 named the toggle column's duplicate table only after the build, and the sharpest instance of it is
one line.** `RadzenFastGrid.cs:1230` reads `var spacerStyle = element == "th" ? ToggleFrozenHeaderStyle :
ToggleFrozenFooterStyle;` - which is "the filter row is a second row of the header, so it takes the
header's style" written as a comparison on a tag name, at a call site. That is this section's central
diagnosis, verbatim, in code the piece did not touch. Left alone deliberately: the toggle is not a
`ColumnBase`, has no class, style or width of its own to fold a pinning into, and its four members are
composed in **one** place where a column's were composed at four - so it is not shallow in the way that
made the column's half worth moving. Both files now point at each other, which is the least that should
have been true before.

**A seventh change, not in the design.** `CellClass` became `CellContentClass`. It classes the span
inside a body cell rather than the cell element, and next to four members named for sections it read as
a fifth. Small, and recorded because the design listed six.

**And one thing outside the piece.** §12 claimed `ItemKey` "already backs selection membership". It does
not - membership is `Contains` on the caller's own collection - and §12 is corrected above. It matters
beyond a wrong sentence: it means focus is the *only* place in the grid where an item is identified by
key rather than compared by reference, which is a point for §15's candidate 7 rather than against it.
