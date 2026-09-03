using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class FastGridLoadDataTests
    {
        readonly List<LoadDataArgs> calls = new();
        readonly List<Person> all = People.Many(30);

        /// <summary>
        /// Renders the grid inside a host that owns Data and Count, which is how a LoadData grid is
        /// actually used: the handler assigns the parent's state and the parent re-renders the grid.
        /// </summary>
        IRenderedComponent<LoadDataHost> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<LoadDataHost>>? extra = null,
            Func<IEnumerable<Person>, IEnumerable<Person>>? source = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<LoadDataHost>(p =>
            {
                p.Add(h => h.Columns, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, string>(x => x.Customer.Name)));
                p.Add(h => h.OnLoad, (args, host) =>
                {
                    calls.Add(args);

                    var rows = (source ?? (x => x)).Invoke(all);

                    // Serve the sequence itself when nothing is paged, so a source that is an
                    // ODataEnumerable reaches the grid as one rather than as a materialized list.
                    host.Serve(
                        args.Skip is null && args.Top is null
                            ? rows
                            : rows.Skip(args.Skip ?? 0).Take(args.Top ?? int.MaxValue).ToList(),
                        all.Count);
                });
                extra?.Invoke(p);
            });
        }

        static string[] FirstNames(IRenderedComponent<LoadDataHost> cut) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        static void ClickHeader(IRenderedComponent<LoadDataHost> cut, int index) =>
            cut.FindAll("thead th")[index].QuerySelector("div")!.Click();

        [Fact]
        public void IsInvokedOnceOnTheFirstRender()
        {
            using var ctx = new TestContext();

            Render(ctx);

            Assert.Single(calls);
        }

        [Fact]
        public void APageSizeSetFromOutsideAsksTheHandlerAgain()
        {
            // The handler owns the page, so a page size it was never told about leaves it serving ten
            // rows onto a page the grid and the pager both believe holds twenty-five.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 10);
            });

            Assert.Equal(10, calls[^1].Top);
            Assert.Equal(10, cut.FindAll("tbody tr").Count);

            cut.SetParametersAndRender(p => p.Add(h => h.PageSize, 25));

            Assert.Equal(25, calls[^1].Top);
            Assert.Equal(0, calls[^1].Skip);
            Assert.Equal(25, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void TheHandlerAssigningDataDoesNotReinvokeIt()
        {
            // The handler sets Data, which sets the grid's parameters again. Reloading on that would
            // never stop.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            cut.SetParametersAndRender(p => p.Add(h => h.ShowPagingSummary, true));

            Assert.Single(calls);
        }

        [Fact]
        public void CarriesSkipAndTopWhenPaging()
        {
            using var ctx = new TestContext();

            Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
            });

            Assert.Equal(0, calls[0].Skip);
            Assert.Equal(4, calls[0].Top);
        }

        [Fact]
        public void CarriesNoSkipOrTopWhenNotPaging()
        {
            // A handler that reads Top as a page size would silently serve one page to an unpaged grid.
            using var ctx = new TestContext();

            Render(ctx);

            Assert.Null(calls[0].Skip);
            Assert.Null(calls[0].Top);
        }

        [Fact]
        public void OrderByIsNullUntilSomethingIsSorted()
        {
            using var ctx = new TestContext();

            Render(ctx);

            Assert.Null(calls[0].OrderBy);
        }

        [Fact]
        public void SortingReloadsAndCarriesTheOrderBy()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowSorting, true));

            ClickHeader(cut, 0);

            Assert.Equal(2, calls.Count);
            Assert.Equal("First asc", calls[1].OrderBy);

            ClickHeader(cut, 0);

            Assert.Equal(3, calls.Count);
            Assert.Equal("First desc", calls[2].OrderBy);
        }

        [Fact]
        public void OrderByUsesTheDottedPathForANestedProperty()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowSorting, true));

            ClickHeader(cut, 1);

            Assert.Equal("Customer.Name asc", calls[1].OrderBy);
        }

        [Fact]
        public void OrderBySwitchesToSlashesForAnODataSource()
        {
            // $orderby=Customer/Name goes over the wire; a dot is not valid there.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowSorting, true),
                source: rows => new ODataEnumerable<Person>(rows));

            ClickHeader(cut, 1);

            Assert.Equal("Customer/Name asc", calls[1].OrderBy);

            ClickHeader(cut, 1);

            Assert.Equal("Customer/Name desc", calls[2].OrderBy);
        }

        [Fact]
        public void RendersWhatTheHandlerServedWithoutPagingItAgain()
        {
            // The handler already applied Skip and Top. Paging its result a second time would show the
            // first four of a four-row page and nothing at all beyond page one.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
            });

            Assert.Equal(new[] { "First1", "First2", "First3", "First4" }, FirstNames(cut));

            cut.Find(".rz-pager-next").Click();

            Assert.Equal(4, calls[^1].Skip);
            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));
        }

        [Fact]
        public void RendersWhatTheHandlerServedWithoutSortingItAgain()
        {
            // The handler owns the order. Re-sorting its page client-side would reorder four rows out of
            // thirty and call the result sorted.
            using var ctx = new TestContext();

            var cut = Render(ctx,
                p =>
                {
                    p.Add(h => h.AllowSorting, true);
                    p.Add(h => h.AllowPaging, true);
                    p.Add(h => h.PageSize, 4);
                },
                source: rows => rows.Reverse());

            ClickHeader(cut, 0);

            Assert.Equal(new[] { "First30", "First29", "First28", "First27" }, FirstNames(cut));
        }

        [Fact]
        public void TheCountParameterDrivesThePager()
        {
            // The grid sees one page, so only the handler knows the total.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
                p.Add(h => h.ShowPagingSummary, true);
            });

            var summary = cut.Find(".rz-pager-summary").TextContent;

            Assert.Contains("8", summary, StringComparison.Ordinal);
            Assert.Contains("30", summary, StringComparison.Ordinal);
        }

        static void TypeInFilter(IRenderedComponent<LoadDataHost> cut, int index, string text) =>
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[index].Change(text);

        [Fact]
        public void FilteringReloadsAndCarriesTheDescriptors()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowFiltering, true));

            TypeInFilter(cut, 0, "First1");

            var filter = Assert.Single(calls[^1].Filters!);

            Assert.Equal("First", filter.Property);
            Assert.Equal("First1", filter.FilterValue);
            Assert.Equal(FilterOperator.Contains, filter.FilterOperator);
        }

        [Fact]
        public void CarriesTheFilterAsALinqString()
        {
            // A handler that hands the string to a queryable needs the LINQ form, not the OData one.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowFiltering, true));

            TypeInFilter(cut, 0, "First1");

            Assert.Contains("First", calls[^1].Filter!, StringComparison.Ordinal);
            Assert.Contains("Contains", calls[^1].Filter!, StringComparison.Ordinal);
            Assert.DoesNotContain("contains(", calls[^1].Filter!, StringComparison.Ordinal);
        }

        [Fact]
        public void CarriesTheFilterAsAnODataStringForAnODataSource()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowFiltering, true),
                source: rows => new ODataEnumerable<Person>(rows));

            TypeInFilter(cut, 0, "First1");

            Assert.Contains("contains(", calls[^1].Filter!, StringComparison.Ordinal);
        }

        [Fact]
        public void CarriesNoFilterUntilSomethingIsFiltered()
        {
            using var ctx = new TestContext();

            Render(ctx, p => p.Add(h => h.AllowFiltering, true));

            Assert.Null(calls[0].Filter);
            Assert.Null(calls[0].Filters);
        }

        [Fact]
        public void AFilterThatProducesNoTextIsSentAsNoFilterAtAll()
        {
            // The Custom operator means "I will filter this myself", so it produces no expression. An
            // empty string is not the same as no filter: a handler appending it to a query would send
            // one that cannot parse.
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(h => h.AllowFiltering, true));

            cut.InvokeAsync(() => cut.Instance.Grid!.ApplyFilters(new[]
            {
                new FilterDescriptor
                {
                    Property = "First", FilterValue = "First1", FilterOperator = FilterOperator.Custom,
                },
            }));

            Assert.NotNull(calls[^1].Filters);
            Assert.Null(calls[^1].Filter);
        }

        [Fact]
        public void RendersWhatTheHandlerServedWithoutFilteringItAgain()
        {
            // The handler owns the filter. Applying it again to its result would narrow twice - and for
            // a handler that filtered on something else entirely, would empty the grid.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowFiltering, true);
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
            });

            TypeInFilter(cut, 0, "Nothing matches this");

            Assert.Equal(new[] { "First1", "First2", "First3", "First4" }, FirstNames(cut));
        }

        [Fact]
        public void FilteringReturnsToTheFirstPageBeforeReloading()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowFiltering, true);
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.Grid!.GoToPage(3));

            Assert.Equal(12, calls[^1].Skip);

            TypeInFilter(cut, 0, "First1");

            Assert.Equal(0, calls[^1].Skip);
        }

        [Fact]
        public void ReloadInvokesItAgain()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            cut.InvokeAsync(() => cut.Instance.Grid!.Reload());

            Assert.Equal(2, calls.Count);
        }

        [Fact]
        public void GoToPageInvokesItWithTheNewOffset()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.Grid!.GoToPage(3));

            Assert.Equal(12, calls[^1].Skip);
        }

        [Fact]
        public void SortingReturnsToTheFirstPageBeforeReloading()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(h => h.AllowPaging, true);
                p.Add(h => h.PageSize, 4);
                p.Add(h => h.AllowSorting, true);
            });

            cut.InvokeAsync(() => cut.Instance.Grid!.GoToPage(3));

            Assert.Equal(12, calls[^1].Skip);

            ClickHeader(cut, 0);

            Assert.Equal(0, calls[^1].Skip);
        }
        [Fact]
        public void AHandlerMayServeAQueryableRatherThanAList()
        {
            // A handler is free to assign the page it built without materializing it. The grid used to
            // check "does an executor own this queryable" before "is there a handler", so it rendered
            // nothing - while the pager, which checks the handler first, went on counting the handler's
            // rows. A grid reading its count above an empty table, and no reload could fix it.
            using var ctx = new TestContext();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new PassThroughExecutor());

            // Unpaged, because the host materializes a paged answer into a List and the queryable is
            // the whole point of this case.
            var cut = Render(ctx, source: rows => rows.AsQueryable());

            Assert.Equal(30, cut.FindAll("tbody tr").Count);
        }

        /// <summary>An executor that claims every queryable, so AsyncOwnsData is true for one.</summary>
        sealed class PassThroughExecutor : IFastGridQueryExecutor
        {
            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.ToList());
        }
    }

    /// <summary>Holds Data and Count for the grid, the way a page using LoadData does.</summary>
    public sealed class LoadDataHost : ComponentBase
    {
        [Parameter] public Action<LoadDataArgs, LoadDataHost> OnLoad { get; set; } = default!;

        [Parameter] public RenderFragment Columns { get; set; } = default!;

        [Parameter] public bool AllowPaging { get; set; }

        [Parameter] public bool AllowSorting { get; set; }

        [Parameter] public bool AllowFiltering { get; set; }

        [Parameter] public bool ShowPagingSummary { get; set; }

        [Parameter] public int PageSize { get; set; } = 10;

        /// <summary>The grid instance, for tests that drive it directly.</summary>
        public RadzenFastGrid<Person>? Grid { get; private set; }

        IEnumerable<Person> data = System.Array.Empty<Person>();
        int count;

        public void Serve(IEnumerable<Person> rows, int total)
        {
            data = rows;
            count = total;

            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RadzenFastGrid<Person>>(0);
            builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), data);
            builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.Count), count);
            builder.AddAttribute(3, nameof(RadzenFastGrid<Person>.ChildContent), Columns);
            builder.AddAttribute(4, nameof(RadzenFastGrid<Person>.AllowPaging), AllowPaging);
            builder.AddAttribute(5, nameof(RadzenFastGrid<Person>.AllowSorting), AllowSorting);
            builder.AddAttribute(6, nameof(RadzenFastGrid<Person>.PageSize), PageSize);
            builder.AddAttribute(7, nameof(RadzenFastGrid<Person>.ShowPagingSummary), ShowPagingSummary);
            builder.AddAttribute(10, nameof(RadzenFastGrid<Person>.AllowFiltering), AllowFiltering);
            builder.AddAttribute(8, nameof(RadzenFastGrid<Person>.LoadData),
                EventCallback.Factory.Create<LoadDataArgs>(this, args => OnLoad(args, this)));
            builder.AddComponentReferenceCapture(9, o => Grid = (RadzenFastGrid<Person>)o);
            builder.CloseComponent();
        }

    }
}
