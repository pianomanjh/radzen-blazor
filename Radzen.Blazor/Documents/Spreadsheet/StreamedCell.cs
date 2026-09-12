namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// One cell of a row appended by
/// <see cref="Workbook.SaveToStreamAsync(System.IO.Stream, System.Collections.Generic.IAsyncEnumerable{StreamedCell[]}, System.Threading.CancellationToken)"/>:
/// a value and the format it is written with.
/// </summary>
/// <param name="data">The value. Null writes no value, and a format still styles the cell.</param>
/// <param name="format">The format, or null for none. A date without a number format is written with a date format.</param>
public readonly struct StreamedCell(CellData? data, Format? format = null)
{
    /// <summary>
    /// The value. Null writes no value, and a format still styles the cell.
    /// </summary>
    public CellData? Data { get; } = data;

    /// <summary>
    /// The format, or null for none.
    /// </summary>
    public Format? Format { get; } = format;

    internal bool IsWritten => Data?.Value is not null || Format?.IsDefault == false;
}
