using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Holds each materialization open until the test releases it, so a test can see what a grid draws
    /// while a query is still in flight - which for a lookup column is the only render its cells are
    /// blank in.
    /// </summary>
    sealed class GatedLookupExecutor : IFastGridQueryExecutor
    {
        /// <summary>The element type this executor answers immediately rather than holding open.</summary>
        /// <remarks>
        /// A grid whose own data is a queryable loads through here too, and gating that as well leaves
        /// a test unable to tell which query it is waiting on.
        /// </remarks>
        public Type PassThrough { get; set; }

        /// <summary>What the next held-open materialization throws instead of answering.</summary>
        public Exception Fails { get; set; }

        /// <summary>
        /// How many materializations are held open before the rest answer at once. A test that has to
        /// see something happen after a query cannot wait for it while the query is held: bUnit
        /// re-checks an assertion on renders, and neither a query starting nor a fit being measured is
        /// one.
        /// </summary>
        public int Holds { get; set; } = int.MaxValue;

        public Gate Pending { get; private set; }

        public Type LastElementType { get; private set; }

        public int Materializations { get; private set; }

        public bool IsSupported<T>(IQueryable<T> queryable) => true;

        public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
            => Task.FromResult(queryable.Count());

        public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
        {
            if (typeof(T) == PassThrough)
            {
                return Task.FromResult(queryable.ToList());
            }

            LastElementType = queryable.ElementType;
            Materializations++;

            if (Materializations > Holds)
            {
                return Fails is null
                    ? Task.FromResult(queryable.ToList())
                    : Task.FromException<List<T>>(Fails);
            }

            var gate = new Gate();

            Pending = gate;

            return gate.Source.Task.ContinueWith(
                _ => Fails is null ? queryable.ToList() : throw Fails,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public sealed class Gate
        {
            public TaskCompletionSource<bool> Source { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release() => Source.TrySetResult(true);
        }
    }
}
