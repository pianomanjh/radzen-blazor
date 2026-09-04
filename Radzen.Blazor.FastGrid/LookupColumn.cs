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
            // What the names were asked for. Reload moves it on, and an answer that arrives against an
            // older one is about a lookup nobody is showing any more.
            var asked = generation;

            // Cleared on every way out - the answer, the throw, and the drop that overtook it - or the
            // auto-fit this defers would be owed forever and never run.
            try
            {
                var fetched = await Lookup.FetchAsync(executor, cancellationToken);

                if (generation == asked)
                {
                    // A lookup that answers with nothing has no names, which is not the same as not
                    // having been asked: left null it would go back on the queue for an answer it has
                    // already given, and each redraw would ask again.
                    names = fetched ?? Unresolved;
                }

                // Redraw either way: when the answer stands, to show it, and when it does not, because
                // the render is what puts this column back on the queue.
                return true;
            }
            catch (OperationCanceledException)
            {
                // The grid is going away. Nothing will render, and nothing needs to.
                return false;
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Every provider throws its own, and a narrow catch here would be a catch for one of
                // them. The rows are drawn and correct and only the names are missing, so the grid
                // stays up - and resolves to no names, which draws every id. That is what a missing
                // entry already draws, and for the same reason: a column of blanks would be a fault
                // nobody can see. Reload is what tries again.
                if (generation == asked)
                {
                    names = Unresolved;
                }

                return true;
            }
            finally
            {
                // Cleared on every way out - the answer, the throw, and the drop that overtook it - or
                // the auto-fit this defers would be owed forever and never run. A drop that overtook
                // it leaves the names still missing, and the column asks again itself: waiting for a
                // parameter set would be waiting on something a retained component may never get.
                outstanding = names is null;

                if (outstanding)
                {
                    Grid?.QueueLookup(this);
                }
            }
        }

        /// <inheritdoc />
        internal override void DropLookup()
        {
            names = null;
            generation++;

            // Straight away rather than on the next parameter set, for the same reason: a Map is
            // resolved again here and now, and a Query goes back on the queue. A fetch still in
            // flight is left alone - its answer is against the old generation and is discarded, and
            // it re-queues itself on the way out.
            EnsureLookup();
        }

        int generation;

        static readonly IReadOnlyDictionary<TKey, string> Unresolved = LookupNames.None<TKey>();

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
