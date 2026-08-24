using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Radzen;

namespace Radzen.Blazor.Benchmarks;

/// <summary>
/// Models what RadzenDataGrid.View / PagedView do to a filtered, sorted, paged in-memory data set,
/// using the grid's own <see cref="QueryableExtension.OrderBy{T}(System.Linq.IQueryable{T}, string)"/>.
///
/// - <see cref="Current_CountThenPage_DoubleSort"/> mirrors today's code: Count() runs on the already
///   sorted query, then the page is materialized - filter+sort executed twice, sort executed for the
///   count even though ordering cannot change a count.
/// - <see cref="CountOnFiltered_SortForPageOnly"/> counts the filtered (unsorted) query and sorts only
///   for the page - one sort pass instead of two (also removes ORDER BY from a SQL COUNT).
/// - <see cref="MaterializeOnce"/> filters+sorts once into a list, then counts and pages the list -
///   correct only for genuinely in-memory data (would defeat server-side paging on a real provider).
/// </summary>
[MemoryDiagnoser]
[MarkdownExporterAttribute.GitHub]
public class PipelineBenchmarks
{
    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    const int PageSize = 50;

    private List<Person> data;

    [GlobalSetup]
    public void Setup() => data = Person.Generate(Rows);

    // Filter that keeps ~half the rows, similar to a column filter narrowing the set.
    static IQueryable<Person> Filtered(IQueryable<Person> q) => q.Where(p => p.Salary >= 40000m + 50 * 137m);

    [Benchmark(Baseline = true, Description = "Current: Count on sorted, then page (sort x2)")]
    public int Current_CountThenPage_DoubleSort()
    {
        var view = Filtered(data.AsQueryable()).OrderBy("LastName desc");
        var count = view.Count();
        var page = view.Skip(count > PageSize ? PageSize : 0).Take(PageSize).ToList();
        return count + page.Count;
    }

    [Benchmark(Description = "Count on filtered, sort for page only (sort x1)")]
    public int CountOnFiltered_SortForPageOnly()
    {
        var filtered = Filtered(data.AsQueryable());
        var count = filtered.Count();
        var page = filtered.OrderBy("LastName desc").Skip(count > PageSize ? PageSize : 0).Take(PageSize).ToList();
        return count + page.Count;
    }

    [Benchmark(Description = "Materialize filtered+sorted once (in-memory only)")]
    public int MaterializeOnce()
    {
        var list = Filtered(data.AsQueryable()).OrderBy("LastName desc").ToList();
        var count = list.Count;
        var page = list.Skip(count > PageSize ? PageSize : 0).Take(PageSize).ToList();
        return count + page.Count;
    }

    // Same result via compiled IEnumerable delegates instead of the IQueryable (expression-tree)
    // path, to isolate how much of the cost is EnumerableQuery expression interpretation vs. the
    // actual data work. This is what an in-memory fast path could use (server-side IQueryable must
    // still go through the expression pipeline so EF can translate it).
    [Benchmark(Description = "Compiled IEnumerable delegates (in-memory fast path)")]
    public int CompiledEnumerable()
    {
        IEnumerable<Person> filtered = data.Where(p => p.Salary >= 40000m + 50 * 137m);
        var count = filtered.Count();
        var page = filtered.OrderByDescending(p => p.LastName).Skip(count > PageSize ? PageSize : 0).Take(PageSize).ToList();
        return count + page.Count;
    }
}
