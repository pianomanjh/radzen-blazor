using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;

namespace Radzen.FastGrid
{
    /// <summary>
    /// How to filter rows by a column whose key type the column itself does not carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter counterpart of <see cref="FastGridSort{TItem}" />, and it exists for the same reason.
    /// <see cref="PropertyColumn{TItem, TProp}" /> needs none of this: its key is <c>TProp</c>, so it
    /// composes its filter through an ordinary generic call. <see cref="TemplateColumn{TItem}" /> has no
    /// expression at all - and until this, nothing to be given one by, so
    /// <c>ColumnBase.CanFilter</c> - which is <c>Filterable &amp;&amp; FilterPropertyPath is not
    /// null</c> - answered false for one by construction. It could be told how to sort and not how to
    /// filter, which was an asymmetry rather than a decision.
    /// </para>
    /// <para>
    /// <strong>The key's type is captured, never recovered.</strong> Saying it as
    /// <c>Expression&lt;Func&lt;TItem, object&gt;&gt;</c> would erase <c>TKey</c> and leave the filter
    /// to reflect it back out of the tree, which is slow and is the one thing an ahead-of-time compiler
    /// cannot do. <see cref="By" /> is generic, and the delegates it builds close over <c>TKey</c>
    /// while it is still known; everything afterwards is an ordinary call.
    /// </para>
    /// <para>
    /// Both routes are built, because the grid has two: an expression for a provider to translate, and a
    /// delegate for a source that is already in memory. The delegate's selector is compiled on first
    /// use, so a grid over a queryable never pays for one.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <example>
    /// <code>
    /// &lt;TemplateColumn TItem="Order" Title="Customer"
    ///                 FilterBy="@(FastGridFilterBy&lt;Order&gt;.By(o =&gt; o.Customer.Name))"&gt;
    /// </code>
    /// </example>
    public sealed class FastGridFilterBy<TItem>
    {
        readonly Func<FastGridFilter, FilterCaseSensitivity, bool, Expression<Func<TItem, bool>>?> apply;
        readonly Func<FastGridFilter, FilterCaseSensitivity, Func<TItem, bool>?> applyInMemory;
        readonly Func<IQueryable<TItem>, IQueryable> distinct;

        FastGridFilterBy(
            Func<FastGridFilter, FilterCaseSensitivity, bool, Expression<Func<TItem, bool>>?> apply,
            Func<FastGridFilter, FilterCaseSensitivity, Func<TItem, bool>?> applyInMemory,
            Func<IQueryable<TItem>, IQueryable> distinct,
            Type propertyType,
            string? path)
        {
            this.apply = apply;
            this.applyInMemory = applyInMemory;
            this.distinct = distinct;

            PropertyType = propertyType;
            Path = path;
        }

        /// <summary>Filters by the given key.</summary>
        /// <remarks>
        /// Which operator compares it, and against what, is the reader's to decide through the filter
        /// menu - so there is no operator here. What this fixes is the <em>key</em>.
        /// </remarks>
        /// <typeparam name="TKey">The key's type, captured here so nothing has to recover it later.</typeparam>
        /// <param name="key">The key to filter by.</param>
        /// <returns>A carrier the column hands its filter to.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key" /> is null.</exception>
        [SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
            Justification = "Naming the row type at the call site is the point: FastGridFilterBy<Order>.By(o => o.Name) both fixes TItem and lets the lambda's parameter be inferred, which a non-generic factory would force the caller to write out.")]
        public static FastGridFilterBy<TItem> By<TKey>(Expression<Func<TItem, TKey>> key)
        {
            ArgumentNullException.ThrowIfNull(key);

            // Compiled on first use and then kept, for the reason FastGridSort gives: a grid over a
            // queryable never filters in memory, and a compile is about 250 us - and, under Native AOT,
            // an interpreted lambda rather than emitted code. The closure holds it, so it lives exactly
            // as long as this carrier does.
            Func<TItem, TKey>? compiled = null;
            Func<TItem, TKey> Selector() => compiled ??= key.Compile();

            return new FastGridFilterBy<TItem>(
                (filter, caseSensitivity, inMemory) =>
                    FilterExpression<TItem, TKey>.For(key, filter, caseSensitivity, inMemory),
                (filter, caseSensitivity) =>
                    FilterExpression<TItem, TKey>.PredicateFor(Selector(), filter, caseSensitivity),
                // Queryable.Distinct by its full name, not the extension-method form. Radzen's own
                // Distinct(this IQueryable) is non-generic, C# prefers a non-generic candidate to a
                // generic one, and it would therefore win - sending this typed projection through the
                // reflective distinct and composing Cast nodes a provider then has to translate.
                source => Queryable.Distinct(source.Select(key)),
                typeof(TKey),
                PropertyPathResolver.For(key));
        }

        /// <summary>
        /// The dotted path of the key, when it is a plain member chain, and null when it is computed.
        /// </summary>
        /// <remarks>
        /// What the column reports as its <c>FilterPropertyPath</c>: the name a <c>LoadData</c>
        /// descriptor travels under, and the key settings persist the filter by. A computed key has no
        /// path, so a column filtering by one has nothing to send a server or to write down - and, since
        /// <c>CanFilter</c> reads the path, such a column does not filter at all. That is stricter than
        /// <see cref="FastGridSort{TItem}" />, where a computed key still sorts in memory, and it is
        /// stricter for a reason: a filter has a check-box list and a menu to populate, both of which
        /// are keyed by the path.
        /// </remarks>
        public string? Path { get; }

        /// <summary>
        /// The key's type, which is what the filter editor and the operators offered are chosen from.
        /// </summary>
        public Type PropertyType { get; }

        internal Expression<Func<TItem, bool>>? Apply(FastGridFilter filter,
            FilterCaseSensitivity caseSensitivity, bool inMemory) =>
            apply(filter, caseSensitivity, inMemory);

        internal Func<TItem, bool>? ApplyInMemory(FastGridFilter filter,
            FilterCaseSensitivity caseSensitivity) =>
            applyInMemory(filter, caseSensitivity);

        internal IQueryable Distinct(IQueryable<TItem> source) => distinct(source);
    }
}
