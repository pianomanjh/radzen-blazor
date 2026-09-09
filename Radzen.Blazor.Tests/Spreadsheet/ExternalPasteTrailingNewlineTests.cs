using Xunit;
using Radzen.Documents.Spreadsheet;

namespace Radzen.Blazor.Spreadsheet.Tests;

public class ExternalPasteTrailingNewlineTests
{
    [Theory]
    [InlineData("1\r\n2\r\n", new[] { "1", "2" })]
    [InlineData("1\r\n\r\n", new[] { "1", "" })]
    [InlineData("1\r\n\r\n\r\n", new[] { "1", "", "" })]
    [InlineData("1\n", new[] { "1", "" })]
    [InlineData("1\r\n2", new[] { "1", "2" })]
    [InlineData("001\t\t\r\n", new[] { "001\t\t" })]
    public void SplitRows_EndsTheLastRowAtOneTrailingCrLf(string text, string[] expected)
    {
        Assert.Equal(expected, Worksheet.SplitRows(text));
    }

    [Fact]
    public void GetPasteRange_DoesNotCountATrailingCrLfAsARow()
    {
        var sheet = new Worksheet(10, 5);
        var clipboard = new SpreadsheetClipboard();

        Assert.Equal(RangeRef.Parse("A1:A2"), clipboard.GetPasteRange(sheet, RangeRef.Parse("A1"), "1\r\n2\r\n"));
    }

    [Fact]
    public void PasteCommand_LeavesTheCellBelowAnExcelRangeAlone()
    {
        var sheet = new Worksheet(10, 5);
        sheet.Cells[2, 0].Value = 99d;
        var clipboard = new SpreadsheetClipboard();

        Assert.True(new PasteCommand(clipboard, sheet, RangeRef.Parse("A1"), "1\r\n2\r\n").Execute());

        Assert.Equal(1d, sheet.Cells[0, 0].Value);
        Assert.Equal(2d, sheet.Cells[1, 0].Value);
        Assert.Equal(99d, sheet.Cells[2, 0].Value);
    }

    [Fact]
    public void PasteCommand_PastesAnExcelRangeAboveALockedRow()
    {
        var sheet = new Worksheet(10, 5);
        sheet.Cells[0, 0].Format.Locked = false;
        sheet.Cells[1, 0].Format.Locked = false;
        sheet.Protection.IsProtected = true;
        var clipboard = new SpreadsheetClipboard();

        Assert.True(new PasteCommand(clipboard, sheet, RangeRef.Parse("A1"), "1\r\n2\r\n").Execute());

        Assert.Equal(2d, sheet.Cells[1, 0].Value);
    }

    [Fact]
    public void PasteCommand_KeepsABlankLastRowFromAnInternalCopy()
    {
        var sheet = new Worksheet(10, 5);
        sheet.Cells[0, 0].Value = "a";
        sheet.Cells[2, 2].Value = "x";
        sheet.Selection.Select(RangeRef.Parse("A1:A2"));
        var clipboard = new SpreadsheetClipboard();
        clipboard.Copy(sheet);
        var text = sheet.GetDelimitedString(RangeRef.Parse("A1:A2"));

        Assert.Equal("a\n", text);
        Assert.Equal(RangeRef.Parse("C2:C3"), clipboard.GetPasteRange(sheet, RangeRef.Parse("C2"), text));
        Assert.True(new PasteCommand(clipboard, sheet, RangeRef.Parse("C2"), text).Execute());
        Assert.Equal("a", sheet.Cells[1, 2].Value);
        Assert.Null(sheet.Cells[2, 2].Value);
    }
}
