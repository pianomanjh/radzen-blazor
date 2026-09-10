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
    public async Task Stores_an_image_without_compressing_it_again()
    {
        var data = new byte[10_008];
        Png(0).AsSpan(0, 8).CopyTo(data);

        var stream = await Stream(Sheet(new Item("a", new StreamedImage(data))));

        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var media = zip.GetEntry("xl/media/image1.png")!;

        Assert.Equal(data.Length, media.Length);
        Assert.True(media.CompressedLength >= media.Length, $"{media.CompressedLength} of {media.Length}");
    }

    [Fact]
    public async Task Keeps_the_type_it_was_given()
    {
        var stream = await Stream(Sheet(new Item("a", new StreamedImage(Png(1), "image/jpeg"))));

        Assert.Equal(new[] { "xl/media/image1.jpeg" }, Entries(stream, "xl/media/"));
        Assert.Equal("image/jpeg", Read(stream).Images.Single().ContentType);
    }

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
}
