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

public class WorkbookStreamedFormatsTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static async IAsyncEnumerable<T[]> Rows<T>(params T[][] rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();

            yield return row;
        }
    }

    private static Workbook Framed()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 1, 2);

        sheet.Cells[0, 0].SetValue("Name");
        sheet.Cells[0, 1].SetValue("Total");

        return workbook;
    }

    private static async Task<MemoryStream> Save(Workbook workbook, IAsyncEnumerable<StreamedCell[]> rows)
    {
        var stream = new MemoryStream();

        await workbook.SaveToStreamAsync(stream, rows);

        stream.Position = 0;

        return stream;
    }

    private static Worksheet Read(MemoryStream stream)
    {
        stream.Position = 0;

        return Workbook.LoadFromStream(stream).Sheets[0];
    }

    private static XDocument Sheet(MemoryStream stream)
    {
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var content = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();

        return XDocument.Load(content);
    }

    private static XElement[] Cells(MemoryStream stream, string column) =>
        Sheet(stream).Descendants(Main + "c")
            .Where(c => ((string?)c.Attribute("r"))!.StartsWith(column, StringComparison.Ordinal))
            .Skip(1)
            .ToArray();

    private static XElement[] Row(MemoryStream stream, string number) =>
        Sheet(stream).Descendants(Main + "row")
            .Single(r => (string?)r.Attribute("r") == number)
            .Elements(Main + "c")
            .ToArray();

    [Fact]
    public async Task Alternating_formats_make_two_styles()
    {
        var even = new Format { BackgroundColor = "#EEEEEE" };
        var odd = new Format { BackgroundColor = "#FFFFFF" };

        var stream = await Save(Framed(), Rows(Enumerable.Range(0, 4)
            .Select(i => new StreamedCell[] { new StreamedCell(CellData.FromString($"row {i}"), i % 2 == 0 ? even : odd), default })
            .ToArray()));

        var styles = Cells(stream, "A").Select(c => (string?)c.Attribute("s")).ToArray();

        Assert.Equal(4, styles.Length);
        Assert.Equal(2, styles.Distinct().Count());
        Assert.Equal(styles[0], styles[2]);
        Assert.Equal(styles[1], styles[3]);
        Assert.NotEqual(styles[0], styles[1]);

        var sheet = Read(stream);

        Assert.Equal(sheet.Cells[1, 0].Format.BackgroundColor, sheet.Cells[3, 0].Format.BackgroundColor);
        Assert.NotEqual(sheet.Cells[1, 0].Format.BackgroundColor, sheet.Cells[2, 0].Format.BackgroundColor);
    }

    [Fact]
    public async Task A_number_format_applies_to_its_own_cell()
    {
        var sheet = Read(await Save(Framed(), Rows(
            new StreamedCell[] { default, new StreamedCell(CellData.FromNumber(1.5), new Format { NumberFormat = "0.000" }) },
            new StreamedCell[] { default, new StreamedCell(CellData.FromNumber(2)) })));

        Assert.Equal("0.000", sheet.Cells[1, 1].Format.NumberFormat);
        Assert.True(string.IsNullOrEmpty(sheet.Cells[2, 1].Format.NumberFormat));
    }

    [Fact]
    public async Task A_date_keeps_a_date_format_under_a_format_that_names_none()
    {
        var cell = Read(await Save(Framed(), Rows(
            new StreamedCell[] { default, new StreamedCell(CellData.FromDate(new DateTime(2024, 3, 1)), new Format { Bold = true }) }))).Cells[1, 1];

        Assert.True(cell.Format.Bold);
        Assert.False(string.IsNullOrEmpty(cell.Format.NumberFormat));
    }

    [Fact]
    public async Task Text_that_looks_like_a_number_stays_text_under_a_format()
    {
        var cell = Read(await Save(Framed(), Rows(
            new StreamedCell[] { new StreamedCell(CellData.FromString("00123"), new Format { Bold = true }), default }))).Cells[1, 0];

        Assert.Equal("00123", cell.Value);
        Assert.True(cell.Format.Bold);
    }

    [Fact]
    public async Task A_default_format_writes_no_style()
    {
        var stream = await Save(Framed(), Rows(
            new StreamedCell[] { new StreamedCell(CellData.FromString("Alice"), new Format()), default }));

        Assert.Null(Cells(stream, "A").Single().Attribute("s"));
    }

    [Fact]
    public async Task A_blank_cell_keeps_its_format_on_either_side_of_a_value()
    {
        var shaded = new Format { BackgroundColor = "#EEEEEE" };

        var stream = await Save(Framed(), Rows(new StreamedCell[]
        {
            new StreamedCell(null, shaded),
            new StreamedCell(CellData.FromString("Alice"), shaded),
            new StreamedCell(null, shaded),
        }));

        var cells = Row(stream, "2");

        Assert.Equal(new[] { "A2", "B2", "C2" }, cells.Select(c => (string?)c.Attribute("r")));
        Assert.NotNull(cells[1].Attribute("s"));
        Assert.All(cells, c => Assert.Equal((string?)cells[1].Attribute("s"), (string?)c.Attribute("s")));
        Assert.Empty(cells[0].Nodes());
        Assert.Empty(cells[2].Nodes());
    }

    [Fact]
    public async Task A_blank_cell_without_a_format_writes_nothing()
    {
        var stream = await Save(Framed(), Rows(new StreamedCell[]
        {
            new StreamedCell(null, new Format()),
            new StreamedCell(CellData.FromString("Alice")),
            new StreamedCell(null, new Format()),
            new StreamedCell(null),
            new StreamedCell(CellData.FromString("Bob")),
        }));

        Assert.Equal(new[] { "B2", "E2" }, Row(stream, "2").Select(c => (string?)c.Attribute("r")));
    }

    [Fact]
    public async Task Rows_without_formats_write_what_rows_with_no_format_write()
    {
        CellData?[][] data =
        [
            [CellData.FromString("a"), CellData.FromNumber(1)],
            [CellData.FromString("00123"), CellData.FromDate(new DateTime(2024, 3, 1))],
            [null, CellData.FromBoolean(true)],
        ];

        var plain = new MemoryStream();
        await Framed().SaveToStreamAsync(plain, Rows(data));

        var tupled = await Save(Framed(), Rows(data.Select(r => r.Select(d => new StreamedCell(d)).ToArray()).ToArray()));

        Assert.Equal(Parts(plain), Parts(tupled));
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
            var document = XDocument.Load(content);

            document.Root!.Attribute(revision + "uid")?.Remove();

            parts[entry.FullName] = document.ToString(SaveOptions.DisableFormatting);
        }

        return parts;
    }

    [Fact]
    public async Task A_new_format_for_every_cell_still_makes_one_style_per_look()
    {
        var stream = await Save(Framed(), Rows(Enumerable.Range(0, 2_000)
            .Select(i => new StreamedCell[] { new StreamedCell(CellData.FromString($"row {i}"), new Format { Bold = i % 2 == 0 }), default })
            .ToArray()));

        var styles = Cells(stream, "A").Select(c => (string?)c.Attribute("s")).ToArray();

        Assert.Equal(2_000, styles.Length);
        Assert.Equal(1, styles.Where((_, i) => i % 2 == 0).Distinct().Count());
        Assert.Equal(1, styles.Where((_, i) => i % 2 == 1).Distinct().Count());
    }
}
