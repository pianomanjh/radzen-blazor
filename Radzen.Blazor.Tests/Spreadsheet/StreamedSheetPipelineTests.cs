using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

/// <summary>
/// §66: that the rows are pulled one at a time and written as they arrive, rather than drained first.
/// </summary>
/// <remarks>
/// <para>
/// Every allocation figure this feature quotes rests on the source and the writer alternating: a path
/// that read the rows into anything before writing them would report the same bytes per row on a small
/// sheet and fall over on a large one. Allocation cannot see the difference - a list that is filled and
/// dropped inside one measured region looks like garbage either way - so this asserts the ordering
/// itself, by watching what the source has yielded at the moment bytes reach the destination.
/// </para>
/// <para>
/// It is not a claim that nothing anywhere buffers. The zip's deflate stream holds tens of kilobytes
/// before it passes any on, which is why the bound below is generous rather than one row; a real
/// provider's reader holds a packet of rows for the same kind of reason. What is asserted is that
/// <em>this</em> code holds nothing: writing begins while the source is near its start, and the source
/// is still being read when the last of the file is written.
/// </para>
/// </remarks>
public class StreamedSheetPipelineTests
{
    private const int Rows = 20_000;

    private sealed class Source
    {
        public int Yielded;

        public async IAsyncEnumerable<int> Read()
        {
            for (var i = 0; i < Rows; i++)
            {
                Yielded++;

                yield return i;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class Watcher(Source source) : Stream
    {
        public int FirstWriteAtRow = -1;
        public int LastWriteAtRow = -1;
        public long Length_;

        public override void Write(byte[] buffer, int offset, int count) => Note(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Note(buffer.Length);

        private void Note(int count)
        {
            if (FirstWriteAtRow < 0)
            {
                FirstWriteAtRow = source.Yielded;
            }

            LastWriteAtRow = source.Yielded;
            Length_ += count;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Length_;
        public override long Position { get => Length_; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static StreamedSheet<int> Sheet(Source source, ColumnWidthMode mode) => new()
    {
        Rows = source.Read(),
        WidthMode = mode,
        Columns =
        {
            new StreamedColumn<int> { Title = "Index", Value = i => i },
            new StreamedColumn<int> { Title = "Name", Value = i => "row " + (i % 100) },
        },
    };

    /// <summary>
    /// Bytes reach the destination while the source is near its beginning, and the source is still
    /// being read when the last of them do.
    /// </summary>
    [Fact]
    public async Task TheRowsAreWrittenAsTheyArrive()
    {
        var source = new Source();
        var watcher = new Watcher(source);

        await Workbook.SaveToStreamAsync(watcher, Sheet(source, ColumnWidthMode.Declared));

        Assert.Equal(Rows, source.Yielded);

        Assert.InRange(watcher.FirstWriteAtRow, 0, Rows / 4);

        Assert.Equal(Rows, watcher.LastWriteAtRow);
    }

    /// <summary>
    /// The sample is the one thing that reads ahead, and it reads exactly as far as it was told to.
    /// </summary>
    [Fact]
    public async Task TheSampleReadsAheadAndNothingElseDoes()
    {
        var source = new Source();
        var watcher = new Watcher(source);

        await Workbook.SaveToStreamAsync(watcher, Sheet(source, ColumnWidthMode.Sampled(200)));

        Assert.InRange(watcher.FirstWriteAtRow, 200, Rows / 4);
        Assert.Equal(Rows, watcher.LastWriteAtRow);
    }

    /// <summary>
    /// A source that stops early stops the write with it - nothing was read past the row that threw.
    /// </summary>
    [Fact]
    public async Task AFaultingSourceIsNotReadPast()
    {
        var source = new Source();

        async IAsyncEnumerable<int> Faulting()
        {
            await foreach (var row in source.Read())
            {
                if (row == 500)
                {
                    throw new InvalidOperationException("the source gave up");
                }

                yield return row;
            }
        }

        var sheet = Sheet(source, ColumnWidthMode.Declared);
        sheet.Rows = Faulting();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workbook.SaveToStreamAsync(new Watcher(source), sheet));

        Assert.Equal(501, source.Yielded);
    }
}
