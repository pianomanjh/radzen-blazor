using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What one render of the table has already worked out, and how long it is allowed to be true for.
    /// </summary>
    /// <remarks>
    /// These need no grid, which is the point of the type existing. The same rules used to be four
    /// fields and a flag on the component, reachable only by rendering a table and counting queries
    /// through an executor spy.
    /// </remarks>
    public class DrawPassTests
    {
        static readonly List<Person> Source = People.Sample();
        static readonly List<Person> Other = People.Sample();
        static readonly List<Person> Result = People.Sample();

        [Fact]
        public void OutsideAPassNothingIsRemembered()
        {
            var pass = default(DrawPass<Person>);

            Assert.Same(Result, pass.Keep(Source, Result));
            Assert.False(pass.Reuses(Source, out _));

            // A caller that is not drawing asks for exactly what it asked for and gets it fresh. The
            // memo would otherwise have to be invalidated by every path that touches a filter.
            Assert.Equal(7, pass.Keep(7));
            Assert.False(pass.Counted(out _));
        }

        [Fact]
        public void WithinAPassOneSourceIsComposedOnce()
        {
            var pass = DrawPass<Person>.Begin(null);

            Assert.False(pass.Reuses(Source, out _));

            pass.Keep(Source, Result);

            Assert.True(pass.Reuses(Source, out var reused));
            Assert.Same(Result, reused);
        }

        [Fact]
        public void ADifferentSourceIsComposedAgain()
        {
            var pass = DrawPass<Person>.Begin(null);

            pass.Keep(Source, Result);

            // Keyed on the instance, not on what it holds: two equal lists are two compositions, and the
            // same list twice is one. The pager and the body pass the same instance, which is the case
            // this exists for.
            Assert.False(pass.Reuses(Other, out _));
        }

        [Fact]
        public void TheTotalIsRememberedForTheRestOfThePass()
        {
            var pass = DrawPass<Person>.Begin(null);

            Assert.False(pass.Counted(out _));
            Assert.Equal(4, pass.Keep(4));
            Assert.True(pass.Counted(out var counted));
            Assert.Equal(4, counted);
        }

        [Fact]
        public void EndingThePassForgetsWhatItKnew()
        {
            var pass = DrawPass<Person>.Begin(null);

            pass.Keep(Source, Result);
            pass.Keep(4);

            pass = default;

            Assert.False(pass.Reuses(Source, out _));
            Assert.False(pass.Counted(out _));
        }

        [Fact]
        public void ThePassCarriesTheFiltersItWasOpenedOver()
        {
            var filters = new List<FilterDescriptor> { new FilterDescriptor { Property = "First" } };

            Assert.Same(filters, DrawPass<Person>.Begin(filters).Filters);
            Assert.Null(DrawPass<Person>.Begin(null).Filters);
        }

        // ---- the rule the pass took over from the grid ----

        /// <summary>Executes against the in-memory queryable, and answers that it can.</summary>
        sealed class Executor : IFastGridQueryExecutor
        {
            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default) =>
                Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default) =>
                Task.FromResult(queryable.ToList());
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, bool filtering)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton<IFastGridQueryExecutor>(new Executor());

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.AllowFiltering, filtering);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last")));
            });
        }

        static Task Filter(IRenderedComponent<RadzenFastGrid<Person>> cut, string value) =>
            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor
                {
                    Property = "First",
                    FilterValue = value,
                    FilterOperator = Radzen.FilterOperator.Contains,
                },
            }));

        [Fact]
        public async Task AColumnCarriesItsFilterWhileFilteringIsOffAndTheQueryDoesNotApplyIt()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, filtering: false);

            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("tbody tr").Count));

            await Filter(cut, "Alice");

            // The asynchronous load is the one caller that composes without asking about AllowFiltering
            // first, so it is the only path on which the question is actually put - and it is the path
            // this grid is built for. The parameter's own summary is the claim being checked: columns
            // still carry their filters when filtering is off.
            Assert.Single(cut.Instance.Filters);
            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("tbody tr").Count));
        }

        [Fact]
        public async Task AndTheQueryAppliesItAsSoonAsFilteringIsOn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, filtering: true);

            cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("tbody tr").Count));

            await Filter(cut, "Alice");

            cut.WaitForAssertion(() => Assert.Equal(1, cut.FindAll("tbody tr").Count));
        }
    }
}
