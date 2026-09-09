using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// §65: the same export, without the workbook in the middle.
    /// </summary>
    public static partial class FastGridExport
    {
        /// <summary>
        /// Writes the grid to a stream, reading its rows once and holding none of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The same columns, the same values and the same widths as
        /// <see cref="ToWorkbook{TItem}" />.</strong> What differs is that no cell is built: §40's
        /// builder holds a value per cell before it writes one, which at eleven columns over a million
        /// rows is the larger half of the cost. The columns are resolved by the same code, the values go
        /// through the same <c>ExportValue.Coerce</c>, and the widths are measured by the same
        /// <c>WidthFor</c> - over a bounded sample of the rows rather than all of them, because a width
        /// is written before the rows are.
        /// </para>
        /// <para>
        /// <strong>Where the rows come from, in order:</strong>
        /// </para>
        /// <list type="number">
        /// <item><see cref="FastGridExportOptions{TItem}.RowsAsync" /> - the caller's own, which outranks
        /// everything below for the reason §63 gives: the application owns a query the grid never ran.</item>
        /// <item>The grid's <see cref="RadzenFastGrid{TItem}.QueryExecutor" /> over its
        /// <see cref="RadzenFastGrid{TItem}.FilteredQuery" /> - which is why a menu entry streams with
        /// no application code at all.</item>
        /// <item><see cref="FastGridExportOptions{TItem}.Rows" />, §63's.</item>
        /// <item><see cref="RadzenFastGrid{TItem}.FilteredRows" />, §40's.</item>
        /// </list>
        /// <para>
        /// <strong>Running the query is this method's act and it happens off the grid.</strong> Route 2
        /// enumerates an unpaged query the grid composed and declined to run; on a database source that
        /// is every matching row. The grid's own state - its visible columns, its filters - is read
        /// before the first row is asked for, so a render moving underneath cannot change what is being
        /// written half way through.
        /// </para>
        /// <para>
        /// A source that faults or is cancelled leaves nothing openable behind it: see
        /// <see cref="Workbook.SaveToStreamAsync" />.
        /// </para>
        /// </remarks>
        /// <typeparam name="TItem">The grid's row type.</typeparam>
        /// <param name="grid">The grid to export.</param>
        /// <param name="destination">Where the file is written. Not closed by this method.</param>
        /// <param name="options">What to call the sheet and which of the trimmings to apply.</param>
        /// <param name="cancellationToken">Cancels the write between rows.</param>
        /// <exception cref="ArgumentNullException"><paramref name="grid" /> or <paramref name="destination" /> is null.</exception>
        public static Task SaveToStreamAsync<TItem>(this RadzenFastGrid<TItem> grid, Stream destination,
            FastGridExportOptions<TItem>? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(grid);
            ArgumentNullException.ThrowIfNull(destination);

            options ??= new FastGridExportOptions<TItem>();

            var columns = Exported(grid, options, out var declared);
            var formats = new string?[columns.Count];
            var asText = Formats(columns, declared, formats);

            var sheet = new StreamedSheet<TItem>
            {
                Name = options.SheetName,
                IncludeHeader = options.IncludeHeader,
                FrozenRows = options.FreezeHeader && options.IncludeHeader ? 1 : 0,
                WidthMode = options.AutoFitColumns ? ColumnWidthMode.Sampled() : ColumnWidthMode.Declared,
                TableName = options.AddTable && columns.Count > 0 ? TableName(options.SheetName) : null,
                Rows = Source(grid, options),
            };

            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                var said = declared[c];
                var text = asText[c];
                var format = formats[c];

                sheet.Columns.Add(new StreamedColumn<TItem>
                {
                    Title = TitleOf(column, said),
                    Value = item =>
                    {
                        // The text only where it is actually wanted. The builder draws it for every cell
                        // because it measures the column width from it; this path measures from the
                        // value the writer formats, so a bound column with a typed value never draws a
                        // string it then throws away - which at five columns is most of the cost per
                        // row.
                        if (text)
                        {
                            return column.CellTextOf(item);
                        }

                        var value = said is { ExportValue: { } declaredValue }
                            ? declaredValue(item)
                            : column.CellValueOf(item);

                        return ExportValue.Coerce(value, column, item);
                    },
                    Format = format is null ? null : new Format { NumberFormat = format },
                    AutoFit = options.AutoFitColumns,

                    // §40's own width function, so a streamed file and a built one are sized the same
                    // way rather than by two sets of metrics that happen to be close.
                    MeasureWidth = static value => WidthFor(value.Length),

                    // What ExportValue.Write insists on when it has a cell to insist with.
                    PreserveText = true,
                });
            }

            return Workbook.SaveToStreamAsync(destination, sheet, cancellationToken);
        }

        /// <summary>The rows, by the resolution the method above documents.</summary>
        static IAsyncEnumerable<TItem> Source<TItem>(RadzenFastGrid<TItem> grid,
            FastGridExportOptions<TItem> options)
        {
            if (options.RowsAsync is { } asked)
            {
                return asked;
            }

            if (grid.FilteredQuery is { } query
                && grid.QueryExecutor is { } executor
                && executor.AsAsyncEnumerable(query) is { } streamed)
            {
                return streamed;
            }

            return Walk(options.Rows ?? grid.FilteredRows);
        }

        /// <summary>
        /// A synchronous sequence as an asynchronous one, for the two routes that have no query behind
        /// them. Nothing is buffered: the rows are pulled one at a time, which is what the caller of an
        /// in-memory sequence already had.
        /// </summary>
        static async IAsyncEnumerable<TItem> Walk<TItem>(IEnumerable<TItem> rows)
        {
            foreach (var row in rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
