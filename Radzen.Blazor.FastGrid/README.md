# Radzen.Blazor.FastGrid

A read-only data grid for [Radzen.Blazor](https://blazor.radzen.com), for large row counts. Same theme,
same markup contract, roughly a hundredth of the allocation.

At 1000 rows x 5 columns, rendering identical output:

| | Time | Allocated |
| --- | ---: | ---: |
| `RadzenDataGrid` | 17,790 us | 18,189 KB |
| **`RadzenFastGrid`** | **1,178 us** | **151 KB** |
| Blazor `QuickGrid` | 2,342 us | 370 KB |

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
                  SortBy="@(p => p.Accounts.Count)" />
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

`RowClick`, `RowDoubleClick`, `CellClick` and `CellContextMenu` are each bound only when something
listens - an unhandled event costs no attribute and no delegate. That matters most for the cell ones:
a delegate per cell is five times a delegate per row on a five-column grid. `ShowCellDataAsTooltip`
puts each cell's value in a `title`, and is off for the same reason.

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
| *bare* | 151.30 KB | - | 1.00x |
| widths and alignment | 151.61 KB | +0.31 KB | 1.04x |
| selection (1 row in 4) | 151.40 KB | +0.10 KB | 1.03x |
| `RowClass` | 151.41 KB | +0.11 KB | 1.00x |
| `Settings` / `SettingsChanged` | 151.41 KB | +0.11 KB | 0.99x |
| header and footer templates | 152.79 KB | +1.49 KB | 0.99x |
| footer templates that aggregate | 153.02 KB | +0.23 KB *over the templates* | 1.05x |
| responsive titles | 151.63 KB | +0.33 KB | 1.39x |
| sorted by one column | 178.35 KB | **+27 KB** | 1.60x |
| sorted by two columns | 200.48 KB | **+22 KB** *over one* | 1.09x *over one* |
| row detail, driven through the API | 151.74 KB | +0.27 KB | 1.02x |
| cell tooltip | 267.28 KB | **+116 KB** | 1.37x |
| row click | 461.38 KB | **+310 KB** | 1.21x |
| row detail with its toggle column | 554.99 KB | **+403 KB** | 1.65x |
| cell click | 1,633.96 KB | **+1,483 KB** | 2.06x |

The layout, selection, row-styling, template and settings features are free, as designed: a couple of
kilobytes at most across a whole render, against a 151 KB baseline. What is not free is a delegate, and
a delegate per *cell* least of all - a cell click costs five times a row click on five columns, and
eleven times the whole rest of the component. Every expensive row is opt-in and costs nothing until you
opt in.

Two rows are worth reading carefully rather than at face value:

- **Sorting is what costs, not sorting by two things.** Sorting at all is +27 KB and 60% more time -
  `OrderBy` over a thousand rows buys a key buffer and does its comparisons whichever grid asked for
  it. The *second* sort key adds 22 KB and 9% on top of that. Measured against the bare grid instead,
  multi-column sorting would look like +49 KB and 72%, and almost all of it would belong to the first
  sort.
- **Responsive titles allocate nothing and still cost 39% more time.** A span and a text frame per cell
  is work even when it is not memory.
- **Row detail has no idle state.** Declaring a `Template` draws a toggle on every row, so the feature
  costs its 403 KB from the moment it is available rather than when a row is expanded - there is no
  "switched on but not in use" for it, because a row that can be expanded has to show that it can. The
  two rows above are the same feature with and without that column, and the difference between them is
  the whole of it.

The tooltip's 116 KB is the `title` attribute plus deriving each cell's text a second time, since
`RenderCell` writes into the builder rather than returning a string.

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

**This is the one feature here whose availability is its cost.** Declaring the `Template` draws a
toggle button on every row, which is 403 KB at 1000 rows - a delegate per row, plus the cell, button
and spans the theme's toggle needs. It is charged whether or not anything is ever expanded, because a
row that can be expanded has to show that it can. Nothing is paid while `Template` is null.

`ShowExpandColumn="false"` is the way out where the cost matters: the feature stays, the per-row toggle
goes, and expansion comes from your own UI through `ToggleRow`. That is +0.27 KB rather than +403.

**Virtualization.** An expanded row is taller than `ItemSize`, so `Virtualize`'s spacers drift and the
scrollbar stops being proportional. `RadzenDataGrid` has the same problem - it renders its `Template`
inside the virtualized row too, and never sets `ItemSize` at all, taking Blazor's 50px default against
its own 37px rows. This grid measures `ItemSize` against the theme, so it is the one giving something
up; what makes that workable is that `ItemSize` is a parameter here, so a grid combining the two can
raise it towards the average expanded height.

## Data

`Data` takes an `IEnumerable<T>` or an `IQueryable<T>`. Sorting, filtering and paging compose onto it,
so an Entity Framework query stays a query - typed expressions throughout, no dynamic-LINQ string parse.

**Asynchronously**, with the adapter registered:

```
dotnet add package Radzen.Blazor.EntityFrameworkAdapter
```

```csharp
builder.Services.AddRadzenQueryableEntityFrameworkAdapter();
```

The grid then awaits its count and page queries instead of blocking the thread on `Count()` / `ToList()`.
Nothing else changes; with no adapter registered, or a source the adapter does not support, it falls back
to the synchronous path.

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

`FilterTemplate` replaces the control for a column that needs more. There is deliberately no operator
menu, date popup, numeric range or enum picker - those are most of `RadzenDataGrid`'s filter code and
none of its filter engine.

The grid exposes `Filters` as `FilterDescriptor`s and accepts them back through `ApplyFilters`, which is
what `RadzenDataFilter` speaks.

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

## What it does not do

Not oversights - the reasons are in `gridbench/SLIM-GRID-SPEC.md` in the repository:

- **Editing.** The per-row component and cascading values that inline editing needs are exactly the cost
  this grid exists to avoid. Use `RadzenDataGrid`.
- **Grouping, column resize, reorder, picking, frozen columns, composite headers.** Resize, reorder and
  frozen columns all want the scroll container below, so that is one decision gating three features.
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
