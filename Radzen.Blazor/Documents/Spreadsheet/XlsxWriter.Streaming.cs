using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

// The sheet written from a row source instead of from cells.
//
// It reuses the shape the built path already has - a skeleton document, `sheetData` streamed into an
// `XmlWriter` over the zip entry - and replaces the sorted cell array with rows that are never
// retained. Every part other than the sheet is the built path's own method, and so is every cell: the
// style resolution, the typed value and the reference are the same code, which is what makes a
// streamed file and a built one comparable rather than merely similar.
//
// What a streamed sheet does not have, by construction: merged cells, hyperlinks, drawings, charts,
// conditional formats, validations and formulas. Each of those is a property of a cell somebody built.
partial class XlsxWriter
{
    internal static async Task WriteStreamedAsync(Stream destination, StreamedSheet spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(spec);

        var scaffold = new Workbook();
        var sheet = scaffold.AddSheet(spec.Name, rows: 1, columns: Math.Max(spec.ColumnCount, 1));

        sheet.Rows.Frozen = spec.FrozenRows;
        sheet.Columns.Frozen = spec.FrozenColumns;

        await new XlsxWriter(scaffold).WriteStreamedAsync(destination, spec, sheet, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteStreamedAsync(Stream destination, StreamedSheet spec, Worksheet sheet, CancellationToken cancellationToken)
    {
        // Where the file starts, so a source that faults halfway leaves nothing behind it.
        var start = destination.CanSeek ? destination.Position : -1L;

        var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        try
        {
            var styleTracker = CreateStylesDocument();
            using var sharedStrings = new SharedStringTable();

            var table = await SaveStreamedSheetAsync(archive, spec, sheet, styleTracker, sharedStrings, cancellationToken)
                .ConfigureAwait(false);

            SaveStreamedWorkbookRelationships(archive, sharedStrings.Count > 0);
            SaveStyles(archive, styleTracker);
            SaveSharedStrings(archive, sharedStrings);
            SaveWorkbook(archive);
            SaveTheme(archive);
            SaveDocPropsCore(archive);
            SaveDocPropsApp(archive);
            SaveContentTypes(archive, includeSharedStrings: sharedStrings.Count > 0, tableCount: table ? 1 : 0);
            SaveRelationships(archive);
        }
        catch
        {
            // The archive is deliberately not disposed: disposing it writes the central directory, and a
            // zip with one is a zip Excel will open and show a truncated sheet in. Without it there is
            // nothing to open, which is the honest outcome of a source that did not finish.
            if (start >= 0)
            {
                destination.SetLength(start);
                destination.Position = start;
            }

            throw;
        }

        archive.Dispose();
    }

    // Answers whether a table part was written.
    private async Task<bool> SaveStreamedSheetAsync(
        ZipArchive archive,
        StreamedSheet spec,
        Worksheet sheet,
        StyleTracker styleTracker,
        SharedStringTable sharedStrings,
        CancellationToken cancellationToken)
    {
        var columns = spec.ColumnCount;

        await using var rows = spec.Read(cancellationToken).GetAsyncEnumerator(cancellationToken);

        // The sample, and the whole source when the sample was big enough to hold it. `buffered` is
        // bounded by the sample size and by nothing else, which is what keeps this a streaming mode.
        List<object?[]>? buffered = null;
        var complete = false;

        if (!spec.WidthMode.IsDeclared)
        {
            buffered = [];

            var limit = spec.WidthMode.SampleRows;

            while (buffered.Count < limit)
            {
                if (!await rows.MoveNextAsync().ConfigureAwait(false))
                {
                    complete = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();

                buffered.Add((object?[])rows.Current.Clone());
            }

            // A source that cancels and then ends leaves through the false from MoveNextAsync, which
            // the check inside the loop never sees. Without this the write would finish and hand back
            // a file, which is the one thing cancelling is supposed to prevent.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var headerRows = spec.IncludeHeader ? 1 : 0;

        // Exact when the caller said so, and exact for free when the sample swallowed the source.
        int? declaredRows = complete ? buffered!.Count : spec.RowCount;

        ApplyStreamedWidths(spec, sheet, buffered);

        if (declaredRows is { } total)
        {
            sheet.Rows.Count = Math.Max(total + headerRows, 1);
        }

        var written = 0;

        using (var entry = archive.CreateEntry("xl/worksheets/sheet1.xml").Open())
        {
            written = await WriteStreamedSheetXmlAsync(entry, spec, sheet, styleTracker, sharedStrings, buffered, rows, declaredRows is not null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (spec.TableName is null || written == 0)
        {
            return false;
        }

        // The range is closed after the last row, so the count the table needs is one it already has.
        // The header cells go into the scaffold for the same reason: a table reads its column names
        // from them, and there are as many as there are columns.
        sheet.Rows.Count = Math.Max(written, 1);

        if (spec.IncludeHeader)
        {
            for (var column = 0; column < columns; column++)
            {
                // As text, not as a value: a table reads its column names from these cells, and a
                // heading of "2026" assigned through Value is inferred to a number, which the table
                // then cannot read as a name and replaces with Column1 - disagreeing with the heading
                // the sheet itself shows.
                sheet.Cells[0, column].SetText(spec.TitleAt(column));
            }
        }

        var range = new RangeRef(new CellRef(0, 0), new CellRef(written - 1, Math.Max(columns - 1, 0)));

        SaveTable(archive, sheet.AddTable(spec.TableName, range, hasHeaders: spec.IncludeHeader), tableId: 1);

        SaveSheetRelationships(archive, "sheet1.xml",
        [
            ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table", "../tables/table1.xml", false),
        ]);

        return true;
    }

    private void SaveStreamedWorkbookRelationships(ZipArchive archive, bool includeSharedStrings)
    {
        var pkgNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        var rels = CreateWorkbookRelationships();

        rels.Root!.Add(new XElement(XName.Get("Relationship", pkgNs),
            new XAttribute("Id", "rId2"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
            new XAttribute("Target", "styles.xml")));

        rels.Root!.Add(new XElement(XName.Get("Relationship", pkgNs),
            new XAttribute("Id", "rId3"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
            new XAttribute("Target", "worksheets/sheet1.xml")));

        rels.Root!.Add(new XElement(XName.Get("Relationship", pkgNs),
            new XAttribute("Id", "rId4"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme"),
            new XAttribute("Target", "theme/theme1.xml")));

        if (includeSharedStrings)
        {
            rels.Root!.Add(new XElement(XName.Get("Relationship", pkgNs),
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"),
                new XAttribute("Target", "sharedStrings.xml")));
        }

        using var entry = archive.CreateEntry("xl/_rels/workbook.xml.rels").Open();
        rels.Save(entry);
    }

    // Answers the number of rows written, header included.
    private async Task<int> WriteStreamedSheetXmlAsync(
        Stream stream,
        StreamedSheet spec,
        Worksheet sheet,
        StyleTracker styleTracker,
        SharedStringTable sharedStrings,
        List<object?[]>? buffered,
        IAsyncEnumerator<object?[]> rows,
        bool hasDimension,
        CancellationToken cancellationToken)
    {
        var sheetDoc = CreateSheetDocument(sheet, sheetId: 1, relId: "rId3");

        if (!hasDimension)
        {
            // Optional by the schema, and a reader that needs it is a reader that would have needed a
            // second pass over the rows to get it.
            sheetDoc.Root!.Element(XName.Get("dimension", Main))?.Remove();
        }

        var root = sheetDoc.Root!;
        var written = 0;

        using var writer = XmlWriter.Create(stream, PartXmlSettings);

        writer.WriteStartDocument();
        writer.WriteStartElement(root.Name.LocalName, root.Name.NamespaceName);

        foreach (var attribute in root.Attributes())
        {
            writer.WriteAttributeString(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value);
        }

        foreach (var element in root.Elements())
        {
            if (element.Name == SheetDataElement)
            {
                writer.WriteStartElement(SheetDataElement.LocalName, Main);

                written = await WriteStreamedRowsAsync(writer, spec, sheet, styleTracker, sharedStrings, buffered, rows, cancellationToken)
                    .ConfigureAwait(false);

                writer.WriteEndElement();
            }
            else
            {
                element.WriteTo(writer);
            }
        }

        // Both of these follow sheetData, which is the whole reason a table costs a streamed sheet
        // nothing: by the time either is written the row count is known.
        CreatePageMargins().WriteTo(writer);

        if (spec.TableName is not null && written > 0)
        {
            XNamespace rNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

            new XElement(XName.Get("tableParts", Main),
                new XAttribute("count", "1"),
                new XElement(XName.Get("tablePart", Main), new XAttribute(rNs + "id", "rId1")))
                .WriteTo(writer);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();

        return written;
    }

    private async Task<int> WriteStreamedRowsAsync(
        XmlWriter writer,
        StreamedSheet spec,
        Worksheet sheet,
        StyleTracker styleTracker,
        SharedStringTable sharedStrings,
        List<object?[]>? buffered,
        IAsyncEnumerator<object?[]> rows,
        CancellationToken cancellationToken)
    {
        var columns = spec.ColumnCount;
        var styles = new Dictionary<(int Column, CellDataType Type), int?>();
        var row = 0;

        if (spec.IncludeHeader)
        {
            WriteStreamedHeader(writer, spec, sheet, styleTracker, sharedStrings, columns);
            row++;
        }

        if (buffered is not null)
        {
            foreach (var line in buffered)
            {
                WriteStreamedRow(writer, spec, sheet, styleTracker, sharedStrings, styles, line, row++, columns);
            }
        }

        while (await rows.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            WriteStreamedRow(writer, spec, sheet, styleTracker, sharedStrings, styles, rows.Current, row++, columns);
        }

        // The same exit as the sample loop's, for the same reason.
        cancellationToken.ThrowIfCancellationRequested();

        return row;
    }

    private void WriteStreamedHeader(
        XmlWriter writer,
        StreamedSheet spec,
        Worksheet sheet,
        StyleTracker styleTracker,
        SharedStringTable sharedStrings,
        int columns)
    {
        var format = spec.HeaderFormat;
        var style = format?.IsDefault == false
            ? GetOrCreateCellStyle(format ?? DefaultFormat, CellDataType.String, quotePrefix: false, styleTracker)
            : (int?)null;

        var first = -1;
        var last = -1;

        for (var column = 0; column < columns; column++)
        {
            if (!string.IsNullOrEmpty(spec.TitleAt(column)))
            {
                if (first < 0)
                {
                    first = column;
                }

                last = column;
            }
        }

        WriteRowStart(writer, sheet, row: 0, first, last);

        for (var column = 0; column < columns; column++)
        {
            var title = spec.TitleAt(column);

            if (!string.IsNullOrEmpty(title))
            {
                WriteStreamedCell(writer, new CellRef(0, column), title, CellDataType.String, style, sharedStrings, spec.InlineStrings);
            }
        }

        writer.WriteEndElement();
    }

    private void WriteStreamedRow(
        XmlWriter writer,
        StreamedSheet spec,
        Worksheet sheet,
        StyleTracker styleTracker,
        SharedStringTable sharedStrings,
        Dictionary<(int Column, CellDataType Type), int?> styles,
        object?[] line,
        int row,
        int columns)
    {
        var first = -1;
        var last = -1;

        for (var column = 0; column < columns; column++)
        {
            if (line[column] is not null)
            {
                if (first < 0)
                {
                    first = column;
                }

                last = column;
            }
        }

        WriteRowStart(writer, sheet, row, first, last);

        for (var column = 0; column < columns; column++)
        {
            if (line[column] is null)
            {
                continue;
            }

            // The same inference a value assigned to a cell goes through, so a streamed sheet types its
            // cells exactly as a built one does - a string that reads as a date included.
            CellData.Infer(line[column], sheet.Workbook!.Culture, out var content, out var type);

            if (content is null)
            {
                continue;
            }

            var key = (column, type);

            if (!styles.TryGetValue(key, out var style))
            {
                var format = spec.FormatAt(column);

                style = format?.IsDefault == false || type == CellDataType.Date
                    ? GetOrCreateCellStyle(format ?? DefaultFormat, type, quotePrefix: false, styleTracker)
                    : null;

                styles[key] = style;
            }

            WriteStreamedCell(writer, new CellRef(row, column), content, type, style, sharedStrings, spec.InlineStrings);
        }

        writer.WriteEndElement();
    }

    private void WriteStreamedCell(
        XmlWriter writer,
        CellRef address,
        object? content,
        CellDataType type,
        int? styleId,
        SharedStringTable sharedStrings,
        bool inlineStrings)
    {
        writer.WriteStartElement("c", Main);
        WriteReferenceAttribute(writer, address);

        if (styleId is not null)
        {
            WriteNumberAttribute(writer, "s", styleId.Value);
        }

        if (type == CellDataType.String)
        {
            var text = content as string ?? string.Empty;

            if (inlineStrings)
            {
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", Main);
                writer.WriteStartElement("t", Main);
                writer.WriteString(text);
                writer.WriteFullEndElement();
                writer.WriteFullEndElement();
            }
            else
            {
                writer.WriteAttributeString("t", "s");

                WriteNumberValue(writer, sharedStrings.GetOrAdd(text));
            }
        }
        else
        {
            var type_ = CellTypeAttribute(new CellView(address, content, type, quotePrefix: false), isFormula: false);

            if (type_ is not null)
            {
                writer.WriteAttributeString("t", type_);
            }

            WriteTypedValue(writer, new CellView(address, content, type, quotePrefix: false));
        }

        writer.WriteEndElement();
    }

    // `cols` precedes `sheetData`, so this is everything the widths are allowed to have seen. Declared
    // takes what the caller set and reads nothing; sampled measures the buffer with the same metrics
    // the built path's auto fit uses, so the two agree wherever the buffer held the rows.
    private static void ApplyStreamedWidths(StreamedSheet spec, Worksheet sheet, List<object?[]>? buffered)
    {
        var columns = spec.ColumnCount;

        for (var column = 0; column < columns; column++)
        {
            if (spec.WidthAt(column) is { } width)
            {
                sheet.Columns[column] = width;
            }
        }

        if (buffered is null)
        {
            return;
        }

        for (var column = 0; column < columns; column++)
        {
            if (!spec.AutoFitAt(column))
            {
                continue;
            }

            var format = spec.FormatAt(column);
            var widest = sheet.Columns[column];

            if (spec.IncludeHeader)
            {
                widest = Math.Max(widest, MeasureStreamed(spec.TitleAt(column), spec.HeaderFormat));
            }

            foreach (var line in buffered)
            {
                var value = line[column];

                if (value is null)
                {
                    continue;
                }

                CellData.Infer(value, sheet.Workbook!.Culture, out var content, out var type);

                var text = NumberFormat.Apply(format?.NumberFormat, content, type, CultureInfo.InvariantCulture)
                    ?? Cell.FormatValue(content, CultureInfo.InvariantCulture);

                widest = Math.Max(widest, MeasureStreamed(text, format));
            }

            sheet.Columns[column] = Math.Min(widest, ColumnWidthConversion.MaxWidthInPixels);
            sheet.Columns.SetAutoFit(column);
        }
    }

    private static double MeasureStreamed(string? text, Format? format)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return ExcelTextMetrics.EstimateWidth(
            ExcelTextMetrics.DisplayLine(text, format?.WrapText == true),
            format?.Bold == true,
            format?.FontSize) + 5;
    }
}
