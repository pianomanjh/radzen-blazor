using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Radzen.FastGrid;
using Xunit;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>A row type with one column per interesting shape: int, string, DateTime, decimal.</summary>
    public sealed class Person
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
                Name = "Person " + i.ToString(CultureInfo.InvariantCulture),
                Age = 20 + (i % 45),
                Hired = new DateTime(2010, 1, 1).AddDays(i),
                Salary = 40000m + (i % 1000) * 37m
            }).ToList();
    }

    /// <summary>
    /// Renders <see cref="RadzenDataGrid{TItem}" /> and <see cref="RadzenFastGrid{TItem}" /> over identical
    /// data, once, and hands both their parsed markup and their browser-rendered geometry to the tests.
    /// </summary>
    /// <remarks>
    /// The two layers exist because each catches faults the other cannot. Markup assertions catch a class
    /// that is missing or a class that lies; only geometry against the real stylesheet catches a structural
    /// coupling - the theme hangs the header padding off <c>th &gt; div</c>, so losing that wrapper leaves
    /// every class name correct and the header row short. That fault shipped past a screenshot being looked
    /// at, which is why this fixture measures rather than depending on an eye.
    /// </remarks>
    public sealed class GridParityFixture : IDisposable
    {
        /// <summary>Rows and columns the recorded geometry baseline was taken at.</summary>
        public const int RowCount = 8;

        /// <summary>Column titles, in order. Both grids get exactly these.</summary>
        public static readonly string[] ColumnTitles = { "Id", "Name", "Age", "Hired", "Salary" };

        readonly Lazy<GeometryReport> geometry;
        string pageDirectory;

        public GridParityFixture()
        {
            RepositoryRoot = FindRepositoryRoot();
            ThemeStylesheet = Path.Combine(RepositoryRoot, "Radzen.Blazor", "wwwroot", "css", "standard-base.css");

            if (!File.Exists(ThemeStylesheet))
            {
                throw new FileNotFoundException(
                    "The parity check measures against the real theme stylesheet and cannot run without it.",
                    ThemeStylesheet);
            }

            var people = Person.Make(RowCount);

            using (var ctx = new Bunit.TestContext())
            {
                ctx.JSInterop.Mode = JSRuntimeMode.Loose;
                ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");

                DataGridMarkup = ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.Columns, DataGridColumns);
                }).Markup;

                FastGridMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.ChildContent, FastGridColumns);
                }).Markup;

                // The same two grids again with row detail on. Collapsed, deliberately: the question is
                // what the toggle cell does to an ordinary row, and an expanded row is a different one.
                DataGridDetailMarkup = ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.Columns, DataGridColumns);
                    p.Add(g => g.Template, Detail);
                }).Markup;

                FastGridDetailMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridColumns);
                    p.Add(g => g.Template, Detail);
                }).Markup;
            }

            var parser = new HtmlParser();
            DataGrid = new Grid("RadzenDataGrid", DataGridMarkup, parser.ParseDocument(Wrap(DataGridMarkup)));
            FastGrid = new Grid("RadzenFastGrid", FastGridMarkup, parser.ParseDocument(Wrap(FastGridMarkup)));

            geometry = new Lazy<GeometryReport>(Measure);
        }

        /// <summary>Absolute path to the repository root.</summary>
        public string RepositoryRoot { get; }

        /// <summary>Absolute path to the theme stylesheet the geometry is measured against.</summary>
        public string ThemeStylesheet { get; }

        public string DataGridMarkup { get; }

        public string FastGridMarkup { get; }

        public string DataGridDetailMarkup { get; }

        public string FastGridDetailMarkup { get; }

        /// <summary>Names of the two panes rendered with row detail.</summary>
        public const string DataGridDetail = "RadzenDataGrid detail";

        public const string FastGridDetail = "RadzenFastGrid detail";

        static readonly RenderFragment<Person> Detail =
            person => builder => builder.AddContent(0, person.Name);

        /// <summary>The reference grid: whatever it does is, by definition, the target.</summary>
        public Grid DataGrid { get; }

        /// <summary>The grid under test.</summary>
        public Grid FastGrid { get; }

        /// <summary>Rendered geometry for both grids, measured once through Chromium.</summary>
        public GeometryReport Geometry => geometry.Value;

        static string Wrap(string markup) =>
            "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" + markup + "</body></html>";

        static readonly RenderFragment DataGridColumns = builder =>
        {
            var s = 0;

            foreach (var title in ColumnTitles)
            {
                builder.OpenComponent<RadzenDataGridColumn<Person>>(s++);
                builder.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Property), title);
                builder.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Title), title);
                builder.CloseComponent();
            }
        };

        static readonly RenderFragment FastGridColumns = builder =>
        {
            var s = 0;

            Column<int>(builder, ref s, x => x.Id, "Id");
            Column<string>(builder, ref s, x => x.Name, "Name");
            Column<int>(builder, ref s, x => x.Age, "Age");
            Column<DateTime>(builder, ref s, x => x.Hired, "Hired");
            Column<decimal>(builder, ref s, x => x.Salary, "Salary");
        };

        static void Column<TProp>(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder,
            ref int sequence, Expression<Func<Person, TProp>> property, string title)
        {
            builder.OpenComponent<PropertyColumn<Person, TProp>>(sequence++);
            builder.AddAttribute(sequence++, "Property", property);
            builder.AddAttribute(sequence++, "Title", title);
            builder.CloseComponent();
        }

        static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Radzen.Blazor", "wwwroot", "css", "standard-base.css")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find the repository root above {AppContext.BaseDirectory}.");
        }

        /// <summary>
        /// Writes both grids into one page that links the real theme stylesheet, then reads the rendered
        /// box heights back out of Chromium.
        /// </summary>
        GeometryReport Measure()
        {
            pageDirectory = Path.Combine(Path.GetTempPath(), "fastgrid-parity-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(pageDirectory);

            var pagePath = Path.Combine(pageDirectory, "compare.html");

            // The stylesheet is linked where it actually lives so its relative font URLs still resolve;
            // a copied-out stylesheet would silently fall back to system fonts and change the metrics.
            var page = $@"<!doctype html>
<html><head><meta charset=""utf-8"">
<link rel=""stylesheet"" href=""{new Uri(ThemeStylesheet).AbsoluteUri}"">
<style>
  body {{ margin: 0; padding: 24px; background: #fff; }}
  .pane {{ margin-bottom: 40px; }}
</style>
</head><body>
<div class=""pane"" data-grid=""{DataGrid.Name}"">{DataGridMarkup}</div>
<div class=""pane"" data-grid=""{FastGrid.Name}"">{FastGridMarkup}</div>
<div class=""pane"" data-grid=""{DataGridDetail}"">{DataGridDetailMarkup}</div>
<div class=""pane"" data-grid=""{FastGridDetail}"">{FastGridDetailMarkup}</div>
</body></html>";

            File.WriteAllText(pagePath, page);

            return GeometryProbe.Run(pagePath);
        }

        public void Dispose()
        {
            if (pageDirectory is not null && Directory.Exists(pageDirectory))
            {
                try
                {
                    Directory.Delete(pageDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // A leftover temp directory is not worth failing the run over.
                }
            }
        }
    }

    /// <summary>One rendered grid: its name, its raw markup, and its parsed document.</summary>
    public sealed class Grid
    {
        readonly IHtmlDocument document;

        internal Grid(string name, string markup, IHtmlDocument document)
        {
            Name = name;
            Markup = markup;
            this.document = document;
        }

        public string Name { get; }

        public string Markup { get; }

        /// <summary>The grid's outermost element.</summary>
        public IElement Root => document.Body.FirstElementChild;

        public IElement QuerySelector(string selector) => document.Body.QuerySelector(selector);

        public IReadOnlyList<IElement> QuerySelectorAll(string selector) =>
            document.Body.QuerySelectorAll(selector).ToArray();
    }

    /// <summary>The xunit collection that shares one fixture - so one render and one browser launch.</summary>
    [CollectionDefinition(Name)]
    public sealed class GridParityCollection : ICollectionFixture<GridParityFixture>
    {
        public const string Name = "grid parity";
    }
}
