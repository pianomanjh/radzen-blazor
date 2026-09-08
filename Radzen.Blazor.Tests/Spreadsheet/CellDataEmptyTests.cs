using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class CellDataEmptyTests
{
    // CellData carries a value and a type and neither can be set, so every empty cell points at one.
    [Fact]
    public void EmptyCellsShareOneCellData()
    {
        var sheet = new Workbook().AddSheet("Sheet1", 4, 4);

        Assert.Same(CellData.Empty, sheet.Cells[0, 0].Data);
        Assert.Same(sheet.Cells[0, 0].Data, sheet.Cells[1, 1].Data);

        sheet.Cells[0, 0].Value = "written";

        Assert.NotSame(CellData.Empty, sheet.Cells[0, 0].Data);
        Assert.Same(CellData.Empty, sheet.Cells[1, 1].Data);

        // And the shared instance still says what an empty cell says.
        Assert.True(sheet.Cells[1, 1].IsEmpty);
        Assert.Equal(CellDataType.Empty, sheet.Cells[1, 1].ValueType);
        Assert.Null(sheet.Cells[1, 1].Value);
    }
}
