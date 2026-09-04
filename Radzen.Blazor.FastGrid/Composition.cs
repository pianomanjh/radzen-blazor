using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Radzen;

namespace Radzen.FastGrid
{
    /// <summary>
    /// What a set of columns and a set of sorts do to a source. Filtering, ordering, the descriptors the
    /// two of them amount to, and the choice between the two routes that can carry them.
    /// </summary>
    /// <remarks>
    /// A function of its arguments, which is the whole point of it being here rather than on the grid.
    /// These were private instance methods over <c>columns</c> and <c>sorts</c> - two fields declared in
    /// the grid's other partial - so the pipeline's only interface was "render a grid and read the DOM",
    /// and the tests that check the two routes agree had to stand up two renderers and diff their rows.
    /// The proof that this was avoidable was already in the suite: <c>FilterExpressionParityTests</c>
    /// calls <see cref="FilterExpression{TItem, TProp}" /> directly and covers eighty-four operator by
    /// route combinations without a renderer at all. That interface sat at the right seam; the
    /// composition above it did not.
    /// <para>
    /// The columns arrive as <see cref="ColumnBase{TItem}" /> rather than through a narrowed interface.
    /// A narrowing here would be a seam with exactly one adapter, since nothing else could satisfy it,
    /// and it would decide what a column exposes as a side effect of moving the pipeline. This module is
    /// one of the call sites that should inform that decision, not pre-empt it. Pre-projecting the
    /// columns into a value of this module's own is ruled out by §3: it allocates per column per
    /// composition and buys nothing.
    /// </para>
    /// <para>
    /// Internal, and reached by the tests through the assembly's <c>InternalsVisibleTo</c>. Public would
    /// commit a shipped package to this shape forever for a seam whose whole justification is internal
    /// testability - and the shape is the half least settled here, since §15's candidates 5 and 6 both
    /// propose changing what this module consumes. The distinction that matters is between reaching at
    /// an interface and reaching past one, and this is the first time there is an interface to reach
    /// at.
    /// </para>
    /// </remarks>
    internal static class Composition
    {
        /// <summary>
        /// The columns' filters as descriptors, or null when nothing is filtered - the common case, and
        /// the one that must allocate nothing.
        /// </summary>
        /// <remarks>
        /// Three things ask what the columns are filtering by: this module, the grid's public
        /// <c>Filters</c> property, and the <c>LoadDataArgs</c> a handler receives. One place to build
        /// them is one place for them to disagree in, which is the recurring finding of §10b - a rule
        /// applied here and not in its neighbour.
        /// </remarks>
        internal static List<FilterDescriptor>? Filters<TItem>(IReadOnlyList<ColumnBase<TItem>> columns)
        {
            List<FilterDescriptor>? filters = null;

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.HasFilter)
                {
                    continue;
                }

                (filters ??= new List<FilterDescriptor>()).Add(DescriptorFor(column));
            }

            return filters;
        }

        /// <summary>
        /// The filters in force: the pass's own while the table is being drawn, worked out on the spot
        /// outside one. Null when nothing is filtered and null when filtering is switched off, so a
        /// caller holding this does not ask about <see cref="CompositionOptions.AllowFiltering" /> a
        /// second time.
        /// </summary>
        internal static List<FilterDescriptor>? ActiveFilters<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns, CompositionOptions options,
            in DrawPass<TItem> pass) =>
            pass.Drawing ? pass.Filters : DeclaredFilters(columns, options);

        /// <summary>
        /// What the columns are asking for right now, or null when filtering is switched off - which is
        /// what a pass is opened over.
        /// </summary>
        /// <remarks>
        /// One gate rather than two. Opening a pass and asking outside one are the same question asked
        /// at two moments, and they were written out separately: the recurring finding of §10b is a rule
        /// applied in one place and not in its neighbour, and two spellings of <c>AllowFiltering ? ... :
        /// null</c> is that shape before it has gone wrong.
        /// </remarks>
        internal static List<FilterDescriptor>? DeclaredFilters<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns, CompositionOptions options) =>
            options.AllowFiltering ? Filters(columns) : null;

        /// <summary>Composes the columns' filters onto a queryable. Untouched when nothing is filtered.</summary>
        /// <remarks>
        /// Each column is asked for its own predicate first. A column that knows the filtered property's
        /// type as a type parameter composes one directly, which is both what a provider translates and
        /// what an ahead-of-time compiler can see through; only the columns that decline - a template
        /// column filtering by a path, a collection column, a column declared as <c>object</c> - are
        /// handed to <c>QueryableExtension</c>, which finds their members by reflection.
        /// </remarks>
        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "ApplyFilter is virtual; the analyzer resolves it to the base implementation, which is the one that always returns null.")]
        internal static IQueryable<TItem> Filter<TItem>(IReadOnlyList<ColumnBase<TItem>> columns,
            IQueryable<TItem> source, CompositionOptions options)
        {
            // The one place that asks. Its three callers used to ask too, and the term this replaces
            // was `!AllowFiltering && !drawing` - which none of them could reach with a false answer,
            // because two guarded on AllowFiltering before calling and the third only ever runs outside
            // a render. A guard written against an ambient that cannot currently vary is the hazard the
            // pass exists to remove rather than an example of it working: nobody can see the rule, and
            // the next caller inherits it.
            if (!options.AllowFiltering)
            {
                return source;
            }

            // What QueryableExtension itself checks to decide whether OrdinalIgnoreCase comparisons are
            // available, so the two builders agree about a given source.
            var inMemory = source is EnumerableQuery;

            // Or is the case where the two groups cannot be applied separately, so it is the only case
            // that needs every descriptor kept in case they have to be applied together. And - the
            // default, and what a filter row produces - never does.
            var either = options.LogicalFilterOperator == LogicalFilterOperator.Or;

            Expression<Func<TItem, bool>>? predicate = null;
            List<FilterDescriptor>? declined = null;
            List<FilterDescriptor>? all = null;

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.HasFilter)
                {
                    continue;
                }

                var descriptor = either ? DescriptorFor(column) : null;

                if (either)
                {
                    (all ??= new List<FilterDescriptor>()).Add(descriptor!);
                }

                if (column.ApplyFilter(options.FilterCaseSensitivity, inMemory) is { } composed)
                {
                    predicate = predicate is null
                        ? composed
                        : FilterPredicate.Join(predicate, composed, options.LogicalFilterOperator);
                }
                else
                {
                    (declined ??= new List<FilterDescriptor>()).Add(descriptor ?? DescriptorFor(column));
                }
            }

            if (declined is null)
            {
                return predicate is null ? source : source.Where(predicate);
            }

            if (predicate is null)
            {
                return Reflective(source, declined, options);
            }

            // Two Wheres are an And between the groups, which is right for And and wrong for Or: a row
            // that matched only a declining column would be dropped by the second Where. So a mixed Or
            // goes through the reflective builder whole rather than being composed wrongly - one
            // builder, one answer. It costs that grid its AOT-cleanliness, which it had already lost to
            // the column that declined.
            return either
                ? Reflective(source, all!, options)
                : Reflective(source.Where(predicate), declined, options);
        }

        /// <summary>
        /// Composes every sort onto the query, in order of precedence. A column that cannot order -
        /// which is what ApplySort returning null means - is skipped rather than allowed to break the
        /// chain, so one uncomparable column does not cost the sort the caller asked for.
        /// </summary>
        internal static IQueryable<TItem> Sort<TItem>(
            IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)> sorts, IQueryable<TItem> source)
        {
            IOrderedQueryable<TItem>? ordered = null;

            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                ordered = ordered is null
                    ? column.ApplySort(source, descending) ?? ordered
                    : column.ApplyThenBy(ordered, descending) ?? ordered;
            }

            return ordered ?? source;
        }

        /// <summary>
        /// Filters and sorts, without paging, and says which route it took. Nothing is wrapped in a
        /// queryable unless something is actually filtered or sorted, so an unfiltered, unsorted grid
        /// enumerates its source directly.
        /// </summary>
        /// <remarks>
        /// The route is part of the answer rather than a field the caller reads afterwards. It was the
        /// latter, and its own comment said it existed "for the tests, and only to them" - a return
        /// value smuggled out sideways because there was no return value to put it in.
        /// </remarks>
        internal static Composed<TItem> Compose<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns,
            IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)> sorts,
            IEnumerable<TItem> source,
            CompositionOptions options,
            ref DrawPass<TItem> pass)
        {
            if (pass.Reuses(source, out var reused))
            {
                return reused;
            }

            var filtering = ActiveFilters(columns, options, in pass) is not null;

            // What the grid calls SortColumn, asked of the list it is the head of: a sort list with
            // anything in it is a grid that sorts.
            var sorting = sorts.Count > 0;

            if (!filtering && !sorting)
            {
                return pass.Keep(source, new Composed<TItem>(source, false));
            }

            // A source that is already in memory is composed with delegates rather than expressions.
            // Wrapping a list in an EnumerableQuery to hand it an expression tree makes it rewrite and
            // recompile that tree every time the result is enumerated: measured at 1000 rows, 1,117 us
            // and 11.8 KB to filter that way against 38 us and 0.07 KB through a delegate, on a render
            // that costs 1,800 us in total. Composing over a real queryable still uses expressions,
            // because there the point is for the provider to translate them.
            if (source is not IQueryable<TItem> queryable)
            {
                if (ComposeInMemory(columns, sorts, source, options, filtering, sorting) is { } composed)
                {
                    return pass.Keep(source, new Composed<TItem>(composed, true));
                }

                // A column that cannot compose in memory - a template column filtering by a path -
                // sends the whole composition back to the expression route rather than half of it.
                queryable = source.AsQueryable();
            }

            if (filtering)
            {
                queryable = Filter(columns, queryable, options);
            }

            // The column applies its own ordering, so it stays a typed expression the provider can
            // translate rather than a parsed string.
            return pass.Keep(source,
                new Composed<TItem>(sorting ? Sort(sorts, queryable) : queryable, false));
        }

        /// <summary>
        /// Filters and sorts an in-memory sequence without wrapping it in a queryable, or returns null
        /// when some column cannot be composed that way and the caller should take the other route.
        /// </summary>
        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
            Justification = "ApplyFilterInMemory is virtual; the analyzer resolves it to the base implementation, which is the one that always returns null.")]
        static IEnumerable<TItem>? ComposeInMemory<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns,
            IReadOnlyList<(ColumnBase<TItem> Column, bool Descending)> sorts,
            IEnumerable<TItem> data, CompositionOptions options, bool filtering, bool sorting)
        {
            if (filtering)
            {
                Func<TItem, bool>? predicate = null;
                var either = options.LogicalFilterOperator == LogicalFilterOperator.Or;

                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];

                    if (!column.HasFilter)
                    {
                        continue;
                    }

                    if (column.ApplyFilterInMemory(options.FilterCaseSensitivity) is not { } composed)
                    {
                        return null;
                    }

                    var previous = predicate;

                    predicate = previous is null ? composed
                        : either ? item => previous(item) || composed(item)
                        : item => previous(item) && composed(item);
                }

                if (predicate is not null)
                {
                    data = data.Where(predicate);
                }
            }

            if (!sorting)
            {
                return data;
            }

            IOrderedEnumerable<TItem>? ordered = null;

            for (var i = 0; i < sorts.Count; i++)
            {
                var (column, descending) = sorts[i];

                var next = ordered is null
                    ? column.ApplySortInMemory(data, descending)
                    : column.ApplyThenByInMemory(ordered, descending);

                // Null here means the column declined, which the queryable route treats as "skip this
                // column". Taking the other route instead would be a different answer, not a slower
                // one, so only a first column that declines sends it back - and only when no ordering
                // has begun, since a half-applied one cannot be handed over.
                if (next is null && ordered is null && i == 0)
                {
                    return null;
                }

                ordered = next ?? ordered;
            }

            return ordered ?? data;
        }

        /// <summary>
        /// The one call in this component that reaches a property by name. Reserved for the columns that
        /// cannot compose their own predicate, and reachable only while dynamic filtering is enabled.
        /// </summary>
        /// <remarks>
        /// The policy travels with the route rather than with the grid: "this way of filtering needs
        /// dynamic code" is a fact about this method, and there is nowhere else in the library it could
        /// be asked from.
        /// </remarks>
        static IQueryable<TItem> Reflective<TItem>(IQueryable<TItem> source,
            List<FilterDescriptor> filters, CompositionOptions options)
        {
            if (!DynamicCode.Supported)
            {
                throw DynamicCode.Unavailable(
                    $"Filtering '{filters[0].Property}' through the column's property path");
            }

            return source.Where(filters, options.LogicalFilterOperator, options.FilterCaseSensitivity);
        }

        static FilterDescriptor DescriptorFor<TItem>(ColumnBase<TItem> column) => new()
        {
            Property = column.FilterPropertyPath,

            // Names a member of the collection's element, so the predicate becomes
            // Customers.Any(c => c.Name ...) rather than a comparison against the collection.
            FilterProperty = column.FilterMemberPath,
            FilterValue = column.CurrentFilterValue,
            FilterOperator = column.CurrentFilterOperator,
            Type = column.FilterPropertyType,
        };
    }

    /// <summary>The rows a composition produced, and how it produced them.</summary>
    /// <remarks>
    /// A struct, and §3 is why: a composition happens once per render and more than once per render on a
    /// grid with two pagers, so a reference here would be an allocation buying nothing that two fields
    /// on the stack do not already give.
    /// </remarks>
    internal readonly struct Composed<TItem>
    {
        internal Composed(IEnumerable<TItem> rows, bool inMemory)
        {
            Rows = rows;
            InMemory = inMemory;
        }

        /// <summary>The filtered and sorted rows, unpaged.</summary>
        internal IEnumerable<TItem> Rows { get; }

        /// <summary>Whether the delegate route ran.</summary>
        /// <remarks>
        /// False for the expression route and false again when neither ran, which is the unfiltered and
        /// unsorted case: there the source is handed straight back and no route is taken at all. One
        /// bool over three states, because the two callers of it both ask the same question - did the
        /// cheap route run - and neither can act on the difference between the other two.
        /// <para>
        /// It has to be said at all because it is invisible in the rows: a column that declines to
        /// compose in memory sends the whole composition to the expression route, which produces the
        /// same answer and costs about 1.1 ms per render at 1000 rows. Without this a column could
        /// quietly stop overriding <c>ApplySortInMemory</c> and every row would still be right.
        /// </para>
        /// </remarks>
        internal bool InMemory { get; }
    }

    /// <summary>
    /// The settings a composition depends on, as against the data it composes: whether filtering is on
    /// at all, how string comparisons are cased, and whether the columns' filters are anded or ored.
    /// </summary>
    /// <remarks>
    /// One value rather than three arguments, and a struct for the same reason
    /// <see cref="Composed{TItem}" /> is one. Six of the nine things the composition used to reach for
    /// on the grid were these three read twice over; collapsing them is what leaves three real
    /// parameters and a pass.
    /// </remarks>
    internal readonly struct CompositionOptions
    {
        internal CompositionOptions(bool allowFiltering, FilterCaseSensitivity filterCaseSensitivity,
            LogicalFilterOperator logicalFilterOperator)
        {
            AllowFiltering = allowFiltering;
            FilterCaseSensitivity = filterCaseSensitivity;
            LogicalFilterOperator = logicalFilterOperator;
        }

        /// <summary>Whether the columns' filters are applied at all.</summary>
        internal bool AllowFiltering { get; }

        /// <summary>How string comparisons are cased.</summary>
        internal FilterCaseSensitivity FilterCaseSensitivity { get; }

        /// <summary>Whether the columns' filters are anded or ored together.</summary>
        internal LogicalFilterOperator LogicalFilterOperator { get; }
    }
}
