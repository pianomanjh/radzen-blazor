using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Radzen;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Measures the search/filter execution a RadzenDropDown performs when the user types in the filter box:
/// View rebuilds Query.Where(TextProperty, searchText, op, cs) and materializes the matches. This is the
/// dropdown search-specific work (the subsequent re-render of the matched items is ordinary item rendering).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class DropDownFilterBenchmarks
{
    [Params(10_000, 100_000)]
    public int Items { get; set; }

    private List<Item> data;

    [GlobalSetup]
    public void Setup() => data = Item.Generate(Items);

    [Benchmark(Description = "Filter items by text (one search operation)")]
    public int Filter()
    {
        var view = data.AsQueryable().Where("Name", "Item 1", StringFilterOperator.StartsWith, FilterCaseSensitivity.CaseInsensitive);
        var matched = view.Cast<Item>().ToList();
        return matched.Count;
    }
}
