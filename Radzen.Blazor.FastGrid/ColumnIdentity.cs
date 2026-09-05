using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// How a grid tells one column from another: by the <c>UniqueID</c> it was given, and by the member
    /// it displays where it was given none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One thing asks that question - what a column's stored width, order, visibility and filter are
    /// restored onto - and before this it borrowed the column's <c>SortPath</c> to ask it. Two
    /// consequences, both recorded in §10b and neither fixable by a better lookup: a column displaying
    /// <c>Last</c> and sorting by <c>First</c> shared an identity with the column displaying
    /// <c>First</c>, and a column naming no member at all had no identity, so nothing it could be
    /// dragged or resized or hidden into was ever stored.
    /// </para>
    /// <para>
    /// It is not a query path and it never becomes one. <see cref="ColumnBase{TItem}.SortPath" /> is the
    /// string a remote sort travels under, <see cref="ColumnBase{TItem}.FilterPropertyPath" /> is what an
    /// incoming <c>FilterDescriptor</c> is matched against, and both answer questions asked from outside
    /// the grid. Nothing outside the grid ever asks which column this is.
    /// </para>
    /// <para>
    /// A struct rather than a class because §3 rules out a reference per column for something a field
    /// gives, and because there is nothing to keep: it is composed from two strings the column already
    /// holds, whenever it is asked.
    /// </para>
    /// </remarks>
    public readonly struct ColumnIdentity : IEquatable<ColumnIdentity>
    {
        ColumnIdentity(string name, bool declared)
        {
            Name = name;
            IsDeclared = declared;
        }

        /// <summary>
        /// What the column is called, or <c>null</c> when nothing names it - which is a template column
        /// declaring neither a <c>UniqueID</c> nor a sort, and a column over a computed expression
        /// declaring no <c>UniqueID</c>. Such a column persists nothing.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Whether <see cref="Name" /> came from the column's <c>UniqueID</c> parameter rather than from
        /// the member it displays. Only the advice in a collision message depends on it.
        /// </summary>
        public bool IsDeclared { get; }

        /// <summary>
        /// Whether anything names this column. Equivalent to a non-empty <see cref="Name" />, because
        /// <see cref="Of" /> is the only way to build one and it never produces the empty string - which
        /// is what lets a caller needing the name pattern-match on it and a caller needing only the
        /// question ask this.
        /// </summary>
        public bool HasName => Name is not null;

        /// <summary>
        /// Composes the two, with the declaration winning. An empty string is not a declaration: a
        /// <c>UniqueID</c> bound to a value that has not arrived yet must fall back rather than name
        /// every such column the same thing.
        /// </summary>
        internal static ColumnIdentity Of(string? declared, string? derived) =>
            declared is { Length: > 0 }
                ? new ColumnIdentity(declared, declared: true)
                : derived is { Length: > 0 }
                    ? new ColumnIdentity(derived, declared: false)
                    : default;

        /// <summary>
        /// Whether two columns carrying these identities cannot be told apart.
        /// </summary>
        /// <remarks>
        /// The question the grid actually asks, rather than equality, and the difference is the whole
        /// reason it is spelled this way: <em>two columns that name nothing are not a collision</em>.
        /// They both persist nothing, which is a different thing from both persisting under one key, and
        /// an <c>Equals</c> comparing two nulls would answer true and stop a perfectly ordinary grid of
        /// template columns from rendering.
        /// </remarks>
        internal bool Collides(ColumnIdentity other) => HasName && Equals(other);

        /// <summary>
        /// What to tell an author whose two columns answer to one name, given a short label for each.
        /// </summary>
        /// <remarks>
        /// Here rather than at the throw because this is the only place that knows what declared and
        /// derived mean, and because a message assembled here can be asserted without standing up a
        /// grid - which is what lets the test for it fail when the advice is wrong rather than only when
        /// it is missing.
        /// </remarks>
        internal static string CollisionMessage(
            ColumnIdentity first, string firstLabel, ColumnIdentity second, string secondLabel)
        {
            var how = (first.IsDeclared, second.IsDeclared) switch
            {
                (true, true) => "Both declare it.",
                (false, false) => "Neither declares a UniqueID, so both derived one from the member they display.",
                _ => "One declares it and the other derived it from the member it displays.",
            };

            return $"{firstLabel} and {secondLabel} share the column identity \"{first.Name}\". {how} " +
                "A column's identity is what its stored width, order, visibility and filter are restored " +
                "onto, so two columns cannot share one - the second column's state would be restored " +
                "onto the first. Declare a distinct UniqueID on one of them.";
        }

        /// <inheritdoc />
        /// <remarks>
        /// The name and nothing else. <see cref="IsDeclared" /> is where the name came from rather than
        /// part of it, and two columns answering to one name are the same identity however each of them
        /// arrived at it - which is the whole point of the check. Comparing it here would have made
        /// <see cref="Collides" /> and this disagree, on a type whose only job is to say whether two
        /// columns are the same one.
        /// </remarks>
        public bool Equals(ColumnIdentity other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ColumnIdentity other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() =>
            Name is null ? 0 : StringComparer.Ordinal.GetHashCode(Name);

        /// <summary>Whether two columns answer to the same name.</summary>
        public static bool operator ==(ColumnIdentity left, ColumnIdentity right) => left.Equals(right);

        /// <summary>Whether two columns answer to different names.</summary>
        public static bool operator !=(ColumnIdentity left, ColumnIdentity right) => !left.Equals(right);

        /// <summary>The name, or the empty string where nothing names the column.</summary>
        public override string ToString() => Name ?? string.Empty;
    }
}
