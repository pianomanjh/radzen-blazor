using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column whose cells are rendered by a template.
    /// </summary>
    /// <remarks>
    /// Measured at roughly 94 bytes per cell more than a <see cref="PropertyColumn{TItem, TProp}" />,
    /// because a template is a <see cref="RenderFragment" /> invoked per cell. That is the price of
    /// arbitrary cell content; prefer a property column where the cell is just a value.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    public sealed class TemplateColumn<TItem> : ColumnBase<TItem>
    {
        /// <summary>The content rendered for each cell.</summary>
        [Parameter] public RenderFragment<TItem>? Template { get; set; }

        /// <summary>
        /// How this column sorts.
        /// </summary>
        /// <remarks>
        /// A template has no expression of its own to order by, so it has to be told - and being told
        /// as a <see cref="FastGridSort{TItem}" /> rather than as a path is what makes the column able
        /// to sort at all. <see cref="SortProperty" /> names a path for a server to sort by, and a
        /// server is the only thing that ever sorted by it.
        /// </remarks>
        [Parameter] public FastGridSort<TItem>? SortBy { get; set; }

        /// <summary>
        /// The property path this column persists by, and sorts by when the sorting is done by a
        /// <c>LoadData</c> handler rather than by the grid.
        /// </summary>
        /// <remarks>
        /// On its own this sorts nothing locally, and used not to say so: the header was clickable, the
        /// sort was recorded, the indicator was drawn, and the rows did not move, because a path is not
        /// something the grid can order by without reaching members by name. Set <see cref="SortBy" />
        /// for a grid that sorts its own rows; <see cref="SortBy" />'s own path is used for
        /// <c>LoadData</c> when both are set, so one column can do both with one declaration.
        /// </remarks>
        [Parameter] public string? SortProperty { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// Not the base's answer, which is the sort's path alone: this column can name a path with no
        /// sort behind it, and a <c>LoadData</c> grid orders by exactly that.
        /// </remarks>
        public override string? SortPath => SortBy?.Path ?? SortProperty;


        /// <inheritdoc />
        /// <remarks>
        /// A path alone still makes the column sortable, because a <c>LoadData</c> grid sorts by it.
        /// </remarks>
        public override bool CanSort => Sortable && (SortBy is not null || SortProperty is not null);

        /// <inheritdoc />
        internal override FastGridSort<TItem>? SortSource => SortBy;

        /// <summary>
        /// How this column filters.
        /// </summary>
        /// <remarks>
        /// <strong>A template column could already filter by its <see cref="SortProperty" /></strong>,
        /// reflectively, and that still works - so this is not what makes it filter, and saying so was
        /// wrong. What it changes is <em>how</em>, and one thing it makes possible at all:
        /// <list type="bullet">
        /// <item>the check-box list. <c>ColumnBase.DistinctValues</c> answers null, so a path column's
        /// list is empty. A carrier composes <c>SELECT DISTINCT</c> over the key, which is the only
        /// route to a populated list on a column drawing something other than what it filters;</item>
        /// <item>a filter key that is not the sort path. One path serves sorting, filtering and the
        /// settings key together, so a column sorting by <c>Customer.Name</c> could not filter by
        /// <c>Customer.Id</c>;</item>
        /// <item>a typed expression instead of a reflected one, which is what a provider translates
        /// cleanly and what an ahead-of-time compiler can emit.</item>
        /// </list>
        /// </remarks>
        [Parameter] public FastGridFilterBy<TItem>? FilterBy { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// <strong><see cref="FilterBy" /> only ever adds.</strong> Without one this is the base's
        /// answer, which is <see cref="SortPath" /> - and that is not a formality: a template column
        /// given a <see cref="SortProperty" /> already filtered, reflectively, by that path. Returning
        /// only the carrier's path here took that away, and four tests said so. What the carrier buys
        /// is a filter key that need not be the sort path, on a column that need not have one at all,
        /// composed through a typed expression rather than reflected member-by-member.
        /// </remarks>
        public override string? FilterPropertyPath => FilterBy?.Path ?? base.FilterPropertyPath;

        /// <inheritdoc />
        /// <remarks>
        /// The base answers <c>object</c>, which is what sends a path column down the reflective route
        /// that discovers the real type from the path. A carrier knows the type outright.
        /// </remarks>
        public override Type FilterPropertyType => FilterBy?.PropertyType ?? base.FilterPropertyType;

        /// <inheritdoc />
        /// <remarks>
        /// Declines without a carrier, which is what leaves a <see cref="SortProperty" /> column on the
        /// reflective route it has always taken.
        /// </remarks>
        public override Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory) =>
            FilterBy is { } filterBy && ActiveFilter is { } filter
                ? filterBy.Apply(filter, caseSensitivity, inMemory)
                : base.ApplyFilter(caseSensitivity, inMemory);

        /// <inheritdoc />
        public override Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity) =>
            FilterBy is { } filterBy && ActiveFilter is { } filter
                ? filterBy.ApplyInMemory(filter, caseSensitivity)
                : base.ApplyFilterInMemory(caseSensitivity);

        /// <inheritdoc />
        /// <remarks>
        /// Composed rather than enumerated, so an Entity Framework source runs SELECT DISTINCT rather
        /// than pulling every row across the wire - the same as
        /// <see cref="PropertyColumn{TItem, TProp}" />. The values are the <em>key's</em>, not the cell's
        /// text, which is the only thing that makes a check-box list usable on a column drawing
        /// something else.
        /// </remarks>
        public override IQueryable? DistinctValues(IQueryable<TItem> source) =>
            source is not null && FilterBy is { } filterBy ? filterBy.Distinct(source) : null;

        /// <inheritdoc />
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
        {
            if (Template is not null)
            {
                builder.AddContent(sequence, Template(item));
            }
        }
    }
}
