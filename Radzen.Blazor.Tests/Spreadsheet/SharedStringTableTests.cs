using System.Collections.Generic;
using System.Globalization;
using Radzen.Documents.Spreadsheet;
using Xunit;

namespace Radzen.Blazor.Spreadsheet.Tests;

#nullable enable

public class SharedStringTableTests
{
    [Fact]
    public void GetOrAdd_NumbersStringsInTheOrderTheyArrive()
    {
        using var table = new SharedStringTable();

        Assert.Equal(0, table.GetOrAdd("b"));
        Assert.Equal(1, table.GetOrAdd("a"));
        Assert.Equal(2, table.GetOrAdd(string.Empty));

        Assert.Equal(new[] { "b", "a", string.Empty }, table.Strings.ToArray());
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public void GetOrAdd_ReturnsTheIndexAnEqualStringAlreadyHas()
    {
        using var table = new SharedStringTable();

        table.GetOrAdd("Yes");
        table.GetOrAdd("No");

        Assert.Equal(0, table.GetOrAdd(new string("Yes".ToCharArray())));
        Assert.Equal(1, table.GetOrAdd("No"));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void GetOrAdd_KeepsEveryIndexAcrossGrowth()
    {
        using var table = new SharedStringTable();

        var texts = new List<string>();

        for (var i = 0; i < 100_000; i++)
        {
            var text = "row " + i.ToString(CultureInfo.InvariantCulture);
            texts.Add(text);

            Assert.Equal(i, table.GetOrAdd(text));
        }

        for (var i = 0; i < texts.Count; i++)
        {
            Assert.Equal(i, table.GetOrAdd(texts[i]));
        }

        Assert.Equal(texts.Count, table.Count);
        Assert.Equal(texts, table.Strings.ToArray());
    }

    [Fact]
    public void GetOrAdd_LooksAStringUpAtTheCapacityBoundaryWithoutAdding()
    {
        using var table = new SharedStringTable();

        for (var i = 0; i < 256; i++)
        {
            table.GetOrAdd(i.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(0, table.GetOrAdd("0"));
        Assert.Equal(255, table.GetOrAdd("255"));
        Assert.Equal(256, table.Count);

        Assert.Equal(256, table.GetOrAdd("256"));
        Assert.Equal(0, table.GetOrAdd("0"));
        Assert.Equal(257, table.Count);
    }

    [Fact]
    public void Dispose_EmptiesTheTable()
    {
        var table = new SharedStringTable();

        for (var i = 0; i < 1000; i++)
        {
            table.GetOrAdd(i.ToString(CultureInfo.InvariantCulture));
        }

        table.Dispose();

        Assert.Equal(0, table.Count);
        Assert.True(table.Strings.IsEmpty);
        Assert.Equal(0, table.GetOrAdd("again"));
    }
}
