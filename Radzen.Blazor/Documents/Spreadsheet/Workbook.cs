using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Radzen.Documents.Spreadsheet;

#nullable enable
/// <summary>
/// Represents a workbook in a spreadsheet.
/// </summary>
public class Workbook
{
    private readonly List<Worksheet> sheets = [];

    /// <summary>
    /// Gets the collection of sheets in the workbook.
    /// </summary>
    public IReadOnlyList<Worksheet> Sheets => sheets;

    private CultureInfo? culture;

    /// <summary>
    /// Gets or sets the culture used to parse typed cell input and to render values and number formats.
    /// If not set, falls back to <see cref="CultureInfo.CurrentCulture"/>.
    /// File storage (XLSX, CSV) and formula storage always use the invariant culture regardless of this setting.
    /// </summary>
    public CultureInfo Culture
    {
        get => culture ?? CultureInfo.CurrentCulture;
        set => culture = value;
    }

    /// <summary>
    /// Gets or sets the workbook protection settings.
    /// </summary>
    public WorkbookProtection Protection { get; set; } = new();

    internal Workbook(Worksheet sheet)
    {
        AddSheet(sheet);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Workbook"/> class.
    /// </summary>
    public Workbook()
    {
    }

    /// <summary>
    /// Adds a new sheet to the workbook with the specified name, rows, and columns.
    /// </summary>
    public Worksheet AddSheet(string name, int rows, int columns)
    {
        var sheet = new Worksheet(rows, columns)
        {
            Name = name
        };
        AddSheet(sheet);
        return sheet;
    }

    /// <summary>
    /// Adds an existing sheet to the workbook.
    /// </summary>
    /// <param name="sheet"></param>
    public void AddSheet(Worksheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        sheets.Add(sheet);
        sheet.Workbook = this;
    }

    /// <summary>
    /// Gets the sheet with the specified name or null if not found.
    /// </summary>
    /// <param name="name"></param>
    public Worksheet? GetSheet(string name)
    {
        foreach (var sheet in sheets)
        {
            if (string.Equals(sheet.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return sheet;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes a sheet from the workbook.
    /// </summary>
    /// <param name="sheet">The sheet to remove.</param>
    /// <returns><c>true</c> if the sheet was removed; otherwise <c>false</c>.</returns>
    public bool RemoveSheet(Worksheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        return sheets.Remove(sheet);
    }

    /// <summary>
    /// Gets the index of the specified sheet in the workbook.
    /// </summary>
    /// <param name="sheet">The sheet to find.</param>
    /// <returns>The zero-based index of the sheet, or -1 if not found.</returns>
    public int IndexOf(Worksheet sheet)
    {
        return sheets.IndexOf(sheet);
    }

    /// <summary>
    /// Moves a sheet from one position to another.
    /// </summary>
    /// <param name="fromIndex">The current index of the sheet.</param>
    /// <param name="toIndex">The target index for the sheet.</param>
    public void MoveSheet(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= sheets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fromIndex));
        }

        if (toIndex < 0 || toIndex >= sheets.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        }

        var sheet = sheets[fromIndex];
        sheets.RemoveAt(fromIndex);
        sheets.Insert(toIndex, sheet);
    }

    /// <summary>
    /// Saves the workbook to the specified stream in the Open XML Spreadsheet format (XLSX).
    /// </summary>
    /// <param name="stream"></param>
    public void SaveToStream(Stream stream)
    {
        new XlsxWriter(this).Write(stream);
    }

    /// <summary>
    /// Writes a single sheet to the specified stream in the Open XML Spreadsheet format (XLSX),
    /// reading its rows once and retaining none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static because there is no workbook: what a streamed sheet needs is its columns and a row
    /// source, and building the cells to hold the rows is the cost this exists to avoid. The rows are
    /// written in the order they arrive, through the columns given, and nothing is filtered, sorted or
    /// counted on the way through.
    /// </para>
    /// <para>
    /// The XML is written synchronously into the archive; what is awaited is the row source. A source
    /// that faults or is cancelled leaves nothing that opens: the archive's central directory is never
    /// written, so whatever reached the destination is not a readable package. A seekable destination
    /// is truncated back to where it started; one that cannot seek keeps the bytes already written,
    /// so a caller streaming to a response body should treat a fault as a failed download.
    /// </para>
    /// </remarks>
    /// <param name="stream">Destination stream.</param>
    /// <param name="sheet">The sheet to write.</param>
    /// <param name="cancellationToken">Cancels the write between rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sheet declares more than 16,384 columns.</exception>
    /// <exception cref="ArgumentException">
    /// A column reads images and also sets <see cref="StreamedColumn{T}.Value"/> or
    /// <see cref="StreamedColumn{T}.AutoFit"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The source yields more than the 1,048,576 rows a worksheet holds, or a number that disagrees
    /// with <see cref="StreamedSheet.RowCount"/>, or an image that was given no content type and whose
    /// bytes are not a format it recognizes. Both are caught as they happen, so the destination is
    /// left with nothing that opens rather than with a file no reader will accept.
    /// </exception>
    public static Task SaveToStreamAsync(Stream stream, StreamedSheet sheet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(sheet);

        return XlsxWriter.WriteStreamedAsync(stream, sheet, cancellationToken);
    }

    /// <summary>
    /// Loads a workbook from the specified stream in the Open XML Spreadsheet format (XLSX).
    /// </summary>
    /// <param name="stream"></param>
    /// <returns></returns>
    /// <exception cref="InvalidDataException"></exception>
    public static Workbook LoadFromStream(Stream stream)
    {
        return XlsxReader.Read(stream);
    }

    /// <summary>
    /// Saves a single sheet of the workbook to the specified stream in CSV format. By default
    /// the first sheet is exported; pass <see cref="CsvExportOptions.Sheet"/> to choose a
    /// different one. CSV is single-sheet by design, matching Excel's "Save As CSV" behavior.
    /// </summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="options">CSV options. When null, defaults are used (comma separator, UTF-8 with BOM, CRLF line endings, RFC 4180 minimal quoting).</param>
    public void SaveAsCsv(Stream stream, CsvExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        options ??= new CsvExportOptions();

        var sheet = options.Sheet
            ?? (sheets.Count > 0 ? sheets[0] : throw new InvalidOperationException("Workbook contains no sheets."));

        new CsvWriter(sheet, options).Write(stream);
    }

    /// <summary>
    /// Loads a CSV stream into a new <see cref="Workbook"/> with a single sheet.
    /// </summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="options">CSV options. When null, defaults are used.</param>
    public static Workbook LoadFromCsv(Stream stream, CsvImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CsvReader.Read(stream, options ?? new CsvImportOptions());
    }
}
