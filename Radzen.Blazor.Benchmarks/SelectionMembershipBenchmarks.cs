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
    // Distinct instances with the SAME key values as the selected rows - models KeyProperty + a data
    // reload (EF/server), where the bound items are new objects that equal the selection by key only.
    private List<Person> reloaded;
    private System.Func<Person, object> keyGetter;

    [GlobalSetup]
    public void Setup()
    {
        data = Person.Generate(Rows);
        selected = data.Take(Selected).ToDictionary(p => p, _ => true);
        keyGetter = p => p.Id;
        // A fresh set of instances carrying the same Ids (so reference equality fails, key equality holds).
        reloaded = data.Select(p => new Person { Id = p.Id }).ToList();
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

    // --- KeyProperty set: the current fallback still scans + invokes the key getter twice per compare. ---

    [Benchmark(Description = "KeyProperty: scan + key getter per row (current)")]
    public int KeyPropertyScan()
    {
        int count = 0;
        foreach (var p in reloaded)
        {
            if (selected.Count != 0 && ContainsByKeyScan(p))
            {
                count++;
            }
        }
        return count;
    }

    bool ContainsByKeyScan(Person item)
    {
        var target = keyGetter(item);
        foreach (var i in selected.Keys)
        {
            if (Equals(keyGetter(i), target))
            {
                return true;
            }
        }
        return false;
    }

    [Benchmark(Description = "KeyProperty: key-value HashSet, O(1) per row (proposed)")]
    public int KeyPropertyHashSet()
    {
        // Build the key-value set once (amortized over all rows/renders), then O(1) per row.
        var keys = new HashSet<object>(selected.Count);
        foreach (var i in selected.Keys)
        {
            keys.Add(keyGetter(i));
        }

        int count = 0;
        foreach (var p in reloaded)
        {
            if (keys.Count != 0 && keys.Contains(keyGetter(p)))
            {
                count++;
            }
        }
        return count;
    }
}
