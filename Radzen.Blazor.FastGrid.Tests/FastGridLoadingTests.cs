using System;
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
    /// The loading indicator, drawn from the grid's own <c>IsLoading</c> rather than from a parameter
    /// the application has to keep in step.
    /// </summary>
    /// <remarks>
    /// RadzenDataGrid needs <c>IsLoading=@isLoading</c> passed in and reset on every path, including the
    /// failing one. This grid already knows, because it owns the load - so the indicator has nothing to
    /// wire up and nothing to leave stuck on.
    /// </remarks>
    public class FastGridLoadingTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, IEnumerable<Person> data,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null)
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

        static (IRenderedComponent<RadzenFastGrid<Person>> Grid, GatedExecutor Executor) Loading(
            TestContext ctx, Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null)
        {
            var executor = new GatedExecutor();

            ctx.Services.AddSingleton<IAsyncQueryExecutor>(executor);

            return (Render(ctx, People.Many(6).AsQueryable(), extra), executor);
        }

        // An in-memory grid never loads asynchronously, so it never covers itself.
        [Fact]
        public void NothingIsDrawnWhenNothingIsLoading()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, People.Many(3));

            Assert.False(cut.Instance.IsLoading);
            Assert.Empty(cut.FindAll(".rz-datatable-loading"));
            Assert.Empty(cut.FindAll(".rz-datatable-loading-content"));
        }

        [Fact]
        public void TheScrimAndTheSpinnerAppearWhileALoadIsInFlight()
        {
            using var ctx = new TestContext();
            var (cut, executor) = Loading(ctx);

            Assert.True(cut.Instance.IsLoading);
            Assert.Single(cut.FindAll(".rz-datatable-loading"));
            Assert.Single(cut.FindAll(".rz-datatable-loading-content i.rzi-circle-o-notch"));

            executor.Pending.Release();

            cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".rz-datatable-loading")));
        }

        // The pair is what the themes style: the scrim carries the dimming, the content the spinner.
        // One without the other is a spinner with no backdrop, or a backdrop with nothing in it.
        [Fact]
        public void BothElementsAreDrawnInsideTheGridsOwnPositionedWrapper()
        {
            using var ctx = new TestContext();
            var (cut, _) = Loading(ctx);

            var children = cut.Find("div.rz-datatable").Children.Select(c => c.ClassName).ToArray();

            Assert.Contains("rz-datatable-loading", children);
            Assert.Contains("rz-datatable-loading-content", children);
        }

        [Fact]
        public void ShowLoadingIndicatorFalseLeavesTheGridUncovered()
        {
            using var ctx = new TestContext();
            var (cut, _) = Loading(ctx, p => p.Add(g => g.ShowLoadingIndicator, false));

            Assert.True(cut.Instance.IsLoading);
            Assert.Empty(cut.FindAll(".rz-datatable-loading"));
            Assert.Empty(cut.FindAll(".rz-datatable-loading-content"));
        }

        [Fact]
        public void ALoadingTemplateReplacesTheSpinnerButNotTheScrim()
        {
            using var ctx = new TestContext();
            var (cut, _) = Loading(ctx, p =>
                p.Add<RenderFragment>(g => g.LoadingTemplate, builder => builder.AddContent(0, "fetching")));

            Assert.Single(cut.FindAll(".rz-datatable-loading"));
            Assert.Empty(cut.FindAll(".rz-datatable-loading-content i"));
            Assert.Equal("fetching", cut.Find(".rz-datatable-loading-content").TextContent);
        }

        // The rows stay in the tree under the scrim, which is the point of an overlay rather than a
        // replacement: a reload does not blank the grid it is reloading.
        [Fact]
        public async Task TheRowsAreStillThereUnderneath()
        {
            using var ctx = new TestContext();
            var (cut, executor) = Loading(ctx);

            executor.Pending.Release();
            cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll("tbody tr").Count));

            var reload = cut.InvokeAsync(() => cut.Instance.Reload());

            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".rz-datatable-loading")));

            Assert.Equal(6, cut.FindAll("tbody tr").Count);

            executor.Pending.Release();

            await reload;
        }

        /// <summary>Holds each materialization open until the test releases it.</summary>
        sealed class GatedExecutor : IAsyncQueryExecutor
        {
            public Gate Pending { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
                => Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
            {
                var gate = new Gate();

                Pending = gate;

                return gate.Source.Task.ContinueWith(_ => queryable.ToList(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            public sealed class Gate
            {
                public TaskCompletionSource<bool> Source { get; } =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);

                public void Release() => Source.TrySetResult(true);
            }
        }
    }
}
