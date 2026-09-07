using System;

namespace Radzen.FastGrid
{
    /// <summary>
    /// One slot of the filter menu's editor: what is in it, what to do when it changes, and what to
    /// call it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One shape for both of a <c>Between</c>'s bounds, because a range's upper editor is its lower
    /// editor with a different value in it. Giving them two entry points is how they drift.
    /// </para>
    /// <para>
    /// A struct, and §3 is why - the panel builds one or two of these per open, and a class here would
    /// be an allocation buying nothing three fields on the stack do not.
    /// </para>
    /// </remarks>
    internal readonly struct FilterEditor
    {
        internal FilterEditor(object? value, Action<object?> set, string ariaLabel)
        {
            Value = value;
            Set = set;
            AriaLabel = ariaLabel;
        }

        /// <summary>What the slot currently holds, in whatever type the column filters by.</summary>
        internal object? Value { get; }

        /// <summary>
        /// What to call when the slot changes. Boxed once per change, which is where §3's rule 5 says a
        /// box is allowed to be: this runs when a person edits a control, not per row and not per cell.
        /// </summary>
        internal Action<object?> Set { get; }

        /// <summary>What a screen reader calls the slot.</summary>
        internal string AriaLabel { get; }
    }
}
