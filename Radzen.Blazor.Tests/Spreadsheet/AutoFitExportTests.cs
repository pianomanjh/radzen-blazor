using System.IO;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

public class AutoFitExportTests
{
    [Fact]
    public void Save_AutoFitColumnWithCellBelowShrunkRowCount_DoesNotThrow()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 20, 4);

        sheet.Cells["A10"].Value = "a wide piece of text";
        sheet.Columns.SetAutoFit(0);

        sheet.Rows.Count = 5;

        workbook.SaveToStream(Stream.Null);
    }
}
