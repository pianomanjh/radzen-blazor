using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Counts and materializes a bound <see cref="IQueryable{T}" /> asynchronously, so the grid does not
    /// block the circuit's thread on database I/O.
    /// </summary>
    /// <remarks>
    /// The grid uses <see cref="AsyncEnumerableQueryExecutor" /> unless one is registered in the service
    /// provider, so a provider that streams through <see cref="IAsyncEnumerable{T}" /> - Entity Framework
    /// Core among them - needs no registration at all. Register an implementation for a provider that
    /// executes asynchronously by some other route.
    /// </remarks>
    public interface IFastGridQueryExecutor
    {
        /// <summary>
        /// Whether this executor can run <paramref name="queryable" /> asynchronously. False sends the
        /// grid down the synchronous path unchanged.
        /// </summary>
        bool IsSupported<T>(IQueryable<T> queryable);

        /// <summary>Asynchronously counts the elements of <paramref name="queryable" />.</summary>
        Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default);

        /// <summary>Asynchronously materializes <paramref name="queryable" /> into a list.</summary>
        Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default);

        /// <summary>
        /// The query as a sequence that can be read one row at a time, or null when this executor cannot
        /// offer one. Nothing in the grid calls this: it exists for a caller that wants the rows without
        /// a list to hold them, which is what a streamed export is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A default member rather than a new one, and that is the whole reason it can exist:
        /// <see cref="IsSupported{T}" /> is already the same cast, so every implementation an
        /// application has already written keeps compiling and answers correctly without being touched.
        /// </para>
        /// <para>
        /// <strong>The enumeration owns the provider for as long as it runs.</strong> Unlike
        /// <see cref="ToListAsync{T}" /> this takes no gate, because a gate held across a caller's
        /// <c>await foreach</c> is a gate held for as long as the caller cares to take, and an Entity
        /// Framework <c>DbContext</c> deadlocked behind one is worse than the concurrent use it would
        /// prevent. Read it to the end, and do not read it while the grid is loading.
        /// </para>
        /// </remarks>
        IAsyncEnumerable<T>? AsAsyncEnumerable<T>(IQueryable<T> queryable) => queryable as IAsyncEnumerable<T>;
    }

    /// <summary>
    /// The built-in <see cref="IFastGridQueryExecutor" />, for providers that expose
    /// <see cref="IAsyncEnumerable{T}" />.
    /// </summary>
    /// <remarks>
    /// Counting composes <c>GroupBy(x =&gt; 1).Select(g =&gt; g.Count())</c> so the aggregate stays a
    /// sequence the provider can stream asynchronously; providers translate it to a plain COUNT.
    /// Operations are serialized per <see cref="IQueryProvider" /> instance because queries created by one
    /// Entity Framework <c>DbContext</c> share a provider that does not allow concurrent use.
    /// <para>
    /// This mirrors <c>Radzen.Blazor</c>'s own built-in executor rather than calling it: that one is
    /// internal to <c>Radzen.Blazor</c>, and a separate package reaching into it would tie this grid's
    /// async path to an implementation detail of the package it depends on.
    /// </para>
    /// </remarks>
    sealed class AsyncEnumerableQueryExecutor : IFastGridQueryExecutor
    {
        internal static readonly AsyncEnumerableQueryExecutor Instance = new();

        static readonly ConditionalWeakTable<IQueryProvider, SemaphoreSlim> providerGates = new();

        /// <inheritdoc />
        public bool IsSupported<T>(IQueryable<T> queryable) => queryable is IAsyncEnumerable<T>;

        /// <inheritdoc />
        public async Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
        {
            var gate = GateFor(queryable);

            await gate.WaitAsync(cancellationToken);

            try
            {
                var counts = queryable.GroupBy(item => 1).Select(group => group.Count());

                if (counts is IAsyncEnumerable<int> asyncCounts)
                {
                    await foreach (var count in asyncCounts.WithCancellation(cancellationToken))
                    {
                        return count;
                    }

                    return 0;
                }

                return queryable.Count();
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
        {
            var gate = GateFor(queryable);

            await gate.WaitAsync(cancellationToken);

            try
            {
                if (queryable is not IAsyncEnumerable<T> asyncItems)
                {
                    return queryable.ToList();
                }

                var items = new List<T>();

                await foreach (var item in asyncItems.WithCancellation(cancellationToken))
                {
                    items.Add(item);
                }

                return items;
            }
            finally
            {
                gate.Release();
            }
        }

        static SemaphoreSlim GateFor<T>(IQueryable<T> queryable) =>
            providerGates.GetValue(queryable.Provider, static _ => new SemaphoreSlim(1, 1));
    }
}
