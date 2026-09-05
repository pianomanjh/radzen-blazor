using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using QG = Microsoft.AspNetCore.Components.QuickGrid;

public class Person
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Age { get; set; }
    public DateTime Hired { get; set; }
    public decimal Salary { get; set; }

    public static List<Person> Make(int n) =>
        Enumerable.Range(0, n).Select(i => new Person
        {
            Id = i,
            Name = "Person " + i,
            Age = 20 + (i % 45),
            Hired = new DateTime(2010, 1, 1).AddDays(i),
            Salary = 40000m + (i % 1000) * 37m
        }).ToList();
}

// ---- Minimal in-memory Blazor renderer (egil/Benchmark.Blazor technique) ----

sealed class NoopJsObjectReference : IJSObjectReference
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object[] args) => Answer<T>(identifier);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object[] args) => Answer<T>(identifier);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // This harness stands in for a browser, so where a component asks the browser whether something
    // worked, the answer has to be the browser's. RadzenFastGrid attaches one listener for its row and
    // cell clicks and renders the per-cell handlers instead if that call comes back false - so a fake
    // answering default(bool) measures the fallback and reports the cost the browser no longer pays.
    //
    // Say yes to any call that asks for a bool. Nothing here is a real DOM, so nothing acts on it; the
    // point is only that the component takes the branch a browser would.
    static ValueTask<T> Answer<T>(string identifier) =>
        typeof(T) == typeof(bool) ? new((T)(object)true) : new(default(T));
}

sealed class NoopJSRuntime : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object[] args) => Stub<T>();
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object[] args) => Stub<T>();

    // QuickGrid imports a JS module and calls into it from OnAfterRender; hand back a no-op module
    // reference instead of null so those calls don't NRE.
    static ValueTask<T> Stub<T>() =>
        typeof(T) == typeof(IJSObjectReference) ? new((T)(object)new NoopJsObjectReference()) : new(default(T));
}

sealed class BenchmarkRenderer : Renderer
{
    public BenchmarkRenderer(IServiceProvider services)
        : base(services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance) { }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    protected override void HandleException(Exception e) => throw e;
    protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

    public Task RenderComponent(Type type, ParameterView parameters) => Dispatcher.InvokeAsync(async () =>
    {
        var component = InstantiateComponent(type);
        var id = AssignRootComponentId(component);
        await RenderRootComponentAsync(id, parameters);
    });

    // The same, handing back the instance so a benchmark can drive it - opening a popup, say - and
    // measure the renders that follow.
    public Task<IComponent> Render(Type type, ParameterView parameters) => Dispatcher.InvokeAsync(async () =>
    {
        var component = InstantiateComponent(type);
        var id = AssignRootComponentId(component);
        await RenderRootComponentAsync(id, parameters);

        return component;
    });

    public Task Drive(Func<Task> action) => Dispatcher.InvokeAsync(action);
}

// ---- Render face-off: same N rows x 5 mixed columns, no paging/virtualization ----

[MemoryDiagnoser]
public class RenderBench
{
    [Params(50, 200, 1000)] public int N;

    IServiceProvider services;
    List<Person> people;
    IQueryable<Person> queryable;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);
        queryable = people.AsQueryable();
    }

    static readonly RenderFragment QuickGridColumns = builder =>
    {
        int s = 0;
        builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(p => p.Id));
        builder.AddAttribute(s++, "Title", "Id");
        builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, string>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(p => p.Name));
        builder.AddAttribute(s++, "Title", "Name");
        builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, int>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(p => p.Age));
        builder.AddAttribute(s++, "Title", "Age");
        builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, DateTime>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, DateTime>>)(p => p.Hired));
        builder.AddAttribute(s++, "Title", "Hired");
        builder.CloseComponent();
        builder.OpenComponent<QG.PropertyColumn<Person, decimal>>(s++);
        builder.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(p => p.Salary));
        builder.AddAttribute(s++, "Title", "Salary");
        builder.CloseComponent();
    };

    static readonly (string prop, string title)[] Cols =
        { ("Id", "Id"), ("Name", "Name"), ("Age", "Age"), ("Hired", "Hired"), ("Salary", "Salary") };

    static readonly RenderFragment RadzenColumns = builder =>
    {
        int s = 0;
        foreach (var (prop, title) in Cols)
        {
            builder.OpenComponent<RadzenDataGridColumn<Person>>(s++);
            builder.AddAttribute(s++, "Property", prop);
            builder.AddAttribute(s++, "Title", title);
            builder.CloseComponent();
        }
    };

    [Benchmark(Baseline = true)]
    public async Task Radzen()
    {
        using var r = new BenchmarkRenderer(services);
        var pv = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Data"] = people,
            ["Columns"] = RadzenColumns,
        });
        await r.RenderComponent(typeof(RadzenDataGrid<Person>), pv);
    }

    [Benchmark]
    public async Task QuickGrid()
    {
        using var r = new BenchmarkRenderer(services);
        var pv = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Items"] = queryable,
            ["ChildContent"] = QuickGridColumns,
        });
        await r.RenderComponent(typeof(QG.QuickGrid<Person>), pv);
    }
}

// ---- In-memory data pipeline: strongly-typed (QuickGrid style) vs dynamic-LINQ string (Radzen style) ----

[MemoryDiagnoser]
public class PipelineBench
{
    [Params(10000)] public int N;
    IQueryable<Person> q;

    [GlobalSetup] public void Setup() => q = Person.Make(N).AsQueryable();

    // QuickGrid composes a strongly-typed Expression<Func<T,TProp>> ordering.
    [Benchmark(Baseline = true)]
    public List<Person> StronglyTyped_OrderSkipTake() =>
        q.OrderBy(p => p.Name).Skip(100).Take(20).ToList();

    // Radzen's IQueryable.OrderBy(string) parses a dynamic-LINQ selector into an expression tree.
    [Benchmark]
    public List<Person> DynamicString_OrderSkipTake() =>
        ((IQueryable<Person>)q.OrderBy("Name asc")).Skip(100).Take(20).ToList();
}

// ---- EF / SQLite: async (QuickGrid EF adapter) vs sync (Radzen direct IQueryable binding) ----

public class PeopleContext : DbContext
{
    public PeopleContext(DbContextOptions options) : base(options) { }
    public DbSet<Person> People { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Seed with explicit Id values instead of letting EF treat the int key as store-generated.
        modelBuilder.Entity<Person>().Property(p => p.Id).ValueGeneratedNever();
    }
}

[MemoryDiagnoser]
public class EfBench
{
    [Params(10000)] public int N;

    SqliteConnection connection;
    DbContextOptions options;

    [GlobalSetup]
    public void Setup()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder().UseSqlite(connection).Options;
        using var db = new PeopleContext(options);
        db.Database.EnsureCreated();
        if (!db.People.Any())
        {
            db.People.AddRange(Person.Make(N));
            db.SaveChanges();
        }
    }

    [GlobalCleanup] public void Cleanup() => connection.Dispose();

    // What QuickGrid's EntityFramework adapter does per page: async count + async page, SQL-translated.
    [Benchmark(Baseline = true)]
    public async Task<int> Ef_Async_Page()
    {
        using var db = new PeopleContext(options);
        var total = await db.People.CountAsync();
        var page = await db.People.OrderBy(p => p.Name).Skip(100).Take(20).ToListAsync();
        return total + page.Count;
    }

    // What binding an EF IQueryable straight to RadzenDataGrid.Data does: sync count + sync page
    // via the dynamic-LINQ string OrderBy (blocks the thread; EF Core discourages sync-over-async).
    [Benchmark]
    public int Ef_Sync_Page()
    {
        using var db = new PeopleContext(options);
        var total = db.People.Count();
        var page = ((IQueryable<Person>)db.People.OrderBy("Name asc")).Skip(100).Take(20).ToList();
        return total + page.Count;
    }
}

public class Program
{
    public static async Task Main(string[] a)
    {
        if (a.Length > 0 && a[0] == "visual")
        {
            VisualDump.Run(a.Length > 1 ? a[1] : "visual-out");
            return;
        }

        if (a.Length > 0 && a[0] == "probe")
        {
            foreach (var n in new[] { 200, 1000 }) await Probe.Run(n);
            return;
        }

        if (a.Length > 0 && a[0] == "dropdown-probe")
        {
            await DropDownProbe.Run(1000);
            return;
        }

        // §11's measurement debt: whether the reference row's 990 KB step is the frame array's pooled
        // rental, asked of ArrayPool's own EventSource rather than of a GC correlation.
        if (a.Length > 0 && a[0] == "pool-probe")
        {
            await PoolProbe.Run(
                a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1000,
                a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 12);
            return;
        }
        // §26's second step, localised: the same ladder over RadzenFastGrid rather than RadzenDataGrid.
        // At 1000 rows the fast grid renders in ~155 KB, so a 48 B/row step would be a third of its
        // total rather than 0.4% of it - which makes its absence as readable as its presence.
        if (a.Length > 0 && a[0] == "fast-ladder")
        {
            await FastLadder.Run(
                a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 1000,
                a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 240);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(a);
    }
}
