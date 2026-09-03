# Radzen.Blazor.FastGrid

A read-only data grid for [Radzen.Blazor](https://blazor.radzen.com), for large row counts. Same theme,
same markup contract, roughly a hundredth of the allocation.

At 1000 rows x 5 columns, rendering identical output, all three in one run - `RadzenDataGrid` as it
stands on master, which is the leanest it gets:

| | Time | Allocated |
| --- | ---: | ---: |
| `RadzenDataGrid` | 12,003 us | 13,172 KB |
| **`RadzenFastGrid`** | **449 us** | **153 KB** |
| Blazor `QuickGrid` | 832 us | 371 KB |

Compare allocation across revisions of this table, not time: the times above are roughly three times
faster than the previous run recorded for all three grids alike, which is the machine and not the code.
Allocation is stable across the same move - `QuickGrid`, whose code did not change at all, came back
within 1.2 KB - so treat differences under about 1.5 KB at this scale as drift.

The feature table below is **one run**, re-measured whole at `2e7f756dc`, so its marginals are
comparable with each other. It had drifted out of that state: rows used to be re-measured individually
as the work that changed them landed, against whatever the bare grid cost at the time, which left its
*bare* the oldest number in it - and subtracting that stale bare from a freshly updated row invents a
regression that is not there. **A row's marginal is only meaningful against the bare of its own run.**

Two rows were also named the opposite way round from the benchmark that produces them. The benchmark's
"+ a filter row" is the as-you-type one, so it reads *higher* than "not as you type", not lower. They
are named here the way the benchmark names them.

The analyses further down cite **control pairs** - a figure measured with a feature's emission taken
out, beside the same figure with it in - and each pair is its own experiment, run together. Read those
two numbers against each other and never against this table's bare, which is a different run. The same
goes for the second cost table near the end of this file, which has a bare of its own.

It gets there by not doing three things a general-purpose grid has to: no component per row, no cascading
value per row, no render fragment per cell. Those are what inline editing needs, and this grid does not
edit. Everything else it does costs what it costs only when you switch it on - measured, not assumed:
with paging, filtering, virtualization and the async executor all present but unused, the whole data
path costs 0.7 KB of that 150.

## Getting started

```
dotnet add package Radzen.Blazor.FastGrid
```

It depends on `Radzen.Blazor`, whose theme you are already loading. Add the namespace to `_Imports.razor`:

```razor
@using Radzen.FastGrid
```

A Radzen app already imports `Radzen` and `Radzen.Blazor`, which is where the enums the grid's
parameters take live - `TextAlign`, `SortOrder`, `Density` and `DataGridGridLines` in the first,
`WhiteSpace` in the second.

```razor
<RadzenFastGrid Data="@people" AllowSorting="true" AllowFiltering="true"
                AllowPaging="true" PageSize="20">
    <PropertyColumn Property="@(p => p.FirstName)" Title="First name" />
    <PropertyColumn Property="@(p => p.Customer.Name)" Title="Customer" />
    <PropertyColumn Property="@(p => p.Salary)" Format="C" Title="Salary" />
</RadzenFastGrid>
```

Columns are **expressions**, not property-name strings. That is both better authoring - a rename is a
compile error, not a blank column - and cheaper: the expression compiles once to a `Func<T, string>`,
where a string property name compiles to `Func<T, object>` and pays a box per cell.

The dotted path a string would have given you is still derived from the expression and used wherever
Radzen needs one: `LoadDataArgs.OrderBy`, OData `$orderby`, and `FilterDescriptor.Property`.

## Columns

| | |
| --- | --- |
| `PropertyColumn<TItem, TProp>` | One value per cell. `Property`, `Format`, `SortBy`, `FilterBy`, `Title`, `CssClass`, `Sortable`, `Filterable` |
| `CollectionColumn<TItem, TElement>` | A collection per cell, listed. `Property`, `DisplayProperty`, `FilterProperty`, `Separator`, `SortBy` |
| `TemplateColumn<TItem>` | A template per cell. `Template`, `SortBy`, `SortProperty`, `Title` |
| `TemplateColumn<TItem>` | Arbitrary content. `Template`, `SortProperty`. Costs ~94 B/cell more than a property column - use it where a cell is not just a value |

Every column also takes the layout parameters, which are per column and cost nothing per row:

| | |
| --- | --- |
| `Width`, and the grid's `ColumnWidth` for a default | Written once onto the table's `colgroup`, not onto every cell |
| `MinWidth`, `MaxWidth`, `TextAlign` | The cell style, composed once per column and shared by every one of its cells |
| `WhiteSpace` | `Truncate` (the default, and RadzenDataGrid's), `Nowrap` or `Wrap` - the cell span's class, so it is a different literal rather than an extra attribute |
| `Visible` | Leaves the column out of the layout. It keeps any filter it carries, which is how a grid filters by something it does not show |
| `OrderIndex` | Puts the column at that position; the rest fill what is left in the order they were declared |
| `SortOrder` | The sort the grid starts in. Read once, as the column registers - call `SortBy` to re-sort a live grid |
| `HeaderTemplate` | Replaces the header's text, inside the theme's title spans rather than instead of them |
| `FooterTemplate`, `FooterCssClass` | Content for the footer cell. The grid draws a footer row when any visible column has one |

A `PropertyColumn` bound to a collection of **values** lists them without a template:

```razor
<PropertyColumn Property="@(p => p.Regions)" Separator=" / " />
```

For a collection of **objects**, `CollectionColumn` names the member to show, and filters on it:

```razor
<CollectionColumn Property="@(p => p.Accounts)" DisplayProperty="@(a => a.Name)" />
```

Razor infers the element type from `Property`, so neither type parameter is written. `FilterProperty`
defaults to `DisplayProperty`. A row matches when **any** member matches.

Neither is sortable by the collection itself - no provider can order rows by a list. `CollectionColumn`
takes a `SortBy` naming something that can be ordered:

```razor
<CollectionColumn Property="@(p => p.Accounts)" DisplayProperty="@(a => a.Name)"
                  SortBy="@(FastGridSort<Person>.By(p => p.Accounts.Count))" />
```

A collection-valued `PropertyColumn` has no such escape: its `SortBy` is typed at the property, which
is the collection, so its header stays unsortable.

## Rows, selection and events

The grid's own chrome is `ShowHeader`, `AllowAlternatingRows` (on by default), `GridLines`, `Density`
for the pager, and `Responsive`, which repeats each column's title inside its cells so a narrow-screen
theme can stack the table into cards.

`RowClass` and `RowStyle` are `Func<TItem, string?>` rather than the event-callback-with-mutable-args
shape `RadzenDataGrid.RowRender` uses, and deliberately: those args are an allocation per row, and this
is the same feature without one. Return one of a few constant strings and the composed class is memoized
against it, so a thousand rows cost one composition.

```razor
<RadzenFastGrid Data="@orders" RowClass="@(o => o.Overdue ? "overdue" : null)" />
```

Selection is membership plus events. The grid renders from `Selection` and never writes to it: a click
computes the new collection and hands it to `SelectionChanged`, so `@bind-Selection` is what makes
clicking take effect. `SelectionMode` chooses whether a click replaces the selection or toggles a row,
`AllowRowSelectOnRowClick` turns the whole thing off, and `RowSelect` / `RowDeselect` report the row
that changed.

```razor
<RadzenFastGrid Data="@orders" @bind-Selection="@chosen"
                SelectionMode="DataGridSelectionMode.Multiple" />
```

A grid with a `SelectionMode` and nothing listening does not select, and does not pretend to: the
selectable class the theme hangs its row hover and selected-row rules off is emitted only when a click
would actually change something, so such a grid neither highlights a row nor responds to one. Handle
`SelectionChanged` - or bind it - and both appear.

Pass a `HashSet<T>` when many rows can be selected: membership is looked up once per row through the
collection's own `Contains`, so a long `List<T>` is a scan per row.

`ItemKey` gives each row a `@key`, as QuickGrid's does - typically the row's primary key. Without one
the diff matches rows by position, so a re-sort rewrites the text of every cell in place; with one it
matches by identity and moves the rows. Not free - the renderer builds a dictionary of the keys, and a
value-typed key boxes once per row - so it is worth it where rows are reordered, not where they only
scroll.

`RowClick`, `RowDoubleClick`, `CellClick` and `CellContextMenu` cost nothing while nothing listens,
and **under a kilobyte between them** once something does - not each. `ShowCellDataAsTooltip` puts each cell's
value in a `title`, and is off by default for the same kind of reason.

They are raised from one listener on the `tbody` rather than from a delegate per row or per cell. The
browser already routes a click to its ancestors, so binding five thousand delegates to be told which
cell was clicked is paying for something the DOM does anyway - and it was expensive: `CellClick` alone
cost **1,483 KB** at 1000 rows x 5 columns, by a distance the most expensive thing this grid could be
asked to do. What is left is one `data-r` attribute per row for the listener to resolve rows by.

**The delegates come back if the listener cannot be attached.** The grid renders the cheap shape first
and re-renders with the handlers only if the script does not confirm - so a browser that could not
fetch the module keeps working, and so does a test host, which has no DOM listeners at all. That
second case is the important one: under bUnit `cut.Find("td").Click()` still reaches `CellClick`, which
it could not if the grid only delegated. A test written the obvious way would otherwise pass while
asserting nothing.

Two things follow from where the cost was rather than from the design:

- **Virtualization keeps the handlers.** A virtualized grid renders a window of some tens of rows
  rather than all of them, so the cost this removes is a few tens of kilobytes and not one and a half
  megabytes - and `Virtualize` hands its `ChildContent` an item with no position, so there is no row
  index for a listener to resolve.
- **Turning a callback on after the first render keeps the handlers too.** The listener is attached
  once. That costs allocation, not correctness.

### What each of these costs

Two different questions, and the table below answers only the second.

**What does having them cost?** Every feature here sits behind a check that is false by construction
when it is unused - `RowClick.HasDelegate`, `column.CellStyle is { }`, `FooterTemplate is not null`,
`sorts.Count == 0`. So an unused feature costs a branch, and the branches are per render or per column,
never per row. Measured end to end: the commit before any of this work renders 1000 x 5 in **150.44 KB**,
and the same grid with every feature on this page present and all of them switched off renders it in
**151.30 KB**. That is **+0.86 KB, or 0.6%**, and it is constant rather than per-row - at 200 rows the
difference is the same fraction of a kilobyte. It is two lists the grid keeps for its drawn columns and
its sort, allocated once per component.

Three things are not conditional, and belong to that 0.86 KB rather than to the table: the drawn-column
list is rebuilt every render whether or not any column sets `Visible` or `OrderIndex`; the colgroup and
footer are each decided by a scan of the columns every render; and `WhiteSpace` is always applied, since
it chooses the cell class rather than adding one.

**What does using them cost?** This, measured on the component, 1000 rows x 5 columns, one feature at a
time against the same baseline:

| | Allocated | Marginal | Time |
| --- | ---: | ---: | ---: |
| *bare* | 154.03 KB | - | 1.00x |
| widths and alignment | 154.33 KB | +0.30 KB | 1.11x |
| selection (1 row in 4) | 154.02 KB | -0.01 KB | 1.06x |
| `RowClass` | 154.13 KB | +0.10 KB | 0.98x |
| `Settings` / `SettingsChanged` | 153.99 KB | -0.04 KB | 1.00x |
| row click | 154.70 KB | +0.67 KB | 1.03x |
| cell click | 154.70 KB | +0.67 KB | 1.10x |
| row detail, driven through the API | 154.37 KB | +0.34 KB | 1.02x |
| row detail with its toggle column | 154.80 KB | +0.77 KB | 1.10x |
| responsive titles | 153.98 KB | -0.05 KB | 1.40x |
| header and footer templates | 155.51 KB | +1.48 KB | 1.00x |
| footer templates that aggregate | 155.60 KB | +0.09 KB *over header and footer templates* | 1.05x |
| a filter row, on change only | 156.79 KB | +2.76 KB | 1.01x |
| a filter row, filtering as you type | 158.25 KB | +1.46 KB *over a filter row, not as you type* | 1.04x |
| a column picker | 176.48 KB | +22.45 KB | 1.04x |
| sorted by one column | 175.15 KB | +21.12 KB | 1.50x |
| sorted by two columns | 195.48 KB | +20.33 KB *over sorted by one column* | 1.05x |
| column resize | 158.95 KB | +4.92 KB | - |
| column reorder | 160.75 KB | +6.72 KB | 0.93x |
| column resize and reorder | 162.91 KB | +8.88 KB | - |
| two frozen columns | 155.11 KB | +1.08 KB | 1.10x |
| keyboard navigation | 155.26 KB | +1.23 KB | 1.00x |
| keyboard navigation and range selection | 155.57 KB | +0.31 KB *over keyboard navigation* | 0.99x |
| a pager and row numbers over one page | 155.84 KB | +1.81 KB | 1.01x |
| six columns with the middle one hidden, and column numbers | 159.85 KB | +5.82 KB | 1.20x |
| `ItemKey` | 177.51 KB | +23.48 KB | 1.07x |
| `ItemKey` over a reference-typed key | 153.96 KB | -0.07 KB | 1.13x |
| cell tooltip | 270.62 KB | +116.59 KB | 1.46x |
| `CellRender` that adds nothing | 154.00 KB | -0.03 KB | - |
| `CellRender` that writes one attribute | 427.97 KB | +273.94 KB | - |
| `HeaderCellRender` that writes one attribute | 155.04 KB | +1.01 KB | - |
| column auto-fit, off | 154.04 KB | 0.00 KB | - |
| column auto-fit, on demand | 154.33 KB | +0.24 KB | - |

The two auto-fit rows come from a run of their own whose bare read 154.09 KB, which is what their
marginals are against - subtracting this table's bare from them invents a difference that is not
there. **And neither of them is what the feature costs.** Auto-fit does its work in the browser, and
this harness is bUnit: the reflow, the `scrollWidth` walk and the `getComputedStyle` calls all read as
zero here. What these two rows can say is that having the feature off is free and that having it on
adds a colgroup and an element id - a fixed cost, not a per-row one. The pass itself is timed through
Chromium and is **~1.7ms plus ~0.03ms a rendered row**: 3.2ms over 50 rows, 7.1ms over 200, ~32ms over
a thousand. A browser millisecond does not belong in a table of allocations, which is why it is here
in words rather than in a column.

The time column is not from this run. `--job short` measures allocation, not time - see the
verification protocol - so the ratios above are the ones settled by full-length runs, and the rows
added since carry no ratio rather than a short-run guess. Allocation was confirmed across two runs
that disagreed by at most 0.71 KB, one of them on a machine with a compile running alongside it.

The layout, selection, row-styling, template and settings features are free, as designed: a couple of
kilobytes at most across a whole render, against a 154 KB baseline. What is not free is a delegate, and
a delegate per *cell* least of all - a cell click costs five times a row click on five columns, and
eleven times the whole rest of the component. Every expensive row is opt-in and costs nothing until you
opt in.

Two rows are worth reading carefully rather than at face value:

- **Sorting is what costs, not sorting by two things.** Sorting at all is +21 KB and 50% more time -
  `OrderBy` over a thousand rows buys a key buffer and does its comparisons whichever grid asked for
  it. The *second* sort key adds 20 KB and 5% on top of that. Measured against the bare grid instead,
  multi-column sorting would look like +41 KB and 57%, and almost all of it would belong to the first
  sort.
- **Responsive titles allocate nothing and still cost 40% more time.** A span and a text frame per cell
  is work even when it is not memory.
- **Row detail has no idle state.** Declaring a `Template` draws a toggle on every row, so the feature
  costs from the moment it is available rather than when a row is expanded - there is no "switched on
  but not in use" for it, because a row that can be expanded has to show that it can. What it costs is
  now 0.77 KB rather than 404, because the toggle goes through the grid's one pointer listener instead
  of carrying a delegate of its own.
- **Range selection's 0.23 KB is the benchmark, not the feature.** The row reads 155.48 KB against
  navigation's 155.25, and the difference is one more parameter passed to the component rather than
  anything rendered for it. Setting `SelectionMode` to its *default* instead of `Multiple` - which
  turns the feature off while leaving the parameter in place - measures the same 155.48 KB. The feature
  itself is reached only through a Shift key, so there is nothing on the render path to charge for.
- **Positional ARIA is free in bytes and not in time, and the two rows say different things.** Row
  numbers cost nothing at all: the pager row measures 155.81 KB with them and 155.80 KB with the
  emission taken out, so all 1.93 KB of its marginal is the pager component. Column numbers cost
  nothing either - forcing the attribute onto every cell of the bare grid moves it 153.88 to 153.97 KB
  - and the column row's 5.99 KB is the sixth declared column, which registers whether or not it is
  drawn. What column numbers do cost is **about 1.1x the render time**, one attribute frame on every
  cell, which is the shape frozen columns already have. That measurement is why the grid writes them on
  every cell only when it has to.
- **`ItemKey`'s 23.5 KB is the boxing, and there is now a row that proves it.** A
  `Func<TItem, object>` over an `int` key boxes once per row, which is 24 bytes a thousand times. The
  claim that follows - that a reference-typed key costs nothing - used to be an inference; the control
  row measures it at **+0.04 KB**. This is the one feature on the list whose price is paid in the key's
  type rather than in the grid, and the only one you can make free by changing what you pass it.
- **Markup is paid in the values, not the frames.** Every large per-row or per-cell cost on this list
  turned out to be a string once a control was put behind it, and every frame-shaped cost turned out to
  be time: the tooltip is 116 KB of derived text and a free attribute; `data-r` was 16 KB of uncached
  `ToString` and a 0.78 KB frame; `aria-colindex` on five thousand cells is 0.09 KB and 1.1x; frozen
  columns are 0.9 KB and 1.10x; responsive titles are 0.17 KB and 1.40x. If a number here is large and
  attributed to a frame, it has not been measured yet.
- **An attribute per row costs 0.78 KB, and it used to read 16.** The row index every delegated click
  resolves a row by was measured at +16 KB at 1000 rows and written up as the render *frame* - the
  values being pre-cached strings, the frame was what was left. It was the other half: the table of
  cached index strings held 512 entries, so a thousand-row grid called `ToString` on 488 rows of every
  render. The table grows to fit now, and row click, cell click and row detail each fell by about 14 KB.
  `gridbench/README.md` has how it was found, which was by measuring a second per-row attribute and a
  per-cell one and getting answers that could not both be about frames.
- **Trimming the toggle's markup saved nothing, and the delegate was all of it.** The empty
  `rz-column-title` span RadzenDataGrid puts in the toggle cell was measured inert and removed, and the
  allocation did not move: 555.13 KB against 554.99 KB with it, which is noise. `RenderTreeBuilder`
  rents its frame array from a pool, so markup is paid in DOM nodes and render time, not in managed
  allocation. An earlier note here decomposed the 404 KB into "310 for the delegate, 93 for the markup"
  and admitted the 93 was inferred rather than measured. Removing the delegate settles it: the feature
  fell from 404 KB to 16, so **the delegate was about 388 KB of it and the markup was not the rest** -
  the split was wrong in the direction the pooled frame array predicted, and the part left unattributed
  was more delegate than anything else.

**The tooltip's 116 KB is the text, and none of it is the attribute.** Writing a constant `title` on
every cell without deriving anything measures 154.03 KB against a bare 154.03 - the attribute frame is
free. All of it is `CellTextOf` allocating a string per cell, because `RenderCell` writes into the
builder rather than returning one. That is the same shape as the `data-r` correction above, found the
same way, and it is why the rule below is worth stating outright.

The filter row is per column and stays per column: `+2.56 KB` for the row itself and `+1.32 KB` for the
second event handler that filtering-as-you-type binds to each of the five boxes - about 0.26 KB a box,
once per render, with the thousand rows below it making no difference. That was the thing worth
checking, since a handler that had leaked into the body would have shown up here as hundreds of
kilobytes rather than one and a half. `+0.41 KB` of the row's own cost is the accessible name each box
now carries, measured by taking it back out again; it is five string joins per render, not per row.

### Where that leaves it against RadzenDataGrid

Marginal cost says what a feature cost; it does not say whether the grid is still worth using once it
is paid, and the two can point different ways. So each feature is measured on `RadzenDataGrid` too,
with the same data and the same five columns, and the ratio below is both grids with that feature on -
the only comparison that is like for like.

Measured against `RadzenDataGrid` **as it stands on master**, which took its baseline from 18,191 KB to
13,172 KB over two rounds of optimization. Every ratio here is smaller than it was before those landed,
and deliberately so: the honest comparison is against the best version of the thing being compared to.

| Feature on both | `RadzenFastGrid` | `RadzenDataGrid` | Gap | Costs RadzenDataGrid |
| --- | ---: | ---: | ---: | ---: |
| *nothing* | 152.92 KB | 13,172 KB | 86x | - |
| cell tooltip | 270.59 KB | 13,172 KB | **49x** | +0 KB |
| row class | 153.17 KB | 14,087 KB | 92x | +914 KB |
| row click | 154.66 KB | 14,834 KB | **96x** | +1,662 KB |
| a filter row | 157.14 KB | 16,098 KB | **102x** | +2,926 KB |
| a column picker | 175.77 KB | 15,618 KB | **89x** | +2,446 KB |
| responsive titles | 153.01 KB | 17,374 KB | **114x** | +4,202 KB |
| row detail | 154.76 KB | 18,467 KB | **119x** | +5,295 KB |
| cell click | 154.66 KB | 22,352 KB | **145x** | +9,180 KB |
| keyboard navigation | 155.25 KB | 13,172 KB | **85x** | +0 KB |

Keyboard navigation is the one row whose reference figure is the baseline itself, and that is the
finding rather than a gap in the table: `RadzenDataGrid` has no switch for it. Its tab stop and its
keydown handler are unconditional, so every grid it renders has already paid - which is the premise
this whole component rests on, seen once more.

Take the modal value of several runs before trusting the `RadzenDataGrid` column: those rows are bimodal
between two values about 990 KB apart, for reasons `gridbench/README.md` sets out.

The gap narrows only where this grid charges for something `RadzenDataGrid` charges for anyway - a
delegate per row or per cell - and widens wherever the feature is markup the other grid pays for per
row. Cell click used to be the narrowest at 14x; it is now 132x, because it stopped costing a
delegate per cell.

Two rows are worth reading rather than skimming, because each changed meaning as `RadzenDataGrid` was
optimized underneath them:

- **The cell tooltip now costs `RadzenDataGrid` nothing at all**, where it used to cost 5,243 KB - 29% of
  everything the grid allocated - because `ShowCellDataAsTooltip` defaults to true and each cell built a
  `Dictionary` to carry one `title` attribute. That was found here and fixed there, and the gap on this
  row closed from 68x to 49x as a result. The last residue has now gone too: turning the tooltip off
  still saved 704 KB after the first round of fixes, and on current master the baseline and the
  tooltip-off measurement are the same 13,172 KB. This table is the reason any of it was found.
- **Responsive titles cost `RadzenDataGrid` 4,202 KB**, where the same measurement against the earliest
  grid said +0.4 KB. The feature has not changed; only the baseline under it has. The likeliest candidate
  is `RenderTreeBuilder` frame-array growth crossing a bucket that the tooltip's frames used to keep it
  past anyway - and that candidate has since been caught in the act on a different row, which flips
  between two values 990 KB apart with gen1/gen2 collections as the visible correlate. The mechanism is
  still inferred rather than demonstrated, so it **stays an open question rather than a finding**, and it
  is a question about `RadzenDataGrid` rather than about this component. `gridbench/README.md` has it.

## Sorting a column that is not typed at its key

`PropertyColumn` sorts by `TProp`, which it already has. The other two columns do not: a template
column has no expression at all, and a collection column's key belongs to the row rather than to the
element it is generic over. Both say it with `FastGridSort<TItem>`:

```razor
<TemplateColumn TItem="Order" Title="Customer"
                SortBy="@(FastGridSort<Order>.By(o => o.Customer.Name))">
    <Template Context="order"><b>@order.Customer.Name</b></Template>
</TemplateColumn>
```

`By` is generic, so the key's type is captured where it is still known, and every ordering afterwards is
an ordinary generic call - which is what a provider translates, what a trimmer follows, and what an
ahead-of-time compiler can emit. The sort builds both routes: an expression for a queryable and a
delegate for a source already in memory, the delegate compiled on first use so a queryable-backed grid
never pays for one.

**This is what made a template column able to sort at all.** It previously offered `SortProperty`, a
string path, and that never sorted anything the grid sorted itself: the header was clickable, the sort
was recorded, the indicator was drawn, and the rows did not move. The path only ever reached a
`LoadData` handler as its `OrderBy`, where a server did the sorting. `SortProperty` still does exactly
that and nothing more, so a `LoadData` grid needs no change; a grid that sorts its own rows needs
`SortBy`. Setting both is the sensible thing for a grid that does both, and then `SortBy`'s own path is
what the server is told.

A computed key sorts but has no path, so there is nothing to send a server or to persist - the same rule
every other computed sort key follows.

Written inline in markup, a `FastGridSort` is a new instance on every render, which is fine: it holds
delegates rather than compiling anything, and the column reads it live rather than caching it. That is
deliberate - a cache would need invalidating, the invalidation would fire every render, and it would
take the column's compiled display expression with it. Measured, getting that wrong costs 14,433 B a
render against 4,895 B, which is what the allocation test in `ReviewRegressionTests` weighs.

## Sorting by more than one column

`AllowMultiColumnSorting` makes a header click add to the sort instead of replacing it, and
`ShowMultiColumnSortingIndex` numbers the sorted headers. A click then cycles a column ascending,
descending, and out of the sort - which is the only way to remove one, since there is nowhere else to
click. Without it the grid sorts by one column and a click only toggles direction: there is no
"unsorted" to cycle back to, because removing the only sort would leave the rows in an order nothing
on screen explains.

Declaring `SortOrder` on several columns composes them in the order they were declared. `Sorts` gives
the current sort as `SortDescriptor`s in precedence order; `SortColumn` and `SortDescending` still
name the first of them.

The second sort key costs 22 KB and 9% more time at 1000 rows, on top of the 27 KB and 60% that
sorting at all costs. Sorting is the expensive part; sorting by two things is a surcharge on it.

## Footers, and the aggregate in them

A `FooterTemplate` runs on every render, so an aggregate written inline runs on every render too.
Measured over an in-memory list, that is cheap: five `Sum`s across a thousand rows cost 0.23 KB and
about 5% - five thousand additions is nothing, and the earlier warning here that it "would dwarf
everything" was wrong for that case.

Where it is not cheap is a source the grid does not hold in memory. Over an `IQueryable`, the same
expression is a database round trip per render:

```razor
@* Fine over a list. A query per render over an IQueryable *@
<FooterTemplate>@people.Sum(p => p.Salary)</FooterTemplate>

@* Fine over anything: computed when the data changes, rendered from a field *@
<FooterTemplate>@totalSalary</FooterTemplate>
```

The second form is the one to reach for by default, because the cost of the first depends on what
`Data` happens to be - and that is not visible in the markup that pays it.

## Remembering what the user changed

`Settings` restores state and `SettingsChanged` reports it, so a grid can come back the way its user
left it:

```razor
<RadzenFastGrid Data="@orders" Settings="@stored" SettingsChanged="@(s => stored = s)" />
```

It carries the sort, the filters and the page, and alongside them the three things a user can change
about the columns themselves: visibility once `AllowColumnPicking` is on, width once
`AllowColumnResize` is, and order once `AllowColumnReorder` is. Each is null until something records a
choice, so a grid whose user cannot change one stores nothing for it and the markup's own value stands
on the way back in - persisting it otherwise would restore only what the markup already said, and would
then override a later edit to that markup. `SettingsChanged`
fires whenever the grid reloads, which is every sort, filter and page change, and also a `Reload()`
called from application code. `CaptureSettings()` gives the same object on demand.

Settings are applied as the grid draws, which is the first moment its columns are known - so on a grid
composing over a queryable in memory, the render that restores the state is the render that shows it.
A grid on `LoadData` or the async executor gets one reload after, since the load that produced what is
on screen ran before the settings existed. A column with no property path - a template column naming no
member - cannot be identified across a reload and is not persisted.

## Row detail

A `Template` gives each row an expandable detail row beneath it:

```razor
<RadzenFastGrid Data="@orders" ExpandMode="DataGridExpandMode.Multiple">
    <Template Context="order">
        <RadzenFastGrid Data="@order.Lines">...</RadzenFastGrid>
    </Template>
    <PropertyColumn Property="@(o => o.Reference)" />
</RadzenFastGrid>
```

`ExpandMode` chooses whether expanding a row collapses the last one, `RowExpand` and `RowCollapse`
report what changed - including the row that single mode closes for you, since a row that leaves the
screen without an event is a row you still think is expanded - and `ToggleRow` / `IsRowExpanded` drive
it from code.

**Availability is its cost, not use.** Declaring the `Template` draws a toggle button on every row,
charged whether or not anything is ever expanded, because a row that can be expanded has to show that
it can. Nothing is paid while `Template` is null.

That used to be 404 KB at 1000 rows, and 310 KB of it was the delegate the toggle needed - the same a
row click cost. The toggle now goes through the same listener as the clicks, so the delegate is gone
and what is left is the row index the listener resolves rows by, under a kilobyte in total and shared
with every other pointer event rather than added to them. The toggle cell itself was already as small as it goes: it was trimmed to
the button alone after the geometry check established RadzenDataGrid's empty `rz-column-title` span
takes no space, and the allocation did not move, because `RenderTreeBuilder` rents its frame array
from a pool.

Against the same feature on `RadzenDataGrid`, which is the comparison that decides whether 404 KB is a
lot:

| | Allocated | Row detail costs it |
| --- | ---: | ---: |
| `RadzenFastGrid` | 153.88 KB -> 154.76 KB | **+0.88 KB** |
| `RadzenDataGrid` | 13,172 KB -> 18,467 KB | **+5,295 KB** |

Row detail costs `RadzenDataGrid` six thousand times what it costs this grid, because there it is a
component per row that can be expanded rather than a marked cell. With the feature on both sides this
grid is **109x leaner** - further ahead than the 86x baseline rather than behind it, which is what a
feature costing one grid 5,295 KB and the other 16 does to a ratio.

QuickGrid has no row detail, so there is nothing to compare; a `RadzenFastGrid` with it on is heavier
than QuickGrid, which is the price of doing something QuickGrid does not do.

`ShowExpandColumn="false"` still drops the per-row toggle and drives expansion from your own UI through
`ToggleRow`. It is now a way of choosing where the control lives rather than a way of avoiding a cost.

**Virtualization.** An expanded row is taller than `ItemSize`, so `Virtualize`'s spacers drift and the
scrollbar stops being proportional. `RadzenDataGrid` has the same problem - it renders its `Template`
inside the virtualized row too, and never sets `ItemSize` at all, taking Blazor's 50px default against
its own 37px rows. This grid measures `ItemSize` against the theme, so it is the one giving something
up; what makes that workable is that `ItemSize` is a parameter here, so a grid combining the two can
raise it towards the average expanded height.

## Data

`Data` takes an `IEnumerable<T>` or an `IQueryable<T>`. Sorting, filtering and paging compose onto it,
so an Entity Framework query stays a query - typed expressions throughout, no dynamic-LINQ string parse.

**Asynchronously**, with nothing to register. A provider that streams through `IAsyncEnumerable<T>` -
Entity Framework Core among them - is detected on the bound queryable itself, and the grid awaits its
count and page queries instead of blocking the thread on `Count()` / `ToList()`. Nothing else changes;
a source that does not support it uses the synchronous path unchanged.

Counting composes `GroupBy(x => 1).Select(g => g.Count())` rather than calling `CountAsync`, so the
aggregate stays a sequence the provider streams and translates to a plain `COUNT` - which is how this
works without referencing Entity Framework. Operations are serialized per `IQueryProvider`, because
queries from one `DbContext` share a provider that rejects concurrent use.

Register an `IFastGridQueryExecutor` to execute a provider that runs asynchronously by some other
route, or set the `Radzen.Blazor.DisableAsyncQueryExecution` `AppContext` switch - the one
`Radzen.Blazor` reads - to turn it off entirely.

**`LoadData`** stays the escape hatch for sources the grid cannot compose over - REST, OData, gRPC,
stored procedures. The handler is given `Skip`, `Top`, `OrderBy` and both `Filter` (as a string, in the
LINQ or OData form depending on the source) and `Filters` (as descriptors), and assigns `Data` and
`Count`. What it returns is rendered verbatim - never sorted, paged or filtered a second time.

## Filtering

`AllowFiltering` adds a filter row. `FilterMode` chooses the control, on the grid or per column:

- `Simple` (default) - a text box per column.
- `CheckBoxList` - a multi-select of the column's distinct values, filtering with `In`. The values come
  from a composed `SELECT DISTINCT`, not from enumerating the data, and are cached until the data
  changes. `FilterLookupData` supplies them instead for a source too large or remote to ask.

`FilterAsYouType` (default `true`) filters while the box still has focus, after `FilterDelay`
milliseconds of no typing (default 500). Turning it off leaves the filter applying when the box is left
or Enter is pressed, which it also does with the flag on - a box abandoned before the pause still
filters on the way out, and the pause it superseded does not then fire behind it.

Keystrokes before the pause cost no render at all, not just no query: typing is bound through a
non-rendering receiver, so a keystroke that is about to be superseded does not redraw a thousand rows
to show what is already on screen. Measured at three keystrokes: three full renders bound the ordinary
way, zero bound this way.

`FilterTemplate` replaces the control for a column that needs more. There is deliberately no operator
menu, date popup, numeric range or enum picker - those are most of `RadzenDataGrid`'s filter code and
none of its filter engine.

The grid exposes `Filters` as `FilterDescriptor`s and accepts them back through `ApplyFilters`, which is
what `RadzenDataFilter` speaks.

## Choosing which columns are drawn

`AllowColumnPicking` puts a drop-down above the grid listing the columns, ticked for the ones on screen.
It is `RadzenDropDown` in `Multiple` mode inside RadzenDataGrid's own `rz-group-header` /
`rz-column-picker` wrappers, so the themes style it unchanged and it needs no popup of the grid's own.

`Pickable="false"` keeps a column out of the list - and out of the picker's reach, so it goes on being
drawn whatever else is ticked. `ColumnPickerTitle` names a column in the list when its `Title` is not
what belongs there; without one the list falls back to `Title`, then to the property path.
`PickedColumnsChanged` reports what is drawn after each change, and `AllowPickAllColumns`,
`ColumnsPickerAllowFiltering` and `ColumnsPickerMaxSelectedLabels` shape the control itself.

The picker costs **+22.2 KB** at 1000 x 5, which is the largest of the cheap features and wants saying
plainly: it is a whole `RadzenDropDown`, popup and item list included, and that is what a drop-down
weighs. It is constant rather than per row by construction - `RenderColumnPicker` is called once from
`RenderGrid` and nothing in it reaches a row - and it is only paid when `AllowColumnPicking` is on. The
same control costs `RadzenDataGrid` **+2,442 KB**, so with a picker on both the gap widens from 90x to
93x.

What the picker chooses is an override, not an assignment: a column's `Visible` parameter is the
markup's word and the grid never writes to it. The override yields whenever the declaration changes
underneath - markup that starts saying `Visible="false"` is not asking to be overruled by an old tick -
which is the same rule a declared `FilterValue` follows.

Visibility joins sort, filters and page in `FastGridSettings`, but **only while the picker is on**. A
grid without one records `null` for every column, because storing a visibility nothing can change would
write the markup back to itself and then overrule a later edit to it on the next load.

## Freezing a column to an edge

`Frozen` pins a column while the rest of the grid scrolls sideways under it. `FrozenPosition` picks the
edge, and defaults to `Left`.

```razor
<PropertyColumn Property="@(o => o.Number)" Title="Order" Width="90px" Frozen="true" />
<PropertyColumn Property="@(o => o.Customer)" Title="Customer" Width="220px" Frozen="true" />
<PropertyColumn Property="@(o => o.Total)" Title="Total" Width="120px"
                Frozen="true" FrozenPosition="FrozenColumnPosition.Right" />
```

**Every frozen column between one and its edge needs a `Width`** - its own is needed only by whatever
comes after it. Where a column is pinned is the sum of the widths in front of it, so a frozen column
that declares none can still be placed while nothing after it can: the run ends there, and the columns
past it are drawn unfrozen rather than stuck to a position nobody worked out. Any unit will do, and
they can be mixed - the widths are added with `calc()` rather than parsed.

Only runs at the edges are pinned. A column marked `Frozen` with an unfrozen column between it and its
edge is stranded, and is drawn as an ordinary column; `RadzenDataGrid`'s `-inner` case is not built.

The runs are worked out from the order the columns are *drawn*, so they follow reordering and picking.
Dragging an unfrozen column to the front unpins what was there - the column no longer at the edge stops
being frozen rather than staying pinned in the middle of the table. Hiding a frozen column is the
gentler case: the next one along simply becomes the column at the edge, and is pinned at zero.

The position is worked out on the server and written into the cell style. `RadzenDataGrid` has the
browser do it - `updateFrozenColumnPositions` measures the header and writes an inline style to every
frozen cell in every row - which is a DOM write per frozen cell per row, and would have to run again
after every render. Composing it per column instead costs one string for the whole grid, is right on
the first paint, and needs nothing on a scroll, a page change or a virtualized window.

## Sizing a column to what is in it

`AutoFitColumns` measures the content and writes each column a width. It takes three values:

```razor
<RadzenFastGrid Data="@orders" AutoFitColumns="AutoFitMode.Once">
    <PropertyColumn Property="@(o => o.Number)" Title="Order" />
    <PropertyColumn Property="@(o => o.Customer)" Title="Customer" MaxWidth="24rem" />
    <PropertyColumn Property="@(o => o.Notes)" Title="Notes" AutoFit="false" />
</RadzenFastGrid>
```

- **`None`**, the default. Nothing is emitted and no script is imported.
- **`Once`** fits when rows first reach the page, and never again on its own.
- **`OnDemand`** fits only when asked.

Either way `AutoFitAsync()` fits the grid and `AutoFitAsync(column)` fits one column, and with
`AllowColumnResize` on, **double-clicking a resize handle fits that column** - the spreadsheet
convention. That is the only pointer route in, so a grid that does not allow resizing has no handle to
double-click and the API is the whole surface.

**A column that declares a `Width` is left alone** - the markup is an instruction, not a suggestion -
and `AutoFit="false"` opts out a column that declares none. `MinWidth` and `MaxWidth` bound the result,
and `MaxWidth` is the one worth setting: without it a single four-hundred-character value takes the
whole table and everything else truncates to nothing. They may be in any unit, including a different
one from each other, because the bound is applied by the browser through `clamp()` rather than by
anything here parsing the string. (Worth knowing that `max-width` on a *cell* does nothing at all under
`table-layout: fixed`, so the `col` is the only place a bound has ever been able to apply.)

**The last column being fitted that is not frozen is left with no width**, so the browser hands it
whatever the other fitted columns did not take. A column the markup has sized is never the bare one, so
on a grid whose last column declares a `Width` the slack lands further left. That is what keeps the table filling its container, and it
stays right through a window resize with nothing watching for one. It is the last rather than the
widest deliberately: which column is widest is a property of the data, so filtering would change which
one stretches and the table would rearrange itself for no reason a reader can see.

**A fit you ask for animates; the one `Once` runs does not.** The columns ease into their new widths
over 200ms rather than snapping to them, so a re-fit shows what moved. The automatic fit is the grid
settling into its first layout, which reads as a page still loading rather than as an answer to
anything, so it lands in one frame. `prefers-reduced-motion: reduce` turns the animation off.

### Fitting the container instead of scrolling

`AutoFitOverflow="AutoFitOverflow.Fit"` keeps the table inside its container and follows it as that
container changes size - the case where the same grid is opened on a laptop and on a desktop.

Mark the columns a row is identified by:

```razor
<RadzenFastGrid AutoFitColumns="AutoFitMode.Once" AutoFitOverflow="AutoFitOverflow.Fit">
    <PropertyColumn Property="@(x => x.Sku)"  Title="SKU"
                    AutoFitPriority="AutoFitPriority.Required" />
    <PropertyColumn Property="@(x => x.Name)" Title="Name"
                    AutoFitPriority="AutoFitPriority.Required" />
    <PropertyColumn Property="@(x => x.Notes)" Title="Notes" MinWidth="80px" />
</RadzenFastGrid>
```

A `Required` column keeps the width its content needs at every container size. Everything else gives
way in proportion to how much it has above its `MinWidth`, and a column that reaches its floor stops
giving and hands its share to the ones still above theirs.

**Give every best-effort column a `MinWidth`.** Without one its floor is zero, and a container narrow
enough will take it there - the column is still in the table and is no longer on the screen.

Below the width where every floor cannot be met at once the grid scrolls, which is the same answer
`Scroll` gives and is reached only when nothing else is left. A grid whose `Required` columns are on
their own wider than the container scrolls at every size; there is no arrangement that would not.

Following the container is free of the server: the measurement is taken once, and a resize is
arithmetic over the widths already in hand plus one layout the browser was doing anyway. Nothing calls
back into .NET, so the cost does not multiply by the number of open circuits.

**A fit wider than its container overflows rather than compressing back**, and the grid's wrapper
scrolls - sizing a column to its content is the whole point, so squeezing it again would undo the
measurement just taken. When the fitted columns already fill the container the last column is sized
like the rest instead of being left bare: there is no slack for it to absorb, and a column with no
width in a table that has overflowed is given nothing at all.

**Nothing is fitted below the `Responsive` breakpoint**, where the theme stacks the rows into cards and
a colgroup width stops deciding anything. A fit taken above it is kept, and is right again when the
window widens.

**It fits what is rendered.** Under paging that is the current page; under virtualization the current
window. A grid that has never scrolled past the widest value in a column has not seen it, and neither
has this - the answer is to fit again once it is on screen. Nothing re-fits on a scroll, a page turn, a
sort or a filter: a column that narrows while somebody is reading it is worse than one that is wider
than it needs to be.

A fitted width is **not** stored in `Settings`. A drag is a choice the user made and is remembered; a
fit is derived from data that will not be the same data next time, so `Once` measures again rather than
restoring a number computed against a different result set.

Which of the two wins depends on who asked. **`Once` leaves alone any column that already carries a
width the user chose** - a drag, or one restored from the settings, which is a drag from a previous
visit - so an automatic fit can never cost somebody a width they saved. **A fit you ask for takes that
column too**, and clears the drag, because a fit that visibly did nothing to the column under the
pointer would be the worse answer.

The measuring and the writing both happen in the browser, in one pass, the way the resize drag already
works: a feature whose whole job is to set a handful of strings should not cost a render of every row
to deliver them. The exception is a grid with a frozen column, which does render - the frozen inset is
a `calc()` sum composed on the server from those same widths, and leaving it alone would pin every
frozen cell to what the columns used to be. The same rule pays for itself: a frozen run ends at the
first frozen column declaring no width, so fitting one **extends** runs that would otherwise give up
and draw unfrozen.

## Keyboard navigation

`AllowKeyboardNavigation` puts a cursor in the grid. The whole grid is **one tab stop**, on the
scroll container that already carries `role="grid"`, and it remembers where the cursor was when you
tab away and back - tabbing out to a filter box and back is a constant gesture.

| Key | |
| --- | --- |
| Arrows | a row up or down, a cell left or right. In RTL the horizontal pair flips |
| `Home` / `End` | the first and last cell **of the row** |
| `Ctrl+Home` / `Ctrl+End` | the first and last cell of the page |
| `PageUp` / `PageDown` | a viewport of rows |
| `Enter` | activates: raises `RowClick` on a row, sorts on a header, expands on a row-detail toggle |
| `Space` | selects the focused row, without activating it |
| `Shift` + any of the above | extends the selection to wherever the cursor lands |
| `Shift+Space` | extends to the focused row without moving the cursor |

The header is row 0, so `ArrowUp` from the first row reaches it and `Enter` there sorts the column -
sorting being the most common thing anyone does to a business grid, and otherwise unreachable without
a mouse. The filter row is not in the arrow space: it holds real inputs that `Tab` already reaches, and
it swallows keydown so a left arrow typed in a filter box moves a caret rather than the cursor.

**Arrowing off the end of a page turns it** and lands on the first row of the next, and arrowing up
past the header turns it back. Under virtualization the cursor moves through the whole data set rather
than the rendered window, which scrolls to follow it.

Two behaviours are asymmetric on purpose. **Focus follows a row's item** through `ItemKey` - a sort or
a filter exists to move the row you were looking for, so the cursor goes with it - and **follows a
column's position**: hiding or reordering a column is a deliberate act on the columns, and having the
cursor stay where it is on screen is less startling than watching it chase a column across the table.
Without an `ItemKey` the row falls back to its position too.

**Holding `Shift` extends a selection** rather than starting one afresh, on a grid with
`SelectionMode="DataGridSelectionMode.Multiple"`. The range reaches from the **selection anchor** - the
last row `Space` or `Enter` acted on - to wherever the cursor is now, so `Shift+Space` after choosing a
row selects everything between the two, and `Shift+Arrow` grows and shrinks that range as the cursor
moves. It is recomputed from the anchor every time rather than accumulated, so backing up gives back
exactly the rows it covered, and rows chosen before the run began are left alone. Any key pressed
without `Shift` ends the run, and so does leaving the grid, a sort, a filter or a page change - both
ends of a range are positions in the view, and a run also holds the selection as it stood when it
opened, which stops being true the moment you can go and change it somewhere else.

Two limits, both deliberate. `Shift` with `ArrowLeft` or `ArrowRight` moves without extending: the
WAI-ARIA pattern extends a *cell* selection with them, and what this grid selects is rows. And range
selection is **off under virtualization** - a range is the rows between two positions, a virtualized
view can only hand over the window it has rendered, and a range reaching past that would select what it
could see and call it the answer. `Shift` there moves the cursor, which is what a grid with no range
selection does.

None of it re-renders. Arrow keys go through a handler that opts out of `StateHasChanged`, so a
keystroke costs one interop call and no render; the cursor is re-asserted after any render that happens
for another reason, which is what keeps it from being wiped when a sort or a selection rewrites a row's
class. `RadzenDataGrid` loses its focus ring exactly there.

**What it costs:** +1.42 KB at 1000 rows and no measurable time. Nothing is per cell - no `tabindex`,
no `id` - because the active cell is named by `aria-activedescendant`, which is one attribute on the
container rather than a frame on every cell. **Range selection on top of that is free**, and measurably
so: it has no parameter, binds nothing and emits nothing, because a Shift key is the whole of its
surface.

## Positional ARIA

**Not tied to `AllowKeyboardNavigation`.** It is a property of the markup rather than of the cursor: a
screen reader on a paged grid needs to know where the window sits whether or not a sighted user can
arrow around it, and gating that behind an unrelated switch is how it ends up off everywhere. So it is
on for every grid, and the cost lands where the grid would otherwise be lying.

A grid holding every row and every column needs nothing here: a browser can count what it has, and the
ARIA specification says as much - "if all of the columns are present in the DOM, including
`aria-colindex` is not necessary". So nothing is emitted until the DOM stops being the whole table,
which is exactly two things.

**Paging or virtualization windows the rows.** The grid then carries `aria-rowcount` and every row its
`aria-rowindex`, counting from one and including the header rows - the title row is row 1, the filter
row is row 2 where there is one, and the first data row follows. The number is the row's place in the
**data set**, so page three of a hundred starts at 201 rather than at 1, and a virtualized grid numbers
by where the row sits in the whole source rather than in the window it scrolled into. A total that is
not known yet - a virtualized grid before its first count, an asynchronous source before it loads -
reads `-1`, which is what the attribute defines for it, rather than a zero that would be a claim.

A row-detail row repeats its parent's number rather than taking one of its own. It is the row's content
in a second `tr` because a table cannot nest one, and numbering it separately would push every row
below it out of step with the data set - the one thing the attribute exists to keep true.

**The column picker hides a column.** The grid then carries `aria-colcount`, and how much more depends
on what hiding did to the run:

| What is hidden | What is written |
| --- | --- |
| nothing | nothing; the browser counts the cells and is right |
| the last columns | `aria-colcount` only - what is left is still columns one upward |
| the first columns | one `aria-colindex` per row, on the first cell, to say where the run starts |
| a column in the middle | `aria-colindex` on every cell, because the run has a hole in it |

Those are the specification's own three cases rather than a simplification of them, and the reason for
keeping all three is measured: one attribute on every cell of a thousand-row grid allocates nothing and
costs **about 1.1x** the render time. Hiding the last column of a grid should not pay for hiding the
middle one - and since none of this is opt-in, the tiers are what keep the bill on the configuration
that earns it. The last row of that table is the only per-cell attribute this component emits by
default, and a grid reaches it by having a user hide a column that is not at the end. A row-detail
toggle pins the first cell to column one, so any run that starts later already has a hole before it
and every cell is numbered.

Column numbers are read against the columns as they were **declared**, which is the only ordering a
hidden column has a place in - a reorder index is a position among the columns that are visible, and a
column nobody can see was never given one. A grid that both hides and reorders therefore has cells
carrying their declared positions rather than their drawn ones.

## Virtualization

`AllowVirtualization` renders only the rows in view, through Blazor's `Virtualize`. The grid needs a
scrolling ancestor with a bounded height for it to do anything. `ItemSize` defaults to 37px, the height
the Radzen themes actually render a row at.

Virtualization and paging solve the same problem, and virtualization wins: with it on, `AllowPaging` is
ignored and no pager is drawn.

With an `IQueryable` and the adapter registered, this is endless scroll against the database: each
window is a `Skip(n).Take(m)` the grid awaits, and the total behind the scrollbar is counted **once per
query, not once per window** - a sort, a filter, a reload or new data re-counts it, scrolling does not.
It is a proportional scrollbar rather than a grows-as-you-go one: `Virtualize` needs a total, so there
is one `COUNT(*)` up front.

## Drop-down

`RadzenFastDropDownDataGrid` is a lookup whose popup is a `RadzenFastGrid`, for choosing a row out of a
large table:

```razor
<RadzenFastDropDownDataGrid TItem="Customer" TValue="int"
                            Data="@customers" TextProperty="Name" ValueProperty="Id"
                            @bind-Value="@order.CustomerId">
    <PropertyColumn Property="@(c => c.Name)" Title="Customer" />
    <PropertyColumn Property="@(c => c.City)" Title="City" />
</RadzenFastDropDownDataGrid>
```

A lookup is paid for twice - once by every form that carries it, once by the user who opens it - so both
are measured. Over 1,000 rows, ten per page, three columns, sorting on:

| | Time | Allocated |
| --- | ---: | ---: |
| `RadzenDropDownDataGrid`, never opened | 4,275 us | 177.3 KB |
| **`RadzenFastDropDownDataGrid`, never opened** | **4.3 us** | **6.3 KB** |
| `RadzenDropDownDataGrid`, opened | 4,273 us | 178.4 KB |
| **`RadzenFastDropDownDataGrid`, opened** | **151 us** | **39.4 KB** |

Both render the same thirty cells when open. The rows behind the lookup barely move either figure - at
fifty rows the numbers are the same - because paging means only ten of them are ever drawn; what is
being compared is the shape of the render, not the size of the source.

The second pair costs the first almost nothing: `RadzenDropDownDataGrid` renders its popup grid whether
or not anyone opens it, so a form with twenty lookups on it has drawn twenty grids before the user
touches one. This one builds nothing until the first open - 26 render-tree frames against 716 - and
keeps what it builds afterwards.

Filtering is off on both in that measurement, because they do not offer the same thing:
`RadzenDropDownDataGrid` has one search box above its popup grid, this one has the grid's own per-column
filter row.

It is **not** a drop-in replacement for `RadzenDropDownDataGrid`. That component's columns are
`RadzenDataGridColumn`, which name their property with a string; these are the grid's own columns, which
name it with an expression - so the row type is a type parameter here and the authoring is checked at
compile time. Everything the popup costs per row is the grid's cost, which is the point.

`Multiple` keeps the popup open while choosing and binds `Value` to whatever collection its type names -
a `List<T>`, a `HashSet<T>` or a `T[]`. Sorting, filtering, paging and virtualization are the grid's, and
are set through the same parameter names; a virtualized popup scrolls inside `PopupHeight`, since
`Virtualize` needs a bounded ancestor and a popup has none of its own. Without a `ValueProperty` the row
itself is the value, which is what a drop-down bound to an entity wants.

It is an `IRadzenFormComponent`, so a `RadzenRequiredValidator` inside a `RadzenTemplateForm` finds it by
`Name`.

The grid is built on the first open and kept afterwards: a lookup nobody opens costs nothing, and one
that has been opened keeps the sort, filter and page the user left it on rather than re-querying from
scratch each time. The popup emits the class names the Radzen drop-down family emits, so a theme styles
it with no extra work.

A value bound before its rows have loaded - the ordinary order for an asynchronous source - shows as
itself until the row arrives, and adopts the row when it does. `ValueText` formats that interim label.
The lookup never walks an `IQueryable` to resolve it: reading the whole table to render one label is
what this component exists not to do.

## Filtering and sorting a list is not filtering and sorting a query

The grid takes an `IEnumerable<TItem>` or an `IQueryable<TItem>`, and until recently it composed both
the same way: wrap whatever arrived in a queryable, hand it an expression tree, let LINQ sort it out.
For a real provider that is exactly right - the expression is the point, and Entity Framework turns it
into SQL. For a `List<T>` it is the expensive way round. `EnumerableQuery` **rewrites and recompiles the
expression tree every time the result is enumerated**, and a grid enumerates on every render.

Measured at 1000 rows, filtering alone:

| | Time | Allocated |
| --- | ---: | ---: |
| `list.AsQueryable().Where(expression)` | 1,117 us | 11.80 KB |
| `list.Where(delegate)` | **38 us** | **0.07 KB** |
| `list.Where(expression.Compile())`, compiling each time | 292 us | 4.36 KB |

The whole bare render is 1,800 us, so an in-memory grid was spending most of a second render deciding
which rows to draw. So a source that is already in memory is now composed with **delegates**, and only a
real queryable gets expression trees:

| | Before | After |
| --- | ---: | ---: |
| a filter that actually filters | 1,842 us / 83.4 KB | **911 us / 77.0 KB** |
| sorted by one column | 2,516 us / 178.9 KB | **2,046 us / 173.1 KB** |
| sorted by two columns | 2,836 us / 200.9 KB | **2,204 us / 193.4 KB** |
| the same filter over an `IQueryable` | 1,742 us | 1,815 us *(unchanged, as intended)* |

Composed rather than compiled, note - `Expression.Compile()` is the 292 us row above, and under Native
AOT it cannot emit code at all and falls back to the interpreter. A closure over the column's getter
needs neither. The getter itself is compiled once per column, on first use, so a grid over a queryable
never pays for one.

Two implementations of sixteen filter operators is a real risk of divergence, so nothing here is taken
on trust: every operator is checked through all three builders - reflective, expression, delegate - over
the same rows, and the grid is checked route against route as a whole. A column that cannot compose in
memory, such as a template column filtering by a string path, sends the **whole** composition back to
the expression route rather than half of it.

## Render hooks, and what a per-cell one costs

`CellRender`, `HeaderCellRender` and `FooterCellRender` are handed each cell before it is drawn, and
whatever they write onto `Attributes` lands on the element. They are RadzenDataGrid's hooks in
everything but the column type, which here is `ColumnBase<TItem>`.

The attributes are written after the grid's own, so a hook can override any of them - which is half of
what a render hook is for, and the reason the grid uses `AddMultipleAttributes` rather than writing the
pairs itself. The renderer only resolves duplicate attribute names on an element that method was called
for; a hand-written loop is 56 bytes a cell cheaper and silently loses the override.

**`CellRender` is the only hook on this component that runs per cell**, and that is the whole of what
makes it expensive. Measured at 1000 x 5:

| | Allocated | Marginal |
| --- | ---: | ---: |
| *bare* | 151.82 KB | - |
| `CellRender` that adds nothing | 151.94 KB | +0.12 KB |
| `CellRender` that writes one attribute | 425.80 KB | **+274 KB** |
| `HeaderCellRender` that writes one attribute | 152.87 KB | +1.05 KB |

The first row is the interesting one, and it did not start there. Written the obvious way - an arguments
object per cell, its dictionary allocated with it - the same no-op hook cost **+195 KB**, and writing one
attribute cost **+1,524 KB**, which would have made it the most expensive feature this component offers,
level with a cell click. One arguments object reused for every cell of the render takes the no-op to
nothing and the writing case to a fifth of that.

What that buys is a rule, stated on the type and worth stating here: **the arguments describe the cell
being drawn and nothing else.** Read them inside the handler; do not keep them, or the dictionary they
hand you, past the end of the call. Writing to `Attributes` is always safe - the grid reads it into the
render tree before the handler is called again - and every cell starts from an empty set whatever the
last one did.

The 274 KB that remains is `AddMultipleAttributes` boxing an enumerator per cell, and the override
semantics above are what it is spent on. If a hook only needs a class or a style, the cheaper doors are
still open: `RowClass` and `RowStyle` for something that depends on the row, the column's own `CssClass`
for something that depends on the column. Neither is per cell.

## While it is loading

`ShowLoadingIndicator` covers the grid with RadzenDataGrid's own scrim and spinner while an asynchronous
load is in flight, and `LoadingTemplate` replaces the spinner with something of your own.

There is nothing to wire up. RadzenDataGrid needs `IsLoading=@isLoading` passed in and reset on every
path the load can leave by, including the one that throws; this grid owns the load, so it already knows,
and the indicator reads the same `IsLoading` the component exposes. There is no flag to forget to clear.

It costs one branch per render when nothing is loading, and two elements when something is. The rows stay
in the tree underneath rather than being replaced, so a reload does not blank the grid it is reloading.

## Language, and the two buttons that had no name

Every string the grid puts on screen is a parameter with a localized default, resolved through Radzen's
own `Localizer`: a custom `ILocalizer` in the container first, then the consuming application's own
`RadzenStrings` resources, then the ones shipped with `Radzen.Blazor`. `UICulture` names the culture, or
a `DefaultUICulture` cascaded from an ancestor does, exactly as every other Radzen component reads it.

The keys are `RadzenDataGrid`'s own, deliberately - `DataGrid_ClearFilterText`,
`DataGrid_FilterValueAriaLabel`, `DataGrid_ExpandChildItemAriaLabel`, `DataGrid_ColumnsText`,
`DataGrid_AllColumnsText`, `DataGrid_ColumnsShowingText`, `DataGrid_SelectVisibleColumnsAriaLabel`. All
seven are already translated into the five cultures Radzen ships, so this grid speaks German, Spanish,
French, Italian and Japanese with nothing added to any resource file, and an application that has
already overridden one of them for `RadzenDataGrid` gets the same override here. Each is also a
parameter - `ClearFilterText`, `ColumnsText` and so on - for a grid that wants its own wording.

Most of what this fixed is not translation. Two of the grid's controls are icon-only buttons, and
neither had an accessible name:

- **the clear-filter button**, whose content is the ligature `close`, which is what a screen reader
  read out;
- **the row-detail toggle**, which had no name at all and announced as nothing.

Both now carry one, and the toggle carries the *same* name in both states - `aria-expanded` is what says
which way it will go, and a button that renames itself under the user is the thing to avoid. The filter
box's own name is the column's title, the phrase, and the current value joined, so it is heard as
"First filter value Ada" rather than as a bare title shared by five identical boxes.

The one thing here that was not free was found by measuring rather than by reading. The obvious way to
write the toggle's name is to read the localized property in the row loop, and that is a
`ResourceManager` lookup per row: at 1000 rows it cost **24 KB and 8% of the render**, which the row
detail figures in the table above would have carried silently. Resolved once for the whole body instead,
row detail measures 555.44 KB against the 555.13 KB recorded before any of this - noise. The filter
row's names are per column and stay per column, `+0.41 KB` for all five, measured by taking them back
out again.

The lesson generalises past this component: a localized string is a dictionary lookup wearing a
property's clothes, and a property in a per-row loop is a per-row cost whatever it looks like.

## The drop-down reads its members through expressions too

`RadzenFastDropDownDataGrid`'s `TextProperty` and `ValueProperty` took a property **name**. Reading a
member that way means splitting the path, looking the property up by name, and invoking it
reflectively - and the drop-down does it **per row**, because matching a bound value to its row is a
scan of the source. Measured over 1000 rows:

| | Time | Allocated |
| --- | ---: | ---: |
| scan reading the member by name | 224.6 us | 58.62 KB |
| scan through a delegate compiled once | **22.9 us** | **35.16 KB** |
| a nested path, by name | 483.0 us | 148.46 KB |
| a nested path, through a delegate | **32.8 us** | **46.88 KB** |

Ten times for a plain member and fifteen for a nested one, and the nested case was also allocating the
split array on every row. Both parameters now take an expression:

```razor
<RadzenFastDropDownDataGrid TItem="Order" TValue="int" Data="@orders"
                            TextProperty="@(o => o.Customer.Name)" ValueProperty="@(o => o.Id)"
                            @bind-Value="@chosen" />
```

The reader is compiled on first use and kept until the expression changes, compared the way the columns
compare theirs - Razor rebuilds the expression every render, so reference equality would never hold for
one written in markup.

## Trimming and Native AOT

The path an ordinary grid takes - typed columns, a filter row, sorting, paging, selection, formatting -
publishes with **no trim warnings**, both trimmed and ahead-of-time compiled, and
`Radzen.Blazor.FastGrid.TrimTest` is a real Blazor WebAssembly application that proves it. It publishes
with `PublishTrimmed`, `TrimMode` `link` and warnings as errors, and then the published application is
**driven in a browser**: sort a string column, sort a numeric one, filter, check the answers.
Publishing without a warning only says the linker had no objection; a trimmed member goes missing when
something reaches for it, which is at run time, so the second half is the half that means anything.

Both halves were run against `RunAOTCompilation=true` as well - the whole application compiled to
WebAssembly ahead of time, a 26 MB native runtime rather than the 2.9 MB interpreted one, confirmed by
which file the browser actually fetched - and the grid renders, sorts and filters there too. CI runs
the trimmed configuration rather than the AOT one only because AOT-compiling the framework takes about
twenty minutes; the warnings the two produce are the same set.

The linker is doing real work in these runs rather than passing the build through: `Radzen.Blazor` goes
from 4,487 KB to 1,621 KB, and this grid from 131 KB to 104 KB.

That is not an accident of this component being small. It is what the typed design buys: a column that
carries `Expression<Func<TItem, TProp>>` composes its own sorting and filtering out of ordinary generic
calls, and a trimmer can follow those. The reflective alternative - reach a property by name, close a
generic method over a type discovered at run time - is exactly what it cannot.

Two features still reach a member by name, and are the ones to avoid in an application published with
Native AOT. Sorting and the drop-down's value and text members used to be there too; typed expressions
took both off the list:

| | Why | Under Native AOT |
| --- | --- | --- |
| a template column filtering by `SortProperty` | the path is a string | filters through the reflective builder |
| a check-box-list filter's distinct scan | projects onto a member typed at run time | supply `FilterLookupData` instead |

`DynamicCode.Supported` is what decides, and it is `RuntimeFeature.IsDynamicCodeSupported` and nothing
else - false under Native AOT, true wherever a lambda can still be compiled. Where a feature can
degrade it does, returning null the same way a column that cannot sort already did; where it cannot,
the exception says which column and what to use instead.

One thing worth knowing if you go looking: `FeatureGuard` cannot guard `RequiresUnreferencedCode`. The
analyzer rejects every candidate offered for it, including `RuntimeFeature.IsDynamicCodeSupported`
itself, and the reason is sound - a switch read at run time cannot promise the trimmer anything at
build time, because the trimmer finished before then. Trim warnings are removed by not calling
reflective code. Everything above is the shape that requirement forces.

## What it does not do

Not oversights - the reasons are in `gridbench/SLIM-GRID-SPEC.md` in the repository:

- **Editing.** The per-row component and cascading values that inline editing needs are exactly the cost
  this grid exists to avoid. Use `RadzenDataGrid`.
- **Grouping and composite headers.** Column resize, reorder and frozen columns were all on this list
  until the scroll container that gated them landed; all three now ship.
- **Auto-fitting a column to rows that are not on the page.** A fit measures what is rendered, so a
  paged or virtualized grid is fitted to the page or the window it is showing. Asking the server for
  the longest value per column would need a query per column and could not rank a `TemplateColumn` at
  all, and re-fitting as the user scrolls is a round trip per scroll.
- **Auto-fit in the drop-down grid.** Its popup sets the width, so "fit to content" has two defensible
  meanings there - the popup grows, or the grid fits inside what the popup already has - and neither
  should be picked by accident.
- **The nested scrollable structure.** No `rz-datatable-scrollable`. The ordinary
  `.rz-data-grid-data` container is emitted instead, and it is what resize overflows into, what frozen
  columns are pinned against, and what carries the keyboard cursor's tab stop.
- **Frozen columns stranded in the middle of the table.** Only runs at the left and right edges are
  pinned, so `RadzenDataGrid`'s `-inner` case is not built; such a column is drawn as an ordinary one.
- **Range selection under virtualization, and across a page.** `Shift` extends a selection on the
  rendered view; a virtualized grid has only the window it drew, and both ends of a range are positions
  in one page. `Shift` moves the cursor in both cases without extending.
- **Multiple selection has no unresolved-value text.** A single-value lookup bound to a row that has
  not loaded shows the value; a multiple one shows the placeholder, because what it asks is whether
  it holds rows rather than whether it has a value.
- **A template column's position is not stored.** Settings identify a column by its property path,
  and a `TemplateColumn` has none - so a grid whose columns were dragged into a new order restores
  only the ones that carry a property. With a single template column the rest still lands correctly,
  because there is one hole and one column to fill it; with two, they fill their holes in declaration
  order rather than the order they were dragged to.
- **The selection anchor is the keyboard's own.** Clicking a row does not move it: the inline click path
  is handed the row rather than its index, and an anchor that moved only on grids whose click listener
  attached would be worse than one that does not move at all.
- **Chips, a search box, and row-by-row keyboard navigation in the drop-down.** The popup is the grid,
  so it is filtered through the grid's own filter row rather than a separate search input, and the
  closed drop-down lists the chosen rows as text rather than as removable chips. The drop-down is a form
  component but not a `RadzenFormField` one: the floating label needs focus and value notifications it
  does not raise.

## Styling

It emits Radzen's own class names, so every theme - including custom ones and CSS variables - styles it
with no extra work. Rendered geometry is checked against `RadzenDataGrid` in CI, laid out by Chromium
against the real stylesheet: header cell, body cell and table heights match to within half a pixel.

That includes the keyboard cursor: `Radzen.Blazor` 11.3.1 draws a focused row and a focused cell on a
grid with no selection wired to it, which is the only kind this component renders. Earlier versions
draw neither - the row rule was scoped to `.rz-selectable` and there was no cell rule at all - so on
one of those, `AllowKeyboardNavigation` moves `aria-activedescendant` correctly and shows a sighted
user nothing. The fix is [radzenhq/radzen-blazor#2698] and its follow-up for frozen cells; there is no
stylesheet to link, and this package no longer ships one.

[radzenhq/radzen-blazor#2698]: https://github.com/radzenhq/radzen-blazor/pull/2698
