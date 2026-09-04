using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column that displays a name and carries an id: the row holds <c>CategoryId</c> and the cell
    /// shows "Toys".
    /// </summary>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TKey">The type of the id the row carries.</typeparam>
    public sealed class LookupColumn<TItem, TKey> : ColumnBase<TItem>
    {
        /// <summary>The id this column resolves.</summary>
        [Parameter, EditorRequired] public Expression<Func<TItem, TKey>> Property { get; set; } = default!;

        /// <summary>Where the names come from.</summary>
        [Parameter, EditorRequired] public FastGridLookup<TKey> Lookup { get; set; } = default!;

        /// <summary>What to sort by. Without it the column is not sortable.</summary>
        [Parameter] public FastGridSort<TItem>? SortBy { get; set; }

        Expression<Func<TItem, TKey>>? property;
        Func<TItem, TKey>? key;
        IReadOnlyDictionary<TKey, string>? names;
        bool outstanding;

        /// <inheritdoc />
        protected override void OnParametersSet()
        {
            if (!PropertyPathResolver.Equivalent(property, Property))
            {
                property = Property;
                key = Property?.Compile();
            }

            EnsureLookup();

            base.OnParametersSet();
        }

        /// <summary>
        /// Resolves the lookup, once. Deliberately not on whether the parameter is the same instance:
        /// Razor rebuilds a query lookup's expressions on every render, so a cache keyed on the
        /// lookup's identity would refetch it every time - which is the defect the check-box list's own
        /// distinct scan already had. <c>Reload</c> is what drops it.
        /// </summary>
        void EnsureLookup()
        {
            if (names is not null || outstanding || Lookup is null)
            {
                return;
            }

            names = Lookup.Resolve();

            if (names is null)
            {
                outstanding = true;

                Grid?.QueueLookup(this);
            }
        }

        /// <inheritdoc />
        internal override bool LookupOutstanding => outstanding;

        /// <inheritdoc />
        internal override async Task<bool> FetchLookupAsync(IFastGridQueryExecutor? executor,
            CancellationToken cancellationToken)
        {
            // Cleared on every way out - the answer, the throw, and the return that was superseded -
            // or the auto-fit this defers would be owed forever and never run.
            try
            {
                var fetched = await Lookup.FetchAsync(executor, cancellationToken);

                // The names may have been dropped while the query ran, and writing them now would put
                // a previous source's back with nothing to clear them until the next Reload.
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                names = fetched;

                return true;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer load, which will ask again on its own render.
                return false;
            }
            finally
            {
                outstanding = false;
            }
        }

        /// <inheritdoc />
        internal override void DropLookup()
        {
            names = null;
        }

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, TextOf(item));

        /// <inheritdoc />
        public override string? CellTextOf(TItem item) => TextOf(item);

        string? TextOf(TItem item)
        {
            if (key is null || names is null)
            {
                return null;
            }

            var id = key(item);

            // A null key and a missing key are different failures. A missing one renders the id
            // because a deleted row, a narrowed lookup or a stale cache is a fault, and the id is the
            // only thing that lets anyone diagnose it. A null one is a row with no category, which is
            // an empty cell. The order matters as well as the answers: a dictionary throws when asked
            // about a null key, Nullable<T> included.
            return id is null ? null
                : names.TryGetValue(id, out var text) ? text
                : id.ToString();
        }
    }
}
