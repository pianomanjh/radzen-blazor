using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Where a lookup column's names come from. A closed set of three cases: a map already in hand, a
    /// sequence already in memory, or a query the grid runs for itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provenance is one parameter of a closed type rather than three nullable ones, so the illegal
    /// combinations are unrepresentable rather than validated. The entity a lookup is built from is a
    /// type parameter of the case and not of the column, so a column is
    /// <see cref="LookupColumn{TItem, TKey}" /> whichever case supplies its names.
    /// </para>
    /// <para>
    /// <see cref="Items{TEntity}" /> takes delegates and <see cref="Query{TEntity}" /> takes expressions,
    /// deliberately: only a query composes into a provider's own tree, and an
    /// <see cref="Expression" /> in the other two would buy nothing and cost a <c>Compile</c> per grid.
    /// </para>
    /// </remarks>
    /// <typeparam name="TKey">The type of the id a row carries.</typeparam>
    public abstract record FastGridLookup<TKey>
    {
        // Nobody outside this file writes a case: the set is closed, and an unknown one would reach the
        // grid as a lookup it has no way to resolve.
        private protected FastGridLookup()
        {
        }

        /// <summary>
        /// The names, or null for a lookup the grid has to fetch before it has any. Called once per
        /// column: a resolved lookup is not resolved again because the parameter arrived as a new
        /// instance.
        /// </summary>
        internal abstract IReadOnlyDictionary<TKey, string>? Resolve();

        /// <summary>
        /// The names for a lookup <see cref="Resolve" /> answered null for. Runs after the render and
        /// through the executor, so it can neither block the circuit nor overlap the page load the same
        /// render was drawn without.
        /// </summary>
        internal virtual Task<IReadOnlyDictionary<TKey, string>?> FetchAsync(
            IFastGridQueryExecutor? executor, CancellationToken token) =>
            Task.FromResult(Resolve());

        /// <summary>Names already in hand, keyed by the id the row carries.</summary>
        /// <param name="Entries">The names, by id.</param>
        internal sealed record Map(IReadOnlyDictionary<TKey, string> Entries) : FastGridLookup<TKey>
        {
            internal override IReadOnlyDictionary<TKey, string> Resolve() => Entries;
        }

        /// <summary>Names carried by a sequence already in memory.</summary>
        /// <param name="Source">The entities holding the names.</param>
        /// <param name="Key">The id of one entity.</param>
        /// <param name="Text">The name of one entity.</param>
        /// <typeparam name="TEntity">The type holding a name and its id.</typeparam>
        internal sealed record Items<TEntity>(IEnumerable<TEntity> Source, Func<TEntity, TKey> Key,
            Func<TEntity, string> Text) : FastGridLookup<TKey>
        {
            internal override IReadOnlyDictionary<TKey, string> Resolve()
            {
                var names = LookupNames.Of<TKey>(0);

                foreach (var entity in Source)
                {
                    var key = Key(entity);

                    // A source row with no id names nothing, and a dictionary will not hold it. The
                    // "(none)" a filter offers belongs to the column, not to the source.
                    if (key is not null)
                    {
                        names[key] = Text(entity);
                    }
                }

                return names;
            }
        }

        /// <summary>Names the grid fetches for itself, once, through the query executor.</summary>
        /// <param name="Source">The query the names are read from.</param>
        /// <param name="Key">The id of one row of it.</param>
        /// <param name="Text">The name of one row of it.</param>
        /// <typeparam name="TEntity">The type holding a name and its id.</typeparam>
        /// <remarks>
        /// Two of these never compare equal, whatever the call site does: the record's members include
        /// <see cref="Expression" />s, which are a fresh object graph on every evaluation and do not
        /// override <c>Equals</c>. That costs a lookup shared by two columns one extra fetch at startup
        /// and nothing afterwards, because a column resolves its lookup once.
        /// </remarks>
        internal sealed record Query<TEntity>(IQueryable<TEntity> Source,
            Expression<Func<TEntity, TKey>> Key, Expression<Func<TEntity, string>> Text)
            : FastGridLookup<TKey>
        {
            internal override IReadOnlyDictionary<TKey, string>? Resolve() => null;

            internal override async Task<IReadOnlyDictionary<TKey, string>?> FetchAsync(
                IFastGridQueryExecutor? executor, CancellationToken token)
            {
                var projected = Source.Select(Projection());

                var rows = executor is not null && executor.IsSupported(projected)
                    ? await executor.ToListAsync(projected, token).ConfigureAwait(false)
                    : projected.ToList();

                var names = LookupNames.Of<TKey>(rows.Count);

                foreach (var row in rows)
                {
                    if (row.Key is not null)
                    {
                        names[row.Key] = row.Text;
                    }
                }

                return names;
            }

            /// <summary>
            /// The two authored expressions as one projection, so the provider sends back an id and a
            /// name per row rather than the rows themselves. Composed rather than compiled: that is
            /// what the expressions are for, and is the whole difference from <see cref="Items{T}" />.
            /// </summary>
            Expression<Func<TEntity, FastGridLookupPair<TKey>>> Projection()
            {
                var parameter = Key.Parameters[0];

                return Expression.Lambda<Func<TEntity, FastGridLookupPair<TKey>>>(
                    Expression.New(FastGridLookupPair<TKey>.Constructor, Key.Body,
                        ExpressionRebind.Onto(Text.Body, Text.Parameters[0], parameter)),
                    parameter);
            }
        }
    }

    /// <summary>The dictionary a lookup's names live in.</summary>
    /// <remarks>
    /// <see cref="Dictionary{TKey, TValue}" /> asks for a key that cannot be null, and a lookup
    /// column's key can be an <c>int?</c> - which is what makes "no category" a value a row can hold.
    /// No null key is ever stored: every writer drops them and no reader asks about one. So the
    /// constraint is stricter than the use, and saying that once here keeps the suppression off the
    /// three places that would otherwise each carry it.
    /// </remarks>
    internal static class LookupNames
    {
#pragma warning disable CS8714
        internal static Dictionary<TKey, string> Of<TKey>(int capacity) => new(capacity);

        /// <summary>No names at all - what a lookup that could not be fetched resolves to.</summary>
        internal static IReadOnlyDictionary<TKey, string> None<TKey>() => Empty<TKey>.Instance;

        static class Empty<TKey>
        {
            internal static readonly IReadOnlyDictionary<TKey, string> Instance =
                new Dictionary<TKey, string>();
        }
#pragma warning restore CS8714
    }

    /// <summary>One row of a lookup query: an id and the name it stands for.</summary>
    /// <remarks>
    /// A named type rather than an anonymous one because the projection is built rather than written,
    /// and <see cref="Expression.New(ConstructorInfo, Expression[])" /> needs a constructor to name.
    /// </remarks>
    sealed class FastGridLookupPair<TKey>(TKey key, string text)
    {
        // Captured from a typed expression rather than looked up by name: an ldtoken the compiler
        // emits, so there is nothing for a trimmer to root and nothing closed at run time.
        internal static readonly ConstructorInfo Constructor =
            ((NewExpression)((Expression<Func<FastGridLookupPair<TKey>>>)(
                () => new FastGridLookupPair<TKey>(default!, default!))).Body).Constructor!;

        internal TKey Key { get; } = key;

        internal string Text { get; } = text;
    }

    /// <summary>Builds a <see cref="FastGridLookup{TKey}" />, inferring both of its type parameters.</summary>
    /// <remarks>
    /// One factory per case rather than three overloads of one name: a lambda converts to both a
    /// delegate and an expression, so an <see cref="IQueryable{T}" /> - which is also an
    /// <see cref="IEnumerable{T}" /> - would match the in-memory overload just as well as the query one.
    /// </remarks>
    public static class FastGridLookup
    {
        /// <summary>Names already in hand, keyed by the id the row carries.</summary>
        public static FastGridLookup<TKey> Map<TKey>(IReadOnlyDictionary<TKey, string> entries) =>
            new FastGridLookup<TKey>.Map(entries);

        /// <summary>Names carried by a sequence already in memory.</summary>
        public static FastGridLookup<TKey> Items<TKey, TEntity>(IEnumerable<TEntity> source,
            Func<TEntity, TKey> key, Func<TEntity, string> text) =>
            new FastGridLookup<TKey>.Items<TEntity>(source, key, text);

        /// <summary>Names the grid fetches for itself, once, through the query executor.</summary>
        public static FastGridLookup<TKey> Query<TKey, TEntity>(IQueryable<TEntity> source,
            Expression<Func<TEntity, TKey>> key, Expression<Func<TEntity, string>> text) =>
            new FastGridLookup<TKey>.Query<TEntity>(source, key, text);
    }
}

