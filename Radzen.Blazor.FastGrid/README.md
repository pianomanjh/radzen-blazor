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

Pass a `HashSet<T>` when many rows can be selected: membership is looked up once per row through the
collection's own `Contains`, so a long `List<T>` is a scan per row.

`ItemKey` gives each row a `@key`, as QuickGrid's does - typically the row's primary key. Without one
the diff matches rows by position, so a re-sort rewrites the text of every cell in place; with one it
matches by identity and moves the rows. Not free - the renderer builds a dictionary of the keys, and a
value-typed key boxes once per row - so it is worth it where rows are reordered, not where they only
scroll.

`RowClick`, `RowDoubleClick`, `CellClick` and `CellContextMenu` cost nothing while nothing listens,
and **16 KB between them** once something does - not each. `ShowCellDataAsTooltip` puts each cell's
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
| *bare* | 151.78 KB | - | 1.00x |
| widths and alignment | 152.13 KB | +0.35 KB | 1.11x |
| selection (1 row in 4) | 151.89 KB | +0.11 KB | 1.06x |
| `RowClass` | 151.88 KB | +0.10 KB | 0.98x |
| `Settings` / `SettingsChanged` | 151.85 KB | +0.07 KB | 1.00x |
| a filter row | 154.34 KB | +2.56 KB | 1.01x |
| a column picker | 174.02 KB | **+22.2 KB** | 1.04x |
| filtering as you type | 155.66 KB | +1.32 KB *over the filter row* | 1.04x |
| header and footer templates | 153.34 KB | +1.56 KB | 1.00x |
| footer templates that aggregate | 153.54 KB | +0.20 KB *over the templates* | 1.05x |
| responsive titles | 151.95 KB | +0.17 KB | 1.40x |
| sorted by one column | 178.88 KB | **+27.1 KB** | 1.50x |
| sorted by two columns | 200.86 KB | **+22 KB** *over one* | 1.05x *over one* |
| row detail, driven through the API | 152.13 KB | +0.35 KB | 1.02x |
| `ItemKey` | 175.28 KB | **+23.5 KB** | 1.05x |
| cell tooltip | 267.63 KB | **+115.9 KB** | 1.45x |
| row click | 169.17 KB | **+16 KB** | 1.05x |
| row detail with its toggle column | 169.27 KB | **+16 KB** | 1.10x |
| cell click | 169.17 KB | **+16 KB** | 1.10x |

Every row is from one run, so the marginals are comparable with each other; the time ratios move a few
points between runs on a shared machine, the allocation figures barely at all.

The layout, selection, row-styling, template and settings features are free, as designed: a couple of
kilobytes at most across a whole render, against a 151 KB baseline. What is not free is a delegate, and
a delegate per *cell* least of all - a cell click costs five times a row click on five columns, and
eleven times the whole rest of the component. Every expensive row is opt-in and costs nothing until you
opt in.

Two rows are worth reading carefully rather than at face value:

- **Sorting is what costs, not sorting by two things.** Sorting at all is +27 KB and 50% more time -
  `OrderBy` over a thousand rows buys a key buffer and does its comparisons whichever grid asked for
  it. The *second* sort key adds 22 KB and 5% on top of that. Measured against the bare grid instead,
  multi-column sorting would look like +49 KB and 57%, and almost all of it would belong to the first
  sort.
- **Responsive titles allocate nothing and still cost 40% more time.** A span and a text frame per cell
  is work even when it is not memory.
- **Row detail has no idle state.** Declaring a `Template` draws a toggle on every row, so the feature
  costs from the moment it is available rather than when a row is expanded - there is no "switched on
  but not in use" for it, because a row that can be expanded has to show that it can. What it costs is
  now 16 KB rather than 404, because the toggle goes through the grid's one pointer listener instead of
  carrying a delegate of its own.
- **`ItemKey`'s 23.5 KB is the boxing.** A `Func<TItem, object>` over an `int` key boxes once per row,
  which is 24 bytes a thousand times. A reference-typed key costs nothing here. This is the one feature
  on the list whose price is paid in the key's type rather than in the grid.
- **Trimming the toggle's markup saved nothing, and the delegate was all of it.** The empty
  `rz-column-title` span RadzenDataGrid puts in the toggle cell was measured inert and removed, and the
  allocation did not move: 555.13 KB against 554.99 KB with it, which is noise. `RenderTreeBuilder`
  rents its frame array from a pool, so markup is paid in DOM nodes and render time, not in managed
  allocation. An earlier note here decomposed the 404 KB into "310 for the delegate, 93 for the markup"
  and admitted the 93 was inferred rather than measured. Removing the delegate settles it: the feature
  fell from 404 KB to 16, so **the delegate was about 388 KB of it and the markup was not the rest** -
  the split was wrong in the direction the pooled frame array predicted, and the part left unattributed
  was more delegate than anything else.

The tooltip's 116 KB is the `title` attribute plus deriving each cell's text a second time, since
`RenderCell` writes into the builder rather than returning a string.

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
| cell tooltip | 269.62 KB | 13,172 KB | **49x** | +0 KB |
| row class | 153.17 KB | 14,087 KB | 92x | +914 KB |
| row click | 169.17 KB | 14,834 KB | **88x** | +1,662 KB |
| a filter row | 157.14 KB | 16,098 KB | **102x** | +2,926 KB |
| a column picker | 175.77 KB | 15,618 KB | **89x** | +2,446 KB |
| responsive titles | 153.01 KB | 17,374 KB | **114x** | +4,202 KB |
| row detail | 169.27 KB | 18,467 KB | **109x** | +5,295 KB |
| cell click | 169.17 KB | 22,352 KB | **132x** | +9,180 KB |

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

It carries the sort, the filters and the page - and nothing else, deliberately. Width, order and
visibility are settings on `RadzenDataGrid` because its user can drag, reorder and pick columns; this
grid has no such UI, so persisting them would restore only what the markup already said. `SettingsChanged`
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
and what is left is the 16 KB the listener costs in total, shared with every other pointer event
rather than added to them. The toggle cell itself was already as small as it goes: it was trimmed to
the button alone after the geometry check established RadzenDataGrid's empty `rz-column-title` span
takes no space, and the allocation did not move, because `RenderTreeBuilder` rents its frame array
from a pool.

Against the same feature on `RadzenDataGrid`, which is the comparison that decides whether 404 KB is a
lot:

| | Allocated | Row detail costs it |
| --- | ---: | ---: |
| `RadzenFastGrid` | 153.25 KB -> 169.27 KB | **+16 KB** |
| `RadzenDataGrid` | 13,172 KB -> 18,467 KB | **+5,295 KB** |

Row detail costs `RadzenDataGrid` three hundred times what it costs this grid, because there it is a
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
- **Grouping, column resize, reorder, frozen columns, composite headers.** Resize, reorder and frozen
  columns all want the scroll container below, so that is one decision gating three features.
- **A scroll container.** No `rz-datatable-scrollable` structure, which is also what carries
  `RadzenDataGrid`'s keyboard navigation.
- **Chips, a search box, and row-by-row keyboard navigation in the drop-down.** The popup is the grid,
  so it is filtered through the grid's own filter row rather than a separate search input, and the
  closed drop-down lists the chosen rows as text rather than as removable chips. The drop-down is a form
  component but not a `RadzenFormField` one: the floating label needs focus and value notifications it
  does not raise.

## Styling

It emits Radzen's own class names, so every theme - including custom ones and CSS variables - styles it
with no extra work. Rendered geometry is checked against `RadzenDataGrid` in CI, laid out by Chromium
against the real stylesheet: header cell, body cell and table heights match to within half a pixel.
