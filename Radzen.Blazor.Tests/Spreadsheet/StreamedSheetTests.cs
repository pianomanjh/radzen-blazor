using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class StreamedSheetTests
{
    private sealed record Person(string Name, int Age, DateTime Joined, bool Active);

    private static readonly DateTime Seed = new(2020, 1, 1);

    private static Person[] People(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Person($"Person {i}", 20 + (i % 50), Seed.AddDays(i % 900), i % 2 == 0))
            .ToArray();

    private static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            await Task.Yield();

            yield return item;
        }
    }

    private static StreamedSheet<Person> Sheet(IEnumerable<Person> rows) => new()
    {
        Name = "People",
        Rows = Async(rows),
        Columns =
        {
            new StreamedColumn<Person> { Title = "Name", Value = p => p.Name },
            new StreamedColumn<Person> { Title = "Age", Value = p => p.Age },
            new StreamedColumn<Person> { Title = "Joined", Value = p => p.Joined },
            new StreamedColumn<Person> { Title = "Active", Value = p => p.Active },
        },
    };

    private static Workbook Built(IReadOnlyList<Person> rows, bool header = true)
    {
        var workbook = new Workbook();
        var sheet = workbook.AddSheet("People", rows.Count + (header ? 1 : 0), 4);

        var offset = 0;

        if (header)
        {
            var titles = new[] { "Name", "Age", "Joined", "Active" };

            for (var column = 0; column < titles.Length; column++)
            {
                var cell = sheet.Cells[0, column];
                cell.Value = titles[column];
                cell.Format.Bold = true;
            }

            offset = 1;
        }

        for (var row = 0; row < rows.Count; row++)
        {
            sheet.Cells.SetValues(row + offset, 0,
            [
                [rows[row].Name, (double)rows[row].Age, rows[row].Joined, rows[row].Active],
            ]);
        }

        return workbook;
    }

    private static async Task<MemoryStream> Stream(StreamedSheet sheet, CancellationToken cancellationToken = default)
    {
        var stream = new MemoryStream();

        await Workbook.SaveToStreamAsync(stream, sheet, cancellationToken);

        stream.Position = 0;

        return stream;
    }

    private static MemoryStream Save(Workbook workbook)
    {
        var stream = new MemoryStream();

        workbook.SaveToStream(stream);
        stream.Position = 0;

        return stream;
    }

    private static Worksheet Read(MemoryStream stream)
    {
        stream.Position = 0;

        var sheet = Workbook.LoadFromStream(stream).Sheets[0];

        stream.Position = 0;

        return sheet;
    }

    private static XDocument SheetXml(MemoryStream stream)
    {
        stream.Position = 0;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();

        var doc = XDocument.Load(entry);

        stream.Position = 0;

        return doc;
    }

    private static void AssertCellsEqual(Worksheet expected, Worksheet actual, int rows, int columns)
    {
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = expected.Cells[row, column];
                var right = actual.Cells[row, column];

                Assert.Equal(left.Value, right.Value);
                Assert.Equal(left.ValueType, right.ValueType);
                Assert.Equal(left.GetEffectiveFormat()?.Bold, right.GetEffectiveFormat()?.Bold);
                Assert.Equal(left.GetEffectiveFormat()?.NumberFormat, right.GetEffectiveFormat()?.NumberFormat);
            }
        }
    }

    [Fact]
    public async Task Writes_the_header_and_every_row()
    {
        var people = People(5);

        var sheet = Read(await Stream(Sheet(people)));

        Assert.Equal("People", sheet.Name);
        Assert.Equal("Name", sheet.Cells[0, 0].Value);
        Assert.True(sheet.Cells[0, 0].GetEffectiveFormat()?.Bold);

        for (var row = 0; row < people.Length; row++)
        {
            Assert.Equal(people[row].Name, sheet.Cells[row + 1, 0].Value);
            Assert.Equal((double)people[row].Age, sheet.Cells[row + 1, 1].Value);
            Assert.Equal(people[row].Joined.ToNumber(), sheet.Cells[row + 1, 2].Value);
            Assert.Equal("mm/dd/yyyy", sheet.Cells[row + 1, 2].GetEffectiveFormat()?.NumberFormat);
            Assert.Equal(people[row].Active, sheet.Cells[row + 1, 3].Value);
        }
    }

    [Fact]
    public async Task Types_a_cell_as_the_builder_does()
    {
        var people = People(5);

        var streamed = Read(await Stream(Sheet(people)));
        var built = Read(Save(Built(people)));

        AssertCellsEqual(built, streamed, people.Length + 1, 4);
    }

    [Fact]
    public async Task Defaults_reproduce_the_built_file()
    {
        var people = People(120);

        var streamed = Read(await Stream(Sheet(people)));
        var built = Read(Save(Built(people)));

        Assert.Equal(built.RowCount, streamed.RowCount);
        Assert.Equal(built.ColumnCount, streamed.ColumnCount);

        AssertCellsEqual(built, streamed, built.RowCount, built.ColumnCount);
    }

    [Fact]
    public async Task Writes_no_rows_for_an_empty_source_but_still_writes_the_header()
    {
        var stream = await Stream(Sheet([]));
        var sheet = Read(stream);

        Assert.Equal("Name", sheet.Cells[0, 0].Value);
        Assert.Equal(1, sheet.RowCount);
    }

    [Fact]
    public async Task Omits_the_header_when_asked()
    {
        var people = People(3);
        var spec = Sheet(people);
        spec.IncludeHeader = false;

        var sheet = Read(await Stream(spec));

        Assert.Equal(people[0].Name, sheet.Cells[0, 0].Value);
    }

    [Fact]
    public async Task Freezes_the_rows_and_columns_it_was_given()
    {
        var spec = Sheet(People(3));
        spec.FrozenRows = 1;
        spec.FrozenColumns = 2;

        var sheet = Read(await Stream(spec));

        Assert.Equal(1, sheet.Rows.Frozen);
        Assert.Equal(2, sheet.Columns.Frozen);
    }

    [Fact]
    public async Task Writes_a_declared_width_without_reading_a_row()
    {
        var spec = Sheet(People(50));
        spec.WidthMode = ColumnWidthMode.Declared;
        spec.Columns[0].Width = 240;

        var sheet = Read(await Stream(spec));

        Assert.Equal(240, sheet.Columns[0], 0);
    }

    [Fact]
    public async Task Measures_an_auto_fit_column_over_the_sample()
    {
        var people = People(10).Append(new Person(new string('x', 60), 1, Seed, true)).ToArray();

        var spec = Sheet(people);
        spec.WidthMode = ColumnWidthMode.Sampled(200);
        spec.Columns[0].AutoFit = true;

        var sheet = Read(await Stream(spec));

        Assert.True(sheet.Columns[0] > sheet.Columns[1]);
        Assert.True(sheet.Columns.IsAutoFit(0));
    }

    [Fact]
    public async Task Measures_past_a_value_its_own_format_cannot_render()
    {
        var spec = new StreamedSheet<double>
        {
            Name = "Sheet1",
            WidthMode = ColumnWidthMode.Sampled(200),
            Rows = Async(new[] { 1e10, 1d }),
            Columns =
            {
                new StreamedColumn<double>
                {
                    Title = "when",
                    Value = d => d,
                    AutoFit = true,
                    Format = new Format { NumberFormat = "yyyy-mm-dd" },
                },
            },
        };

        var stream = new MemoryStream();
        await Workbook.SaveToStreamAsync(stream, spec);

        Assert.True(stream.Length > 0);
        Assert.True(Read(stream).Columns.IsAutoFit(0));
    }

    [Fact]
    public async Task A_table_is_not_written_for_a_source_that_had_only_a_header()
    {
        var spec = Sheet([]);
        spec.TableName = "People";

        var stream = await Stream(spec);

        Assert.Empty(Read(stream).Tables);

        stream.Position = 0;

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        Assert.Null(archive.GetEntry("xl/tables/table1.xml"));
        Assert.Null(archive.GetEntry("xl/worksheets/_rels/sheet1.xml.rels"));

        using var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        using var reader = new StreamReader(entry);

        Assert.DoesNotContain("tablePart", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recovery_that_cannot_run_does_not_replace_the_failure()
    {
        var spec = Sheet(People(3));
        spec.Rows = Faulting();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workbook.SaveToStreamAsync(new FixedLength(), spec));

        static async IAsyncEnumerable<Person> Faulting()
        {
            await Task.Yield();

            yield return new Person("Ada", 1, Seed, true);

            throw new InvalidOperationException("the source failed");
        }
    }

    private sealed class FixedLength : MemoryStream
    {
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Types_a_value_through_the_culture_it_was_given()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");

        var spec = new StreamedSheet<string>
        {
            Name = "Sheet1",
            Culture = german,
            Rows = Async(new[] { "17.08.2024" }),
            Columns = { new StreamedColumn<string> { Title = "when", Value = v => v } },
        };

        var german_ = Read(await Stream(spec)).Cells[1, 0];

        var invariant = new StreamedSheet<string>
        {
            Name = "Sheet1",
            Culture = CultureInfo.InvariantCulture,
            Rows = Async(new[] { "17.08.2024" }),
            Columns = { new StreamedColumn<string> { Title = "when", Value = v => v } },
        };

        var invariant_ = Read(await Stream(invariant)).Cells[1, 0];

        Assert.Equal(CellDataType.Number, german_.ValueType);
        Assert.Equal(CellDataType.String, invariant_.ValueType);
        Assert.Equal(new DateTime(2024, 8, 17), Assert.IsType<double>(german_.Value).ToDate());
    }

    [Fact]
    public async Task A_sheet_wider_than_the_format_is_refused_before_a_row_is_read()
    {
        var read = false;

        var spec = new StreamedSheet<int> { Name = "Sheet1", Rows = Counted() };

        for (var c = 0; c <= 16_384; c++)
        {
            spec.Columns.Add(new StreamedColumn<int> { Title = "c" + c, Value = v => v });
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Workbook.SaveToStreamAsync(new MemoryStream(), spec));

        Assert.False(read);

        async IAsyncEnumerable<int> Counted()
        {
            read = true;

            await Task.Yield();

            yield return 1;
        }
    }

    [Fact]
    public async Task A_source_longer_than_the_format_can_hold_is_refused()
    {
        var spec = new StreamedSheet<int>
        {
            Name = "Sheet1",
            IncludeHeader = false,
            WidthMode = ColumnWidthMode.Declared,
            Rows = Endless(),
            Columns = { new StreamedColumn<int> { Title = "n", Value = v => v } },
        };

        var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workbook.SaveToStreamAsync(stream, spec));

        Assert.Equal(0, stream.Length);

        static async IAsyncEnumerable<int> Endless()
        {
            for (var i = 0; i <= 1_048_576; i++)
            {
                yield return i;
            }

            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_row_count_the_source_does_not_keep_is_refused()
    {
        var spec = Sheet(People(3));
        spec.RowCount = 5;
        spec.WidthMode = ColumnWidthMode.Declared;

        var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workbook.SaveToStreamAsync(stream, spec));

        Assert.Contains("RowCount", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Does_not_measure_past_the_sample()
    {
        var people = People(10).Append(new Person(new string('x', 200), 1, Seed, true)).ToArray();

        var spec = Sheet(people);
        spec.WidthMode = ColumnWidthMode.Sampled(4);
        spec.Columns[0].AutoFit = true;

        var narrow = Read(await Stream(spec));

        var wide = Sheet(people);
        wide.WidthMode = ColumnWidthMode.Sampled(200);
        wide.Columns[0].AutoFit = true;

        Assert.True(Read(await Stream(wide)).Columns[0] > narrow.Columns[0]);
    }

    [Fact]
    public async Task Writes_the_dimension_when_the_sample_held_the_source()
    {
        var stream = await Stream(Sheet(People(5)));

        var dimension = SheetXml(stream).Root!
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "dimension")?
            .Attribute("ref")?.Value;

        Assert.Equal("A1:D6", dimension);
    }

    [Fact]
    public async Task Omits_the_dimension_when_the_count_is_unknown()
    {
        var spec = Sheet(People(20));
        spec.WidthMode = ColumnWidthMode.Sampled(4);

        var stream = await Stream(spec);

        Assert.DoesNotContain(SheetXml(stream).Root!.Elements(), e => e.Name.LocalName == "dimension");
    }

    [Fact]
    public async Task Writes_the_dimension_the_caller_declared()
    {
        var spec = Sheet(People(20));
        spec.WidthMode = ColumnWidthMode.Sampled(4);
        spec.RowCount = 20;

        var stream = await Stream(spec);

        var dimension = SheetXml(stream).Root!
            .Elements()
            .First(e => e.Name.LocalName == "dimension")
            .Attribute("ref")!.Value;

        Assert.Equal("A1:D21", dimension);
    }

    [Fact]
    public async Task Interns_strings_by_default_and_inlines_them_when_asked()
    {
        var people = People(6);

        var shared = await Stream(Sheet(people));

        using (var zip = new ZipArchive(shared, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.NotNull(zip.GetEntry("xl/sharedStrings.xml"));
        }

        var spec = Sheet(people);
        spec.InlineStrings = true;

        var inline = await Stream(spec);

        using (var zip = new ZipArchive(inline, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Null(zip.GetEntry("xl/sharedStrings.xml"));
        }

        Assert.Equal(people[0].Name, Read(inline).Cells[1, 0].Value);
    }

    [Fact]
    public async Task Sizes_the_table_to_the_rows_it_wrote()
    {
        var spec = Sheet(People(7));
        spec.TableName = "People";

        var stream = await Stream(spec);
        var sheet = Read(stream);

        var table = Assert.Single(sheet.Tables);

        Assert.Equal("A1:D8", $"{table.Range.Start}:{table.Range.End}");
        Assert.Equal("Name", table.Columns[0].Name);
    }

    [Fact]
    public async Task Applies_a_column_format_to_every_cell_of_it()
    {
        var spec = Sheet(People(4));
        spec.Columns[1].Format = new Format { NumberFormat = "0.00" };

        var sheet = Read(await Stream(spec));

        Assert.Equal("0.00", sheet.Cells[1, 1].GetEffectiveFormat()?.NumberFormat);
        Assert.Equal("0.00", sheet.Cells[4, 1].GetEffectiveFormat()?.NumberFormat);
    }

    [Fact]
    public async Task A_faulting_source_leaves_nothing_that_opens()
    {
        static async IAsyncEnumerable<Person> Faulting()
        {
            await Task.Yield();

            yield return new Person("first", 1, Seed, true);

            throw new InvalidOperationException("the source gave up");
        }

        var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workbook.SaveToStreamAsync(stream, new StreamedSheet<Person>
            {
                Rows = Faulting(),
                Columns = { new StreamedColumn<Person> { Title = "Name", Value = p => p.Name } },
            }));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task A_source_that_cancels_as_it_ends_leaves_nothing_that_opens()
    {
        using var cancellation = new CancellationTokenSource();

        async IAsyncEnumerable<Person> CancelsLast()
        {
            await Task.Yield();

            yield return new Person("first", 1, Seed, true);

            cancellation.Cancel();
        }

        var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Workbook.SaveToStreamAsync(stream, new StreamedSheet<Person>
            {
                Rows = CancelsLast(),
                Columns = { new StreamedColumn<Person> { Title = "Name", Value = p => p.Name } },
            }, cancellation.Token));

        Assert.Equal(0, stream.Length);
    }

    // The source above fits the sample, so the buffering loop's check is what catches it. A source
    // longer than the sample gets past that loop and is read by the streaming one, whose own end is the
    // second place a token cancelled after the last row can slip through.
    [Fact]
    public async Task A_source_longer_than_the_sample_that_cancels_as_it_ends_leaves_nothing_that_opens()
    {
        using var cancellation = new CancellationTokenSource();

        async IAsyncEnumerable<Person> CancelsLast()
        {
            foreach (var person in People(6))
            {
                await Task.Yield();

                yield return person;
            }

            cancellation.Cancel();
        }

        var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Workbook.SaveToStreamAsync(stream, new StreamedSheet<Person>
            {
                WidthMode = ColumnWidthMode.Sampled(2),
                Rows = CancelsLast(),
                Columns = { new StreamedColumn<Person> { Title = "Name", Value = p => p.Name } },
            }, cancellation.Token));

        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task A_table_column_keeps_a_heading_that_reads_as_a_number()
    {
        var spec = Sheet(People(3));
        spec.TableName = "People";
        spec.Columns[0].Title = "2026";

        var stream = await Stream(spec);

        var header = SheetXml(stream).Root!
            .Elements().First(e => e.Name.LocalName == "sheetData")
            .Elements().First()
            .Elements().First();

        Assert.Equal("s", header.Attribute("t")?.Value);
        Assert.Equal("2026", Assert.Single(Read(stream).Tables).Columns[0].Name);
    }

    [Fact]
    public async Task A_cancelled_write_leaves_nothing_that_opens()
    {
        using var cancellation = new CancellationTokenSource();

        async IAsyncEnumerable<Person> Cancelling()
        {
            await Task.Yield();

            yield return new Person("first", 1, Seed, true);

            cancellation.Cancel();

            yield return new Person("second", 2, Seed, true);
        }

        var stream = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Workbook.SaveToStreamAsync(stream, new StreamedSheet<Person>
            {
                Rows = Cancelling(),
                Columns = { new StreamedColumn<Person> { Title = "Name", Value = p => p.Name } },
            }, cancellation.Token));

        Assert.Equal(0, stream.Length);
    }
}
