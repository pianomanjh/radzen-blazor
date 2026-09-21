# Radzen.Blazor.FastGrid.Export

Turns a `RadzenFastGrid<TItem>` into a spreadsheet, using the workbook writer that already ships inside
`Radzen.Blazor`.

## Put an Export entry in every grid's menu

Reference this package, register it once, and every grid showing the band menu offers **Export**.
Clicking it saves an `.xlsx` file.

```csharp
builder.Services.AddRadzenFastGridExport();
```

```razor
<RadzenFastGrid TItem="Order" Data="@orders" ShowGridMenu="true" />
```

Name the file, or take the export over entirely:

```csharp
builder.Services.AddRadzenFastGridExport(o =>
{
    o.SheetName = "Orders";
    o.FileName  = _ => $"orders-{DateTime.Today:yyyy-MM-dd}.xlsx";
});

// or send the bytes somewhere other than the browser
builder.Services.AddRadzenFastGridExport(o =>
{
    o.OnExport = (workbook, grid) => archive.StoreAsync(workbook);
});
```

**Registration is the switch.** The grid asks its service provider for an exporter once, and draws the
entry only if it gets one. An application that does not reference this package registers nothing,
resolves nothing, and draws a menu with one entry. A single grid opts out with `ShowExport="false"`,
keeping the menu and its other entries.

The grid shows its loading indicator while the export runs.

## Or call it yourself

```csharp
using Radzen.FastGrid.Export;

Workbook workbook = grid.ToWorkbook();

using var stream = File.Create("orders.xlsx");
workbook.SaveToStream(stream);
```

## It gives you a workbook, not a file

That is the whole shape of the package, and the numbers are why. At 50,000 rows over eleven columns:

| step | time | allocated |
| --- | --- | --- |
| `ToWorkbook()` | ~210 ms | 212 MB |
| `SaveToStream()` | ~1.5 s | 430 MB |
| `SaveAsCsv()` | ~90 ms | 71 MB |

The expensive step is the one this package does not take, by about seven to one. A method that returned
bytes would have baked all of it into the call and chosen the format for you; handing back the model leaves you
free to write CSV instead, to write on a background thread, or to put two grids in one file:

```csharp
var workbook = grid.ToWorkbook(new FastGridExportOptions<Order> { SheetName = "Open" });

workbook.AddSheet(closed.ToWorkbook().Sheets[0]);       // a second grid, same file
workbook.SaveAsCsv(stream);                             // or no xlsx at all
```

**On Blazor Server, do not call `SaveToStream` on the circuit's thread for a large grid.** A second and a
half is a second and a half of a frozen page. The registered export already does this for you.

## Its own package, and here is what that saves you

`XlsxWriter` needs `System.IO.Compression` and `System.Xml.Linq`. Nothing in `Radzen.Blazor.FastGrid`
touches it, so a trimmed build drops it — but the moment anything calls it, the trimmer has to keep it.
Published as a trimmed Blazor WebAssembly app, with and without a call to `ToWorkbook`:

| | raw | brotli |
| --- | --- | --- |
| the grid alone | 9,764 KB | 3,172 KB |
| the grid and the export | 11,052 KB | 3,572 KB |

**+400 KB over the wire**, and four assemblies rather than the two you would expect —
`System.Private.Xml` and `System.Private.Xml.Linq` come along. Every consumer that does not export pays
none of it.

## What comes out

**The rows are `FilteredRows`: everything the filters and the sort produce, not the page.** On a grid
backed by `LoadData` or the asynchronous executor the grid only ever holds one page, and that is what you
get — asking for more would mean running a query neither of those ran. You can run it yourself and hand
the result over as `Rows`, which is written instead: nothing is re-filtered or re-sorted, so what you
pass is what comes out, through the same columns.

**The columns are the ones on screen, in the order they are on screen.** A column the reader hid is
absent; a column they dragged is where they dragged it.

Numbers stay numbers and dates stay dates, so Excel sorts and sums them. Everything else — an enum, a
`Guid`, a `TimeSpan`, a lookup id, a joined collection — exports as the text the grid drew, which is what
the reader was looking at. The header row is bold and frozen, the range becomes a table so Excel draws
its filter buttons, and the columns are sized to their contents.

Two of those are worth stating plainly because they are not obvious:

- **A `bool` exports as a real boolean**, which Excel shows as TRUE/FALSE and can filter as a boolean.
  Loading the file back through `Workbook.LoadFromStream` currently answers `1`, because the reader drops
  the type — the file itself is correct, and a fix is offered upstream.
- **A `TemplateColumn` exports blank** unless you say otherwise. Its cell is a render fragment, and the
  only way to text would be a renderer pass per cell.

## Telling a column what to do

```csharp
var options = new FastGridExportOptions<Order>
{
    SheetName = "Orders",
};

options.Columns["Total"]   = new() { ExportFormat = "#,##0.00" };
options.Columns["Actions"] = new() { ExportIgnore = true };
options.Columns["Status"]  = new() { ExportValue = o => o.Status.ToString(), ExportTitle = "State" };

var workbook = grid.ToWorkbook(options);
```

The key is the column's `UniqueID` — the same name the stored settings use. A column with no `UniqueID`
takes one from its `Property`, so `Property="Order.Total"` answers to `"Order.Total"`.

**Why a dictionary and not an attribute on the column?** Because every built-in column is `sealed`. A
column you write yourself can implement `IFastGridExportColumn<TItem>` and say all four things directly,
and that wins over an entry here — but you cannot implement an interface on a `PropertyColumn`, and
unsealing it would cost the render path more than an export is worth.

## Options

| | |
| --- | --- |
| `SheetName` | `"Sheet1"` |
| `Rows` | `null` — the grid's own. Set it to export rows the grid does not hold, which is the `LoadData` case |
| `IncludeHeader` | `true` |
| `FreezeHeader` | `true` |
| `AddTable` | `true` — Excel's filter buttons. A table is not quite `SetAutoFilter`: it also carries a name and a style, which matters if your readers open these somewhere other than Excel. |
| `AutoFitColumns` | `true` |
| `Columns` | per-column settings, by `UniqueID` |

A column's own `Format` reaches the file where it can be said in the file's language — `C`, `N2`, `P`,
`Dn` and Excel-compatible custom patterns. Where it cannot (`E2`, `G`, `X`), the column exports the text
it drew rather than a number wearing the wrong format. `ExportFormat` overrides both.

## Registration options

These are for `AddRadzenFastGridExport`, and apply to every grid in the application.

| | |
| --- | --- |
| `SheetName` | `"Sheet1"` |
| `IncludeHeader`, `FreezeHeader`, `AddTable`, `AutoFitColumns` | as above, applied to every grid |
| `FileName` | `_ => "export.xlsx"`. Given the grid, so two grids on one page do not save the same name. |
| `OnExport` | takes the workbook and the grid, instead of the browser. Null means download. |

The menu entry is worded by the grid's own `ExportText` parameter, not from here — so it is localized
like every other word the grid draws, and a single grid can be given a different word without changing
what every other grid says.

**Where the time goes.** The workbook is built on the renderer's thread, because it reads the grid's
rows and columns — about 210 ms at 50,000 rows. Writing the file is moved off that thread, because it is
the slow part at about 1.5 s, and holding a Blazor Server circuit for it would freeze the page.

## Not in the box

**Styling to match the grid.** Colours, borders and conditional formats are all on `Format`, and you have
the workbook.
