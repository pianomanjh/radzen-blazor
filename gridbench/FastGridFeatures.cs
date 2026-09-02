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
using Radzen.Blazor;
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

    // The same five columns with the first two pinned to the left edge. Widths are required to pin
    // anything, so this row carries them and is read against the sized baseline rather than the bare one.
    static readonly RenderFragment FrozenColumnSet = b =>
    {
        var s = 0;

        void Column<TProp>(Expression<Func<Person, TProp>> property, string title, string width, bool frozen)
        {
            b.OpenComponent<PropertyColumn<Person, TProp>>(s++);
            b.AddAttribute(s++, "Property", property);
            b.AddAttribute(s++, "Title", title);
            b.AddAttribute(s++, "Width", width);

            if (frozen)
            {
                b.AddAttribute(s++, "Frozen", true);
            }

            b.CloseComponent();
        }

        Column<int>(x => x.Id, "Id", "80px", true);
        Column<string>(x => x.Name, "Name", "220px", true);
        Column<int>(x => x.Age, "Age", "60px", false);
        Column<DateTime>(x => x.Hired, "Hired", "140px", false);
        Column<decimal>(x => x.Salary, "Salary", "120px", false);
    };

    static readonly RenderFragment ReferenceFrozenColumnSet = b =>
    {
        var s = 0;

        void Column(string property, string title, string width, bool frozen)
        {
            b.OpenComponent<RadzenDataGridColumn<Person>>(s++);
            b.AddAttribute(s++, "Property", property);
            b.AddAttribute(s++, "Title", title);
            b.AddAttribute(s++, "Width", width);

            if (frozen)
            {
                b.AddAttribute(s++, "Frozen", true);
            }

            b.CloseComponent();
        }

        Column("Id", "Id", "80px", true);
        Column("Name", "Name", "220px", true);
        Column("Age", "Age", "60px", false);
        Column("Hired", "Hired", "140px", false);
        Column("Salary", "Salary", "120px", false);
    };

    static readonly RenderFragment Plain = Columns(geometry: false);
    static readonly RenderFragment Sized = Columns(geometry: true);

    // The same five columns with a filter value declared on one of them, so the grid actually filters
    // rather than only drawing somewhere to type.
    static readonly RenderFragment FilteredColumns = b =>
    {
        var s = 0;

        void Column<TProp>(Expression<Func<Person, TProp>> property, string title, object filterValue)
        {
            b.OpenComponent<PropertyColumn<Person, TProp>>(s++);
            b.AddAttribute(s++, "Property", property);
            b.AddAttribute(s++, "Title", title);

            if (filterValue is not null)
            {
                b.AddAttribute(s++, "FilterValue", filterValue);
            }

            b.CloseComponent();
        }

        Column<int>(x => x.Id, "Id", null);
        Column<string>(x => x.Name, "Name", "5");
        Column<int>(x => x.Age, "Age", null);
        Column<DateTime>(x => x.Hired, "Hired", null);
        Column<decimal>(x => x.Salary, "Salary", null);
    };

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

    // The two reference points, in the same table as the features rather than in a document beside it.
    // A feature's marginal cost says what it cost; only these say whether the grid is still worth using
    // once it is paid - and for row detail the answer differs depending on which one you ask.
    [Benchmark(Description = "= RadzenDataGrid, same columns")]
    public async Task ReferenceDataGrid()
    {
        using var r = new BenchmarkRenderer(services);

        await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["Columns"] = SlimBench.RadzenColumnsForComparison }));
    }

    // One reference row per feature this grid charges for, so a commit can say what the same thing
    // costs the grid it is measured against rather than only what it cost here. Nothing is added for
    // the features that measured free on this grid: RadzenDataGrid pays for those in its baseline
    // whether they are used or not, which is the premise the whole component rests on, and a marginal
    // cost of zero on both sides says nothing about it.
    async Task Reference(Action<Dictionary<string, object?>> configure)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Data"] = people,
            ["Columns"] = SlimBench.RadzenColumnsForComparison,
        };

        configure?.Invoke(parameters);

        using var r = new BenchmarkRenderer(services);

        await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(parameters));
    }

    [Benchmark(Description = "= RadzenDataGrid + row click")]
    public Task ReferenceRowClick() => Reference(p =>
        p["RowClick"] = EventCallback.Factory.Create<DataGridRowMouseEventArgs<Person>>(new object(), _ => { }));

    [Benchmark(Description = "= RadzenDataGrid + cell click")]
    public Task ReferenceCellClick() => Reference(p =>
        p["CellClick"] = EventCallback.Factory.Create<DataGridCellMouseEventArgs<Person>>(new object(), _ => { }));

    // Backwards, and deliberately: RadzenDataGrid's ShowCellDataAsTooltip defaults to *true*, so its
    // baseline row above is already the tooltip-on measurement and setting the parameter true measures
    // nothing at all - which is what the first version of this row did. Turning it off is the only way
    // to learn what it costs that grid, and it is the row that lines up with this grid's bare baseline.
    [Benchmark(Description = "= RadzenDataGrid, cell tooltip turned off")]
    public Task ReferenceNoTooltip() => Reference(p => p["ShowCellDataAsTooltip"] = false);

    [Benchmark(Description = "= RadzenDataGrid + row class")]
    public Task ReferenceRowClass() => Reference(p =>
        p["RowRender"] = (Action<RowRenderEventArgs<Person>>)(args => args.Attributes["class"] = "senior"));

    [Benchmark(Description = "= RadzenDataGrid + responsive titles")]
    public Task ReferenceResponsive() => Reference(p => p["Responsive"] = true);

    [Benchmark(Description = "= RadzenDataGrid + row detail")]
    public async Task ReferenceDataGridRowDetail()
    {
        using var r = new BenchmarkRenderer(services);

        await r.RenderComponent(typeof(RadzenDataGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                ["Data"] = people,
                ["Columns"] = SlimBench.RadzenColumnsForComparison,
                ["Template"] = Detail,
            }));
    }

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

    // Two rows, because row detail has an availability cost and a use cost and they are nothing alike:
    // declaring the Template is what turns the per-row toggle on, whether or not a row is expanded.
    static readonly RenderFragment<Person> Detail =
        person => b => b.AddContent(0, person.Name);

    [Benchmark(Description = "+ row detail available, none expanded")]
    public Task RowDetail() => Render(p => p["Template"] = Detail);

    [Benchmark(Description = "+ row detail, no toggle column")]
    public Task RowDetailNoToggle() => Render(p =>
    {
        p["Template"] = Detail;
        p["ShowExpandColumn"] = false;
    });

    [Benchmark(Description = "+ ItemKey")]
    public Task ItemKeyed() => Render(p => p["ItemKey"] = (Func<Person, object>)(x => x.Id));

    [Benchmark(Description = "+ settings raised on every reload")]
    public Task Settings() => Render(p =>
        p["SettingsChanged"] = EventCallback.Factory.Create<FastGridSettings>(new object(), _ => { }));

    // Three rows for the filter row, because the question the debounce raises is whether binding a
    // second event to every filter box is per column or per row. A filter box exists once per column
    // and nowhere else, so the two filtering rows should be indistinguishable at 1000 rows - and if
    // they are not, the handler has leaked into the body.
    [Benchmark(Description = "+ a filter row")]
    public Task Filtering() => Render(p => p["AllowFiltering"] = true);

    [Benchmark(Description = "+ a filter row, not as you type")]
    public Task FilteringOnChangeOnly() => Render(p =>
    {
        p["AllowFiltering"] = true;
        p["FilterAsYouType"] = false;
    });

    [Benchmark(Description = "= RadzenDataGrid + a filter row")]
    public Task ReferenceFiltering() => Reference(p => p["AllowFiltering"] = true);

    // The picker is one drop-down above the table. The question is whether it stays that way at a
    // thousand rows, or whether anything about it turns out to be per row.
    [Benchmark(Description = "+ column resize")]
    public Task ColumnResize() => Render(p => p["AllowColumnResize"] = true);

    [Benchmark(Description = "= RadzenDataGrid + column resize")]
    public Task ReferenceColumnResize() => Reference(p => p["AllowColumnResize"] = true);

    // Reorder is resize's sibling: a handle and a pair of callbacks per header, never per row. These
    // two rows are what says so - if either drifts towards the row-click rows, something has leaked
    // into the body.
    [Benchmark(Description = "+ column reorder")]
    public Task ColumnReorder() => Render(p => p["AllowColumnReorder"] = true);

    [Benchmark(Description = "= RadzenDataGrid + column reorder")]
    public Task ReferenceColumnReorder() => Reference(p => p["AllowColumnReorder"] = true);

    [Benchmark(Description = "+ column resize and reorder")]
    public Task ColumnResizeAndReorder() => Render(p =>
    {
        p["AllowColumnResize"] = true;
        p["AllowColumnReorder"] = true;
    });

    // Frozen columns are the first feature that puts a class on every cell of a column, so this row
    // is the one that says whether that stayed per column. The inset is composed once and handed to
    // every row, so what a frozen column costs is an attribute frame per cell and nothing else.
    [Benchmark(Description = "+ two frozen columns")]
    public Task FrozenColumns() => Render(p => p["ChildContent"] = FrozenColumnSet);

    [Benchmark(Description = "= RadzenDataGrid + two frozen columns")]
    public Task ReferenceFrozenColumns() => Reference(p => p["Columns"] = ReferenceFrozenColumnSet);

    [Benchmark(Description = "+ a column picker")]
    public Task ColumnPicking() => Render(p => p["AllowColumnPicking"] = true);

    [Benchmark(Description = "= RadzenDataGrid + a column picker")]
    public Task ReferenceColumnPicking() => Reference(p => p["AllowColumnPicking"] = true);

    // The benchmarks above draw a filter row without filtering by it. These filter. The difference
    // matters more than it looks: over a plain List<T> the grid wraps the source in an EnumerableQuery,
    // which rewrites and recompiles the expression tree every time the result is enumerated.
    [Benchmark(Description = "+ a filter that actually filters")]
    public Task FilteringApplied() => Render(p =>
    {
        p["AllowFiltering"] = true;
        p["ChildContent"] = FilteredColumns;
    });

    // The same over an IQueryable rather than a List, which is the shape an EF-backed grid has.
    [Benchmark(Description = "+ a filter that actually filters, over a queryable")]
    public Task FilteringAppliedQueryable() => Render(p =>
    {
        p["Data"] = people.AsQueryable();
        p["AllowFiltering"] = true;
        p["ChildContent"] = FilteredColumns;
    });

    // The one hook on this component that runs per cell rather than per row or per column, so the
    // question these two answer together is what the seam itself costs before a handler does anything:
    // the no-op measures the arguments object and the null check, the writing one adds the dictionary
    // and the splat. A grid that never sets it is the row above, and pays neither.
    [Benchmark(Description = "+ CellRender that adds nothing")]
    public Task CellRenderNoOp() => Render(p =>
        p["CellRender"] = (Action<FastGridCellRenderEventArgs<Person>>)(_ => { }));

    [Benchmark(Description = "+ CellRender that writes one attribute")]
    public Task CellRenderWriting() => Render(p =>
        p["CellRender"] = (Action<FastGridCellRenderEventArgs<Person>>)(args =>
            args.Attributes["data-row"] = "x"));

    // Per column rather than per cell, which is the claim worth checking at a thousand rows.
    [Benchmark(Description = "+ HeaderCellRender that writes one attribute")]
    public Task HeaderCellRenderWriting() => Render(p =>
        p["HeaderCellRender"] = (Action<FastGridCellRenderEventArgs<Person>>)(args =>
            args.Attributes["data-col"] = "x"));

    // What grouping costs the grid that has it, to size what building it here would have to beat.
    // Grouped by Age, which at 1000 rows is 45 groups of ~22 - a realistic shape rather than one
    // group of everything or a thousand groups of one.
    [Benchmark(Description = "= RadzenDataGrid + grouped by one column")]
    public Task ReferenceGrouping() => Reference(p =>
    {
        p["AllowGrouping"] = true;
        p["Groups"] = new System.Collections.ObjectModel.ObservableCollection<GroupDescriptor> { new() { Property = nameof(Person.Age) } };
    });
}
