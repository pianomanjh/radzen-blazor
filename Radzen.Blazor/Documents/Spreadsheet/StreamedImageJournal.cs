using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

internal sealed class StreamedImageJournal(Func<Stream> open) : IDisposable
{
    private const int RecordLength = 16;

    private readonly Dictionary<Digest, int> media = [];

    private readonly List<string> extensions = [];

    private readonly byte[] record = new byte[RecordLength];

    private Stream? store;

    public int Count { get; private set; }

    public IReadOnlyList<string> Extensions => extensions;

    public void Add(CellRef address, StreamedImage image)
    {
        var data = image.Data;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);

        var digest = new Digest(hash);

        if (media.TryGetValue(digest, out var index))
        {
            Write(address, index, -1);
        }
        else
        {
            var contentType = image.ResolveContentType() ?? throw new InvalidOperationException(
                $"The image for {address} is not a PNG, JPEG, GIF, BMP or TIFF, and was given no content type.");

            index = extensions.Count;

            media.Add(digest, index);
            extensions.Add(XlsxWriter.ContentTypeToExtension(contentType));

            Write(address, index, data.Length);
            store!.Write(data);
        }

        Count++;
    }

    public void CopyMedia(Func<int, Stream> create)
    {
        if (store is null)
        {
            return;
        }

        store.Position = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(81_920);

        try
        {
            for (var i = 0; i < Count; i++)
            {
                var (_, _, index, length) = Read();

                if (length < 0)
                {
                    continue;
                }

                using var target = create(index);

                while (length > 0)
                {
                    var read = store.Read(buffer, 0, Math.Min(buffer.Length, length));

                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    target.Write(buffer, 0, read);
                    length -= read;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public IEnumerable<(int Row, int Column, int Media)> Anchors()
    {
        if (store is null)
        {
            yield break;
        }

        store.Position = 0;

        for (var i = 0; i < Count; i++)
        {
            var (row, column, index, length) = Read();

            if (length > 0)
            {
                store.Seek(length, SeekOrigin.Current);
            }

            yield return (row, column, index);
        }
    }

    public void Dispose() => store?.Dispose();

    private void Write(CellRef address, int index, int length)
    {
        store ??= open();

        BinaryPrimitives.WriteInt32LittleEndian(record, address.Row);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), address.Column);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), index);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), length);

        store.Write(record);
    }

    private (int Row, int Column, int Index, int Length) Read()
    {
        store!.ReadExactly(record);

        return (
            BinaryPrimitives.ReadInt32LittleEndian(record),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12)));
    }

    private readonly record struct Digest(ulong A, ulong B, ulong C, ulong D)
    {
        public Digest(ReadOnlySpan<byte> hash)
            : this(
                BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]))
        {
        }
    }
}
