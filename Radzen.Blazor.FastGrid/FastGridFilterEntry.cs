using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// One entry of a check-box list on a nullable column: a value the column filters by, or the
    /// absence of one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §36. A raw null in a list control draws as an empty row that says nothing, which is why
    /// <c>FilterLookup</c> stripped nulls out of the values it offered - and why a nullable column
    /// could not ask for the rows with no value in them, a question §31 named as one a narrower model
    /// could not express. This is what the list is bound to instead: the blank draws the grid's
    /// <c>BlankFilterText</c>, every other entry draws its value, and the column maps an entry back to
    /// the value it stands for when the selection becomes a filter.
    /// </para>
    /// <para>
    /// <strong>Every entry, not only the blank - and the browser is what said so.</strong> The first
    /// build put a lone sentinel among the raw values, which reads as the smaller change and is a
    /// broken one: <c>DropDownBase</c> infers a multiple selection's element type from the first item
    /// in <c>Data</c> and casts the whole selection to it, so a blank at the head of the list made
    /// upstream cast an <c>int</c> to the sentinel's type and took the circuit down on the second tick.
    /// <see cref="LookupColumnBase{TItem, TKey}" /> never meets this because every one of its entries
    /// is a <c>FastGridLookupEntry</c> including the blank. A homogeneous list is the whole of §14's
    /// shape and half of it was not enough.
    /// </para>
    /// <para>
    /// Wrapped only where the column is nullable. A column that cannot hold a blank offers the raw
    /// values it always did, so nothing that has no absence to express pays for one.
    /// </para>
    /// </remarks>
    public sealed class FastGridFilterEntry
    {
        readonly string text;

        internal FastGridFilterEntry(object? value, string text)
        {
            Value = value;
            this.text = text;
        }

        /// <summary>The value this entry stands for, or null for the blank.</summary>
        public object? Value { get; }

        /// <summary>The word the list draws for it.</summary>
        public override string ToString() => text;
    }
}
