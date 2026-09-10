using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// How a streamed sheet decides the column widths it has to write before it has seen the rows.
/// </summary>
/// <remarks>
/// The <c>cols</c> element precedes <c>sheetData</c> in the same part, so a width is chosen before a
/// single row has been read. <see cref="Declared"/> takes the widths it was given and reads nothing;
/// <see cref="Sampled"/> buffers a bounded prefix of the rows, measures those, and replays the buffer.
/// Both are streaming: neither holds a number of rows that grows with the source.
/// </remarks>
public readonly struct ColumnWidthMode : IEquatable<ColumnWidthMode>
{
    private readonly int rows;

    private ColumnWidthMode(int rows) => this.rows = rows;

    /// <summary>
    /// Writes the width each column declares and measures nothing.
    /// </summary>
    public static ColumnWidthMode Declared => new(0);

    /// <summary>
    /// Buffers the first <paramref name="rows"/> rows, measures the auto-fit columns over them, then
    /// replays the buffer and streams the rest.
    /// </summary>
    /// <param name="rows">How many rows to buffer. Must be greater than zero.</param>
    public static ColumnWidthMode Sampled(int rows = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        return new(rows);
    }

    /// <summary>
    /// Whether this mode measures nothing.
    /// </summary>
    public bool IsDeclared => rows == 0;

    internal int SampleRows => rows;

    /// <inheritdoc />
    public bool Equals(ColumnWidthMode other) => rows == other.rows;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ColumnWidthMode other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => rows;

    /// <summary>Compares two modes.</summary>
    public static bool operator ==(ColumnWidthMode left, ColumnWidthMode right) => left.Equals(right);

    /// <summary>Compares two modes.</summary>
    public static bool operator !=(ColumnWidthMode left, ColumnWidthMode right) => !left.Equals(right);
}

/// <summary>
/// One column of a <see cref="StreamedSheet{T}"/>: its heading, the value it reads from a row, and the
/// format both are written with.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class StreamedColumn<T>
{
    private static readonly Func<T, object?> NoValue = static _ => null;

    /// <summary>
    /// The heading written in the header row.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Reads the cell value from a row. The value is typed exactly as it would be if it were assigned
    /// to a <see cref="Cell.Value"/>, so a streamed column and a built one produce the same cell.
    /// </summary>
    public Func<T, object?> Value { get; set; } = NoValue;

    /// <summary>
    /// Reads a picture from a row, drawn to fill the row's cell in this column. A row that yields null
    /// leaves the cell empty. A column that reads images writes no values, so it cannot also set
    /// <see cref="Value"/> or <see cref="AutoFit"/>, and its <see cref="Format"/> is not used.
    /// </summary>
    public Func<T, StreamedImage?>? Image { get; set; }

    internal bool HasValue => Value is not null && !ReferenceEquals(Value, NoValue);

    /// <summary>
    /// The format applied to every data cell of the column. Null leaves them unformatted.
    /// </summary>
    public Format? Format { get; set; }

    /// <summary>
    /// The column width in pixels. Null takes the sheet's default width.
    /// </summary>
    public double? Width { get; set; }

    /// <summary>
    /// Whether the column is measured from its content under <see cref="ColumnWidthMode.Sampled"/>,
    /// and persisted as <c>bestFit</c>. Ignored under <see cref="ColumnWidthMode.Declared"/>.
    /// </summary>
    public bool AutoFit { get; set; }
}

/// <summary>
/// A sheet written from a row source rather than built. The rows are read once, in order, and never
/// retained, so what is written costs the same in memory whether the source has a hundred rows or a
/// million.
/// </summary>
/// <remarks>
/// Passed to <see cref="Workbook.SaveToStreamAsync(System.IO.Stream, StreamedSheet, CancellationToken)"/>.
/// Construct <see cref="StreamedSheet{T}"/>; this base is what the writer is handed.
/// </remarks>
public abstract class StreamedSheet
{
    private protected StreamedSheet()
    {
    }

    /// <summary>
    /// The sheet name.
    /// </summary>
    public string Name { get; set; } = "Sheet1";

    /// <summary>
    /// The culture the column values are typed through, matching <see cref="Workbook.Culture"/> on the
    /// built path. If not set, falls back to <see cref="CultureInfo.CurrentCulture"/>, so a sheet whose
    /// columns yield strings that parse as numbers or dates should set it rather than inherit whatever
    /// the machine writing the file happens to use. File storage is invariant regardless.
    /// </summary>
    public CultureInfo? Culture { get; set; }

    /// <summary>
    /// How the column widths are decided. Defaults to <see cref="ColumnWidthMode.Sampled"/> over 200
    /// rows, which is what a built sheet's auto-fit measures over all of them.
    /// </summary>
    public ColumnWidthMode WidthMode { get; set; } = ColumnWidthMode.Sampled();

    /// <summary>
    /// Writes each string into the cell that holds it instead of interning it in the shared string
    /// table. The table's memory is <c>O(distinct strings)</c> and usually small; turning this on makes
    /// the whole write <c>O(1)</c> in rows at the cost of a larger file.
    /// </summary>
    public bool InlineStrings { get; set; }

    /// <summary>
    /// Whether the column titles are written as the first row, and counted in the table range when it
    /// is written. Freezing it is <see cref="FrozenRows"/>, which counts the header.
    /// </summary>
    public bool IncludeHeader { get; set; } = true;

    /// <summary>
    /// The format of the header row. Defaults to bold.
    /// </summary>
    public Format? HeaderFormat { get; set; } = new Format { Bold = true };

    /// <summary>
    /// The number of rows frozen at the top of the sheet, header included.
    /// </summary>
    public int FrozenRows { get; set; }

    /// <summary>
    /// The number of columns frozen at the left of the sheet.
    /// </summary>
    public int FrozenColumns { get; set; }

    /// <summary>
    /// The name of a table over the written range, or null for no table. The range is closed after the
    /// last row, so the table part costs nothing to size. A table needs a row of its own: a source that
    /// yields none leaves the sheet without one, header or no header.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// How many rows the source will yield, when that is known without asking for them.
    /// </summary>
    /// <remarks>
    /// Only the <c>dimension</c> element needs it, and only because it precedes the rows. Left null the
    /// element is omitted, which readers are required to tolerate, and a reader then sizes the sheet
    /// from the rows it finds. A sampled write whose source ends inside the sample knows the count
    /// without being told and writes the element anyway; a source that exactly fills the sample does
    /// not, because nothing has asked it for another row.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown by the write when the source yields a different number of rows. The element goes in
    /// before the rows do, so a count that turns out wrong cannot be corrected, and a sheet whose
    /// dimension disagrees with its own rows is worse than one that omits it.
    /// </exception>
    public int? RowCount { get; set; }

    private const double MaxRowHeight = 409 * 96.0 / 72.0;

    private double? dataRowHeight;

    /// <summary>
    /// The height of every row below the header, in pixels, as <see cref="StreamedColumn{T}.Width"/> is.
    /// Null leaves them at the default height. A column of images fills its cells, so this is what sizes
    /// the pictures.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The height is not greater than zero, or is taller than the 409 points (about 545 pixels) a row can be.
    /// </exception>
    public double? DataRowHeight
    {
        get => dataRowHeight;
        set
        {
            if (value is not (null or (> 0 and <= MaxRowHeight)))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "A row is more than zero and at most 409 points, about 545 pixels, tall.");
            }

            dataRowHeight = value;
        }
    }

    internal abstract int ColumnCount { get; }

    internal abstract string TitleAt(int column);

    internal abstract Format? FormatAt(int column);

    internal abstract double? WidthAt(int column);

    internal abstract bool AutoFitAt(int column);

    internal abstract bool IsImageAt(int column);

    internal abstract bool HasValueAt(int column);

    internal abstract IAsyncEnumerable<object?[]> Read(CancellationToken cancellationToken);
}

/// <summary>
/// A <see cref="StreamedSheet"/> over a typed asynchronous row source.
/// </summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class StreamedSheet<T> : StreamedSheet
{
    /// <summary>
    /// The rows, read once and in the order they arrive. Nothing is filtered, sorted or counted on the
    /// way through.
    /// </summary>
    public IAsyncEnumerable<T> Rows { get; set; } = EmptyRows();

    /// <summary>
    /// The columns, in the order they are written.
    /// </summary>
    public IList<StreamedColumn<T>> Columns { get; } = [];

    private static async IAsyncEnumerable<T> EmptyRows()
    {
        await Task.CompletedTask;

        yield break;
    }

    internal override int ColumnCount => Columns.Count;

    internal override string TitleAt(int column) => Columns[column].Title ?? string.Empty;

    internal override Format? FormatAt(int column) => Columns[column].Format;

    internal override double? WidthAt(int column) => Columns[column].Width;

    internal override bool AutoFitAt(int column) => Columns[column].AutoFit;

    internal override bool IsImageAt(int column) => Columns[column].Image is not null;

    internal override bool HasValueAt(int column) => Columns[column].HasValue;

    internal override async IAsyncEnumerable<object?[]> Read(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = Columns.Count;
        var readers = new Func<T, object?>[count];

        for (var i = 0; i < count; i++)
        {
            readers[i] = Columns[i].Image ?? Columns[i].Value ?? (static _ => null);
        }

        var line = new object?[count];

        await foreach (var row in Rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < count; i++)
            {
                line[i] = readers[i](row);
            }

            yield return line;
        }
    }
}
