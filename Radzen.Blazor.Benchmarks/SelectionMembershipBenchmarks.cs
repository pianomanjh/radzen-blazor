using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// The grid tests row membership in selectedItems / expandedItems / editedItems for every row on every
/// render (row style, aria-selected, edit mode, expansion). The original form,
/// <c>items.Keys.Any(i => ItemEquals(i, item))</c>, is an O(selected) scan plus a LINQ closure per row.
/// When no KeyProperty is set (the default), dictionary equality already matches, so ContainsKey is an
/// equivalent O(1) test. This isolates that difference over a page of rows.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class SelectionMembershipBenchmarks
{
    [Params(1_000, 10_000)]
    public int Rows { get; set; }

    // How many rows are currently selected (drives the O(selected) scan cost of the old form).
    [Params(50)]
    public int Selected { get; set; }

    private List<Person> data;
    private Dictionary<Person, bool> selected;

    [GlobalSetup]
    public void Setup()
    {
        data = Person.Generate(Rows);
        selected = data.Take(Selected).ToDictionary(p => p, _ => true);
    }

    [Benchmark(Baseline = true, Description = "Keys.Any(i => Equals(i, item)) per row")]
    public int LinqScan()
    {
        int count = 0;
        foreach (var p in data)
        {
            if (selected.Keys.Any(i => Equals(i, p)))
            {
                count++;
            }
        }
        return count;
    }

    [Benchmark(Description = "ContainsKey(item) per row")]
    public int ContainsKey()
    {
        int count = 0;
        foreach (var p in data)
        {
            if (selected.Count != 0 && selected.ContainsKey(p))
            {
                count++;
            }
        }
        return count;
    }
}
