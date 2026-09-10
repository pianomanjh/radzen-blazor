# Streamed Images Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `StreamedSheet` columns can yield pictures that export as real embedded images filling their cells, with memory that stays flat when the caller asks for it.

**Architecture:** An `Image` accessor on `StreamedColumn<T>` yields `StreamedImage`s. The writer appends each to a `StreamedImageJournal` (memory or a `DeleteOnClose` temp file, SHA-256 deduplicated) while the rows stream, because `sheet1.xml` is an open zip entry for the whole loop. After it closes, the journal is replayed three times: media entries, `drawing1.xml` through an `XmlWriter`, and the drawing rels.

**Tech Stack:** C# (`LangVersion` latest), multi-targeted net8.0/net9.0/net10.0, `System.IO.Compression`, `System.Xml`, xUnit.

**Spec:** `gridbench/SLIM-GRID-SPEC.md` §69 on `tech/fastgrid-streamed-export`.

## Global Constraints

- Worktree: `/Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-images`, branch `upstream/xlsx-streaming-images` off `e041dad58`. Never `cd` to `/Users/jhuber/RiderProjects/radzen-blazor`. Never commit in `radzen-decouple`.
- No code comments at all. XML docs on public members only (`CS1591` + `TreatWarningsAsErrors` in `Radzen.Blazor`).
- Commit messages: a subject plus two or three lines. No `Claude-Session` trailer, no AI attribution.
- American English. No `benchmarks/` directory upstream.
- Nothing is pushed or posted to GitHub.
- Every new test is red before it is green, then mutation-checked with restore from a `cp` snapshot, never `git checkout --`. A mutation that does not compile proves nothing.
- A code review (the `code-review` skill, Standards and Spec) runs after each task. Its findings are fixed before the next task starts.
- Test command (from the worktree): `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Debug --filter "FullyQualifiedName~<Class>"`.

## Corrections to §69, made in this plan's commit

- `DataRowHeight` is not set on the scaffold's `Rows`. `Axis` keeps a height per index in a dictionary (`Axis.cs:167`), so that would be `O(rows)`. It is passed to `WriteRowStart` for each data row instead.
- `Image` with `Value`, and `Image` with `AutoFit`, throw `ArgumentException`, as the column-count check throws `ArgumentOutOfRangeException`. Both are thrown before a byte is written.
- The journal opens its store on the first image, so a sheet without images creates no temp file.
- The journal keeps a string reference per distinct image for its extension, not a byte.

## File Structure

| file | responsibility |
| --- | --- |
| `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs` | `WriteRowStart` writes points and takes an optional height; `ContentTypeToExtension` becomes `internal`; `SaveContentTypes` takes streamed image extensions |
| `Radzen.Blazor/Documents/Spreadsheet/StreamedImage.cs` (new) | the public image value and its magic-byte sniffer |
| `Radzen.Blazor/Documents/Spreadsheet/StreamedImageJournal.cs` (new) | append-only record store, dedup map, and the two readers |
| `Radzen.Blazor/Documents/Spreadsheet/StreamedSheet.cs` | `Image`, `DataRowHeight`, `SpillImages`, and the internal `IsImageAt` and `HasValueAt` |
| `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs` | validation, the journal's lifetime, row writing, drawing parts, sheet rels |
| `Radzen.Blazor/Documents/Spreadsheet/Workbook.cs` | exception docs on `SaveToStreamAsync` |
| `Radzen.Blazor.Tests/Spreadsheet/RowHeightXlsxRoundTripTests.cs` (new) | Task 1 |
| `Radzen.Blazor.Tests/Spreadsheet/StreamedImageTests.cs` (new) | Task 2 |
| `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetImageTests.cs` (new) | Tasks 3 to 5 |

---

### Task 1: A row height is written in points

**Files:**
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs:1471-1480` (`WriteRowStart`)
- Test: `Radzen.Blazor.Tests/Spreadsheet/RowHeightXlsxRoundTripTests.cs`

**Interfaces:**
- Produces: `private const double PointsPerPixel = 72.0 / 96.0;` in `XlsxWriter`, used again in Task 4.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class RowHeightXlsxRoundTripTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static MemoryStream Save(double height)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 4, 2);

        sheet.Cells[1, 0].SetValue("tall");
        sheet.Rows[1] = height;

        var stream = new MemoryStream();

        workbook.SaveToStream(stream);
        stream.Position = 0;

        return stream;
    }

    [Fact]
    public void A_row_height_is_written_in_points()
    {
        using var stream = Save(48);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();

        var row = XDocument.Load(entry).Descendants(Main + "row").Single(r => (string?)r.Attribute("r") == "2");

        Assert.Equal("36", (string?)row.Attribute("ht"));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(25)]
    public void A_row_height_reads_back_as_the_pixels_it_was_given(double height)
    {
        using var stream = Save(height);

        Assert.Equal(height, Workbook.LoadFromStream(stream).Sheets[0].Rows[1]);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Debug --filter "FullyQualifiedName~RowHeightXlsxRoundTripTests"`
Expected: 3 fail. `ht` is `"48"`, and the heights read back as 64 and 33.

- [ ] **Step 3: Implement**

In `XlsxWriter.cs`, directly above `WriteRowStart`, add `private const double PointsPerPixel = 72.0 / 96.0;`, then change its `ht` line to:

```csharp
            writer.WriteAttributeString("ht", XmlConvert.ToString(sheet.Rows[row] * PointsPerPixel));
```

- [ ] **Step 4: Run to verify they pass, then the whole spreadsheet suite**

Run the Step 2 command, expect 3 pass. Then run `--filter "FullyQualifiedName~Spreadsheet"`, expect all pass.

- [ ] **Step 5: Mutation-check**

Snapshot `XlsxWriter.cs` to the scratchpad. Replace `* PointsPerPixel` with `* 1.0` and confirm the three tests fail. Restore with `cp`.

- [ ] **Step 6: Byte gate**

Point `SavedParts`' `ProjectReference` at `radzen-images` and run it into a scratch directory, then `python3 /tmp/compare-parts.py /tmp/parts-before <dir>`. Expected: 16 parts compared, **1 differing**, which is `xl_worksheets_sheet1.xml`, and the only change is row 33's `ht="44.25"` becoming `ht="33.1875"`. The fixture sets `sheet.Rows[32] = 44.25` (`SavedParts.cs:87`). Show the diff. Any other difference fails the gate.

- [ ] **Step 7: Review, then commit**

```bash
git add Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs Radzen.Blazor.Tests/Spreadsheet/RowHeightXlsxRoundTripTests.cs
git commit -m "Write a row's height in points, as the reader reads it

OOXML's ht is in points and the writer wrote pixels, so a 48 px row
opened in Excel at 64 px and read back as 64."
```

---

### Task 2: `StreamedImage`, and the type read from its bytes

**Files:**
- Create: `Radzen.Blazor/Documents/Spreadsheet/StreamedImage.cs`
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedImageTests.cs`

**Interfaces:**
- Produces: `public sealed class StreamedImage(byte[] data, string? contentType = null)` with `byte[] Data`, `string? ContentType`, `internal string? ResolveContentType()`, and `internal static string? Sniff(ReadOnlySpan<byte> data)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class StreamedImageTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 }, "image/bmp")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "image/tiff")]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, "image/tiff")]
    public void Reads_the_type_from_the_bytes(byte[] data, string expected)
    {
        Assert.Equal(expected, new StreamedImage(data).ResolveContentType());
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    public void Does_not_guess_a_type_it_cannot_read(byte[] data)
    {
        Assert.Null(new StreamedImage(data).ResolveContentType());
    }

    [Fact]
    public void Keeps_a_type_it_was_given_over_the_bytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        Assert.Equal("image/jpeg", new StreamedImage(png, "image/jpeg").ResolveContentType());
    }

    [Fact]
    public void Refuses_no_data()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamedImage(null!));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `... --filter "FullyQualifiedName~StreamedImageTests"`. Expected: a build failure, because `StreamedImage` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// A picture written into one cell of a streamed sheet, drawn to fill the cell.
/// </summary>
public sealed class StreamedImage
{
    /// <summary>
    /// Creates an image from its encoded bytes.
    /// </summary>
    /// <param name="data">The encoded image. It is read when its row is written, so it must not change
    /// until the save completes.</param>
    /// <param name="contentType">The MIME type, such as <c>image/jpeg</c>. Null reads it from the bytes,
    /// which recognizes PNG, JPEG, GIF, BMP and TIFF.</param>
    public StreamedImage(byte[] data, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        Data = data;
        ContentType = contentType;
    }

    /// <summary>
    /// The encoded image.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// The MIME type as given, or null when it is read from <see cref="Data"/>.
    /// </summary>
    public string? ContentType { get; }

    internal string? ResolveContentType() => ContentType ?? Sniff(Data);

    internal static string? Sniff(ReadOnlySpan<byte> data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => "image/tiff",
        _ => null,
    };
}
```

- [ ] **Step 4: Run to verify they pass**

Expected: all 14 cases pass.

- [ ] **Step 5: Mutation-check**

Snapshot `StreamedImage.cs`. For each arm in turn, delete it and confirm its case fails. Change `0x37 or 0x39` to `_` and confirm the `GIF88a` case fails. Change `ContentType ?? Sniff(Data)` to `Sniff(Data)` and confirm `Keeps_a_type_it_was_given_over_the_bytes` fails. Restore with `cp` after each.

- [ ] **Step 6: Review, then commit**

```bash
git add Radzen.Blazor/Documents/Spreadsheet/StreamedImage.cs Radzen.Blazor.Tests/Spreadsheet/StreamedImageTests.cs
git commit -m "Add an image a streamed column can yield

Its type is read from the magic bytes when none is given, so a JPEG is
never stored as a .png the way SheetImage's default would store it."
```

---

### Task 3: Image columns draw their pictures, held in memory

**Files:**
- Create: `Radzen.Blazor/Documents/Spreadsheet/StreamedImageJournal.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/StreamedSheet.cs` (`StreamedColumn<T>`, `StreamedSheet`, `StreamedSheet<T>`)
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs` (`ContentTypeToExtension` becomes `internal`; `SaveContentTypes` gains a parameter)
- Modify: `Radzen.Blazor/Documents/Spreadsheet/Workbook.cs:170-174` (exception docs)
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetImageTests.cs`

**Interfaces:**
- Consumes: `StreamedImage`, `ResolveContentType()` from Task 2.
- Produces:
  - `public Func<T, StreamedImage?>? Image { get; set; }` on `StreamedColumn<T>`
  - `internal abstract bool IsImageAt(int column)` and `internal abstract bool HasValueAt(int column)` on `StreamedSheet`
  - `internal sealed class StreamedImageJournal(Func<Stream> open) : IDisposable` with `int Count`, `IReadOnlyList<string> Extensions`, `void Add(CellRef address, StreamedImage image)`, `void CopyMedia(Func<int, Stream> create)`, and `IEnumerable<(int Row, int Column, int Media)> Anchors()`
  - The test helpers `Sheet`, `Stream`, `Read`, `Entries`, `Part`, and `Png`, which Tasks 4 and 5 extend.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class StreamedSheetImageTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    private sealed record Item(string Name, StreamedImage? Photo);

    private static byte[] Png(byte seed) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, seed, seed, seed];

    private static Item With(string name, byte seed) => new(name, new StreamedImage(Png(seed)));

    private static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            await Task.Yield();

            yield return item;
        }
    }

    private static StreamedSheet<Item> Sheet(params Item[] items) => new()
    {
        Name = "Items",
        WidthMode = ColumnWidthMode.Declared,
        Rows = Async(items),
        Columns =
        {
            new StreamedColumn<Item> { Title = "Name", Value = i => i.Name },
            new StreamedColumn<Item> { Title = "Photo", Image = i => i.Photo },
        },
    };

    private static async Task<MemoryStream> Stream(StreamedSheet sheet)
    {
        var stream = new MemoryStream();

        await Workbook.SaveToStreamAsync(stream, sheet);

        stream.Position = 0;

        return stream;
    }

    private static Worksheet Read(MemoryStream stream)
    {
        stream.Position = 0;

        return Workbook.LoadFromStream(stream).Sheets[0];
    }

    private static string[] Entries(MemoryStream stream, string prefix)
    {
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        return zip.Entries.Select(e => e.FullName).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order().ToArray();
    }

    private static XDocument? Part(MemoryStream stream, string name)
    {
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        if (zip.GetEntry(name) is not { } entry)
        {
            return null;
        }

        using var content = entry.Open();

        return XDocument.Load(content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Draws_each_image_over_its_own_cell(int sampled)
    {
        var spec = Sheet(With("a", 1), With("b", 2), With("c", 3));
        spec.WidthMode = sampled == 0 ? ColumnWidthMode.Declared : ColumnWidthMode.Sampled(sampled);

        var images = Read(await Stream(spec)).Images;

        Assert.Equal(3, images.Count);

        for (var i = 0; i < 3; i++)
        {
            var image = images[i];

            Assert.Equal(DrawingAnchorMode.TwoCellAnchor, image.AnchorMode);
            Assert.Equal((i + 1, 1, 0.0, 0.0), (image.From.Row, image.From.Column, image.From.RowOffset, image.From.ColumnOffset));
            Assert.Equal((i + 2, 2, 0.0, 0.0), (image.To!.Row, image.To.Column, image.To.RowOffset, image.To.ColumnOffset));
            Assert.Equal(Png((byte)(i + 1)), image.Data);
            Assert.Equal("image/png", image.ContentType);
        }
    }

    [Fact]
    public async Task Anchors_below_a_header_only_when_there_is_one()
    {
        var spec = Sheet(With("a", 1));
        spec.IncludeHeader = false;

        var image = Read(await Stream(spec)).Images.Single();

        Assert.Equal((0, 1), (image.From.Row, image.From.Column));
        Assert.Equal((1, 2), (image.To!.Row, image.To.Column));
    }

    [Fact]
    public async Task Stores_an_image_repeated_across_rows_once()
    {
        var stream = await Stream(Sheet(With("a", 7), With("b", 8), With("c", 7)));

        Assert.Equal(new[] { "xl/media/image1.png", "xl/media/image2.png" }, Entries(stream, "xl/media/"));
        Assert.Equal(new[] { Png(7), Png(8), Png(7) }, Read(stream).Images.Select(i => i.Data));
    }

    [Fact]
    public async Task Writes_no_cell_under_an_image()
    {
        var stream = await Stream(Sheet(With("a", 1)));

        var row = Part(stream, "xl/worksheets/sheet1.xml")!
            .Descendants(Main + "row").Single(r => (string?)r.Attribute("r") == "2");

        Assert.Equal(new[] { "A2" }, row.Elements(Main + "c").Select(c => (string?)c.Attribute("r")));
        Assert.Equal("1:1", (string?)row.Attribute("spans"));
    }

    [Fact]
    public async Task Declares_the_drawing_and_its_media_type()
    {
        var types = Part(await Stream(Sheet(With("a", 1))), "[Content_Types].xml")!;

        Assert.Contains(types.Descendants(ContentTypes + "Override"),
            o => (string?)o.Attribute("PartName") == "/xl/drawings/drawing1.xml");
        Assert.Contains(types.Descendants(ContentTypes + "Default"),
            d => (string?)d.Attribute("Extension") == "png" && (string?)d.Attribute("ContentType") == "image/png");
    }

    [Fact]
    public async Task A_column_of_no_images_writes_no_drawing()
    {
        var stream = await Stream(Sheet(new Item("a", null), new Item("b", null)));

        Assert.Empty(Entries(stream, "xl/drawings/"));
        Assert.Empty(Entries(stream, "xl/media/"));
        Assert.Null(Part(stream, "xl/worksheets/_rels/sheet1.xml.rels"));
        Assert.Empty(Part(stream, "xl/worksheets/sheet1.xml")!.Descendants(Main + "drawing"));
        Assert.DoesNotContain(Part(stream, "[Content_Types].xml")!.Descendants(ContentTypes + "Override"),
            o => ((string?)o.Attribute("PartName"))!.Contains("drawing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_and_a_drawing_both_resolve()
    {
        var spec = Sheet(With("a", 1), With("b", 2));
        spec.TableName = "Items";

        var stream = await Stream(spec);
        var sheet = Read(stream);

        Assert.Single(sheet.Tables);
        Assert.Equal(2, sheet.Images.Count);

        var ids = Part(stream, "xl/worksheets/_rels/sheet1.xml.rels")!
            .Descendants(Package + "Relationship").Select(r => (string?)r.Attribute("Id")).ToArray();

        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task Refuses_a_column_that_reads_an_image_and_a_value()
    {
        var spec = Sheet(With("a", 1));
        spec.Columns[1].Value = i => i.Name;

        var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Workbook.SaveToStreamAsync(stream, spec));

        Assert.Contains("Photo", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Refuses_to_auto_fit_a_column_of_images()
    {
        var spec = Sheet(With("a", 1));
        spec.Columns[1].AutoFit = true;

        var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Workbook.SaveToStreamAsync(stream, spec));

        Assert.Contains("Photo", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Names_the_cell_of_an_image_it_cannot_read()
    {
        var spec = Sheet(With("a", 1), new Item("b", new StreamedImage([1, 2, 3])));

        var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Workbook.SaveToStreamAsync(stream, spec));

        Assert.Contains("B3", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg", "jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif", "gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 }, "image/bmp", "bmp")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "image/tiff", "tiff")]
    public async Task Names_the_media_by_the_type_read_from_its_bytes(byte[] data, string type, string extension)
    {
        var stream = await Stream(Sheet(new Item("a", new StreamedImage(data))));

        Assert.Equal(new[] { $"xl/media/image1.{extension}" }, Entries(stream, "xl/media/"));
        Assert.Equal(type, Read(stream).Images.Single().ContentType);
    }

    [Fact]
    public async Task Keeps_the_type_it_was_given()
    {
        var stream = await Stream(Sheet(new Item("a", new StreamedImage(Png(1), "image/jpeg"))));

        Assert.Equal(new[] { "xl/media/image1.jpeg" }, Entries(stream, "xl/media/"));
        Assert.Equal("image/jpeg", Read(stream).Images.Single().ContentType);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `... --filter "FullyQualifiedName~StreamedSheetImageTests"`. Expected: a build failure, because `StreamedColumn<T>.Image` does not exist.

- [ ] **Step 3: `StreamedColumn<T>` and `StreamedSheet`**

In `StreamedColumn<T>`:

```csharp
    private static readonly Func<T, object?> NoValue = static _ => null;

    public Func<T, object?> Value { get; set; } = NoValue;

    /// <summary>
    /// Reads a picture from a row, drawn to fill the row's cell in this column. A row that yields null
    /// leaves the cell empty. A column that reads images writes no values, so it cannot also set
    /// <see cref="Value"/> or <see cref="AutoFit"/>, and its <see cref="Format"/> is not used.
    /// </summary>
    public Func<T, StreamedImage?>? Image { get; set; }

    internal bool HasValue => Value is not null && !ReferenceEquals(Value, NoValue);
```

Keep `Value`'s existing XML doc. In `StreamedSheet`, next to `AutoFitAt`:

```csharp
    internal abstract bool IsImageAt(int column);

    internal abstract bool HasValueAt(int column);
```

In `StreamedSheet<T>`:

```csharp
    internal override bool IsImageAt(int column) => Columns[column].Image is not null;

    internal override bool HasValueAt(int column) => Columns[column].HasValue;
```

and in `Read`: `readers[i] = Columns[i].Image ?? Columns[i].Value ?? (static _ => null);`.

- [ ] **Step 4: The journal**

```csharp
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

internal sealed class StreamedImageJournal(Func<Stream> open) : IDisposable
{
    private const int RecordLength = 16;

    private readonly Dictionary<Digest, int> media = [];

    private readonly List<string> extensions = [];

    private readonly byte[] record = new byte[RecordLength];

    private Stream? store;

    public int Count { get; private set; }

    public IReadOnlyList<string> Extensions => extensions;

    public void Add(CellRef address, StreamedImage image)
    {
        var data = image.Data;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);

        var digest = new Digest(hash);

        if (media.TryGetValue(digest, out var index))
        {
            Write(address, index, -1);
        }
        else
        {
            var contentType = image.ResolveContentType() ?? throw new InvalidOperationException(
                $"The image for {address} is not a PNG, JPEG, GIF, BMP or TIFF, and was given no content type.");

            index = extensions.Count;

            media.Add(digest, index);
            extensions.Add(XlsxWriter.ContentTypeToExtension(contentType));

            Write(address, index, data.Length);
            store!.Write(data);
        }

        Count++;
    }

    public void CopyMedia(Func<int, Stream> create)
    {
        if (store is null)
        {
            return;
        }

        store.Position = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(81_920);

        try
        {
            for (var i = 0; i < Count; i++)
            {
                var (_, _, index, length) = Read();

                if (length < 0)
                {
                    continue;
                }

                using var target = create(index);

                while (length > 0)
                {
                    var read = store.Read(buffer, 0, Math.Min(buffer.Length, length));

                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    target.Write(buffer, 0, read);
                    length -= read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IEnumerable<(int Row, int Column, int Media)> Anchors()
    {
        if (store is null)
        {
            yield break;
        }

        store.Position = 0;

        for (var i = 0; i < Count; i++)
        {
            var (row, column, index, length) = Read();

            if (length > 0)
            {
                store.Seek(length, SeekOrigin.Current);
            }

            yield return (row, column, index);
        }
    }

    public void Dispose() => store?.Dispose();

    private void Write(CellRef address, int index, int length)
    {
        store ??= open();

        BinaryPrimitives.WriteInt32LittleEndian(record, address.Row);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), address.Column);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), index);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), length);

        store.Write(record);
    }

    private (int Row, int Column, int Index, int Length) Read()
    {
        store!.ReadExactly(record);

        return (
            BinaryPrimitives.ReadInt32LittleEndian(record),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12)));
    }

    private readonly record struct Digest(ulong A, ulong B, ulong C, ulong D)
    {
        public Digest(ReadOnlySpan<byte> hash)
            : this(
                BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]))
        {
        }
    }
}
```

In `XlsxWriter.cs`, make `ContentTypeToExtension` `internal static`. Check that `CellRef` exposes `Row` and `Column`, and that its `ToString()` gives `B3`, as the table code's `new CellRef(0, column)` and the 32,767-character message suggest.

- [ ] **Step 5: The writer**

In `XlsxWriter.Streaming.cs`:

1. In the static `WriteStreamedAsync`, after the column-count check:

```csharp
        for (var column = 0; column < spec.ColumnCount; column++)
        {
            if (!spec.IsImageAt(column))
            {
                continue;
            }

            if (spec.HasValueAt(column))
            {
                throw new ArgumentException(
                    $"The column '{spec.TitleAt(column)}' reads both an image and a value, and the cell under a picture holds nothing.",
                    nameof(spec));
            }

            if (spec.AutoFitAt(column))
            {
                throw new ArgumentException(
                    $"The column '{spec.TitleAt(column)}' reads images and asks to be auto-fit, and a picture has no text to measure.",
                    nameof(spec));
            }
        }
```

2. In the instance `WriteStreamedAsync`, after the archive is created: `using var journal = new StreamedImageJournal(static () => new MemoryStream());`. Pass `journal` to `SaveStreamedSheetAsync`, and pass `journal.Extensions` to `SaveContentTypes` as `streamedImageExtensions`.

3. `SaveStreamedSheetAsync(..., StreamedImageJournal journal, ...)` passes `journal` down to `WriteStreamedSheetXmlAsync`. After the `RowCount` check, replace the early-return table block with:

```csharp
        var relationships = new List<(string Id, string Type, string Target, bool External)>();
        var table = HasTable(spec, written);

        if (table)
        {
            sheet.Rows.Count = Math.Max(written, 1);

            if (spec.IncludeHeader)
            {
                for (var column = 0; column < columns; column++)
                {
                    sheet.Cells[0, column].SetText(spec.TitleAt(column));
                }
            }

            var range = new RangeRef(new CellRef(0, 0), new CellRef(written - 1, Math.Max(columns - 1, 0)));

            SaveTable(archive, sheet.AddTable(spec.TableName!, range, hasHeaders: spec.IncludeHeader), tableId: 1);

            relationships.Add(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table", "../tables/table1.xml", false));
        }

        if (journal.Count > 0)
        {
            SaveStreamedDrawing(archive, journal);

            relationships.Add((DrawingRelationshipId(spec, written), "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing", "../drawings/drawing1.xml", false));
        }

        if (relationships.Count > 0)
        {
            SaveSheetRelationships(archive, "sheet1.xml", relationships);
        }

        return table;
```

with `private static string DrawingRelationshipId(StreamedSheet spec, int written) => HasTable(spec, written) ? "rId2" : "rId1";`.

4. In `WriteStreamedSheetXmlAsync`, hoist `rNs` above `CreatePageMargins().WriteTo(writer);` and write, between the page margins and the table parts:

```csharp
        if (journal.Count > 0)
        {
            new XElement(XName.Get("drawing", Main), new XAttribute(rNs + "id", DrawingRelationshipId(spec, written)))
                .WriteTo(writer);
        }
```

5. In `WriteStreamedRowsAsync`, build `var images = new bool[columns];` filled from `spec.IsImageAt`, and pass `images` and `journal` to `WriteStreamedRow`. In `WriteStreamedRow`, the spans condition becomes `line[column] is not null && !images[column]`. The cell loop becomes:

```csharp
            var value = line[column];

            if (value is null)
            {
                continue;
            }

            if (images[column])
            {
                journal.Add(new CellRef(row, column), (StreamedImage)value);

                continue;
            }

            CellData.Infer(value, sheet.Workbook!.Culture, out var content, out var type);
```

6. The drawing parts:

```csharp
    private const string SpreadsheetDrawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    private const string DrawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private const string OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private void SaveStreamedDrawing(ZipArchive archive, StreamedImageJournal journal)
    {
        var extensions = journal.Extensions;

        journal.CopyMedia(index => archive.CreateEntry($"xl/media/image{index + 1}.{extensions[index]}").Open());

        using (var entry = archive.CreateEntry("xl/drawings/drawing1.xml").Open())
        using (var writer = XmlWriter.Create(entry, PartXmlSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("xdr", "wsDr", SpreadsheetDrawing);
            writer.WriteAttributeString("xmlns", "a", null, DrawingMain);
            writer.WriteAttributeString("xmlns", "r", null, OfficeRelationships);

            var id = 1;

            foreach (var (row, column, media) in journal.Anchors())
            {
                id++;

                writer.WriteStartElement("twoCellAnchor", SpreadsheetDrawing);
                WriteDrawingMarker(writer, "from", column, row);
                WriteDrawingMarker(writer, "to", column + 1, row + 1);

                writer.WriteStartElement("pic", SpreadsheetDrawing);
                writer.WriteStartElement("nvPicPr", SpreadsheetDrawing);
                writer.WriteStartElement("cNvPr", SpreadsheetDrawing);
                WriteNumberAttribute(writer, "id", id);
                writer.WriteAttributeString("name", string.Create(CultureInfo.InvariantCulture, $"Image {id - 1}"));
                writer.WriteEndElement();
                writer.WriteStartElement("cNvPicPr", SpreadsheetDrawing);
                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteStartElement("blipFill", SpreadsheetDrawing);
                writer.WriteStartElement("blip", DrawingMain);
                writer.WriteAttributeString("embed", OfficeRelationships, ImageRelationshipId(media));
                writer.WriteEndElement();
                writer.WriteStartElement("stretch", DrawingMain);
                writer.WriteStartElement("fillRect", DrawingMain);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteStartElement("spPr", SpreadsheetDrawing);
                writer.WriteStartElement("prstGeom", DrawingMain);
                writer.WriteAttributeString("prst", "rect");
                writer.WriteStartElement("avLst", DrawingMain);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteEndElement();

                writer.WriteStartElement("clientData", SpreadsheetDrawing);
                writer.WriteEndElement();

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        using (var entry = archive.CreateEntry("xl/drawings/_rels/drawing1.xml.rels").Open())
        using (var writer = XmlWriter.Create(entry, PartXmlSettings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Relationships", PackageRelationships);

            for (var media = 0; media < extensions.Count; media++)
            {
                writer.WriteStartElement("Relationship", PackageRelationships);
                writer.WriteAttributeString("Id", ImageRelationshipId(media));
                writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image");
                writer.WriteAttributeString("Target", $"../media/image{media + 1}.{extensions[media]}");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
    }

    private static string ImageRelationshipId(int media) =>
        string.Create(CultureInfo.InvariantCulture, $"rId{media + 1}");

    private void WriteDrawingMarker(XmlWriter writer, string name, int column, int row)
    {
        writer.WriteStartElement(name, SpreadsheetDrawing);
        WriteDrawingNumber(writer, "col", column);
        writer.WriteElementString("colOff", SpreadsheetDrawing, "0");
        WriteDrawingNumber(writer, "row", row);
        writer.WriteElementString("rowOff", SpreadsheetDrawing, "0");
        writer.WriteEndElement();
    }

    private void WriteDrawingNumber(XmlWriter writer, string name, int value)
    {
        value.TryFormat(scratch, out var length, provider: CultureInfo.InvariantCulture);

        writer.WriteStartElement(name, SpreadsheetDrawing);
        writer.WriteRaw(scratch, 0, length);
        writer.WriteEndElement();
    }
```

7. `SaveContentTypes(ZipArchive archive, bool includeSharedStrings, int tableCount = 0, IReadOnlyList<string>? streamedImageExtensions = null)`: before the `foreach (var ext in imageExtensions)` loop, add each streamed extension to `imageExtensions`. After the per-sheet drawing loop, add the `/xl/drawings/drawing1.xml` override when `streamedImageExtensions is { Count: > 0 }`. The built path passes nothing, so its bytes do not move.

8. `Workbook.SaveToStreamAsync` docs: add `<exception cref="ArgumentException">A column reads images and also sets <see cref="StreamedColumn{T}.Value"/> or <see cref="StreamedColumn{T}.AutoFit"/>.</exception>`, and extend the `InvalidOperationException` text with "or an image yields no content type and its bytes are not a format it recognizes".

- [ ] **Step 6: Run to verify they pass, then the spreadsheet suite**

Run the filter for `StreamedSheetImageTests`, then `StreamedSheet`, then `Spreadsheet`. Expected: all pass.

- [ ] **Step 7: Mutation-check**

Snapshot `XlsxWriter.Streaming.cs`, `StreamedImageJournal.cs`, `StreamedSheet.cs` and `XlsxWriter.cs`. One mutation at a time, confirm the build succeeds, name the test that fails, then restore everything with `cp`:
- drop `&& !images[column]` from spans (`Writes_no_cell_under_an_image`);
- skip the `TryGetValue` so every image is new (`Stores_an_image_repeated_across_rows_once`);
- a repeat writes `extensions.Count - 1` instead of `index` (same test);
- `to` written as `column, row` (`Draws_each_image_over_its_own_cell`);
- `DrawingRelationshipId` always `"rId1"` (`A_table_and_a_drawing_both_resolve`);
- the `<drawing>` element not written (`Draws_each_image_over_its_own_cell`);
- no seek past bytes in `Anchors` (`Draws_each_image_over_its_own_cell`);
- no drawing override in content types (`Declares_the_drawing_and_its_media_type`);
- either validation removed (the matching `Refuses_` test);
- `HasValue` becomes `Value is not null` (every test, since the default `Value` is non-null).

- [ ] **Step 8: Review, then commit**

```bash
git add -A Radzen.Blazor/Documents/Spreadsheet Radzen.Blazor.Tests/Spreadsheet/StreamedSheetImageTests.cs
git commit -m "Draw a streamed column's images over their cells

The images wait in a journal until sheet1.xml closes, since the zip holds
one open entry. Then the media, the drawing and its rels are replayed from it."
```

---

### Task 4: `DataRowHeight`

**Files:**
- Modify: `Radzen.Blazor/Documents/Spreadsheet/StreamedSheet.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs` (`WriteRowStart`)
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs` (`WriteStreamedRow`)
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetImageTests.cs`

**Interfaces:**
- Consumes: `PointsPerPixel` (Task 1); the test helpers (Task 3).
- Produces: `public double? DataRowHeight { get; set; }` on `StreamedSheet`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Every_data_row_takes_the_declared_height()
    {
        var spec = Sheet(With("a", 1), new Item("b", null));
        spec.DataRowHeight = 48;

        var stream = await Stream(spec);
        var sheet = Read(stream);

        Assert.Equal(48, sheet.Rows[1]);
        Assert.Equal(48, sheet.Rows[2]);
        Assert.Null(Part(stream, "xl/worksheets/sheet1.xml")!.Descendants(Main + "row").First().Attribute("ht"));
    }

    [Fact]
    public async Task A_declared_height_equal_to_the_default_is_still_written()
    {
        var spec = Sheet(With("a", 1));
        spec.DataRowHeight = 22;

        var row = Part(await Stream(spec), "xl/worksheets/sheet1.xml")!
            .Descendants(Main + "row").Single(r => (string?)r.Attribute("r") == "2");

        Assert.Equal("16.5", (string?)row.Attribute("ht"));
        Assert.Equal("1", (string?)row.Attribute("customHeight"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(546)]
    [InlineData(double.NaN)]
    public void Refuses_a_height_a_row_cannot_have(double height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamedSheet<Item> { DataRowHeight = height });
    }

    [Fact]
    public void Accepts_the_tallest_row_there_is()
    {
        var spec = new StreamedSheet<Item> { DataRowHeight = 409 * 96.0 / 72.0 };

        Assert.Equal(409 * 96.0 / 72.0, spec.DataRowHeight);
    }
```

- [ ] **Step 2: Run to verify they fail**

Expected: a build failure, because `DataRowHeight` does not exist.

- [ ] **Step 3: Implement**

In `StreamedSheet`:

```csharp
    private double? dataRowHeight;

    /// <summary>
    /// The height of every row below the header, in pixels, as <see cref="StreamedColumn{T}.Width"/> is.
    /// Null leaves them at the default height. A column of images fills its cells, so this is what sizes
    /// the pictures.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The height is not greater than zero, or is taller than the 409 points (about 545 pixels) a row can be.
    /// </exception>
    public double? DataRowHeight
    {
        get => dataRowHeight;
        set
        {
            if (value is { } height && !(height > 0 && height <= 409 * 96.0 / 72.0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), height,
                    "A row is more than zero and at most 409 points, about 545 pixels, tall.");
            }

            dataRowHeight = value;
        }
    }
```

`WriteRowStart` gains `double? height = null` as its last parameter:

```csharp
        var size = height ?? sheet.Rows[row];

        if (height is not null || Math.Abs(size - sheet.Rows.Size) > 1e-6)
        {
            writer.WriteAttributeString("ht", XmlConvert.ToString(size * PointsPerPixel));
            writer.WriteAttributeString("customHeight", "1");
        }
```

`WriteStreamedRow` calls `WriteRowStart(writer, sheet, row, first, last, spec.DataRowHeight);`. The header call is unchanged.

- [ ] **Step 4: Run to verify they pass, then the spreadsheet suite**

- [ ] **Step 5: Mutation-check**

Drop `height is not null ||` and confirm `A_declared_height_equal_to_the_default_is_still_written` fails. Pass `spec.DataRowHeight` to the header too and confirm `Every_data_row_takes_the_declared_height` fails. Change `<=` to `<` and confirm `Accepts_the_tallest_row_there_is` fails. Change the guard to `height <= 0 || height > max` and confirm the NaN case fails. Restore with `cp`.

- [ ] **Step 6: Review, then commit**

```bash
git commit -m "Let a streamed sheet declare its data rows' height

Written per row as it streams, since the axis keeps one entry per row it is
given. An image column fills its cells, so this sizes the pictures."
```

---

### Task 5: `SpillImages`

**Files:**
- Modify: `Radzen.Blazor/Documents/Spreadsheet/StreamedSheet.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs`
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetImageTests.cs`

**Interfaces:**
- Consumes: `StreamedImageJournal(Func<Stream>)` (Task 3).
- Produces: `public bool SpillImages { get; set; }`, and `internal string SpillDirectory { get; set; }` as the test seam.

- [ ] **Step 1: Write the failing tests**

```csharp
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName())).FullName;

        public int Files => Directory.GetFiles(Path).Length;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private static async IAsyncEnumerable<Item> Watched(Scratch scratch, Action<int> seen, Item first, Item second)
    {
        await Task.Yield();

        yield return first;

        seen(scratch.Files);

        yield return second;
    }

    [Fact]
    public async Task Spilling_holds_the_images_in_a_file_while_the_rows_stream()
    {
        using var scratch = new Scratch();
        var during = -1;

        var spec = Sheet();
        spec.Rows = Watched(scratch, n => during = n, With("a", 1), With("b", 2));
        spec.SpillImages = true;
        spec.SpillDirectory = scratch.Path;

        await Stream(spec);

        Assert.Equal(1, during);
        Assert.Equal(0, scratch.Files);
    }

    [Fact]
    public async Task Spilling_a_sheet_without_images_opens_no_file()
    {
        using var scratch = new Scratch();
        var during = -1;

        var spec = Sheet();
        spec.Rows = Watched(scratch, n => during = n, new Item("a", null), new Item("b", null));
        spec.SpillImages = true;
        spec.SpillDirectory = scratch.Path;

        await Stream(spec);

        Assert.Equal(0, during);
    }

    [Fact]
    public async Task A_failed_write_leaves_no_spill_behind()
    {
        using var scratch = new Scratch();

        var spec = Sheet();
        spec.Rows = Faulting();
        spec.SpillImages = true;
        spec.SpillDirectory = scratch.Path;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Workbook.SaveToStreamAsync(new MemoryStream(), spec));

        Assert.Equal(0, scratch.Files);

        static async IAsyncEnumerable<Item> Faulting()
        {
            await Task.Yield();

            yield return With("a", 1);

            throw new InvalidOperationException("the source failed");
        }
    }

    [Fact]
    public async Task Spilling_writes_the_same_file()
    {
        using var scratch = new Scratch();

        Item[] items = [With("a", 1), With("b", 2), With("c", 1), new Item("d", null)];

        var memory = await Stream(Sheet(items));

        var spilled = Sheet(items);
        spilled.SpillImages = true;
        spilled.SpillDirectory = scratch.Path;

        var disk = await Stream(spilled);

        Assert.Equal(Parts(memory), Parts(disk));
    }

    private static SortedDictionary<string, string> Parts(MemoryStream stream)
    {
        XNamespace revision = "http://schemas.microsoft.com/office/spreadsheetml/2014/revision";

        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries.Where(e => e.FullName != "docProps/core.xml"))
        {
            using var content = entry.Open();
            using var copy = new MemoryStream();

            content.CopyTo(copy);

            parts[entry.FullName] = entry.FullName == "xl/worksheets/sheet1.xml"
                ? WithoutUid(copy.ToArray(), revision)
                : Convert.ToBase64String(copy.ToArray());
        }

        return parts;
    }

    private static string WithoutUid(byte[] xml, XNamespace revision)
    {
        var document = XDocument.Load(new MemoryStream(xml));

        document.Root!.Attribute(revision + "uid")?.Remove();

        return document.ToString(SaveOptions.DisableFormatting);
    }
```

- [ ] **Step 2: Run to verify they fail**

Expected: a build failure, because `SpillImages` does not exist.

- [ ] **Step 3: Implement**

In `StreamedSheet`:

```csharp
    /// <summary>
    /// Holds the images in a temporary file until the sheet has been written, instead of in memory. Off,
    /// what the write holds grows with the bytes of the distinct images it is given; on, it holds about
    /// fifty bytes for each distinct image, and the file is deleted when the write ends, whether or not
    /// it succeeds. A sheet that yields no image opens no file.
    /// </summary>
    public bool SpillImages { get; set; }

    internal string SpillDirectory { get; set; } = Path.GetTempPath();
```

In the instance `WriteStreamedAsync`:

```csharp
        using var journal = new StreamedImageJournal(spec.SpillImages
            ? () => Spill(spec.SpillDirectory)
            : static () => new MemoryStream());
```

```csharp
    private static FileStream Spill(string directory) =>
        new(Path.Combine(directory, Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 81_920, FileOptions.DeleteOnClose);
```

Add `using System.IO;` to `StreamedSheet.cs` if it is missing. Amend the `StreamedSheet` class summary, "costs the same in memory whether the source has a hundred rows or a million", to add "images aside; see `SpillImages`".

- [ ] **Step 4: Run to verify they pass, then the spreadsheet suite**

- [ ] **Step 5: Mutation-check**

Ignore `SpillImages` (always memory) and confirm `Spilling_holds_the_images_in_a_file_while_the_rows_stream` fails. Drop `FileOptions.DeleteOnClose` and confirm that test and `A_failed_write_leaves_no_spill_behind` fail. Open the store eagerly in the journal's constructor and confirm `Spilling_a_sheet_without_images_opens_no_file` fails. Restore with `cp`.

- [ ] **Step 6: Review, then commit**

```bash
git commit -m "Spill a streamed sheet's images to a temporary file on request

The journal is backed by a file deleted on close rather than memory, so what
a write holds no longer grows with the image bytes. Same file either way."
```

---

### Task 6: Verification, on the fork branches

**Files:**
- Create: `gridbench/spreadsheet/StreamedImageFloor.cs` on `gridbench/spreadsheet-perf` (`radzen-spreadsheet-bench`), plus a README row
- Modify: `gridbench/SLIM-GRID-SPEC.md` §69 on `tech/fastgrid-streamed-export`, with the results

- [ ] **Step 1: Build as upstream CI does**

`dotnet build Radzen.Blazor/Radzen.Blazor.csproj -c Debug`, then `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Debug`. Report the pass count against the 5,236 on the PR tip.

- [ ] **Step 2: Allocation sweep**

`StreamedImageFloor.cs`, modelled on `StreamedExportFloor.cs` with its `Sink` and interleaved passes, and with no comments. Prebuild 2,000 distinct 32 KB images outside the armed window. Run arms `memory` and `spill` at 500, 1,000 and 2,000 image rows, interleaved, three passes each, and report MB and the growth per step. Expected: **memory grows by at least the added image bytes per step** (16 MB per 500 × 32 KB), which is the arm that can be worked out by hand, and **spill grows by under 1 KB per image**. If memory does not grow, the rig is wrong and the spill figure is not quoted. Both arms assert the sink received more than the image bytes.

- [ ] **Step 3: Northwind photos**

Decode the five `Employee.Photo` data URIs (the Northwind seed in `RadzenBlazorDemos`) to bytes, and stream them with no content type given, so the sniffer runs, with `DataRowHeight = 64` and `Width = 64`, into `scratchpad/northwind-streamed.xlsx`. Confirm there are five `xl/media/*.jpeg` entries, then `open -a "Numbers Creator Studio"` and screenshot. Say plainly that Excel has not been checked.

- [ ] **Step 4: Record and commit**

Add the sweep table, the byte-gate result and the Northwind check to §69. Commit the harness on `gridbench/spreadsheet-perf` and the spec on `tech/fastgrid-streamed-export`.
