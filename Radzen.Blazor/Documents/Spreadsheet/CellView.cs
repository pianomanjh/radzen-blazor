using System.Globalization;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// One cell as the writer needs to see it: the cell object where one exists, and the stored value
/// where none does. A sheet that was filled in bulk has a cell object for none of its cells, and
/// building one for each of them to write a file is the cost this avoids.
/// </summary>
/// <remarks>
/// A cell nothing has asked for cannot carry a format, a formula or a hyperlink, because each of
/// those is set through a cell. The members that read them are null for such a view by construction.
/// </remarks>
internal readonly struct CellView
{
    private readonly Cell? cell;
    private readonly object? value;
    private readonly CellDataType type;
    private readonly bool quotePrefix;

    public readonly CellRef Address;

    public CellView(Cell cell)
    {
        this.cell = cell;
        Address = cell.Address;
        value = null;
        type = CellDataType.Empty;
        quotePrefix = false;
    }

    public CellView(CellRef address, object? value, CellDataType type, bool quotePrefix)
    {
        cell = null;
        Address = address;
        this.value = value;
        this.type = type;
        this.quotePrefix = quotePrefix;
    }

    public object? Value => cell is not null ? cell.Value : value;

    public CellDataType ValueType => cell is not null ? cell.ValueType : type;

    public bool QuotePrefix => cell is not null ? cell.QuotePrefix : quotePrefix;

    public string? Formula => cell?.Formula;

    public Format? FormatOrNull => cell?.FormatOrNull;

    public Hyperlink? Hyperlink => cell?.Hyperlink;

    public string? FormatDisplayText(CultureInfo culture) => cell is not null
        ? cell.FormatDisplayText(culture)
        : NumberFormat.Apply(null, value, type, culture) ?? Cell.FormatValue(value, culture);
}
