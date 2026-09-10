using System;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class StreamedImageTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "image/gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 }, "image/bmp")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, "image/tiff")]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }, "image/tiff")]
    public void Reads_the_type_from_the_bytes(byte[] data, string expected)
    {
        Assert.Equal(expected, new StreamedImage(data).ResolveContentType());
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 })]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    public void Does_not_guess_a_type_it_cannot_read(byte[] data)
    {
        Assert.Null(new StreamedImage(data).ResolveContentType());
    }

    [Fact]
    public void Keeps_a_type_it_was_given_over_the_bytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        Assert.Equal("image/jpeg", new StreamedImage(png, "image/jpeg").ResolveContentType());
    }

    [Fact]
    public void Refuses_no_data()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamedImage(null!));
    }
}
