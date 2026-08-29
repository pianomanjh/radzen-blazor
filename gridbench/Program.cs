using System;
using System.Collections.Generic;
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
    public ValueTask<T> InvokeAsync<T>(string identifier, object[] args) => new(default(T));
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object[] args) => new(default(T));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(a);
    }
}
