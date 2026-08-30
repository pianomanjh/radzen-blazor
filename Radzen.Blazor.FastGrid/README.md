# Radzen.Blazor.FastGrid

A read-only data grid for [Radzen.Blazor](https://blazor.radzen.com), for large row counts. Same theme,
same markup contract, roughly a hundredth of the allocation.

At 1000 rows x 5 columns, rendering identical output:

| | Time | Allocated |
| --- | ---: | ---: |
| `RadzenDataGrid` | 16,849 us | 18,189 KB |
| **`RadzenFastGrid`** | **1,072 us** | **150 KB** |
| Blazor `QuickGrid` | 2,270 us | 370 KB |

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
