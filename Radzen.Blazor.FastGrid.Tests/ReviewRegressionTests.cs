using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Faults a review found that no existing test could see, each pinned by the thing that made it
    /// invisible: a fake executor that answered synchronously, a test helper that reused one expression
    /// instance where Razor builds a new one, and a lookup whose values happened to be comparable.
    /// </summary>
    public class ReviewRegressionTests
    {
        /// <summary>Counts every time the query is walked from the calling thread.</summary>
        sealed class WalkCountingQueryable : IQueryable<Person>
        {
            readonly IQueryable<Person> inner;

            public WalkCountingQueryable(IQueryable<Person> inner) => this.inner = inner;

            public int Walks { get; private set; }

            public Type ElementType => inner.ElementType;

            public Expression Expression => inner.Expression;

            public IQueryProvider Provider => inner.Provider;

            public IEnumerator<Person> GetEnumerator()
            {
                Walks++;

                return inner.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>Answers after a yield, the way a database does - not from an already-complete task.</summary>
        sealed class YieldingExecutor : IFastGridQueryExecutor
        {
            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public async Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                return queryable.Count();
            }

            public async Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                return queryable.ToList();
            }
        }

        [Fact]
        public void AnExecutorOwnedQueryIsNeverRunFromTheRenderThread()
        {
            // The whole point of the async path. Composing over the query while its load is in flight
            // pulls the entire unpaged table synchronously - twice, once for the rows and once for the
            // pager's total - for rows the awaited load is about to replace.
            using var ctx = new TestContext();
            var source = new WalkCountingQueryable(People.Many(40).AsQueryable());

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new YieldingExecutor());

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, source);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ShowPagingSummary, true);
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
            });

            Assert.Equal(0, source.Walks);

            cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("tbody tr").Count));
            Assert.Contains("40", cut.Find(".rz-pager-summary").TextContent, StringComparison.Ordinal);
        }

        [Fact]
        public void ACheckBoxListLookupIsNeverRunFromTheRenderThread()
        {
            // The same rule, for the one path that did not follow it. The distinct query behind a
            // check-box list was composed and enumerated inside BuildRenderTree - a blocking round trip
            // on the render thread, and on Entity Framework a second operation on the context that the
            // awaited page load is still using.
            using var ctx = new TestContext();
            var source = new WalkCountingQueryable(People.Many(40).AsQueryable());

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new YieldingExecutor());

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, source);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
            });

            // Not one walk from the render itself: every query the grid ran went through the executor.
            Assert.Equal(0, source.Walks);

            // And the values still arrive - fetched after the render rather than during it.
            cut.WaitForAssertion(() => Assert.Equal(40,
                cut.FindComponents<RadzenDropDown<IEnumerable>>()[0].Instance.Data.Cast<object>().Count()));
        }

        [Fact]
        public void AColumnTypedAsObjectStillFiltersOnItsRealType()
        {
            // The column knows only object, so the text stayed a string and the predicate builder put a
            // string constant where an int belongs: "argument types do not match", from the filter box.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, object>(x => (object)x.Id),
                    Columns.Property<Person, string>(x => x.First)));
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("3");

            Assert.Equal(new[] { "Carol" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent));
        }

        [Fact]
        public void ATemplateColumnWithASortPropertyFiltersOnItsRealTypeToo()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Template<Person>(item => b => b.AddContent(0, item.Id), sortProperty: "Id"),
                    Columns.Property<Person, string>(x => x.First)));
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("2");

            Assert.Equal(new[] { "Bob" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent));
        }

        [Fact]
        public void ALookupOfValuesThatCannotBeComparedStillRenders()
        {
            // Comparer<object>.Default throws for a type that is not IComparable, which a collection of
            // entities with no display member is. That took the grid's first render down entirely.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Collection<Person, Company>(x => x.Accounts)));
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.NotEmpty(cut.FindComponents<RadzenDropDown<IEnumerable>>()[0].Instance.Data.Cast<object>());
        }

        [Fact]
        public void ComparableLookupValuesAreStillSorted()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
            });

            Assert.Equal(
                new object[] { "Alice", "Bob", "Carol", "Dave" },
                cut.FindComponents<RadzenDropDown<IEnumerable>>()[0].Instance.Data.Cast<object>());
        }

        [Fact]
        public void AColumnAuthoredInMarkupDoesNotRecompilePerRender()
        {
            // Razor builds a fresh expression tree on every render, so reference equality never holds
            // for a column written in markup and every render recompiled every column. The test helpers
            // elsewhere hide this by reusing one expression instance.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            RebuildingHost.Compiles = 0;

            var cut = ctx.RenderComponent<RebuildingHost>(p => p.Add(h => h.Data, People.Many(5)));

            Assert.Equal(1, RebuildingHost.Compiles);

            for (var i = 1; i <= 10; i++)
            {
                cut.SetParametersAndRender(p => p.Add(h => h.Tick, i));
            }

            Assert.Equal(1, RebuildingHost.Compiles);
            Assert.Equal(5, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void AComputedColumnStillRecompilesWhenItsExpressionIsRebuilt()
        {
            // A computed expression can capture, so two rebuilt trees are not interchangeable - it has
            // no derived path, and must be recompiled rather than assumed equivalent.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            RebuildingHost.Compiles = 0;

            var cut = ctx.RenderComponent<RebuildingHost>(p =>
            {
                p.Add(h => h.Data, People.Many(5));
                p.Add(h => h.Computed, true);
            });

            cut.SetParametersAndRender(p => p.Add(h => h.Tick, 1));

            Assert.True(RebuildingHost.Compiles > 1);
        }

        [Fact]
        public void ChangingTheAuthoredPropertyStillTakesEffect()
        {
            // The other half of treating equal paths as equivalent: a different path is a different
            // column, and must recompile.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RebuildingHost>(p => p.Add(h => h.Data, People.Many(3)));

            Assert.Equal("First1", cut.FindAll("tbody td")[0].TextContent);

            cut.SetParametersAndRender(p => p.Add(h => h.ShowLast, true));

            Assert.Equal("Last1", cut.FindAll("tbody td")[0].TextContent);
        }

        [Fact]
        public void TheRealPropertyColumnDoesNotRecompilePerRenderEither()
        {
            // The counting column above proves the rule; this proves PropertyColumn applies it. There is
            // nothing observable in the output either way, so what is measured is the cost: compiling an
            // expression allocates tens of kilobytes, and doing it per column per render is unmissable
            // next to a re-render that does not.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<MarkupAuthoredHost>(p => p.Add(h => h.Data, People.Many(5)));

            for (var i = 1; i <= 5; i++)
            {
                cut.SetParametersAndRender(p => p.Add(h => h.Tick, i));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 6; i <= 25; i++)
            {
                cut.SetParametersAndRender(p => p.Add(h => h.Tick, i));
            }

            var perRender = (GC.GetAllocatedBytesForCurrentThread() - before) / 20d;

            // Measured: 6,207 B per re-render of five rows and two columns when the columns reuse their
            // compiled delegates, 14,511 when they recompile. The threshold sits between the two with
            // room either side, and the gap only widens with more columns.
            Assert.InRange(perRender, 0, 10_000);
        }

        [Fact]
        public void ACollectionColumnAuthoredInMarkupDoesNotRecompileEither()
        {
            // CollectionColumn has its own guard over four expressions, so it needs its own weighing.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<MarkupAuthoredCollectionHost>(p => p.Add(h => h.Data, People.Many(5)));

            for (var i = 1; i <= 5; i++)
            {
                cut.SetParametersAndRender(p => p.Add(h => h.Tick, i));
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 6; i <= 25; i++)
            {
                cut.SetParametersAndRender(p => p.Add(h => h.Tick, i));
            }

            var perRender = (GC.GetAllocatedBytesForCurrentThread() - before) / 20d;

            // Measured: 4,895 B reusing the compiled delegates, 13,489 recompiling.
            Assert.InRange(perRender, 0, 9_000);
        }

        [Fact]
        public void AStringPropertyTypedAsObjectStillDefaultsToContains()
        {
            // The declared type says object, which is not string, so the default operator was Equals and
            // typing a fragment of a name matched nothing. The resolved type is what decides.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, object>(x => x.First)));
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("ar");

            Assert.Equal(FilterOperator.Contains, Assert.Single(cut.Instance.Filters).FilterOperator);
            Assert.Equal(new[] { "Carol" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent));
        }

        [Fact]
        public void ALoadDataGridRebuildsItsLookupWhenThePageChanges()
        {
            // The lookup was cached before the LoadData branch could clear it, so a check-box list built
            // from page one was still being offered on every page after it.
            using var ctx = new TestContext();
            var all = People.Many(20);

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<LookupLoadDataHost>(p =>
            {
                p.Add(h => h.OnLoad, (args, host) =>
                    host.Serve(all.Skip(args.Skip ?? 0).Take(args.Top ?? 5).ToList(), all.Count));
            });

            var first = cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Data.Cast<object>().ToArray();

            Assert.Equal(new object[] { "First1", "First2", "First3", "First4", "First5" }, first);

            cut.Find(".rz-pager-next").Click();

            var second = cut.FindComponents<RadzenDropDown<IEnumerable>>()[0]
                .Instance.Data.Cast<object>().ToArray();

            Assert.Equal(new object[] { "First10", "First6", "First7", "First8", "First9" }, second);
        }

        [Fact]
        public async Task DisposeCancelsAnInFlightLoad()
        {
            // Disposing without cancelling leaves the query running against a component that is gone,
            // holding its context open until it finishes.
            using var ctx = new TestContext();
            var executor = new ObservingExecutor();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(5).AsQueryable());
                p.Add(g => g.ChildContent, Columns.Of(Columns.Property<Person, string>(x => x.First)));
            });

            var token = executor.LastToken;

            Assert.False(token.IsCancellationRequested);

            await cut.InvokeAsync(() => ((IDisposable)cut.Instance).Dispose());

            Assert.True(token.IsCancellationRequested);
        }

        sealed class ObservingExecutor : IFastGridQueryExecutor
        {
            public CancellationToken LastToken { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                LastToken = cancellationToken;

                return Task.FromResult(queryable.ToList());
            }
        }

        // --- A settings restore and the columns it cannot name ---------------------------------

        [Fact]
        public void ASettingsRestoreDoesNotClearTheFilterOfAColumnItCannotName()
        {
            // Settings identify a column by PropertyPath, but a column filters by FilterPropertyPath,
            // and for a CollectionColumn without SortBy those disagree: it has no PropertyPath, so it
            // is never stored - yet the restore cleared every column's filter before putting back the
            // ones it could name. A reset must not reach further than the restore that follows it.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var settings = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = nameof(Person.First), Visible = true },
                },
            };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.Settings, settings);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Collection<Person, string>(x => x.Regions, filterValue: "South")));
            });

            // Alice and Bob list South. Cleared, all four rows come back.
            Assert.Equal(
                new[] { "Alice", "Bob" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent).ToArray());
        }

        [Fact]
        public void ASettingsRestoreDoesNotDropASortItCannotName()
        {
            // The same reach problem on the sort side. A FastGridSort over a computed key has no Path -
            // the type documents that as "nothing to write down" - but the column can still sort by it,
            // so clearing the list wholesale threw away a sort the restore had no way to re-add.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var settings = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = nameof(Person.First), Visible = true },
                },
            };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.Settings, settings);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Collection<Person, string>(x => x.Regions,
                        sortBy: FastGridSort<Person>.By(x => x.Salary * 2),
                        sortOrder: SortOrder.Ascending)));
            });

            // Salary ascending: Dave 1000, Alice 2000, Bob 3000, Carol 4000.
            Assert.Equal(
                new[] { "Dave", "Alice", "Bob", "Carol" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent).ToArray());
        }

        // --- A declared sort on a column that cannot be sorted by ------------------------------

        [Fact]
        public void ADeclaredSortOnACollectionColumnDoesNotOrderTheGrid()
        {
            // CanSort is false for a collection-typed property - no provider can order rows by a list -
            // but a declared SortOrder was the one route into the sort list that never asked. The grid
            // then ordered by it, and LINQ has no comparer for a List<string>: the whole render threw.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, List<string>>(x => x.Regions,
                        sortOrder: SortOrder.Ascending)));
            });

            // Declaration order, because the declared sort was refused rather than applied.
            Assert.Equal(
                new[] { "Carol", "Alice", "Dave", "Bob" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent).ToArray());
        }

        [Fact]
        public void ADeclaredSortOnAnUnsortableColumnDoesNotOrderTheGrid()
        {
            // Sortable="false" removes the header control, the icon and aria-sort - so a sort declared
            // beside it is one the user can see no sign of and has no way to clear.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First,
                        sortable: false, sortOrder: SortOrder.Descending)));
            });

            Assert.Equal(
                new[] { "Carol", "Alice", "Dave", "Bob" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent).ToArray());
        }

        [Fact]
        public void AnUnsortableColumnDoesNotDisplaceADeclaredSortThatWorks()
        {
            // Single-column sorting means the last declaration wins, so a declared sort clears the
            // list before adding itself. A column that cannot be ordered by therefore has to be
            // refused before that clear and not after it: refusing it later leaves the grid with no
            // sort at all, having thrown away the one the markup above it asked for.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, sortOrder: SortOrder.Ascending),
                    Columns.Property<Person, List<string>>(x => x.Regions,
                        sortOrder: SortOrder.Ascending)));
            });

            Assert.Equal(
                new[] { "Alice", "Bob", "Carol", "Dave" },
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[0].TextContent).ToArray());
        }

        [Fact]
        public void APropertyColumnThatCannotSortAnswersNullLikeEveryOtherColumn()
        {
            // ColumnBase declares ApplySort nullable and documents null as "cannot be ordered by";
            // CollectionColumn and TemplateColumn both honour it. PropertyColumn overrode it
            // non-nullable and ordered regardless, which is what let the declared sort through.
            using var ctx = new TestContext();
            var data = People.Sample();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, sortable: false)));
            });

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Null(column.ApplySort(data.AsQueryable(), descending: false));
            Assert.Null(column.ApplyThenBy(data.AsQueryable().OrderBy(x => x.Id), descending: false));
        }
    }

    /// <summary>Rebuilds its column expressions on every render, the way Razor does.</summary>
    public sealed class RebuildingHost : ComponentBase
    {
        public static int Compiles;

        [Parameter] public IEnumerable<Person> Data { get; set; } = Array.Empty<Person>();

        [Parameter] public int Tick { get; set; }

        [Parameter] public bool ShowLast { get; set; }

        [Parameter] public bool Computed { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var last = ShowLast;
            var computed = Computed;

            builder.OpenComponent<RadzenFastGrid<Person>>(0);
            builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), Data);
            builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<CompileCountingColumn<Person, string>>(0);
                b.AddAttribute(1, nameof(CompileCountingColumn<Person, string>.Property),
                    computed ? (Expression<Func<Person, string>>)(x => x.First + "!")
                        : last ? (Expression<Func<Person, string>>)(x => x.Last)
                        : (Expression<Func<Person, string>>)(x => x.First));
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A property column that records how often it compiles.</summary>
    public sealed class CompileCountingColumn<TItem, TProp> : ColumnBase<TItem>
    {
        [Parameter] public Expression<Func<TItem, TProp>> Property { get; set; } = default!;

        Expression<Func<TItem, TProp>>? last;
        Func<TItem, TProp>? compiled;

        protected override void OnParametersSet()
        {
            base.OnParametersSet();

            if (PropertyPathResolver.Equivalent(last, Property))
            {
                return;
            }

            last = Property;
            compiled = Property.Compile();

            RebuildingHost.Compiles++;
        }

        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, compiled!(item)?.ToString());
    }

    /// <summary>Authors real PropertyColumns with a fresh expression tree per render, as Razor does.</summary>
    public sealed class MarkupAuthoredHost : ComponentBase
    {
        [Parameter] public IEnumerable<Person> Data { get; set; } = Array.Empty<Person>();

        [Parameter] public int Tick { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RadzenFastGrid<Person>>(0);
            builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), Data);
            builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<PropertyColumn<Person, string>>(0);
                b.AddAttribute(1, nameof(PropertyColumn<Person, string>.Property),
                    (Expression<Func<Person, string>>)(x => x.First));
                b.CloseComponent();
                b.OpenComponent<PropertyColumn<Person, string>>(2);
                b.AddAttribute(3, nameof(PropertyColumn<Person, string>.Property),
                    (Expression<Func<Person, string>>)(x => x.Customer.Name));
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>The same for CollectionColumn, which has its own equivalence guard.</summary>
    public sealed class MarkupAuthoredCollectionHost : ComponentBase
    {
        [Parameter] public IEnumerable<Person> Data { get; set; } = Array.Empty<Person>();

        [Parameter] public int Tick { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RadzenFastGrid<Person>>(0);
            builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), Data);
            builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<CollectionColumn<Person, Company>>(0);
                b.AddAttribute(1, nameof(CollectionColumn<Person, Company>.Property),
                    (Expression<Func<Person, IEnumerable<Company>>>)(x => x.Accounts));
                b.AddAttribute(2, nameof(CollectionColumn<Person, Company>.DisplayProperty),
                    (Expression<Func<Company, object?>>)(a => a.Name));

                // Built here, as markup builds it: a FastGridSort written inline is a new instance on
                // every render, so a column that let its sort decide whether to re-derive would
                // recompile everything else along with it.
                b.AddAttribute(3, nameof(CollectionColumn<Person, Company>.SortBy),
                    FastGridSort<Person>.By(x => x.Salary));
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A paged LoadData grid with a check-box-list filter over what it was served.</summary>
    public sealed class LookupLoadDataHost : ComponentBase
    {
        [Parameter] public Action<LoadDataArgs, LookupLoadDataHost> OnLoad { get; set; } = default!;

        IEnumerable<Person> data = Array.Empty<Person>();
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
            builder.AddAttribute(3, nameof(RadzenFastGrid<Person>.AllowPaging), true);
            builder.AddAttribute(4, nameof(RadzenFastGrid<Person>.PageSize), 5);
            builder.AddAttribute(5, nameof(RadzenFastGrid<Person>.AllowFiltering), true);
            builder.AddAttribute(6, nameof(RadzenFastGrid<Person>.FilterMode), FilterMode.CheckBoxList);
            builder.AddAttribute(7, nameof(RadzenFastGrid<Person>.ChildContent),
                Columns.Of(Columns.Property<Person, string>(x => x.First)));
            builder.AddAttribute(8, nameof(RadzenFastGrid<Person>.LoadData),
                EventCallback.Factory.Create<LoadDataArgs>(this, args => OnLoad(args, this)));
            builder.CloseComponent();
        }
    }
}
