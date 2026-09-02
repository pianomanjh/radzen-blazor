using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.FastGrid;

// Renders RadzenDataGrid and the slim prototype to real HTML and writes a side-by-side page that
// links the actual Radzen theme stylesheet, so the markup can be looked at rather than only measured.
// Allocation numbers say nothing about whether the thing renders correctly.
static class VisualDump
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

        var people = Person.Make(8);

        var radzen = ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
        {
            p.Add(g => g.Data, people);
            p.Add(g => g.AllowSorting, true);
            p.Add<RenderFragment>(g => g.Columns, b =>
            {
                var s = 0;
                foreach (var (prop, title) in new[] { ("Id", "Id"), ("Name", "Name"), ("Age", "Age"), ("Hired", "Hired"), ("Salary", "Salary") })
                {
                    b.OpenComponent<RadzenDataGridColumn<Person>>(s++);
                    b.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Property), prop);
                    b.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Title), title);
                    b.CloseComponent();
                }
            });
        });

        var slimCols = new[] { "Id", "Name", "Age", "Hired", "Salary" }
            .Select(p => new SlimColumn<Person> { Property = p, Title = p }).ToArray();

        var slim = ctx.RenderComponent<SlimGrid<Person>>(p =>
        {
            p.Add(g => g.Data, people);
            p.Add(g => g.Columns, slimCols);
        });

        var fast = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
        {
            p.Add(g => g.Data, people);
            p.Add(g => g.AllowSorting, true);
            p.Add(g => g.ChildContent, (RenderFragment)(b =>
            {
                var s = 0;
                b.OpenComponent<PropertyColumn<Person, int>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Id));
                b.AddAttribute(s++, "Title", "Id"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, string>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(x => x.Name));
                b.AddAttribute(s++, "Title", "Name"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, int>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Age));
                b.AddAttribute(s++, "Title", "Age"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, DateTime>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, DateTime>>)(x => x.Hired));
                b.AddAttribute(s++, "Title", "Hired"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, decimal>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(x => x.Salary));
                b.AddAttribute(s++, "Title", "Salary"); b.CloseComponent();
            }));
        });

        // Everything the component grew after the first visual pass: a filter row, a check-box list, a
        // pager. Allocation numbers say nothing about whether any of it renders correctly.
        var featured = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
        {
            p.Add(g => g.Data, Person.Make(40));
            p.Add(g => g.AllowSorting, true);
            p.Add(g => g.AllowFiltering, true);
            p.Add(g => g.AllowPaging, true);
            p.Add(g => g.PageSize, 5);
            p.Add(g => g.ShowPagingSummary, true);
            p.Add(g => g.PageSizeOptions, new[] { 5, 10, 20 });
            p.Add(g => g.AllowColumnResize, true);
            p.Add(g => g.ChildContent, (RenderFragment)(b =>
            {
                var s = 0;
                b.OpenComponent<PropertyColumn<Person, int>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Id));
                b.AddAttribute(s++, "Title", "Id"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, string>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, string>>)(x => x.Name));
                b.AddAttribute(s++, "Title", "Name"); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, int>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, int>>)(x => x.Age));
                b.AddAttribute(s++, "Title", "Age");
                b.AddAttribute(s++, "FilterMode", Radzen.FilterMode.CheckBoxList); b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, decimal>>(s++);
                b.AddAttribute(s++, "Property", (Expression<Func<Person, decimal>>)(x => x.Salary));
                b.AddAttribute(s++, "Format", "C");
                b.AddAttribute(s++, "Title", "Salary"); b.CloseComponent();
            }));
        });

        // Sorted, so the sort icon is in the dump. Both grids draw it only on the column actually
        // sorted, so an unsorted screenshot says nothing about whether it renders at all.
        featured.FindAll("thead th")[1].QuerySelector("div")!.Click();

        File.WriteAllText(Path.Combine(outDir, "featured.html"), featured.Markup);
        File.WriteAllText(Path.Combine(outDir, "fast.html"), fast.Markup);
        File.WriteAllText(Path.Combine(outDir, "radzen.html"), radzen.Markup);
        File.WriteAllText(Path.Combine(outDir, "slim.html"), slim.Markup);

        var page = $@"<!doctype html>
<html><head><meta charset=""utf-8"">
<link rel=""stylesheet"" href=""theme.css"">
<style>
  body {{ font-family: system-ui, sans-serif; margin: 0; padding: 24px; background: #fff; }}
  h2 {{ font: 600 13px/1.4 system-ui; text-transform: uppercase; letter-spacing: .08em; color: #666; margin: 24px 0 8px; }}
  .pane {{ margin-bottom: 40px; }}
</style>
</head><body>
<div class=""pane""><h2>RadzenDataGrid</h2>{radzen.Markup}</div>
<div class=""pane""><h2>SlimGrid prototype</h2>{slim.Markup}</div>
<div class=""pane""><h2>RadzenFastGrid</h2>{fast.Markup}</div>
<div class=""pane""><h2>RadzenFastGrid — sorting, filtering, check-box list, paging</h2>{featured.Markup}</div>
</body></html>";
        File.WriteAllText(Path.Combine(outDir, "compare.html"), page);
        Console.WriteLine($"wrote {outDir}/compare.html");
    }
}
