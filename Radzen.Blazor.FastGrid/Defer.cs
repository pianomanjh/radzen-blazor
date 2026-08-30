using System;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Renders its content one step later in the render pass than its siblings.
    /// </summary>
    /// <remarks>
    /// The grid needs its columns before it can draw a header or any rows, but child components only
    /// register while the renderer walks them - after the grid's own render method has returned. Placing
    /// the table inside a Defer that sits after the column content means the table is built once the
    /// columns have registered themselves, without a throwaway first render.
    /// </remarks>
    public sealed class Defer : ComponentBase
    {
        /// <summary>The deferred content.</summary>
        [Parameter] public RenderFragment? ChildContent { get; set; }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.AddContent(0, ChildContent);
        }
    }
}
