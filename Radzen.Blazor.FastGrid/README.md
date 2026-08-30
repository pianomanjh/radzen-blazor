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

It is **not** a drop-in replacement for `RadzenDropDownDataGrid`. That component's columns are
`RadzenDataGridColumn`, which name their property with a string; these are the grid's own columns, which
name it with an expression - so the row type is a type parameter here and the authoring is checked at
compile time. Everything the popup costs per row is the grid's cost, which is the point.

`Multiple` keeps the popup open while choosing and binds `Value` to a list. Sorting, filtering, paging
and virtualization are the grid's, and are set through the same parameter names. Without a
`ValueProperty` the row itself is the value, which is what a drop-down bound to an entity wants.

The grid is built only while the popup is open, and the popup emits the class names the Radzen drop-down
family emits, so a theme styles it with no extra work.

## What it does not do

Not oversights - the reasons are in `gridbench/SLIM-GRID-SPEC.md` in the repository:

- **Editing.** The per-row component and cascading values that inline editing needs are exactly the cost
  this grid exists to avoid. Use `RadzenDataGrid`.
- **Grouping, column resize, reorder, picking, frozen columns, composite headers.**
- **`title="<value>"` on cells.** `RadzenDataGrid` emits one so a truncated cell reveals itself on hover.
  It costs ~61 B/cell - 305 KB at 1000 x 5, against a 150 KB budget - so it would triple the
  component's allocation for a hover affordance. A `TemplateColumn` can emit it where it is wanted.
- **A scroll container.** No `rz-datatable-scrollable` structure, which is also what carries
  `RadzenDataGrid`'s keyboard navigation.
- **Chips, a search box, and row-by-row keyboard navigation in the drop-down.** The popup is the grid,
  so it is filtered through the grid's own filter row rather than a separate search input, and the
  closed drop-down lists the chosen rows as text rather than as removable chips.

## Styling

It emits Radzen's own class names, so every theme - including custom ones and CSS variables - styles it
with no extra work. Rendered geometry is checked against `RadzenDataGrid` in CI, laid out by Chromium
against the real stylesheet: header cell, body cell and table heights match to within half a pixel.
