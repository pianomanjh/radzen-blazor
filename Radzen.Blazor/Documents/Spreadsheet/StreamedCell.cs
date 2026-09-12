using System;
using System.Globalization;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// One cell of a row appended by
/// <see cref="Workbook.SaveToStreamAsync(System.IO.Stream, System.Collections.Generic.IAsyncEnumerable{StreamedCell[]}, System.Threading.CancellationToken)"/>:
/// a value and the format it is written with.
/// </summary>
/// <remarks>
/// A number, date or boolean is held in the cell itself, so a row built from the typed factories
/// allocates nothing. <see cref="From"/> takes a value whose type is known only at run time.
/// </remarks>
public readonly struct StreamedCell
{
    private readonly object? value;

    private readonly double number;

    private readonly CellDataType type;

    private readonly bool numeric;

    private StreamedCell(object? value, double number, CellDataType type, Format? format)
    {
        this.value = value;
        this.number = number;
        this.type = type;
        numeric = value is null && type != CellDataType.Empty;
        Format = format;
    }

    /// <summary>
    /// Creates a cell from a value already held as <see cref="CellData"/>.
    /// </summary>
    /// <param name="data">The value. Null writes no value, and a format still styles the cell.</param>
    /// <param name="format">The format, or null for none. A date without a number format is written with a date format.</param>
    public StreamedCell(CellData? data, Format? format = null)
        : this(data?.Value, 0, data?.Value is null ? CellDataType.Empty : data.Type, format)
    {
    }

    /// <summary>
    /// A cell holding text, written as text even when it looks like a number or a date.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="format">The format, or null for none.</param>
    public static StreamedCell FromString(string value, Format? format = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new(value, 0, CellDataType.String, format);
    }

    /// <summary>
    /// A cell holding a number.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <param name="format">The format, or null for none.</param>
    public static StreamedCell FromNumber(double value, Format? format = null) => new(null, value, CellDataType.Number, format);

    /// <summary>
    /// A cell holding a date, written with a date format unless <paramref name="format"/> names one.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <param name="format">The format, or null for none.</param>
    public static StreamedCell FromDate(DateTime value, Format? format = null) => new(null, value.ToNumber(), CellDataType.Date, format);

    /// <summary>
    /// A cell holding a boolean.
    /// </summary>
    /// <param name="value">The boolean.</param>
    /// <param name="format">The format, or null for none.</param>
    public static StreamedCell FromBoolean(bool value, Format? format = null) => new(null, value ? 1 : 0, CellDataType.Boolean, format);

    /// <summary>
    /// A cell holding a value whose type is known only at run time, typed as
    /// <see cref="Cell.Value"/> types it, except that a string is kept as text.
    /// </summary>
    /// <param name="value">The value. Null writes no value, and a format still styles the cell.</param>
    /// <param name="format">The format, or null for none.</param>
    public static StreamedCell From(object? value, Format? format = null) => value switch
    {
        null => new(null, 0, CellDataType.Empty, format),
        string text => FromString(text, format),
        double number => FromNumber(number, format),
        int number => FromNumber(number, format),
        decimal number => FromNumber((double)number, format),
        DateTime date => FromDate(date, format),
        bool flag => FromBoolean(flag, format),
        long number => FromNumber(number, format),
        float number => FromNumber(number, format),
        short number => FromNumber(number, format),
        byte number => FromNumber(number, format),
        uint number => FromNumber(number, format),
        ulong number => FromNumber(number, format),
        ushort number => FromNumber(number, format),
        sbyte number => FromNumber(number, format),
        CellData data => new(data, format),
        _ => Inferred(value, format),
    };

    private static StreamedCell Inferred(object value, Format? format)
    {
        CellData.Infer(value, CultureInfo.InvariantCulture, out var inferred, out var inferredType);

        return new(inferred, 0, inferred is null ? CellDataType.Empty : inferredType, format);
    }

    /// <summary>
    /// The format, or null for none.
    /// </summary>
    public Format? Format { get; }

    internal CellDataType Type => type;

    internal object? Value => value;

    internal double Number => number;

    internal bool HasValue => value is not null || numeric;

    internal bool IsWritten => HasValue || Format?.IsDefault == false;
}
