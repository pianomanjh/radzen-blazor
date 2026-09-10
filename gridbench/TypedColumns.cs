using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Radzen;

// Does a strongly-typed expression column actually render cheaper than Radzen's string-property
// getter, or does it just move the allocation from a box to a string? Three shapes, identical output.

public abstract class TypedColumnBase<TItem>
{
    public string Title { get; init; }
    public abstract void RenderCell(RenderTreeBuilder b, int seq, TItem item);
}

// (a) Radzen's shape: string property name -> Func<TItem, object>. Value types box on the way out.
public sealed class ObjectGetterColumn<TItem> : TypedColumnBase<TItem>
{
    Func<TItem, object> getter;
    public string Property { init { getter = PropertyAccess.NullSafeGetter<TItem>(value); } }
    public override void RenderCell(RenderTreeBuilder b, int seq, TItem item) => b.AddContent(seq, getter(item));
}

// (b) QuickGrid's ergonomics, naive: Expression<Func<TItem,TProp>> -> Func<TItem,TProp>, handed to
// AddContent. There is no generic AddContent<T>, so it still binds the object overload and still boxes.
public sealed class TypedValueColumn<TItem, TProp> : TypedColumnBase<TItem>
{
    readonly Func<TItem, TProp> getter;
    public TypedValueColumn(Expression<Func<TItem, TProp>> property) => getter = property.Compile();
    public override void RenderCell(RenderTreeBuilder b, int seq, TItem item) => b.AddContent(seq, getter(item));
}

// (c) QuickGrid's actual shape: compile the expression once into a Func<TItem,string> and pass the
// string overload, so no box - but a string is allocated for anything that is not already a string.
public sealed class TypedTextColumn<TItem, TProp> : TypedColumnBase<TItem>
{
    readonly Func<TItem, string> text;
    public TypedTextColumn(Expression<Func<TItem, TProp>> property, string format = null)
    {
        var compiled = property.Compile();
        text = format is null
            ? item => compiled(item)?.ToString()
            : item => string.Format("{0:" + format + "}", compiled(item));
    }
    public override void RenderCell(RenderTreeBuilder b, int seq, TItem item) => b.AddContent(seq, text(item));
}

public sealed class TypedGrid<TItem> : ComponentBase
{
    [Parameter] public IEnumerable<TItem> Data { get; set; }
    [Parameter] public IReadOnlyList<TypedColumnBase<TItem>> Columns { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, "table");
        b.OpenElement(1, "tbody");
        foreach (var item in Data)
        {
            b.OpenElement(2, "tr");
            b.AddAttribute(3, "class", "rz-data-row");
            for (var j = 0; j < Columns.Count; j++)
            {
                b.OpenElement(4, "td");
                b.AddAttribute(5, "class", "rz-cell-data");
                Columns[j].RenderCell(b, 6, item);
                b.CloseElement();
            }
            b.CloseElement();
        }
        b.CloseElement();
        b.CloseElement();
    }
}

[MemoryDiagnoser]
public class TypedColumnBench
{
    [Params(1000)] public int N;

    IServiceProvider services;
    List<Person> people;
    TypedColumnBase<Person>[] objectCols, typedValueCols, typedTextCols;

    [GlobalSetup]
    public void Setup()
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IJSRuntime, NoopJSRuntime>();
        services = sc.BuildServiceProvider();
        people = Person.Make(N);

        objectCols = new TypedColumnBase<Person>[]
        {
            new ObjectGetterColumn<Person> { Property = "Id", Title = "Id" },
            new ObjectGetterColumn<Person> { Property = "Name", Title = "Name" },
            new ObjectGetterColumn<Person> { Property = "Age", Title = "Age" },
            new ObjectGetterColumn<Person> { Property = "Hired", Title = "Hired" },
            new ObjectGetterColumn<Person> { Property = "Salary", Title = "Salary" },
        };

        typedValueCols = new TypedColumnBase<Person>[]
        {
            new TypedValueColumn<Person, int>(p => p.Id) { Title = "Id" },
            new TypedValueColumn<Person, string>(p => p.Name) { Title = "Name" },
            new TypedValueColumn<Person, int>(p => p.Age) { Title = "Age" },
            new TypedValueColumn<Person, DateTime>(p => p.Hired) { Title = "Hired" },
            new TypedValueColumn<Person, decimal>(p => p.Salary) { Title = "Salary" },
        };

        typedTextCols = new TypedColumnBase<Person>[]
        {
            new TypedTextColumn<Person, int>(p => p.Id) { Title = "Id" },
            new TypedTextColumn<Person, string>(p => p.Name) { Title = "Name" },
            new TypedTextColumn<Person, int>(p => p.Age) { Title = "Age" },
            new TypedTextColumn<Person, DateTime>(p => p.Hired) { Title = "Hired" },
            new TypedTextColumn<Person, decimal>(p => p.Salary) { Title = "Salary" },
        };
    }

    async Task Render(TypedColumnBase<Person>[] cols)
    {
        using var r = new BenchmarkRenderer(services);
        await r.RenderComponent(typeof(TypedGrid<Person>), ParameterView.FromDictionary(
            new Dictionary<string, object?> { ["Data"] = people, ["Columns"] = cols }));
    }

    [Benchmark(Baseline = true, Description = "a) string Property -> Func<T,object>")]
    public Task ObjectGetter() => Render(objectCols);

    [Benchmark(Description = "b) Expression -> Func<T,TProp>, AddContent(value)")]
    public Task TypedValue() => Render(typedValueCols);

    [Benchmark(Description = "c) Expression -> Func<T,string>, AddContent(string)")]
    public Task TypedText() => Render(typedTextCols);
}
