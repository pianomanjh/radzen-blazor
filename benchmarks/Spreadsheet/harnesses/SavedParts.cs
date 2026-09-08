using System;
using System.IO;
using System.IO.Compression;
using Radzen;
using Radzen.Documents.Spreadsheet;

// Every part of a workbook that uses every worksheet feature, written out for byte comparison.
//
// A rewrite of the writer that claims to change no byte has to be held to that on more than a sheet of
// plain values. This builds one workbook carrying every child element a worksheet can have, saves it,
// and unpacks every part to a directory. Run it against two checkouts into two directories and compare
// them with compare-parts.py, which normalises the two things that are meant to differ between any two
// runs - the random revision uid and the docProps timestamp - and nothing else.
//
//     dotnet run -c Release -- ./tree      # with the project reference pointed at one checkout
//     dotnet run -c Release -- ./stream    # and then at the other
//     python3 compare-parts.py ./tree ./stream
//
// Mutate a byte in one of the outputs before believing a clean result: a comparison that cannot fail is
// not evidence, and this one has already reported a pass over two empty directories when the program
// behind it failed to build.
static class Program
{
    // The smallest PNG that decodes: an 8-bit RGBA 1x1.
    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static void Main(string[] args)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("Sheet1", 40, 10);

        // A column of copies, which is what makes a shared formula group.
        sheet.Cells[0, 0].Value = 1;
        sheet.Cells[1, 0].Value = 2;

        for (var r = 0; r < 6; r++)
        {
            sheet.Cells[r, 1].SetValue("=A" + (r + 1) + "*2");
        }

        // A formula whose result is an error, and one whose result is text.
        sheet.Cells[8, 0].SetValue("=1/0");
        sheet.Cells[8, 1].SetValue("=\"a\"&\"b\"");
        sheet.Cells[9, 0].SetValue("=UPPER(\"a<b>&c\")");
        sheet.Cells[9, 1].SetValue("=IF(A1<2,\"yes\",\"no\")");

        // Text the writer has to escape, including a carriage return and a surrogate pair.
        sheet.Cells[10, 0].SetValue("<tag> & \"quoted\" 'apos'");
        sheet.Cells[11, 0].SetValue("line\r\nbreak\ttab");
        sheet.Cells[12, 0].SetValue("emoji \U0001F600 and éü");
        sheet.Cells[13, 0].SetValue("'0012");
        sheet.Cells[14, 0].SetValue(string.Empty);

        sheet.Cells[15, 0].Value = true;
        sheet.Cells[15, 1].Value = false;
        sheet.Cells[16, 0].Value = new DateTime(2021, 3, 4, 5, 6, 7);
        sheet.Cells[16, 1].Value = 1234.5678;
        sheet.Cells[16, 2].Value = -0.000001234;

        sheet.Cells[17, 0].Hyperlink = new Hyperlink { Url = "https://example.com/a?b=1&c=2", Text = "link & more" };
        sheet.Cells[17, 1].Hyperlink = new Hyperlink { Url = "https://example.com/plain" };

        sheet.Cells[18, 0].Format.Bold = true;
        sheet.Cells[18, 0].Format.NumberFormat = "#,##0.00;[Red](#,##0.00)";
        sheet.Cells[18, 0].Value = 42;
        sheet.Cells[18, 1].Format.BackgroundColor = "#00FF00";
        sheet.Cells[18, 1].Format.WrapText = true;
        sheet.Cells[18, 1].Format.TextAlign = TextAlign.Center;
        sheet.Cells[18, 1].Format.VerticalAlign = VerticalAlign.Middle;
        sheet.Cells[18, 1].Value = "styled";
        sheet.Cells[18, 2].Format.Locked = true;
        sheet.Cells[18, 2].Format.FormulaHidden = true;
        sheet.Cells[18, 2].Value = "protected";

        sheet.Cells[20, 0].Format.BorderTop = new BorderStyle { LineStyle = BorderLineStyle.Double, Color = "#123456" };
        sheet.Cells[20, 0].Value = "bordered";
        sheet.MergedCells.Add(new RangeRef(new CellRef(20, 0), new CellRef(21, 2)));
        sheet.MergedCells.Add(new RangeRef(new CellRef(23, 4), new CellRef(23, 6)));

        sheet.ConditionalFormats.Add(
            new RangeRef(new CellRef(25, 0), new CellRef(25, 5)),
            new GreaterThanRule { Value = 10, Format = new Format { BackgroundColor = "#FF0000" } });

        sheet.AutoFilter.Range = new RangeRef(new CellRef(27, 0), new CellRef(30, 3));

        sheet.Rows[32] = 44.25;
        sheet.Rows.Hide(33);
        sheet.Columns[5] = 180.0;
        sheet.Columns.Hide(6);

        sheet.Cells[35, 0].SetValue("=SUM(A1:A2)");

        // A drawing, a table and a hyperlink all take relationship ids, whose namespace prefix the
        // writer does not name - it is generated, and generated per document.
        sheet.AddTable("Sales", new RangeRef(new CellRef(27, 0), new CellRef(30, 3)));

        // Worksheet.AddImage is internal, so the drawing goes in the way the test project would.
        AddImage(sheet, new SheetImage
        {
            AnchorMode = DrawingAnchorMode.OneCellAnchor,
            From = new CellAnchor { Row = 2, Column = 6, RowOffset = 12345, ColumnOffset = 67890 },
            Width = 2000000,
            Height = 1500000,
            Data = Png,
            ContentType = "image/png",
            Name = "test.png",
        });

        static void AddImage(Worksheet sheet, SheetImage image) =>
            typeof(Worksheet).GetMethod("AddImage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(sheet, new object[] { image });

        sheet.Validation.Add(
            new RangeRef(new CellRef(26, 0), new CellRef(26, 3)),
            new DataValidationRule
            {
                Type = DataValidationType.Decimal,
                Operator = DataValidationOperator.Between,
                Formula1 = "0",
                Formula2 = "100",
                ErrorTitle = "Out of range",
                Error = "Enter 0 to 100 & no more",
            });

        sheet.Protection.IsProtected = true;
        sheet.Protection.AllowFormatCells = true;

        // A sheet with no cells at all, which is the one shape an empty sheetData reaches.
        workbook.AddSheet("Empty", 10, 4);

        using var stream = new MemoryStream();
        workbook.SaveToStream(stream);
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var written = 0;

        foreach (var entry in zip.Entries)
        {
            using var part = entry.Open();
            using var target = File.Create(Path.Combine(args[0], entry.FullName.Replace('/', '_')));
            part.CopyTo(target);
            written++;
        }

        if (written < 8)
        {
            throw new InvalidOperationException($"only {written} parts written; the comparison would be of nothing");
        }

        Console.WriteLine($"wrote {written} parts to {args[0]}");
    }
}
