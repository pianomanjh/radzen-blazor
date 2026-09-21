using System;
using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// How a grid tells one row from another: by the key it was given for them, and by the row itself
    /// where it was given none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four things ask that question - whether a row is expanded, whether it is selected, what a click
    /// toggles, and which rows a range added or removed - and before this they asked it of a
    /// <see cref="HashSet{T}" /> or an <see cref="ICollection{T}" /> with the default comparer, which
    /// for the entity types this grid is built for is reference equality. Over a source that
    /// re-materialises - <c>AsNoTracking()</c> read per render, a <c>LoadData</c> handler assigning a
    /// fresh page - none of those answers survives the next render, and each of the four has a recorded
    /// fault to show for it. §21 has them.
    /// </para>
    /// <para>
    /// It is an <see cref="IEqualityComparer{T}" /> because that is what the sets doing the asking take:
    /// handing one to a <see cref="HashSet{T}" /> is the whole of the fix at three of the four places,
    /// and a set that compares this way cannot accumulate stale instances either - <c>Add</c> keeps the
    /// entry it already has rather than putting an equal one beside it, which is what made the row
    /// expansion §10 recorded a leak in the first place.
    /// </para>
    /// <para>
    /// A row whose key is null is compared as itself. That keeps the fallback rule the same one at both
    /// levels - no key, be yourself - and it matters because a lookup row's id legitimately is null.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    internal sealed class RowIdentity<TItem> : IEqualityComparer<TItem>
    {
        readonly Func<TItem, object?> key;

        /// <summary>Names rows by the given key.</summary>
        /// <param name="key">
        /// Reads a row's key. Called rather than captured, so a component whose key parameter changes
        /// does not need this rebuilt - which matters because a key written in markup that captures
        /// anything is a new delegate on every render, and rebuilding on that would be the
        /// <c>!ReferenceEquals</c> trap §10 has four recorded participants in.
        /// </param>
        internal RowIdentity(Func<TItem, object?> key) => this.key = key;

        /// <inheritdoc />
        public bool Equals(TItem? x, TItem? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            // Both keys, or neither: a row that answers null is compared as itself, and comparing a
            // named row against an unnamed one by anything but identity would be a guess.
            return key(x) is { } left && key(y) is { } right
                ? left.Equals(right)
                : EqualityComparer<TItem>.Default.Equals(x, y);
        }

        /// <inheritdoc />
        public int GetHashCode(TItem obj) =>
            obj is null ? 0 : (key(obj) ?? (object)obj).GetHashCode();
    }
}
