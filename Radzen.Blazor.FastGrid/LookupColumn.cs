using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column that displays a name and carries an id: the row holds <c>CategoryId</c> and the cell
    /// shows "Toys".
    /// </summary>
    /// <remarks>
    /// A thousand rows with a category each are a thousand integers and one lookup of however many
    /// categories exist, against a thousand materialized entities - and what a cell renders is a string
    /// the lookup already holds, so the cell itself allocates nothing.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TKey">The type of the id the row carries.</typeparam>
    public sealed class LookupColumn<TItem, TKey> : LookupColumnBase<TItem, TKey>
    {
        /// <summary>The id this column resolves.</summary>
        [Parameter, EditorRequired] public Expression<Func<TItem, TKey>> Property { get; set; } = default!;

        Expression<Func<TItem, TKey>>? property;
        Func<TItem, TKey>? key;
        string? path;

        /// <inheritdoc />
        public override string? FilterPropertyPath => path;

        /// <inheritdoc />
        /// <remarks>
        /// The id member, which is what the column is bound to even though its cells show a name. §14
        /// refused to make this the settings key while doing so would silently collide with a
        /// PropertyColumn over the same id; §27 makes that collision throw, which is what lets this be
        /// the honest answer.
        /// </remarks>
        internal override string? DisplayPath => path;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TKey);

        /// <inheritdoc />
        public override string? HeaderText => Title ?? path;

        /// <inheritdoc />
        protected override void OnDerive()
        {
            // Equivalent rather than ReferenceEquals: Razor hands this a freshly built expression tree
            // on every render, so reference equality never holds for a column authored in markup.
            if (!PropertyPathResolver.Equivalent(property, Property))
            {
                property = Property;
                path = PropertyPathResolver.For(Property);

                key = Property?.Compile();
            }

            // Last, because the base resolves the lookup and this reads the id it is resolved against.
            base.OnDerive();
        }

        /// <summary>
        /// Draws the cell, in place of the name this column resolved.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a cell that is more than its name - a link to the row the id points at, a name coloured
        /// by another property, an icon beside it. Without it such a column has to be a
        /// <see cref="TemplateColumn{TItem}" />, and that trades away the reason to use this one: the
        /// check-box list stops offering the lookup's <em>names</em> and offers the <em>ids</em> the row
        /// carries, which no reader can use.
        /// </para>
        /// <para>
        /// <strong>It replaces the cell's markup and nothing else.</strong> Filtering, sorting, the
        /// list, the settings key and <see cref="CellTextOf" /> - so the export and the cell tooltip -
        /// all still come from the lookup. That is what keeps this a lookup column rather than a
        /// template column that happens to hold one.
        /// </para>
        /// <para>
        /// It takes the row rather than the resolved name, because a template that wanted only the name
        /// would not need a template; the ones that do need it are reaching for the row's other members.
        /// The name is <see cref="CellTextOf" /> away for a template that wants both.
        /// </para>
        /// </remarks>
        [Parameter] public RenderFragment<TItem>? Template { get; set; }

        /// <inheritdoc />
        /// <remarks>
        /// Costs a null check per cell when no template is given, and the base's own write otherwise -
        /// so a column that does not use this pays for it once per cell and nothing more.
        /// </remarks>
        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
        {
            if (Template is null)
            {
                base.RenderCell(builder, sequence, item);

                return;
            }

            builder.AddContent(sequence, Template(item));
        }

        /// <inheritdoc />
        public override string? CellTextOf(TItem item) => key is null ? null : NameOf(key(item));

        /// <inheritdoc />
        /// <remarks>
        /// The row carries an id, so the filter compares ids: no join is needed, it translates on any
        /// provider, and a filter stored in the settings survives someone renaming the lookup row.
        /// Every generic argument is <typeparamref name="TKey" />, so nothing here is closed over a
        /// type known only at run time.
        /// </remarks>
        public override Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            if (Property is not { } selector)
            {
                return null;
            }

            if (SoleCondition is not { } condition)
            {
                return null;
            }

            if (condition.Operator is not (FastGridFilterOperator.In or FastGridFilterOperator.NotIn))
            {
                return FilterExpression<TItem, TKey>.For(selector, ActiveFilter!, caseSensitivity,
                    inMemory);
            }

            var contains = (Expression)Expression.Call(
                Expression.Constant(SelectedKeys(), typeof(List<TKey?>)), ListContains, selector.Body);

            return Expression.Lambda<Func<TItem, bool>>(
                condition.Operator == FastGridFilterOperator.NotIn ? Expression.Not(contains) : contains,
                selector.Parameters);
        }

        /// <inheritdoc />
        public override Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity)
        {
            if (key is null)
            {
                return null;
            }

            if (SoleCondition is not { } condition)
            {
                return null;
            }

            if (condition.Operator is not (FastGridFilterOperator.In or FastGridFilterOperator.NotIn))
            {
                return FilterExpression<TItem, TKey>.PredicateFor(key, ActiveFilter!, caseSensitivity);
            }

            var keys = SelectedKeys();
            var getter = key;

            return condition.Operator == FastGridFilterOperator.NotIn
                ? item => !keys.Contains(getter(item))
                : item => keys.Contains(getter(item));
        }
    }
}
