using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// What a cell's own content can say to the grid it sits in.
    /// </summary>
    public static class FastGridCell
    {
        /// <summary>
        /// Marks content whose clicks belong to it rather than to the row around it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Rarely needed.</strong> Row clicks are raised from one listener on the body, and
        /// that listener already leaves interactive content alone - buttons, links, form controls, and
        /// the ARIA roles that stand in for them. This is for the rest: a wrapper that swallows a click
        /// without being any of those, which is what a template column tends to draw.
        /// </para>
        /// <para>
        /// <c>@onclick:stopPropagation</c> does not do this and cannot. Blazor enforces it from its own
        /// delegator, which is above the grid's listener, so the row click has already been raised by
        /// the time Blazor could stop it.
        /// </para>
        /// <code>
        /// &lt;TemplateColumn TItem="Order"&gt;
        ///     &lt;Template&gt;
        ///         &lt;div @attributes="FastGridCell.NoRowClick"&gt;...&lt;/div&gt;
        ///     &lt;/Template&gt;
        /// &lt;/TemplateColumn&gt;
        /// </code>
        /// </remarks>
        public static IReadOnlyDictionary<string, object> NoRowClick { get; } =
            new Dictionary<string, object> { [BrowserContract.NoRowClickAttribute] = "" };

        /// <summary>The attribute name, for a caller writing it into a render tree by hand.</summary>
        public const string NoRowClickAttributeName = BrowserContract.NoRowClickAttribute;
    }
}
