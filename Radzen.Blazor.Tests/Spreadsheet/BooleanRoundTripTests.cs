using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Xml.Linq;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class BooleanRoundTripTests
{
    private static Worksheet RoundTrip(Workbook workbook)
    {
        using var stream = new MemoryStream();

        workbook.SaveToStream(stream);
        stream.Position = 0;

        return Workbook.LoadFromStream(stream).Sheets[0];
    }

    [Fact]
    public void BooleanSurvivesTheRoundTrip()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 3, 1);

        sheet.Cells[0, 0].Value = true;
        sheet.Cells[1, 0].Value = false;

        Assert.Equal(CellDataType.Boolean, sheet.Cells[0, 0].ValueType);

        var loaded = RoundTrip(workbook);

        Assert.Equal(CellDataType.Boolean, loaded.Cells[0, 0].ValueType);
        Assert.Equal(true, loaded.Cells[0, 0].Value);

        Assert.Equal(CellDataType.Boolean, loaded.Cells[1, 0].ValueType);
        Assert.Equal(false, loaded.Cells[1, 0].Value);
    }

    // ECMA-376 types <v> as ST_Xstring, so a producer may write the word. This writer writes 1 and 0,
    // which is why the file has to be edited to produce the case at all.
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public void ABooleanWrittenAsAWordIsRead(string written, bool expected)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 2, 1);

        sheet.Cells[0, 0].Value = true;

        using var saved = new MemoryStream();

        workbook.SaveToStream(saved);
        saved.Position = 0;

        using var edited = WithCellValue(saved, written);

        var loaded = Workbook.LoadFromStream(edited).Sheets[0];

        Assert.Equal(CellDataType.Boolean, loaded.Cells[0, 0].ValueType);
        Assert.Equal(expected, loaded.Cells[0, 0].Value);
    }

    // Numbers keep the branch they always took: the boolean is chosen by t="b" and not by what the
    // text happens to say, so a cell holding 1 is still the number 1.
    [Fact]
    public void NumbersAreUnaffected()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 3, 1);

        sheet.Cells[0, 0].Value = 42;
        sheet.Cells[1, 0].Value = 1;
        sheet.Cells[2, 0].Value = 0;

        var loaded = RoundTrip(workbook);

        Assert.Equal(CellDataType.Number, loaded.Cells[0, 0].ValueType);
        Assert.Equal(42d, loaded.Cells[0, 0].Value);

        Assert.Equal(CellDataType.Number, loaded.Cells[1, 0].ValueType);
        Assert.Equal(1d, loaded.Cells[1, 0].Value);

        Assert.Equal(CellDataType.Number, loaded.Cells[2, 0].ValueType);
        Assert.Equal(0d, loaded.Cells[2, 0].Value);
    }

    /// <summary>Rewrites the single cell's &lt;v&gt;, so the file reads as another producer's.</summary>
    private static MemoryStream WithCellValue(Stream source, string value)
    {
        var result = new MemoryStream();
        source.CopyTo(result);
        result.Position = 0;

        using (var archive = new ZipArchive(result, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
            XDocument doc;
            using (var stream = entry.Open())
            {
                doc = XDocument.Load(stream);
            }

            var ns = doc.Root!.Name.Namespace;
            var cell = doc.Descendants(ns + "c").First();

            Assert.Equal("b", cell.Attribute("t")!.Value);

            cell.Element(ns + "v")!.Value = value;

            entry.Delete();
            var newEntry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var output = newEntry.Open();
            doc.Save(output);
        }

        result.Position = 0;
        return result;
    }
}
