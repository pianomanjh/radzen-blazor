using System;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class ColumnRefTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    [InlineData(16383, "XFD")]
    public void ToString_WritesTheColumnInA1Notation(int column, string expected)
    {
        Assert.Equal(expected, ColumnRef.ToString(column));
    }

    [Fact]
    public void ToString_RejectsANegativeColumn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ColumnRef.ToString(-1));
    }

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(1, 26, "AA2")]
    [InlineData(99, 702, "AAA100")]
    public void CellRef_ToString_WritesTheColumnThenTheRow(int row, int column, string expected)
    {
        Assert.Equal(expected, new CellRef(row, column).ToString());
    }

    [Theory]
    [InlineData(1048575, 16383, "XFD1048576")]
    [InlineData(int.MaxValue, 0, "A-2147483648")]
    public void CellRef_ToString_WritesTheWidestReferences(int row, int column, string expected)
    {
        Assert.Equal(expected, new CellRef(row, column).ToString());
    }

    [Fact]
    public void CellRef_ToString_WritesTheWidestReferenceOfAll()
    {
        var reference = new CellRef(int.MaxValue, 2147483646)
        {
            IsRowAbsolute = true,
            IsColumnAbsolute = true,
        };

        var text = reference.ToString();

        Assert.Equal("$FXSHRXW$-2147483648", text);
        Assert.Equal(CellRef.MaxLength, text.Length);
    }

    [Fact]
    public void CellRef_ToString_MarksAnAbsoluteReference()
    {
        var reference = new CellRef(1, 27) { IsRowAbsolute = true, IsColumnAbsolute = true };

        Assert.Equal("$AB$2", reference.ToString());
    }
}
