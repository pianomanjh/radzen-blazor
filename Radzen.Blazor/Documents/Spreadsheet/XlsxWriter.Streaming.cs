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

partial class XlsxWriter
{
    internal static async Task WriteStreamedAsync(Stream destination, StreamedSheet spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.ColumnCount > MaxColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), spec.ColumnCount,
                $"A worksheet holds at most {MaxColumns} columns.");
        }

        var scaffold = new Workbook();

        if (spec.Culture is not null)
        {
            scaffold.Culture = spec.Culture;
        }

        var sheet = scaffold.AddSheet(spec.Name, rows: 1, columns: Math.Max(spec.ColumnCount, 1));

        sheet.Rows.Frozen = spec.FrozenRows;
        sheet.Columns.Frozen = spec.FrozenColumns;

        await new XlsxWriter(scaffold).WriteStreamedAsync(destination, spec, sheet, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteStreamedAsync(Stream destination, StreamedSheet spec, Worksheet sheet, CancellationToken cancellationToken)
    {
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
            Discard(destination, start);

            throw;
        }

        try
        {
            archive.Dispose();
        }
        catch
        {
            Discard(destination, start);

            throw;
        }
    }

    private const int MaxRows = 1_048_576;

    private const int MaxColumns = 16_384;

    private const int MaxCellCharacters = 32_767;

    private static int HeaderRows(StreamedSheet spec) => spec.IncludeHeader ? 1 : 0;

    private static bool HasTable(StreamedSheet spec, int written) =>
        spec.TableName is not null && written > HeaderRows(spec);

    private static void Discard(Stream destination, long start)
    {
        if (start < 0)
        {
            return;
        }

        try
        {
            destination.SetLength(start);
            destination.Position = start;
        }
        catch
        {
        }
    }

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

            cancellationToken.ThrowIfCancellationRequested();
        }

        var headerRows = HeaderRows(spec);

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

        if (declaredRows is { } promised && written - headerRows != promised)
        {
            throw new InvalidOperationException(
                $"RowCount said {promised} rows and the source yielded {written - headerRows}, so the "
                + "dimension written before them does not describe the sheet.");
        }

        if (spec.TableName is not { } tableName || written <= headerRows)
        {
            return false;
        }

        sheet.Rows.Count = Math.Max(written, 1);

        if (spec.IncludeHeader)
        {
            for (var column = 0; column < columns; column++)
            {
                sheet.Cells[0, column].SetText(spec.TitleAt(column));
            }
        }

        var range = new RangeRef(new CellRef(0, 0), new CellRef(written - 1, Math.Max(columns - 1, 0)));

        SaveTable(archive, sheet.AddTable(tableName, range, hasHeaders: spec.IncludeHeader), tableId: 1);

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

        CreatePageMargins().WriteTo(writer);

        if (HasTable(spec, written))
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

            if (row == MaxRows)
            {
                throw new InvalidOperationException(
                    $"The row source yielded more than the {MaxRows} rows a worksheet can hold.");
            }

            WriteStreamedRow(writer, spec, sheet, styleTracker, sharedStrings, styles, rows.Current, row++, columns);
        }

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

            if (text.Length > MaxCellCharacters)
            {
                throw new InvalidOperationException(
                    $"A cell holds at most {MaxCellCharacters} characters, and {address} was given {text.Length}.");
            }

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
            var type_ = CellTypeAttribute(type, isFormula: false);

            if (type_ is not null)
            {
                writer.WriteAttributeString("t", type_);
            }

            WriteTypedValue(writer, content, type);
        }

        writer.WriteEndElement();
    }

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

                string? text;

                try
                {
                    CellData.Infer(value, sheet.Workbook!.Culture, out var content, out var type);

                    text = NumberFormat.Apply(format?.NumberFormat, content, type, CultureInfo.InvariantCulture)
                        ?? Cell.FormatValue(content, CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

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
