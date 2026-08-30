using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen;
using Radzen.FastGrid;

// What each feature added to RadzenFastGrid actually costs, measured on the shipped component rather
// than on the prototype SlimFeatureBench weighed. Same rows and cells rendered every time: the only
// difference between a run and the baseline is the one parameter it sets.
[MemoryDiagnoser]
public class FastGridFeatureBench
{
    [Params(1000)] public int N;

    IServiceProvider services;
    List<Person> people;
    HashSet<Person> selection;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);

        // Every fourth row, so the selected and unselected branches are both walked.
        selection = new HashSet<Person>();
        for (var i = 0; i < people.Count; i += 4)
        {
            selection.Add(people[i]);
        }
    }

    // Five columns, as everywhere else in this harness, with the geometry parameters attached to the
    // ones that would carry them in practice: a width on each, and alignment on the numeric columns.
    static RenderFragment Columns(bool geometry) => b =>
    {
        var s = 0;

        void Column<TProp>(Expression<Func<Person, TProp>> property, string title, string width, TextAlign align)
        {
            b.OpenComponent<PropertyColumn<Person, TProp>>(s++);
            b.AddAttribute(s++, "Property", property);
            b.AddAttribute(s++, "Title", title);

            if (geometry)
            {
                b.AddAttribute(s++, "Width", width);

                if (align != TextAlign.Left)
                {
                    b.AddAttribute(s++, "TextAlign", align);
                }
            }

            b.CloseComponent();
        }

        Column<int>(x => x.Id, "Id", "80px", TextAlign.Right);
        Column<string>(x => x.Name, "Name", "220px", TextAlign.Left);
        Column<int>(x => x.Age, "Age", "60px", TextAlign.Right);
        Column<DateTime>(x => x.Hired, "Hired", "140px", TextAlign.Left);
        Column<decimal>(x => x.Salary, "Salary", "120px", TextAlign.Right);
    };

    static readonly RenderFragment Plain = Columns(geometry: false);
    static readonly RenderFragment Sized = Columns(geometry: true);

    async Task Render(Action<Dictionary<string, object?>> configure, RenderFragment columns = null)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Data"] = people,
            ["ChildContent"] = columns ?? Plain,
        };

        configure?.Invoke(parameters);

        using var r = new BenchmarkRenderer(services);

        await r.RenderComponent(typeof(RadzenFastGrid<Person>), ParameterView.FromDictionary(parameters));
    }

    [Benchmark(Baseline = true, Description = "bare")]
    public Task Bare() => Render(null);

    [Benchmark(Description = "+ widths and alignment")]
    public Task Geometry() => Render(null, Sized);

    [Benchmark(Description = "+ selection (1 in 4 rows)")]
    public Task Selection() => Render(p => p["Selection"] = selection);

    [Benchmark(Description = "+ row class")]
    public Task RowClass() => Render(p =>
        p["RowClass"] = (Func<Person, string?>)(person => person.Age > 40 ? "senior" : null));

    [Benchmark(Description = "+ row click")]
    public Task RowClick() => Render(p =>
        p["RowClick"] = EventCallback.Factory.Create<Person>(new object(), _ => { }));

    [Benchmark(Description = "+ responsive titles")]
    public Task Responsive() => Render(p => p["Responsive"] = true);

    [Benchmark(Description = "+ cell tooltip")]
    public Task Tooltip() => Render(p => p["ShowCellDataAsTooltip"] = true);

    [Benchmark(Description = "+ cell click")]
    public Task CellClick() => Render(p =>
        p["CellClick"] = EventCallback.Factory.Create<FastGridCellEventArgs<Person>>(new object(), _ => { }));

    // Five columns, each with a header and a footer template: per column, so the row count should not
    // reach it. That is the claim; this is what tests it.
    static RenderFragment Templated(bool footerAggregate, List<Person> people) => b =>
    {
        var s = 0;

        void Column<TProp>(Expression<Func<Person, TProp>> property, string title)
        {
            b.OpenComponent<PropertyColumn<Person, TProp>>(s++);
            b.AddAttribute(s++, "Property", property);
            b.AddAttribute(s++, "Title", title);
            b.AddAttribute(s++, "HeaderTemplate",
                (RenderFragment<ColumnBase<Person>>)(column => inner => inner.AddContent(0, title)));
            b.AddAttribute(s++, "FooterTemplate", (RenderFragment<ColumnBase<Person>>)(column => inner =>
            {
                // The trap the README warns about, measured rather than only warned about: an aggregate
                // written in a footer template is a full scan of the data on every render.
                inner.AddContent(0, footerAggregate
                    ? Enumerable.Sum(people, x => x.Salary).ToString(CultureInfo.InvariantCulture)
                    : title);
            }));
            b.CloseComponent();
        }

        Column<int>(x => x.Id, "Id");
        Column<string>(x => x.Name, "Name");
        Column<int>(x => x.Age, "Age");
        Column<DateTime>(x => x.Hired, "Hired");
        Column<decimal>(x => x.Salary, "Salary");
    };

    RenderFragment templates;
    RenderFragment aggregates;

    [Benchmark(Description = "+ header and footer templates")]
    public Task Templates() => Render(null, templates ??= Templated(false, people));

    [Benchmark(Description = "+ footer templates that aggregate")]
    public Task FooterAggregate() => Render(null, aggregates ??= Templated(true, people));

    // Against the sorted-by-one row below, not against bare: bare is not sorted at all, so measuring
    // multi-column sorting against it charges the second sort for the cost of sorting at all.
    [Benchmark(Description = "+ sorted by one column")]
    public Task SingleSort() => Render(p => p["AllowSorting"] = true, Sorted(1));

    [Benchmark(Description = "+ sorted by two columns")]
    public Task MultiSort() => Render(p =>
    {
        p["AllowSorting"] = true;
        p["AllowMultiColumnSorting"] = true;
        p["ShowMultiColumnSortingIndex"] = true;
    }, Sorted(2));

    static RenderFragment one;
    static RenderFragment two;

    static RenderFragment Sorted(int count) =>
        count == 1 ? one ??= SortedBy(1) : two ??= SortedBy(2);

    // Declared rather than clicked, so the sort is in place for the render being measured.
    static RenderFragment SortedBy(int count) => b =>
    {
        var s = 0;

        b.OpenComponent<PropertyColumn<Person, int>>(s++);
        b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Id));
        b.AddAttribute(s++, "Title", "Id");
        b.CloseComponent();
        b.OpenComponent<PropertyColumn<Person, string>>(s++);
        b.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(x => x.Name));
        b.AddAttribute(s++, "Title", "Name");
        b.CloseComponent();
        b.OpenComponent<PropertyColumn<Person, int>>(s++);
        b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Age));
        b.AddAttribute(s++, "Title", "Age");
        b.AddAttribute(s++, "SortOrder", SortOrder.Ascending);
        b.CloseComponent();

        // Age repeats every 45 rows, so the second sort has real work to do rather than tie-breaking
        // a key that is already unique.
        b.OpenComponent<PropertyColumn<Person, DateTime>>(s++);
        b.AddAttribute(s++, "Property", (Expression<Func<Person, DateTime>>)(x => x.Hired));
        b.AddAttribute(s++, "Title", "Hired");
        b.CloseComponent();
        b.OpenComponent<PropertyColumn<Person, decimal>>(s++);
        b.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(x => x.Salary));
        b.AddAttribute(s++, "Title", "Salary");

        if (count > 1)
        {
            b.AddAttribute(s++, "SortOrder", SortOrder.Descending);
        }

        b.CloseComponent();
    };

    [Benchmark(Description = "+ settings raised on every reload")]
    public Task Settings() => Render(p =>
        p["SettingsChanged"] = EventCallback.Factory.Create<FastGridSettings>(new object(), _ => { }));
}
