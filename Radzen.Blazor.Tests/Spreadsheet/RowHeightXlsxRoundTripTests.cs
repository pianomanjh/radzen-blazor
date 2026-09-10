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
