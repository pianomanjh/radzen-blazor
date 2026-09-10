using System;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// A picture written into one cell of a streamed sheet, drawn to fill the cell.
/// </summary>
public sealed class StreamedImage
{
    /// <summary>
    /// Creates an image from its encoded bytes.
    /// </summary>
    /// <param name="data">The encoded image. It is read when its row is written, so it must not change
    /// until the save completes.</param>
    /// <param name="contentType">The MIME type, such as <c>image/jpeg</c>. Null reads it from the bytes,
    /// which recognizes PNG, JPEG, GIF, BMP and TIFF.</param>
    public StreamedImage(byte[] data, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        Data = data;
        ContentType = contentType;
    }

    /// <summary>
    /// The encoded image.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// The MIME type as given, or null when it is read from <see cref="Data"/>.
    /// </summary>
    public string? ContentType { get; }

    internal string? ResolveContentType() => ContentType ?? Sniff(Data);

    internal static string? Sniff(ReadOnlySpan<byte> data) => data switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, 0x37 or 0x39, 0x61, ..] => "image/gif",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => "image/tiff",
        _ => null,
    };
}
