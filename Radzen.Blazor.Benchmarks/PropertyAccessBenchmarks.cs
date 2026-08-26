using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Isolates the property-access primitive used to read a cell value for every
/// visible cell on every render. Compares the reflection-based
/// <see cref="PropertyAccess.GetValue(object, string)"/> path against the
/// cached compiled getter (<see cref="PropertyAccess.Getter{TItem, TValue}(string, Type)"/>).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PropertyAccessBenchmarks
{
    // Number of items the value getter is invoked over (cells for one column across the data set).
    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    private List<Person> data;
    private Func<Person, object> flatGetter;
    private Func<Person, object> nestedGetter;

    [GlobalSetup]
    public void Setup()
    {
        data = Person.Generate(Rows);
        // The grid caches one compiled getter per column for the lifetime of the column.
        flatGetter = PropertyAccess.Getter<Person, object>("FirstName");
        nestedGetter = PropertyAccess.Getter<Person, object>("Address.City");
    }

    [Benchmark(Baseline = true, Description = "Reflection - flat property")]
    public object Reflection_Flat()
    {
        object last = null;
        foreach (var p in data)
        {
            last = PropertyAccess.GetValue(p, "FirstName");
        }
        return last;
    }

    [Benchmark(Description = "Compiled cached getter - flat property")]
    public object Compiled_Flat()
    {
        object last = null;
        foreach (var p in data)
        {
            last = flatGetter(p);
        }
        return last;
    }

    [Benchmark(Description = "Reflection - nested property (Address.City)")]
    public object Reflection_Nested()
    {
        object last = null;
        foreach (var p in data)
        {
            last = PropertyAccess.GetValue(p, "Address.City");
        }
        return last;
    }

    [Benchmark(Description = "Compiled cached getter - nested property")]
    public object Compiled_Nested()
    {
        object last = null;
        foreach (var p in data)
        {
            last = nestedGetter(p);
        }
        return last;
    }
}
