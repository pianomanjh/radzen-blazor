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

        /// <summary>Rows in the auto-fit pane, which is the size §13's gate for the pass is written at.</summary>
        public const int AutoFitRowCount = 1000;

        /// <summary>Column titles, in order. Both grids get exactly these.</summary>
        public static readonly string[] ColumnTitles = { "Id", "Name", "Age", "Hired", "Salary" };

        readonly Lazy<GeometryReport> geometry;
        string pageDirectory;

        public GridParityFixture()
        {
            RepositoryRoot = FindRepositoryRoot();
            ThemeStylesheet = Path.Combine(RepositoryRoot, "Radzen.Blazor", "wwwroot", "css", "standard-base.css");
            PackageStylesheet = Path.Combine(RepositoryRoot, "Radzen.Blazor.FastGrid", "wwwroot", "fastgrid.css");

            foreach (var stylesheet in new[] { ThemeStylesheet, PackageStylesheet })
            {
                if (!File.Exists(stylesheet))
                {
                    throw new FileNotFoundException(
                        "The parity check measures against the real stylesheets and cannot run without them.",
                        stylesheet);
                }
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
                    p.Add(g => g.Columns, DataGridDetailColumns);
                    p.Add(g => g.Template, Detail);
                }).Markup;

                FastGridDetailMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridDetailColumns);
                    p.Add(g => g.Template, Detail);
                }).Markup;

                // And again with the first row selected. The theme nests its selected-row rule inside
                // .rz-selectable, so a grid can carry rz-state-highlight on the right tr and still paint
                // nothing - which is what happened, and what no markup assertion could see.
                var selected = new[] { people[0] };

                DataGridSelectedMarkup = ctx.RenderComponent<RadzenDataGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.Columns, DataGridColumns);
                    p.Add(g => g.Value, selected);
                    p.Add(g => g.SelectionMode, DataGridSelectionMode.Single);
                    p.Add(g => g.RowSelect, EventCallback.Factory.Create<Person>(new object(), _ => { }));
                }).Markup;

                FastGridSelectedMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridColumns);
                    p.Add(g => g.Selection, selected);
                    p.Add(g => g.SelectionMode, DataGridSelectionMode.Single);
                    p.Add(g => g.SelectionChanged,
                        EventCallback.Factory.Create<ICollection<Person>>(new object(), _ => { }));
                }).Markup;

                // And once more with the first two columns frozen. The theme makes a frozen cell sticky
                // but supplies no inset, so this pane is where "does it actually hold still" is decided.
                // With a filter row and a footer, because the theme stacks the title row, the filter
                // row, the body and the footer differently - a frozen column can win in one section and
                // be painted over in the next, which is exactly what happened to the filter row.
                FastGridFrozenMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridFrozenColumns);
                    p.Add(g => g.AllowFiltering, true);
                }).Markup;

                // And with the keyboard cursor on the second row's second cell. No selection is wired,
                // deliberately: a read-only grid is the only configuration this component promises, and
                // it is exactly the one Radzen's own theme draws no cursor for - the row rule is nested
                // inside .rz-selectable and there is no cell rule at all. This pane is what says whether
                // the package's interim stylesheet closes that.
                //
                // The classes are placed here the way the script places them at runtime, through the
                // grid's own RowClass and CellRender hooks. What is being checked is the paint.
                FastGridFocusMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridColumns);
                    p.Add(g => g.RowClass, Focused(people[1]));
                    p.Add(g => g.CellRender, FocusedCell(people[1], "Name"));
                }).Markup;

                // The same cursor on a frozen cell. A frozen cell paints its own background over the
                // row's, so the focus colour has to reach the pseudo-element the theme uses for the
                // seam - and if the cell loses its opaque background instead, the column scrolling
                // underneath shows through it.
                FastGridFrozenFocusMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    p.Add(g => g.Data, people);
                    p.Add(g => g.ChildContent, FastGridFrozenColumns);
                    p.Add(g => g.RowClass, Focused(people[1]));
                    p.Add(g => g.CellRender, FocusedCell(people[1], "Id"));
                }).Markup;

                // The pane the script is run against. Nothing here declares a width, which is both the
                // grid that wants fitting and - because a colgroup is otherwise only emitted when
                // something has a width - the grid that has nowhere to write one. AutoFitColumns is
                // what puts the colgroup and the table's id on the page.
                FastGridAutoFitMarkup = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
                {
                    // The one pane not rendered at RowCount. The fit's cost is a walk over every
                    // rendered cell, and §13 states its gate at a thousand rows - a number taken over
                    // eight would not be the number that gate is about.
                    p.Add(g => g.Data, Person.Make(AutoFitRowCount));
                    p.Add(g => g.ChildContent, FastGridAutoFitColumns);
                    p.Add(g => g.AutoFitColumns, AutoFitMode.OnDemand);
                    p.Add(g => g.AllowSorting, true);
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

        /// <summary>
        /// The package's own stylesheet, which carries the keyboard cursor until Radzen's theme draws
        /// one. Linked after the theme, which is the order an application links them in.
        /// </summary>
        public string PackageStylesheet { get; }

        public string DataGridMarkup { get; }

        public string FastGridMarkup { get; }

        public string DataGridDetailMarkup { get; }

        public string FastGridDetailMarkup { get; }

        public string DataGridSelectedMarkup { get; }

        public string FastGridSelectedMarkup { get; }

        public string FastGridFrozenMarkup { get; }

        public string FastGridFocusMarkup { get; }

        public string FastGridFrozenFocusMarkup { get; }

        public string FastGridAutoFitMarkup { get; }

        /// <summary>Names of the two panes rendered with row detail.</summary>
        public const string DataGridDetail = "RadzenDataGrid detail";

        public const string FastGridDetail = "RadzenFastGrid detail";

        /// <summary>Names of the two panes rendered with a row selected.</summary>
        public const string DataGridSelected = "RadzenDataGrid selected";

        public const string FastGridSelected = "RadzenFastGrid selected";

        /// <summary>The pane with its first two columns frozen to the left edge.</summary>
        public const string FastGridFrozen = "RadzenFastGrid frozen";

        /// <summary>The panes carrying a keyboard cursor, on an ordinary cell and on a frozen one.</summary>
        public const string FastGridFocus = "RadzenFastGrid focus";

        public const string FastGridFrozenFocus = "RadzenFastGrid frozen focus";

        /// <summary>The pane the auto-fit script is actually run against.</summary>
        public const string FastGridAutoFit = "RadzenFastGrid auto-fit";

        static Func<Person, string> Focused(Person row) =>
            person => ReferenceEquals(person, row) ? "rz-state-focused" : null;

        // Added to whatever the cell already carries, which is what the script does - it calls
        // classList.add. Replacing the class instead takes a frozen cell's rz-frozen-cell with it, and
        // the column silently stops being sticky while every other assertion about it still passes.
        static Action<FastGridCellRenderEventArgs<Person>> FocusedCell(Person row, string title) =>
            args =>
            {
                if (ReferenceEquals(args.Data, row) && args.Column.Title == title)
                {
                    args.Attributes["class"] = args.Column.CellElementClass is { } existing
                        ? existing + " rz-state-focused"
                        : "rz-state-focused";
                }
            };

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

        /// <summary>
        /// Declared widths, distinct so that a column drawn at its neighbour's width is visible. The
        /// detail panes use these: the toggle column is a cell with no column of its own, so it is the
        /// one place a colgroup can be misaligned, and every width there lands one column to the left.
        /// </summary>
        static readonly string[] ColumnWidths = { "90px", "180px", "120px", "150px", "140px" };

        static readonly RenderFragment DataGridDetailColumns = builder =>
        {
            var s = 0;

            for (var i = 0; i < ColumnTitles.Length; i++)
            {
                builder.OpenComponent<RadzenDataGridColumn<Person>>(s++);
                builder.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Property), ColumnTitles[i]);
                builder.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Title), ColumnTitles[i]);
                builder.AddAttribute(s++, nameof(RadzenDataGridColumn<Person>.Width), ColumnWidths[i]);
                builder.CloseComponent();
            }
        };

        static readonly RenderFragment FastGridFrozenColumns = builder =>
        {
            var s = 0;

            Column<int>(builder, ref s, x => x.Id, "Id", "90px", frozen: true, footer: true);
            Column<string>(builder, ref s, x => x.Name, "Name", "180px", frozen: true, footer: true);
            Column<int>(builder, ref s, x => x.Age, "Age", "400px", footer: true);
            Column<DateTime>(builder, ref s, x => x.Hired, "Hired", "400px", footer: true);
            Column<decimal>(builder, ref s, x => x.Salary, "Salary", "400px", footer: true);
        };

        static readonly RenderFragment FastGridDetailColumns = builder =>
        {
            var s = 0;

            Column<int>(builder, ref s, x => x.Id, "Id", ColumnWidths[0]);
            Column<string>(builder, ref s, x => x.Name, "Name", ColumnWidths[1]);
            Column<int>(builder, ref s, x => x.Age, "Age", ColumnWidths[2]);
            Column<DateTime>(builder, ref s, x => x.Hired, "Hired", ColumnWidths[3]);
            Column<decimal>(builder, ref s, x => x.Salary, "Salary", ColumnWidths[4]);
        };

        /// <summary>
        /// Columns chosen so that each half of the measurement has something only it can explain.
        /// Two columns hold the same values and differ only in their titles, so a width difference
        /// between them can only have come from the header - which is the half that needs a
        /// max-content flip to be measurable at all. Hired holds much the longest values, so a width
        /// difference from Id can only have come from the body. Name is clamped, so the one column
        /// allowed to stay truncated is the one that said it could be, and Age follows it only so that
        /// the clamped column is not the trailing one - which is left bare, and a bare column has no
        /// width for a clamp to apply to.
        /// </summary>
        static readonly RenderFragment FastGridAutoFitColumns = builder =>
        {
            var s = 0;

            Column<int>(builder, ref s, x => x.Id, "Id");
            Column<int>(builder, ref s, x => x.Id, "An extremely long column heading indeed");
            Column<DateTime>(builder, ref s, x => x.Hired, "Hired");
            Column<string>(builder, ref s, x => x.Name, "Name", maxWidth: "40px");

            // Last, and only so that the clamped column is not: the trailing column is the one left
            // bare, and a bare column has no width for a clamp to apply to.
            Column<int>(builder, ref s, x => x.Age, "Age");
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
            ref int sequence, Expression<Func<Person, TProp>> property, string title, string width = null,
            bool frozen = false, bool footer = false, string maxWidth = null)
        {
            builder.OpenComponent<PropertyColumn<Person, TProp>>(sequence++);
            builder.AddAttribute(sequence++, "Property", property);
            builder.AddAttribute(sequence++, "Title", title);

            if (maxWidth is not null)
            {
                builder.AddAttribute(sequence++, "MaxWidth", maxWidth);
            }

            if (width is not null)
            {
                builder.AddAttribute(sequence++, "Width", width);
            }

            if (frozen)
            {
                builder.AddAttribute(sequence++, "Frozen", true);
            }

            if (footer)
            {
                builder.AddAttribute(sequence++, "FooterTemplate",
                    (RenderFragment<ColumnBase<Person>>)(_ => b => b.AddContent(0, title)));
            }

            builder.CloseComponent();
        }

        /// <summary>
        /// The shipped <c>fastgrid.js</c>, made callable from a page with no module loader.
        /// </summary>
        /// <remarks>
        /// The file the package ships, not a copy of it: a check that measures a transcription proves
        /// only that the transcription is right. It is loaded off a file:// page, where an ES module
        /// import is refused, so the export keywords come off and the one function under test is hung
        /// on the window instead. Nothing else about the source is touched.
        /// </remarks>
        string AutoFitScript()
        {
            var source = File.ReadAllText(
                Path.Combine(RepositoryRoot, "Radzen.Blazor.FastGrid", "wwwroot", "fastgrid.js"));

            return System.Text.RegularExpressions.Regex.Replace(source, "(?m)^export ", "")
                + "\nwindow.__fastgrid = { autoFit };";
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
<link rel=""stylesheet"" href=""{new Uri(PackageStylesheet).AbsoluteUri}"">
<style>
  body {{ margin: 0; padding: 24px; background: #fff; }}
  .pane {{ margin-bottom: 40px; }}
  .pane-narrow {{ width: 500px; }}
  .pane-fit {{ width: 900px; }}
</style>
</head><body>
<div class=""pane"" data-grid=""{DataGrid.Name}"">{DataGridMarkup}</div>
<div class=""pane"" data-grid=""{FastGrid.Name}"">{FastGridMarkup}</div>
<div class=""pane"" data-grid=""{DataGridDetail}"">{DataGridDetailMarkup}</div>
<div class=""pane"" data-grid=""{FastGridDetail}"">{FastGridDetailMarkup}</div>
<div class=""pane"" data-grid=""{DataGridSelected}"">{DataGridSelectedMarkup}</div>
<div class=""pane"" data-grid=""{FastGridSelected}"">{FastGridSelectedMarkup}</div>
<div class=""pane pane-narrow"" data-grid=""{FastGridFrozen}"">{FastGridFrozenMarkup}</div>
<div class=""pane"" data-grid=""{FastGridFocus}"">{FastGridFocusMarkup}</div>
<div class=""pane pane-narrow"" data-grid=""{FastGridFrozenFocus}"">{FastGridFrozenFocusMarkup}</div>
<div class=""pane pane-fit"" data-grid=""{FastGridAutoFit}"" data-autofit=""1"">{FastGridAutoFitMarkup}</div>
<script>{AutoFitScript()}</script>
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
