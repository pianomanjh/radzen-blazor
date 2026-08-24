using System.Collections.Generic;

namespace Radzen.Blazor.Benchmarks;

public sealed class Item
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public bool Disabled { get; set; }

    public static List<Item> Generate(int count)
    {
        var categories = new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" };
        var list = new List<Item>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new Item
            {
                Id = i,
                Name = "Item " + i,
                Category = categories[i % categories.Length],
                Disabled = (i % 13) == 0,
            });
        }
        return list;
    }
}
