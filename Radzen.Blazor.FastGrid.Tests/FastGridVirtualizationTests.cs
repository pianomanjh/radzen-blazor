using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Virtualization renders only the rows in view. How many that is depends on the viewport, which no
    /// renderer without a browser has, so bUnit asks the provider for everything - what these check is
    /// the structure around it, the exclusivity with paging, and that a sort, filter or reload actually
    /// reaches the provider rather than redrawing the window Virtualize already holds.
    /// <para>
    /// Two things are deliberately not covered, because that same missing viewport makes them
    /// unreachable: the request always arrives with a start index of zero and a count equal to the whole
    /// set, so neither the offset the provider applies nor its reporting the total rather than the
    /// window can be made to fail here. Both were confirmed by mutation to survive every test in this
    /// project; verifying them needs a browser.
    /// </para>
    /// </summary>
    public class FastGridVirtualizationTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IEnumerable<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Many(40));
                p.Add(g => g.AllowVirtualization, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                extra?.Invoke(p);
            });
        }

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr[role=row]")
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        [Fact]
        public void NoVirtualizeComponentUnlessAskedFor()
        {
            using var ctx = new TestContext();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(40));
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
            });

            Assert.Empty(cut.FindComponents<Virtualize<Person>>());
            Assert.Null(cut.Instance.Virtualize);
        }

        [Fact]
        public void TheRowsGoThroughVirtualize()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Single(cut.FindComponents<Virtualize<Person>>());
            Assert.NotNull(cut.Instance.Virtualize);
            Assert.Equal(40, cut.FindAll("tbody tr[role=row]").Count);
            Assert.Equal("First1", FirstNames(cut)[0]);
        }

        [Fact]
        public void TheSpacersAreTableRows()
        {
            // Virtualize spaces its window with two elements, divs by default. A div inside a tbody is
            // hoisted out of the table by the HTML parser, so the rows lose their sizing entirely.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll("tbody div"));
            Assert.Equal(2, cut.FindAll("tbody tr[aria-hidden=true]").Count);
        }

        [Fact]
        public void TheCellsAreStillRenderedInline()
        {
            // Virtualize costs a fragment per visible row, which is its contract. The cells inside must
            // not also become fragments - that is the whole shape the component exists for.
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var cells = cut.FindAll("tbody tr[role=row]")[0].QuerySelectorAll("td");

            Assert.Equal(2, cells.Length);
            Assert.Equal("rz-cell-data rz-text-truncate", cells[0].QuerySelector("span")!.ClassName);
        }

        [Fact]
        public void PagingIsIgnoredAndNoPagerIsDrawn()
        {
            // The two solve the same problem. Combining them would page the source and then virtualize
            // within the page, which is a window of a window and no use to anyone.
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
            });

            Assert.Empty(cut.FindAll(".rz-pager"));
            Assert.Equal(40, cut.FindAll("tbody tr[role=row]").Count);
        }

        [Fact]
        public void SortingRefetchesRatherThanRedrawing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowSorting, true));

            cut.FindAll("thead th")[1].QuerySelector("div")!.Click();
            cut.FindAll("thead th")[1].QuerySelector("div")!.Click();

            cut.WaitForAssertion(() => Assert.Equal("First40", FirstNames(cut)[0]));
        }

        [Fact]
        public void FilteringRefetchesRatherThanRedrawing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.AllowFiltering, true));

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            // First1 and First10..First19.
            cut.WaitForAssertion(() => Assert.Equal(11, cut.FindAll("tbody tr[role=row]").Count));
        }

        [Fact]
        public async Task ReloadRefetches()
        {
            // The source changed underneath the grid, which it cannot see. Reordering rather than adding
            // is what discriminates: Virtualize redrawing its cached window shows the old order, and only
            // a refetch shows the new one.
            using var ctx = new TestContext();
            var data = new List<Person>(People.Many(5));

            var cut = Render(ctx, data: data);

            Assert.Equal("First1", FirstNames(cut)[0]);

            data.Reverse();

            await cut.InvokeAsync(() => cut.Instance.Reload());

            Assert.Equal("First5", FirstNames(cut)[0]);
        }

        [Fact]
        public void TheLoadDataHandlerIsAskedForTheWindow()
        {
            // Skip and Top come from the scroll request rather than from the pager, which is what makes
            // a LoadData source virtualizable at all. bUnit has no viewport, so the window it asks for
            // is the whole set - what is checked here is that a window is asked for, not its size.
            using var ctx = new TestContext();
            var calls = new List<LoadDataArgs>();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<VirtualizedLoadDataHost>(p =>
            {
                p.Add(h => h.Columns, Columns.Of(Columns.Property<Person, string>(x => x.First)));
                p.Add(h => h.OnLoad, (args, host) =>
                {
                    calls.Add(args);
                    host.Serve(People.Many(20).Skip(args.Skip ?? 0).Take(args.Top ?? 20).ToList(), 20);
                });
            });

            cut.WaitForAssertion(() => Assert.NotEmpty(calls));

            Assert.Equal(0, calls[0].Skip);
            Assert.NotNull(calls[0].Top);
            Assert.NotEqual(0, calls[0].Top);
        }

        [Fact]
        public void NothingIsFetchedBeforeTheProviderAsks()
        {
            // The provider owns fetching while virtualizing. Pre-loading a page in OnParametersSetAsync
            // as well would run the query twice for one render, and throw the first answer away.
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(20).AsQueryable());

            cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll("tbody tr[role=row]").Count));

            Assert.Equal(1, executor.ToListCalls);
        }

        [Fact]
        public void TheTotalIsCountedWithoutTheOrdering()
        {
            // A count wraps the query in GroupBy(_ => 1).Select(g => g.Count()), and an ORDER BY inside
            // that aggregate is what a provider is entitled to refuse - SQL Server rejects it outright.
            // The paged path has always counted the filtered query; this one was counting the sorted one.
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(20).AsQueryable(), extra: p => p.Add(g => g.AllowSorting, true));

            var column = cut.FindComponents<PropertyColumn<Person, string>>()[0].Instance;

            cut.InvokeAsync(() => cut.Instance.SortBy(column)).Wait();

            cut.WaitForAssertion(() => Assert.NotNull(executor.CountedExpression));

            Assert.DoesNotContain("OrderBy", executor.CountedExpression, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTotalComesFromCountingTheSourceNotTheWindow()
        {
            // The scrollbar's length is the total. Reporting the window instead makes it claim there is
            // nothing below the fold.
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(20).AsQueryable());

            cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll("tbody tr[role=row]").Count));

            Assert.Equal(1, executor.CountCalls);
        }

        [Fact]
        public async Task ScrollingDoesNotRecountTheSource()
        {
            // Every window fetched is a scroll. The total behind the scrollbar does not change while
            // scrolling, so counting per window means a COUNT(*) per scroll against the database - which
            // is what makes an endless scroll expensive rather than the fetching itself.
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(40).AsQueryable());

            cut.WaitForAssertion(() => Assert.Equal(1, executor.ToListCalls));
            Assert.Equal(1, executor.CountCalls);

            // Straight at Virtualize rather than through Reload: this is what a scroll does.
            await cut.InvokeAsync(() => cut.Instance.Virtualize!.RefreshDataAsync());
            await cut.InvokeAsync(() => cut.Instance.Virtualize!.RefreshDataAsync());

            Assert.Equal(3, executor.ToListCalls);
            Assert.Equal(1, executor.CountCalls);
        }

        [Fact]
        public async Task ReloadingDoesRecountTheSource()
        {
            // The other half of the rule: a reload may be a new filter or new data, so the total it
            // cached is no longer trustworthy.
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(40).AsQueryable());

            cut.WaitForAssertion(() => Assert.Equal(1, executor.CountCalls));

            await cut.InvokeAsync(() => cut.Instance.Reload());

            Assert.Equal(2, executor.CountCalls);
        }

        [Fact]
        public void ANewDataSourceRecountsToo()
        {
            using var ctx = new TestContext();
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = Render(ctx, data: People.Many(40).AsQueryable());

            cut.WaitForAssertion(() => Assert.Equal(1, executor.CountCalls));

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(10).AsQueryable()));

            cut.WaitForAssertion(() => Assert.Equal(2, executor.CountCalls));
            Assert.Equal(10, cut.FindAll("tbody tr[role=row]").Count);
        }

        [Fact]
        public async Task ScrollingDoesNotRewalkAnInMemorySourceForItsTotal()
        {
            // Counting a sequence that is not an ICollection means walking it. The same rule as the
            // query above, and the same cost: a walk of the whole source for every window.
            using var ctx = new TestContext();
            var source = new WalkCountingSequence(People.Many(40));

            var cut = Render(ctx, data: source);

            Assert.Equal(40, cut.FindAll("tbody tr[role=row]").Count);

            var afterFirst = source.Walks;

            await cut.InvokeAsync(() => cut.Instance.Virtualize!.RefreshDataAsync());

            // The window the provider returns is lazy, so it is the render that walks it. Counting is
            // not lazy, and would have walked already.
            cut.Render();

            Assert.Equal(afterFirst + 1, source.Walks);
        }

        /// <summary>
        /// Records how many times it is walked. Deliberately not an <see cref="ICollection{T}" />, which
        /// LINQ counts without walking - the point is to see the walks.
        /// </summary>
        sealed class WalkCountingSequence : IEnumerable<Person>
        {
            readonly List<Person> source;

            public WalkCountingSequence(List<Person> source) => this.source = source;

            public int Walks { get; private set; }

            public IEnumerator<Person> GetEnumerator()
            {
                Walks++;

                return source.GetEnumerator();
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        [Fact]
        public void TheRowHeightItAssumesIsTheOneTheThemeRenders()
        {
            // 37px is measured, not guessed - it is what GeometryParityTests pins for a body row. A wrong
            // ItemSize makes the scrollbar lie about how far there is to scroll.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal(37f, cut.FindComponent<Virtualize<Person>>().Instance.ItemSize);
        }

        [Fact]
        public void TheOverscanCountIsPassedOnWhenSet()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(g => g.VirtualizationOverscanCount, 7));

            Assert.Equal(7, cut.FindComponent<Virtualize<Person>>().Instance.OverscanCount);
        }

        [Fact]
        public void AnUnsetOverscanCountLeavesVirtualizesOwnDefault()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal(3, cut.FindComponent<Virtualize<Person>>().Instance.OverscanCount);
        }

        [Fact]
        public void SelectionAndRowClickStillWork()
        {
            using var ctx = new TestContext();
            var data = People.Many(5);
            var clicked = new List<Person>();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.Selection, new[] { data[1] });
                p.Add(g => g.RowClick, EventCallback.Factory.Create<Person>(this, clicked.Add));
            }, data: data);

            var rows = cut.FindAll("tbody tr[role=row]");

            Assert.Contains("rz-state-highlight", rows[1].ClassName);
            Assert.Equal("true", rows[1].GetAttribute("aria-selected"));

            rows[2].Click();

            Assert.Equal("First3", Assert.Single(clicked).First);
        }

        /// <summary>Records what the grid asked it to run.</summary>
        sealed class CountingExecutor : IFastGridQueryExecutor
        {
            public int ToListCalls { get; private set; }

            public int CountCalls { get; private set; }

            /// <summary>The expression of the last query counted, so a test can see what was in it.</summary>
            public string? CountedExpression { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                CountCalls++;
                CountedExpression = queryable.Expression.ToString();

                return Task.FromResult(queryable.Count());
            }

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                ToListCalls++;

                return Task.FromResult(queryable.ToList());
            }
        }
    }

    /// <summary>Holds Data and Count for a virtualized LoadData grid.</summary>
    public sealed class VirtualizedLoadDataHost : ComponentBase
    {
        [Parameter] public Action<LoadDataArgs, VirtualizedLoadDataHost> OnLoad { get; set; } = default!;

        [Parameter] public RenderFragment Columns { get; set; } = default!;

        IEnumerable<Person> data = Array.Empty<Person>();
        int count;

        public void Serve(IEnumerable<Person> rows, int total)
        {
            data = rows;
            count = total;

            StateHasChanged();
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<RadzenFastGrid<Person>>(0);
            builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), data);
            builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.Count), count);
            builder.AddAttribute(3, nameof(RadzenFastGrid<Person>.ChildContent), Columns);
            builder.AddAttribute(4, nameof(RadzenFastGrid<Person>.AllowVirtualization), true);
            builder.AddAttribute(5, nameof(RadzenFastGrid<Person>.LoadData),
                EventCallback.Factory.Create<LoadDataArgs>(this, args => OnLoad(args, this)));
            builder.CloseComponent();
        }
    }
}
