using System;
using System.IO;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class QuotePrefixRoundTripTests
{
    private static Worksheet RoundTrip(Workbook workbook)
    {
        using var stream = new MemoryStream();

        workbook.SaveToStream(stream);
        stream.Position = 0;

        return Workbook.LoadFromStream(stream).Sheets[0];
    }

    [Theory]
    [InlineData("007")]
    [InlineData("1E3")]
    [InlineData("00123")]
    [InlineData("2020-03-04")]
    [InlineData("1/2")]
    public void TextEnteredWithALeadingApostropheStaysText(string text)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 2, 1);

        sheet.Cells[0, 0].SetValue("'" + text);

        Assert.Equal(CellDataType.String, sheet.Cells[0, 0].ValueType);

        var loaded = RoundTrip(workbook);

        Assert.Equal(CellDataType.String, loaded.Cells[0, 0].ValueType);
        Assert.Equal(text, loaded.Cells[0, 0].Value);
        Assert.True(loaded.Cells[0, 0].QuotePrefix);
    }

    // Text without the marker is still inferred, which is what a cell typed as a number relies on.
    [Fact]
    public void TextWithoutTheMarkerIsStillInferred()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 2, 1);

        sheet.Cells[0, 0].Value = "007";

        var loaded = RoundTrip(workbook);

        Assert.Equal(CellDataType.Number, loaded.Cells[0, 0].ValueType);
        Assert.False(loaded.Cells[0, 0].QuotePrefix);
    }

    // The flag lives on the cell format and the format is shared by key, so a number or a date can
    // carry it without ever having been text. Only a text cell is read back as text; the others keep
    // the type they were written with, or a SUM over the range silently drops them.
    [Fact]
    public void ATypedCellCarryingTheFlagKeepsItsType()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 4, 1);

        sheet.Cells[0, 0].Value = 5;
        sheet.Cells[0, 0].QuotePrefix = true;

        sheet.Cells[1, 0].Value = new DateTime(2020, 3, 4);
        sheet.Cells[1, 0].QuotePrefix = true;

        sheet.Cells[2, 0].Value = true;
        sheet.Cells[2, 0].QuotePrefix = true;

        var loaded = RoundTrip(workbook);

        Assert.Equal(CellDataType.Number, loaded.Cells[0, 0].ValueType);
        Assert.Equal(5d, loaded.Cells[0, 0].Value);

        // A number rather than a date: QuotePrefix is part of the style key, so a cell carrying it does
        // not share the format that makes a serial read as a date. That is the writer's, it predates
        // this, and it still sums - which a string would not.
        Assert.Equal(CellDataType.Number, loaded.Cells[1, 0].ValueType);
        Assert.Equal(43894d, loaded.Cells[1, 0].Value);

        Assert.Equal(CellDataType.Boolean, loaded.Cells[2, 0].ValueType);
        Assert.Equal(true, loaded.Cells[2, 0].Value);
    }
}
