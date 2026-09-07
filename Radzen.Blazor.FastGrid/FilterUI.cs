namespace Radzen.FastGrid
{
    /// <summary>
    /// Where a column's filter is authored: in a row of boxes under the headers, or in a menu the
    /// header's own icon opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This grid's own enum, and §31 argued why rather than reusing
    /// <see cref="Radzen.FilterMode" />. That is <c>Radzen.Blazor</c>'s, it has four values of which
    /// this grid implements two, and a fifth cannot be added without editing a file this branch does
    /// not own. Redefining <c>SimpleWithMenu</c> or <c>Advanced</c> - both of which have documented
    /// upstream meanings - would break the one promise that matters for a near-drop-in, that a reader
    /// can carry knowledge across. <strong>Where the editor lives is ours; what the editor is stays
    /// upstream's vocabulary.</strong>
    /// </para>
    /// <para>
    /// Grid-wide, while <c>FilterMode</c> stays per-column overridable. §31 records that as a
    /// separation which reads cleanly and has never been used; §35 is the first thing that could have
    /// found out and did not.
    /// </para>
    /// </remarks>
    public enum FilterUI
    {
        /// <summary>
        /// A second header row of filter controls.
        /// </summary>
        /// <remarks>
        /// The grid's original behaviour, and its default until the menu was built and looked at. It is
        /// still first in this enum - the numbering is public and reordering it would move every
        /// serialized value - but the parameter now initialises to <see cref="Menu" />.
        /// </remarks>
        Row,

        /// <summary>
        /// A filter icon on each filterable header, opening one context-aware menu. The default.
        /// </summary>
        /// <remarks>
        /// <strong>There is no filter row under this.</strong> Not a hidden one and not an empty one:
        /// two places to author one filter would be two places that have to agree, which is §10b's
        /// recurring finding with the arrow reversed.
        /// </remarks>
        Menu,
    }
}
