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

| | Costs |
| --- | ---: |
| bare | 153 KB |
| sorting, filtering, paging, virtualization, column picking, settings, templates, `ItemKey` | see `README.md` |
| row click, cell click, cell context menu, row detail - **all four together** | **+0.9 KB** |
| column resize | +4.1 KB |
| column reorder | +6.7 KB |
| two frozen columns | +0.9 KB |
| keyboard navigation | **+1.4 KB, 1.00x** |
| range selection, on top of navigation | **+0 KB** |
| positional ARIA, row numbers | **+0 KB** |
| positional ARIA, column numbers on every cell | **+0.1 KB, ~1.1x** |
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

Keyboard navigation measured 155.2 KB against a 153.85 KB bare grid over three full-length runs, inside
the +2 KB and 1.02x gate §12 set for it. It cost eight times that until an assumption in §12 was
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
the keys, range selection and positional ARIA. §10 has what is still open.

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
- The built-in filter UI is a text box or a check-box list, and nothing else: no operator menu, no date
  popup, no numeric range, no enum picker. `RadzenDataGrid` has all four and they are most of its filter
  code. `FilterTemplate` is the escape hatch; whether any of them should be built in is open.

## 11. What is next, in the order it was argued

Nothing here is committed to; this is the list as it stood, so it can be picked up cold.

**Not built:**

- ~~**Keyboard navigation**~~ - **built, all four steps of §12.** It is the last of the three the scroll
  container unblocked; resize, reorder and frozen columns are all built. The roving-focus model turned
  out not to be the obstacle it looked like, because `RadzenDataGrid` does not use one either: focus
  stays on `.rz-data-grid-data` and the active cell is named by `aria-activedescendant`. What the design
  had to settle instead was where the algorithm lives, what paints a focused cell when the theme has no
  rule for one, and what a keystroke costs on a server-rendered circuit.
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

**A run ends at the next key without `Shift`, and at a sort, a filter or a page.** Those last three end
the anchor with it, because both ends of a range are positions in the view and all three are ways a row
arrives at an index that used to belong to another one. That is one call in `RefreshAsync`, which is
where every state change a user can make already funnels.

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

The package carries the same rules meanwhile so the grid does not depend on a version bump, and the
styling section says so rather than quietly weakening its claim that no stylesheet is needed.

A read-only grid is the *only* configuration this component promises, so until one of the two lands
this feature has no visible cursor at all. That makes the upstream fix a prerequisite rather than a
courtesy, which is why it is first in the order below.

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
