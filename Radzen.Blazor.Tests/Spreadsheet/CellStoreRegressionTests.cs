using System;
using System.Globalization;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

public class CellStoreRegressionTests
{
    private sealed class CountingCellStore(Worksheet sheet) : CellStore(sheet)
    {
        public int Assignments { get; private set; }

        public override Cell this[int row, int column]
        {
            get => base[row, column];
            set
            {
                Assignments++;
                base[row, column] = value;
            }
        }
    }

    [Fact]
    public void ReadingAMissingCell_DispatchesThroughTheOverriddenSetter()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 8, 4);
        var store = new CountingCellStore(sheet);

        _ = store[2, 1];

        Assert.Equal(1, store.Assignments);
    }

    [Fact]
    public void BulkFill_OnADerivedStore_DispatchesThroughTheOverriddenSetter()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 8, 4);
        var store = new CountingCellStore(sheet);

        store.SetValues(0, 0, [[1, 2], [3, 4]]);

        Assert.Equal(4, store.Assignments);
    }

    // A derived store may hand back a snapshot rather than the stored cell. A bulk fill must still
    // write through to the store, not into the copy.
    private sealed class SnapshotCellStore(Worksheet sheet) : CellStore(sheet)
    {
        public override Cell this[int row, int column]
        {
            get => base[row, column].Clone();
            set => base[row, column] = value;
        }
    }

    [Fact]
    public void BulkFill_OnADerivedStoreThatSnapshotsItsGetter_WritesThroughToTheStore()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 8, 4);
        var store = new SnapshotCellStore(sheet);

        store.SetValues(0, 0, [[42]]);

        Assert.True(store.TryGet(0, 0, out var stored));
        Assert.Equal(42d, stored.Value);
    }

    // The store keeps a value without its type, and reads the type back from the value. Every way to
    // produce a CellData has to agree with that, or a stored value changes meaning.
    [Theory]
    [InlineData("text", CellDataType.String)]
    [InlineData(1.5, CellDataType.Number)]
    [InlineData(true, CellDataType.Boolean)]
    [InlineData(null, CellDataType.Empty)]
    public void CellDataTypeIsTheValuesType(object? value, CellDataType expected)
    {
        Assert.Equal(expected, CellStore.TypeOf(value));
        Assert.Equal(expected, new CellData(value, CultureInfo.InvariantCulture).Type);
    }

    [Fact]
    public void CellDataTypeIsTheValuesType_ForTheRemainingFactories()
    {
        Assert.Equal(CellData.FromDate(new DateTime(2024, 1, 1)).Type, CellStore.TypeOf(new DateTime(2024, 1, 1)));
        Assert.Equal(CellData.FromError(CellError.Name).Type, CellStore.TypeOf(CellError.Name));
        Assert.Equal(CellData.FromString("0123").Type, CellStore.TypeOf("0123"));
        Assert.Equal(CellData.FromNumber(1).Type, CellStore.TypeOf(1d));
    }

    // A value standing on its own is never quote-prefixed, because every setter of the flag is on
    // Cell and reaching one materialises it.
    [Fact]
    public void QuotePrefix_SurvivesTheCellItWasSetOn()
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 8, 4);

        sheet.Cells.SetValues(0, 0, [["0123"]]);
        Assert.False(sheet.Cells[0, 0].QuotePrefix);

        sheet.Cells[0, 0].SetValue("'0123");

        Assert.True(sheet.Cells[0, 0].QuotePrefix);
        Assert.Equal("0123", sheet.Cells[0, 0].Value);
        Assert.Equal(CellDataType.String, sheet.Cells[0, 0].ValueType);
    }
}
