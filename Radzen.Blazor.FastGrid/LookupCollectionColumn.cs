using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column that lists names and carries ids: the row holds <c>BrandIds</c> and the cell shows
    /// "Acme, Globex".
    /// </summary>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TKey">The type of the ids the row carries.</typeparam>
    public sealed class LookupCollectionColumn<TItem, TKey> : ColumnBase<TItem>
    {
        /// <summary>The ids this column resolves.</summary>
        [Parameter, EditorRequired]
        public Expression<Func<TItem, IEnumerable<TKey>>> Property { get; set; } = default!;

        /// <summary>Where the names come from.</summary>
        [Parameter, EditorRequired] public FastGridLookup<TKey> Lookup { get; set; } = default!;

        /// <summary>What separates the names in the cell.</summary>
        [Parameter] public string Separator { get; set; } = ", ";

        /// <summary>What to sort by. A collection cannot be ordered, so without it the column is not sortable.</summary>
        [Parameter] public FastGridSort<TItem>? SortBy { get; set; }

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
        {
        }
    }
}
