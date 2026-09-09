using System.Collections.Generic;
using Xunit;
using Radzen.Documents.Spreadsheet;
namespace Radzen.Blazor.Spreadsheet.Tests;

public class CellDataTests
{
    [Fact]
    public void IsEqualTo_BothEmpty_ReturnsTrue()
    {
        var a = new CellData(null);
        var b = new CellData(null);
        Assert.True(a.IsEqualTo(b));
    }

    [Fact]
    public void IsEqualTo_OnlyOtherEmpty_ReturnsTrue_WhenValueIsEmptyString()
    {
        var a = new CellData("");
        var b = new CellData(null);
        Assert.True(a.IsEqualTo(b));
    }

    [Fact]
    public void IsEqualTo_SameNumbers_ReturnsTrue()
    {
        var a = new CellData(42.0);
        var b = new CellData(42.0);
        Assert.True(a.IsEqualTo(b));
    }

    [Fact]
    public void IsEqualTo_DifferentTypes_ReturnsFalse()
    {
        var a = new CellData(1.0);
        var b = new CellData("hello");
        Assert.False(a.IsEqualTo(b));
    }

    private static Worksheet CreateSheet()
    {
        var workbook = new Workbook();

        return workbook.AddSheet("Sheet1", 8, 4);
    }

    [Fact]
    public void Data_ReadTwiceFromOneCell_IsEqualAndHashesAlike()
    {
        var sheet = CreateSheet();
        sheet.Cells["A1"].Value = 42.0;

        var first = sheet.Cells["A1"].Data;
        var second = sheet.Cells["A1"].Data;

        Assert.NotSame(first, second);
        Assert.True(first.Equals(second));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Data_ReadTwiceFromOneCell_IsFoundInAHashSet()
    {
        var sheet = CreateSheet();
        sheet.Cells["A1"].SetValueInvariant("0123");

        var set = new HashSet<CellData> { sheet.Cells["A1"].Data };

        Assert.Contains(sheet.Cells["A1"].Data, set);
    }

    [Fact]
    public void Equals_SameValueAndType_IsTrue()
    {
        Assert.True(CellData.FromNumber(42).Equals(CellData.FromNumber(42)));
        Assert.True(CellData.FromString("a").Equals(CellData.FromString("a")));
        Assert.True(CellData.Empty.Equals(new CellData(null)));
    }

    [Fact]
    public void Equals_DifferentValueOrType_IsFalse()
    {
        Assert.False(CellData.FromNumber(42).Equals(CellData.FromNumber(43)));
        Assert.False(CellData.FromNumber(1).Equals(CellData.FromString("one")));
        Assert.False(CellData.FromNumber(42).Equals(null));
        Assert.False(CellData.FromNumber(42).Equals("not a cell value"));
    }
}
