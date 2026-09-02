# Slim read-only Radzen grid — build spec

Everything here is derived from measurements in `README.md` in this folder. Where a decision was made,
the reason and the number behind it are given, so it can be re-argued rather than merely obeyed.

Read `README.md` first for the raw data. This file is the design that follows from it.

---

## 0. Where this is

Shipped as `Radzen.Blazor.FastGrid` on `tech/radzen-datagrid-slim`, rebased onto `upstream/master`.
The branch is **almost purely additive**: the only change to `Radzen.Blazor` is an eight-line
`QueryableExtension` array-filter fix, and the package needs **no `InternalsVisibleTo`** - the async
executor, the string resolver and the non-rendering event handler are all mirrored over public
surface, so it installs against stock `Radzen.Blazor`.

Upstream has since absorbed the async IQueryable seam (#2689) and the render optimizations (#2684),
which is why `Radzen.Blazor.EntityFrameworkAdapter` no longer exists: the built-in
`AsyncEnumerableQueryExecutor` made it redundant.

**Built and measured** (1000 x 5, allocation, modal of several runs):

| | Costs |
| --- | ---: |
| bare | 153 KB |
| sorting, filtering, paging, virtualization, column picking, settings, templates, `ItemKey` | see `README.md` |
| row click, cell click, cell context menu, row detail - **all four together** | **+16 KB** |
| column resize | +4.1 KB |
| column reorder | +6.7 KB |
| two frozen columns | +0.9 KB |
| the scroll container and `role="grid"` | 0 |

Resize and reorder re-measured together in one run, against a 153.3 KB bare grid: resize 158.3 KB,
reorder 160.0 KB, both at once 162.3 KB. They are additive because they are the same kind of cost -
a handle and a pair of callbacks per *header*. Against `RadzenDataGrid` with reorder on both sides,
which allocates 13,184 KB for it, that is **82x**.

Frozen columns measured 154.4 KB and 154.6 KB across two runs against a 153.6 KB bare grid - the inset
belongs to the column rather than the cell, so what is paid is one memoized string for the whole grid
plus a class and a style frame on the cells of a frozen column. `RadzenDataGrid` allocates 19,785 KB
for the same two frozen columns, which is **128x**.

Frozen is the one feature here that costs measurably more *time* than it does memory: a full-length run
puts it at **1.10x** (478.0us to 525.0us, error 3.7 and 10.5), which is those two frames on two
thousand cells. Two frames per cell being visible while a kilobyte is not is the pooled-frame-array
question in §11 again, from the other side.

Against `RadzenDataGrid` with the same feature on both sides, the narrowest row is cell click at
**132x** and row detail is **109x**. Nothing in the grid charges a delegate per row any more.

**Not built**: editing, grouping, composite headers, keyboard navigation. §10 has what is still open.

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
   can see what a browser does with it. Seven bugs got through every one of them and were found here,
   most within a minute of the first click, and the last three by a person looking at the screen rather
   than by anything that could have been automated first:

   | Fault | Why nothing above caught it |
   | --- | --- |
   | Resize ran, raised its callback, and moved nothing | The script takes a base id and appends `-col`; it was handed the already-suffixed one, found no col, and wrote the width to the `th`, which `table-layout: fixed` discards. The id test agreed with the markup rather than with the script. |
   | The row-detail toggle counted as a row click | Whether a click was a toggle was decided by a flag settled when the listener attached, so a grid that gained a `Template` later drew a toggle the listener had never heard of. |
   | Unhandled `JSDisconnectedException` on every teardown | It derives from `Exception`, not `JSException`. Nothing failed; the only trace was a line in a server log. |
   | A render loop at ~3,600 renders/sec | Nothing on screen changed while the circuit spun, so it read as "the grid is slow". |
   | A selected row was never painted | The theme nests its selected-row rule inside `.rz-selectable`, which the grid did not emit. `rz-state-highlight` sat on exactly the right `tr` and matched nothing. |
   | Scrolled columns drawn over the frozen ones, in the header only | The theme stacks every header cell at the same z-index, frozen or not, so a frozen one tied with its neighbours and document order let the column to its right win. The body was correct, which made it look like a rendering glitch rather than a rule. |
   | The filter row not pinned with its column | It is a second `tr` inside `thead` rather than part of the title row, so it never received the class or the inset - and the check written for the previous fault skipped it, because it searched for cells already carrying the frozen class. |

   Watch **renders/sec** on the metrics strip: a grid at rest is 0, and the panel turns it red above
   five. That reading alone names a render loop in a glance.

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
  detail costs 16 KB, so the parameter is about where the control lives.
- ~~Whether virtualization is in scope for v1~~ - **done.** `AllowVirtualization` puts the rows through
  `Virtualize` with `SpacerElement="tr"`, and one items provider serves every source. It is exclusive
  with paging: the two solve the same problem, so `Paging` is a single property both the pager and the
  view read.
- ~~Whether to support `RadzenDataFilter` interop in v1~~ - **resolved.** The grid speaks
  `FilterDescriptor` in both directions, which is what `RadzenDataFilter` emits. The path derivation of
  §4 is what makes that possible.
- The built-in filter UI is a text box or a check-box list, and nothing else: no operator menu, no date
  popup, no numeric range, no enum picker. `RadzenDataGrid` has all four and they are most of its filter
  code. `FilterTemplate` is the escape hatch; whether any of them should be built in is open.

## 11. What is next, in the order it was argued

Nothing here is committed to; this is the list as it stood, so it can be picked up cold.

**Not built:**

- **Keyboard navigation.** `RadzenDataGrid` hangs it off `.rz-data-grid-data` with `tabindex` and a
  keydown handler; that element now exists here. It would be one delegate per grid, not per row, so
  the budget is not the obstacle - the roving-focus model is. This is the last of the three the scroll
  container unblocked; resize, reorder and frozen columns are all built.
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
