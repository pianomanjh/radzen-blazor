using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class WorkbookStreamedCellTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly DateTime Seed = new(2024, 3, 1, 13, 30, 0);

    private static readonly Format Money = new() { NumberFormat = "#,##0.00" };

    private static readonly Format Shaded = new() { BackgroundColor = "#EEEEEE" };

    private static async IAsyncEnumerable<StreamedCell[]> Rows(params StreamedCell[][] rows)
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
        var sheet = workbook.AddSheet("Sheet1", 1, 4);

        sheet.Cells[0, 0].SetValue("Name");

        return workbook;
    }

    private static async Task<MemoryStream> Save(IAsyncEnumerable<StreamedCell[]> rows)
    {
        var stream = new MemoryStream();

        await Framed().SaveToStreamAsync(stream, rows);

        stream.Position = 0;

        return stream;
    }

    private static string SheetXml(MemoryStream stream)
    {
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());

        return Regex.Replace(reader.ReadToEnd(), " \\w+:uid=\"[^\"]*\"", string.Empty);
    }

    private static Cell Read(MemoryStream stream, int row, int column)
    {
        stream.Position = 0;

        return Workbook.LoadFromStream(stream).Sheets[0].Cells[row, column];
    }

    [Fact]
    public async Task The_typed_factories_write_what_cell_data_writes()
    {
        var typed = await Save(Rows(
            [StreamedCell.FromString("Alice"), StreamedCell.FromNumber(1234.5, Money), StreamedCell.FromDate(Seed), StreamedCell.FromBoolean(true, Shaded)],
            [StreamedCell.FromString("00123", Shaded), StreamedCell.FromNumber(-0.125), StreamedCell.FromDate(Seed.AddDays(-400), Money), StreamedCell.FromBoolean(false)]));

        var data = await Save(Rows(
            [new(CellData.FromString("Alice")), new(CellData.FromNumber(1234.5), Money), new(CellData.FromDate(Seed)), new(CellData.FromBoolean(true), Shaded)],
            [new(CellData.FromString("00123"), Shaded), new(CellData.FromNumber(-0.125)), new(CellData.FromDate(Seed.AddDays(-400)), Money), new(CellData.FromBoolean(false))]));

        Assert.Equal(SheetXml(data), SheetXml(typed));
    }

    [Fact]
    public async Task A_boolean_reads_back_as_the_boolean_it_was()
    {
        var stream = await Save(Rows([StreamedCell.FromBoolean(true), StreamedCell.FromBoolean(false), default, default]));

        Assert.Equal(true, Read(stream, 1, 0).Value);
        Assert.Equal(false, Read(stream, 1, 1).Value);
    }

    [Fact]
    public async Task A_date_reads_back_as_its_serial_under_a_date_format()
    {
        var cell = Read(await Save(Rows([StreamedCell.FromDate(Seed), default, default, default])), 1, 0);

        Assert.Equal(Seed.ToOADate(), cell.Value);
        Assert.False(string.IsNullOrEmpty(cell.Format.NumberFormat));
    }

    [Fact]
    public async Task Text_from_the_factory_stays_text()
    {
        var cell = Read(await Save(Rows([StreamedCell.FromString("00123"), default, default, default])), 1, 0);

        Assert.Equal("00123", cell.Value);
    }

    [Fact]
    public async Task Text_of_only_spaces_from_the_factory_stays_as_it_was()
    {
        var cell = Read(await Save(Rows([StreamedCell.FromString("   "), default, default, default])), 1, 0);

        Assert.Equal("   ", cell.Value);
    }

    public static TheoryData<object, object> Values => new()
    {
        { 7, 7.0 },
        { 7L, 7.0 },
        { 1.25m, 1.25 },
        { 2.5f, 2.5 },
        { (short)3, 3.0 },
        { (byte)4, 4.0 },
        { 5.5, 5.5 },
        { true, true },
        { "00123", "00123" },
        { (uint)9, 9.0 },
        { (ulong)6, 6.0 },
        { 9_007_199_254_740_993UL, 9_007_199_254_740_992.0 },
        { (ushort)8, 8.0 },
        { (sbyte)-2, -2.0 },
        { -1.5f, -1.5 },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task A_value_known_at_run_time_is_typed_as_a_cell_types_it(object value, object expected)
    {
        var cell = Read(await Save(Rows([StreamedCell.From(value), default, default, default])), 1, 0);

        Assert.Equal(expected, cell.Value);
    }

    [Fact]
    public async Task A_date_known_at_run_time_is_written_as_a_date()
    {
        var stream = await Save(Rows(
            [StreamedCell.From(Seed), default, default, default],
            [StreamedCell.FromDate(Seed), default, default, default]));

        var cells = XDocument.Parse(SheetXml(stream)).Descendants(Main + "c").Where(c => ((string?)c.Attribute("r"))!.StartsWith('A')).Skip(1).ToArray();

        Assert.Equal(cells[0].Attribute("s")?.Value, cells[1].Attribute("s")?.Value);
        Assert.Equal(cells[0].Value, cells[1].Value);
    }

    [Fact]
    public async Task Cell_data_known_at_run_time_is_used_as_it_is()
    {
        var cell = Read(await Save(Rows([StreamedCell.From(CellData.FromString("42")), default, default, default])), 1, 0);

        Assert.Equal("42", cell.Value);
    }

    [Fact]
    public async Task Nothing_is_written_for_no_value()
    {
        var stream = await Save(Rows([StreamedCell.From(null), default, StreamedCell.FromString("x"), new StreamedCell(null)]));

        var cells = XDocument.Parse(SheetXml(stream)).Descendants(Main + "row").Skip(1).Single().Elements(Main + "c");

        Assert.Equal(new[] { "C2" }, cells.Select(c => (string?)c.Attribute("r")));
    }

    [Fact]
    public async Task No_value_known_at_run_time_still_takes_its_format()
    {
        var stream = await Save(Rows([StreamedCell.From(null, Shaded), StreamedCell.FromString("Alice", Shaded), default, default]));

        var cells = XDocument.Parse(SheetXml(stream)).Descendants(Main + "row").Skip(1).Single().Elements(Main + "c").ToArray();

        Assert.Equal(new[] { "A2", "B2" }, cells.Select(c => (string?)c.Attribute("r")));
        Assert.Empty(cells[0].Nodes());
        Assert.NotNull(cells[1].Attribute("s"));
        Assert.Equal((string?)cells[1].Attribute("s"), (string?)cells[0].Attribute("s"));
    }

    [Fact]
    public void Refuses_no_text()
    {
        Assert.Throws<ArgumentNullException>(() => StreamedCell.FromString(null!));
    }
}
