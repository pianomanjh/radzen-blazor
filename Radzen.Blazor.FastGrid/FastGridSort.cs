using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;

namespace Radzen.FastGrid
{
    /// <summary>
    /// How to order rows by a column whose key type the column itself does not carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PropertyColumn{TItem, TProp}" /> needs none of this: its key is <c>TProp</c>, so it
    /// orders by an ordinary generic call. The two columns that are not typed at their key do need it -
    /// a template column has no expression at all, and a collection column's key belongs to the row
    /// rather than to the element, so it cannot be a type parameter of the column.
    /// </para>
    /// <para>
    /// Both used to say it as <c>Expression&lt;Func&lt;TItem, object&gt;&gt;</c>, which loses the key's
    /// type in the markup - and the type cannot be recovered afterwards except by reflecting on the
    /// expression and closing <c>OrderBy</c> over what it finds. That is slow, and it is the one thing
    /// an ahead-of-time compiler cannot do. So instead of erasing the type and recovering it, this
    /// captures it where it is still known: <see cref="By" /> is generic, and the delegates it builds
    /// close over <c>TKey</c> at that point. Everything afterwards is an ordinary call.
    /// </para>
    /// <para>
    /// Both routes are built, because the grid has two: an expression for a provider to translate, and a
    /// delegate for a source that is already in memory. The delegate's key selector is compiled on first
    /// use, so a grid over a queryable never pays for one.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <example>
    /// <code>
    /// &lt;TemplateColumn TItem="Order" Title="Customer"
    ///                 SortBy="@(FastGridSort&lt;Order&gt;.By(o =&gt; o.Customer.Name))"&gt;
    /// </code>
    /// </example>
    public sealed class FastGridSort<TItem>
    {
        readonly Func<IQueryable<TItem>, bool, IOrderedQueryable<TItem>> order;
        readonly Func<IOrderedQueryable<TItem>, bool, IOrderedQueryable<TItem>> then;
        readonly Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> orderInMemory;
        readonly Func<IOrderedEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> thenInMemory;

        FastGridSort(
            Func<IQueryable<TItem>, bool, IOrderedQueryable<TItem>> order,
            Func<IOrderedQueryable<TItem>, bool, IOrderedQueryable<TItem>> then,
            Func<IEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> orderInMemory,
            Func<IOrderedEnumerable<TItem>, bool, IOrderedEnumerable<TItem>> thenInMemory,
            string? path)
        {
            this.order = order;
            this.then = then;
            this.orderInMemory = orderInMemory;
            this.thenInMemory = thenInMemory;

            Path = path;
        }

        /// <summary>Orders by the given key.</summary>
        /// <remarks>
        /// Ascending or descending is the grid's to decide, not this - the reader decides it by clicking
        /// - so there is no <c>ByAscending</c> here. What this fixes is the <em>key</em>.
        /// </remarks>
        /// <typeparam name="TKey">The key's type, captured here so nothing has to recover it later.</typeparam>
        /// <param name="key">The key to order by.</param>
        [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
            Justification = "Naming the row type at the call site is the point: FastGridSort<Order>.By(o => o.Name) both fixes TItem and lets the lambda's parameter be inferred, which a non-generic factory would force the caller to write out.")]
        public static FastGridSort<TItem> By<TKey>(Expression<Func<TItem, TKey>> key)
        {
            ArgumentNullException.ThrowIfNull(key);

            // Compiled on first use and then kept: a grid over a queryable never orders in memory, and a
            // compile is about 250 us - and, under Native AOT, an interpreted lambda rather than emitted
            // code. The closure holds it, so it lives exactly as long as this sort does.
            Func<TItem, TKey>? compiled = null;
            Func<TItem, TKey> Selector() => compiled ??= key.Compile();

            return new FastGridSort<TItem>(
                (source, descending) => descending ? source.OrderByDescending(key) : source.OrderBy(key),
                (source, descending) => descending ? source.ThenByDescending(key) : source.ThenBy(key),
                (source, descending) => descending
                    ? source.OrderByDescending(Selector())
                    : source.OrderBy(Selector()),
                (source, descending) => descending
                    ? source.ThenByDescending(Selector())
                    : source.ThenBy(Selector()),
                PropertyPathResolver.For(key));
        }

        /// <summary>
        /// The dotted path of the key, when it is a plain member chain, and null when it is computed.
        /// </summary>
        /// <remarks>
        /// What a <c>LoadData</c> handler receives as its <c>OrderBy</c>, and what settings persist a
        /// sort as. A computed key has no path, so a grid that sorts by one has nothing to send a server
        /// or to write down - the same as any other computed sort key.
        /// </remarks>
        public string? Path { get; }

        internal IOrderedQueryable<TItem> Apply(IQueryable<TItem> source, bool descending) =>
            order(source, descending);

        internal IOrderedQueryable<TItem> ApplyThen(IOrderedQueryable<TItem> source, bool descending) =>
            then(source, descending);

        internal IOrderedEnumerable<TItem> Apply(IEnumerable<TItem> source, bool descending) =>
            orderInMemory(source, descending);

        internal IOrderedEnumerable<TItem> ApplyThen(IOrderedEnumerable<TItem> source, bool descending) =>
            thenInMemory(source, descending);
    }
}
