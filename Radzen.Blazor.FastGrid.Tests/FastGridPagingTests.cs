using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Radzen.Blazor;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    public class FastGridPagingTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                extra?.Invoke(p);
            });
        }

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[0].TextContent).ToArray();

        [Fact]
        public void NoPagerUnlessPagingIsAllowed()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30));

            Assert.Empty(cut.FindAll(".rz-pager"));
            Assert.Equal(30, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void RendersOnlyOnePageOfRows()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(new[] { "First1", "First2", "First3", "First4" }, FirstNames(cut));
        }

        [Fact]
        public void ThePagerCountsTheWholeSourceNotThePage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.ShowPagingSummary, true);
            });

            // 30 rows at 4 a page is 8 pages. Counting the page instead of the source would say 1 of 1.
            Assert.Single(cut.FindAll(".rz-pager"));

            var summary = cut.Find(".rz-pager-summary").TextContent;

            Assert.Contains("8", summary, StringComparison.Ordinal);
            Assert.Contains("30", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void GoingToAPageMovesTheWindow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(2));

            Assert.Equal(new[] { "First9", "First10", "First11", "First12" }, FirstNames(cut));
            Assert.Equal(2, cut.Instance.CurrentPage);
        }

        [Fact]
        public void ClickingTheNextPageButtonMovesTheWindow()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.Find(".rz-pager-next").Click();

            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));
        }

        [Fact]
        public void SortingReturnsToTheFirstPage()
        {
            // The row that was on page 3 is not on page 3 under a different order, so staying put would
            // show an arbitrary window of the newly sorted set.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.AllowSorting, true);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(3));

            Assert.Equal(3, cut.Instance.CurrentPage);

            cut.FindAll("thead th")[1].QuerySelector("div")!.Click();

            Assert.Equal(0, cut.Instance.CurrentPage);
            Assert.Equal("First1", FirstNames(cut)[0]);
        }

        [Fact]
        public void ChangingPageSizeFromOutsideReturnsToTheFirstPage()
        {
            // The offset is in rows, so keeping it across a page-size change lands on a page nobody asked
            // for - skip 12 is page 4 at size 4 and page 2 at size 6.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(3));
            cut.SetParametersAndRender(p => p.Add(g => g.PageSize, 6));

            Assert.Equal(0, cut.Instance.CurrentPage);
            Assert.Equal(6, cut.FindAll("tbody tr").Count);
            Assert.Equal("First1", FirstNames(cut)[0]);
        }

        [Fact]
        public void NoPageSizeDropdownUnlessOptionsAreGiven()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Empty(cut.FindAll(".rz-dropdown"));
        }

        [Fact]
        public void PageSizeOptionsAddTheDropdown()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.PageSizeOptions, new[] { 4, 8 });
            });

            Assert.NotEmpty(cut.FindAll(".rz-dropdown"));
        }

        [Fact]
        public void ThePagerSitsWhereItIsAskedTo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.PagerPosition, PagerPosition.TopAndBottom);
            });

            var children = cut.Find(".rz-data-grid").Children.Select(c => c.ClassName).ToArray();

            Assert.Equal(3, children.Length);
            Assert.Contains("rz-pager", children[0]);
            Assert.Contains("rz-grid-table", children[1]);
            Assert.Contains("rz-pager", children[2]);
        }

        [Fact]
        public void BothPagersRenderAndBothDrivePaging()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.PagerPosition, PagerPosition.TopAndBottom);
            });

            Assert.Equal(2, cut.FindAll(".rz-pager").Count);

            cut.FindAll(".rz-pager-next")[1].Click();

            Assert.Equal(new[] { "First5", "First6", "First7", "First8" }, FirstNames(cut));
        }

        [Fact]
        public void APlainGridRendersExactlyOnce()
        {
            // Setting up the data path must not queue a render of its own: ComponentBase already renders
            // after OnParametersSetAsync, so an extra StateHasChanged there costs a second full pass over
            // every row. Measured at +94% allocation when that happened.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30));

            Assert.Equal(1, cut.RenderCount);
        }

        [Fact]
        public void APagedGridAlsoRendersExactlyOnce()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(1, cut.RenderCount);
        }

        [Fact]
        public void AnUnpagedGridNeverCountsItsSource()
        {
            // Rule 3: a grid that does not page must not pay for the pager's existence. Counting a
            // sequence that is not an ICollection means walking it, so a second walk is the tell.
            using var ctx = new TestContext();
            var source = new CountingSequence(People.Many(5));

            var cut = Render(ctx, source);

            Assert.Equal(5, cut.FindAll("tbody tr").Count);
            Assert.Equal(1, source.Walks);
        }

        [Fact]
        public void APagedGridCountsItsSourceOnce()
        {
            using var ctx = new TestContext();
            var source = new CountingSequence(People.Many(30));

            var cut = Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Equal(2, source.Walks);
        }

        [Fact]
        public void ACollectionIsCountedWithoutBeingWalked()
        {
            using var ctx = new TestContext();
            var source = new CountingCollection(People.Many(30));

            var cut = Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Equal(1, source.Walks);
        }

        [Fact]
        public void ANonGenericCollectionIsAlsoCountedWithoutBeingWalked()
        {
            // A source can be an IEnumerable<T> and a non-generic ICollection without being an
            // ICollection<T>. Count() asks that one too, which is why the grid does not test for it
            // itself - and if that ever stopped being true, this is where it would show.
            using var ctx = new TestContext();
            var source = new CountingLegacyCollection(People.Many(30));

            var cut = Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
            Assert.Equal(1, source.Walks);
        }

        // RadzenPager keeps its own offset and has no CurrentPage parameter to be told through, so
        // everything that sends the grid back to page one left the pager highlighting the old page -
        // and paging onward from it, so Next from a reset grid jumped two pages.
        [Fact]
        public void SortingPutsThePagerBackOnTheFirstPage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(100), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 10);
                p.Add(g => g.AllowSorting, true);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(3));

            Assert.Equal(3, cut.Instance.CurrentPage);
            Assert.Equal(3, cut.FindComponent<RadzenPager>().Instance.CurrentPage);

            cut.FindAll("thead th")[0].QuerySelector("div")!.Click();

            Assert.Equal(0, cut.Instance.CurrentPage);
            Assert.Equal(0, cut.FindComponent<RadzenPager>().Instance.CurrentPage);
        }

        [Fact]
        public void FilteringPutsThePagerBackOnTheFirstPage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(100), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 10);
                p.Add(g => g.AllowFiltering, true);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(3));

            Assert.Equal(3, cut.FindComponent<RadzenPager>().Instance.CurrentPage);

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            Assert.Equal(0, cut.Instance.CurrentPage);
            Assert.Equal(0, cut.FindComponent<RadzenPager>().Instance.CurrentPage);
        }

        [Fact]
        public void APageThatOutlivesItsRowsIsBroughtBackIntoRange()
        {
            // Parked on page 6 of 30 rows, then handed 5. Without a clamp the grid renders an empty
            // table while the pager reports rows that exist.
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(5));

            Assert.Equal(5, cut.Instance.CurrentPage);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, People.Many(5)));

            Assert.Equal(1, cut.Instance.CurrentPage);
            Assert.Equal(1, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void AnEmptiedSourceGoesBackToTheFirstPage()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            cut.InvokeAsync(() => cut.Instance.GoToPage(5));

            cut.SetParametersAndRender(p => p.Add(g => g.Data, new List<Person>()));

            Assert.Equal(0, cut.Instance.CurrentPage);
        }

        [Fact]
        public void APageSizeSetFromOutsideChangesWhatIsShown()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(30), p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);

            cut.SetParametersAndRender(p => p.Add(g => g.PageSize, 10));

            Assert.Equal(10, cut.FindAll("tbody tr").Count);
        }

        // Filtering and sorting composed onto the bound provider but paging and counting did not: View()
        // and TotalCount() are typed IEnumerable<TItem>, so Skip/Take/Count bound LINQ to Objects even
        // over an IQueryable. A database source was streaming every filtered row across the wire to be
        // skipped in memory, and scanning again to be counted.
        [Fact]
        public void ThePageIsComposedOntoTheProvider()
        {
            using var ctx = new TestContext();
            var source = new RecordingQueryable<Person>(People.Many(30));

            var cut = Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
            });

            Assert.Equal(4, cut.FindAll("tbody tr").Count);

            Assert.Contains(source.Executed, e =>
                e.Contains("Skip(", StringComparison.Ordinal) && e.Contains("Take(", StringComparison.Ordinal));
        }

        [Fact]
        public void TheTotalIsCountedByTheProvider()
        {
            using var ctx = new TestContext();
            var source = new RecordingQueryable<Person>(People.Many(30));

            Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.ShowPagingSummary, true);
            });

            Assert.Contains(source.Executed, e => e.Contains("Count(", StringComparison.Ordinal));
        }

        [Fact]
        public void AFilteredPageComposesTheFilterAndThePageTogether()
        {
            using var ctx = new TestContext();
            var source = new RecordingQueryable<Person>(People.Many(30));

            var cut = Render(ctx, source, p =>
            {
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 4);
                p.Add(g => g.AllowFiltering, true);
            });

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("First1");

            Assert.Contains(source.Executed, e =>
                e.Contains("Where(", StringComparison.Ordinal) && e.Contains("Skip(", StringComparison.Ordinal));
        }

        /// <summary>
        /// Records the expression tree its provider is asked to run, so a test can tell an operator that
        /// reached the provider from one that was applied to the rows after they arrived.
        /// </summary>
        public sealed class RecordingQueryable<T> : IQueryable<T>, IQueryProvider
        {
            readonly IQueryable<T> inner;
            readonly List<string> log;

            public RecordingQueryable(IEnumerable<T> source)
                : this(source.AsQueryable(), new List<string>())
            {
            }

            RecordingQueryable(IQueryable<T> inner, List<string> log)
            {
                this.inner = inner;
                this.log = log;
            }

            public IReadOnlyList<string> Executed => log;

            public Type ElementType => inner.ElementType;

            public Expression Expression => inner.Expression;

            public IQueryProvider Provider => this;

            public IEnumerator<T> GetEnumerator()
            {
                log.Add(inner.Expression.ToString());

                return inner.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            // Left unwrapped: nothing under test reads back through a projection of another element type.
            public IQueryable CreateQuery(Expression expression) => inner.Provider.CreateQuery(expression);

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
                new RecordingQueryable<TElement>(inner.Provider.CreateQuery<TElement>(expression), log);

            public object CreateQueryObject(Expression expression) => CreateQuery(expression);

            public object Execute(Expression expression)
            {
                log.Add(expression.ToString());

                return inner.Provider.Execute(expression);
            }

            public TResult Execute<TResult>(Expression expression)
            {
                log.Add(expression.ToString());

                return inner.Provider.Execute<TResult>(expression);
            }
        }

        /// <summary>
        /// Records how many times it is enumerated, so a test can tell composing a view from taking a
        /// total. Deliberately not an <see cref="ICollection{T}" />, which LINQ counts without walking.
        /// </summary>
        class CountingSequence : IEnumerable<Person>
        {
            protected readonly List<Person> Source;

            public CountingSequence(List<Person> source) => Source = source;

            public int Walks { get; private set; }

            public IEnumerator<Person> GetEnumerator()
            {
                Walks++;

                return Source.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>The same, but a collection, so its count is free.</summary>
        sealed class CountingCollection : CountingSequence, ICollection<Person>
        {
            public CountingCollection(List<Person> source) : base(source)
            {
            }

            public int Count => Source.Count;

            public bool IsReadOnly => true;

            public void Add(Person item) => throw new NotSupportedException();

            public void Clear() => throw new NotSupportedException();

            public bool Contains(Person item) => Source.Contains(item);

            public void CopyTo(Person[] array, int arrayIndex) => Source.CopyTo(array, arrayIndex);

            public bool Remove(Person item) => throw new NotSupportedException();
        }

        /// <summary>A collection of the pre-generics kind, which LINQ also counts without walking.</summary>
        sealed class CountingLegacyCollection : CountingSequence, ICollection
        {
            public CountingLegacyCollection(List<Person> source) : base(source)
            {
            }

            public int Count => Source.Count;

            public bool IsSynchronized => false;

            public object SyncRoot => Source;

            public void CopyTo(Array array, int index) => ((ICollection)Source).CopyTo(array, index);
        }
    }
}
