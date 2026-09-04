using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Radzen.FastGrid
{
    /// <summary>One pass of the table being drawn, and what has already been worked out during it.</summary>
    /// <remarks>
    /// Drawing the table asks what is filtered more than once: the pager counts, the body enumerates,
    /// and a grid with a pager above and below counts twice. None of it can change while the table is
    /// being written, and rebuilding the descriptors means rebuilding the filter expression tree with
    /// them - so both are worked out once for the render and dropped again after. A cache that outlived
    /// the render would have to be invalidated by every path that touches a filter.
    /// <para>
    /// That much was always true. What this type changes is that the memo is a value rather than four
    /// fields on the grid and a flag saying whether they mean anything. As fields it was ambient: five
    /// methods changed behaviour depending on whether a render was in progress and not one of them said
    /// so in its signature, so a caller could not tell which of two answers it was about to get and a
    /// new caller inherited a rule nobody could see. Everything that consults it now names it.
    /// </para>
    /// <para>
    /// A struct, mutated in place through the field that holds it. The memo is written by the first call
    /// of a pass and read by the rest, so it has to be the same storage each time; and §3 is why it is
    /// not a class, since a reference would be an allocation per render buying nothing that a field
    /// does not already give.
    /// </para>
    /// </remarks>
    internal struct DrawPass<TItem>
    {
        /// <summary>Whether a render is in progress, and so whether anything below may be reused.</summary>
        /// <remarks>
        /// Settable only by <see cref="Begin" />, which is what makes a pass all-or-nothing. The readers
        /// below do not test it - they test whether anything was remembered - so a pass closed by
        /// clearing this flag alone would go on answering with a stale composition to any caller holding
        /// the same source instance. Closing a pass is <c>pass = default</c> and there is deliberately
        /// no other way to write it.
        /// </remarks>
        internal bool Drawing { get; private set; }

        /// <summary>
        /// The columns' filters as descriptors, worked out once at the start of the pass. Null both when
        /// nothing is filtered and when filtering is switched off, which is why nothing downstream needs
        /// to ask about <c>AllowFiltering</c> a second time.
        /// </summary>
        internal List<FilterDescriptor>? Filters { get; private set; }

        IEnumerable<TItem>? composed;
        IEnumerable<TItem>? composedOf;
        int? total;

        /// <summary>Opens a pass over the filters that hold for the whole of it.</summary>
        internal static DrawPass<TItem> Begin(List<FilterDescriptor>? filters) =>
            new DrawPass<TItem> { Drawing = true, Filters = filters };

        /// <summary>
        /// Whether this pass has already composed that source, and if so what it composed it to.
        /// </summary>
        /// <remarks>
        /// Keyed on the source instance rather than on what it holds: within one pass the same instance
        /// composes to the same thing, and a different one has to be composed again. Outside a pass the
        /// answer is always no, and without a second test for it: <c>Keep</c> is where
        /// <see cref="Drawing" /> is consulted, so there is nothing here to have been remembered.
        /// </remarks>
        internal readonly bool Reuses(IEnumerable<TItem> data,
            [NotNullWhen(true)] out IEnumerable<TItem>? reused)
        {
            reused = composed;

            return ReferenceEquals(composedOf, data) && reused is not null;
        }

        /// <summary>Records a composition for the rest of the pass, and answers it.</summary>
        /// <remarks>
        /// The one place <see cref="Drawing" /> is consulted, and deliberately: outside a pass nothing
        /// is written, so nothing can be read back and nothing is held on to. Gating the readers as well
        /// looks safer and is not - it is a branch no caller can reach with a different answer, which is
        /// a claim no test can check.
        /// </remarks>
        internal IEnumerable<TItem> Keep(IEnumerable<TItem> data, IEnumerable<TItem> result)
        {
            if (Drawing)
            {
                composedOf = data;
                composed = result;
            }

            return result;
        }

        /// <summary>Whether this pass has already counted, and if so what it counted.</summary>
        internal readonly bool Counted(out int counted)
        {
            counted = total ?? 0;

            return total is not null;
        }

        /// <summary>Records a total for the rest of the pass, and answers it.</summary>
        internal int Keep(int counted)
        {
            if (Drawing)
            {
                total = counted;
            }

            return counted;
        }
    }
}
