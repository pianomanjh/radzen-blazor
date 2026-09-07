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
    /// <b>What is done when a column declines.</b> <see cref="ColumnBase{TItem}" /> states what
    /// declining <em>means</em>, which is all a column decides; this is the only place that says what
    /// follows from it, and it is not the same in both routes, because the two differ in whether they
    /// have anywhere to put a decline.
    /// </para>
    /// <para>
    /// <b>The expression route absorbs it.</b> A declining filter is built from the column's path by
    /// reflection instead; a declining sort is left out of the ordering and the rest of it stands. Both
    /// are somewhere to put it, so nothing has to be given up.
    /// </para>
    /// <para>
    /// <b>The delegate route hands the whole composition over</b> - while handing over is still
    /// possible. For a filter it always is: the predicate has been built up and not yet applied, so
    /// dropping it costs nothing. For a sort it stops being possible the moment an ordering has begun,
    /// because a half-applied <see cref="IOrderedEnumerable{TElement}" /> cannot be given to the other
    /// route - so the first column can send it back and a later one is left out, which is what the
    /// expression route would have done with it anyway.
    /// </para>
    /// <para>
    /// Both loops below say only what is local to them and refer here for the rule, which is the point:
    /// this used to be four inline policies with a restatement beside each of the six declarations, and
    /// one of those restatements had drifted into describing a different one of the four. §17 records
    /// which and how it was found.
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
        internal static List<CompositeFilterDescriptor>? Filters<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns, DateTimeOffset now)
        {
            List<CompositeFilterDescriptor>? filters = null;

            Resolve(columns, now);

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                if (!column.HasFilter)
                {
                    continue;
                }

                (filters ??= new List<CompositeFilterDescriptor>()).Add(DescriptorFor(column));
            }

            return filters;
        }

        /// <summary>
        /// Reads every filtered column's relative dates at one instant - §34.
        /// </summary>
        /// <remarks>
        /// Here rather than in the grid because this is what a composition is: the two routes and the
        /// descriptors all begin by asking the columns what they are filtering by, and this is that
        /// question asked as of a moment. A filter holding no tokens resolves to itself, so a grid with
        /// no relative filter anywhere pays a null check per filtered column and nothing else.
        /// <para>
        /// All three entries call this - <see cref="Filter{TItem}" />, <c>ComposeInMemory</c> and
        /// <see cref="Filters{TItem}" />. The descriptors path spelled the loop out again instead until
        /// the review found it, which left one rule in two places and this remark already claiming the
        /// third caller it did not have.
        /// </para>
        /// </remarks>
        static void Resolve<TItem>(IReadOnlyList<ColumnBase<TItem>> columns, DateTimeOffset now)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].HasFilter)
                {
                    columns[i].ResolveFilter(now);
                }
            }
        }

        /// <summary>
        /// The filters in force: the pass's own while the table is being drawn, worked out on the spot
        /// outside one. Null when nothing is filtered and null when filtering is switched off, so a
        /// caller holding this does not ask about <see cref="CompositionOptions.AllowFiltering" /> a
        /// second time.
        /// </summary>
        internal static List<CompositeFilterDescriptor>? ActiveFilters<TItem>(
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
        internal static List<CompositeFilterDescriptor>? DeclaredFilters<TItem>(
            IReadOnlyList<ColumnBase<TItem>> columns, CompositionOptions options) =>
            options.AllowFiltering ? Filters(columns, options.Now) : null;

        /// <summary>
        /// Whether a row total counted on <paramref name="countedOn" /> can have been outlived by a
        /// relative filter as of <paramref name="now" />.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Here rather than inline in the grid because it is a rule, and a rule inlined in a virtualized
        /// provider is a rule no test can reach - the mutation loop proved that by changing it and
        /// watching 990 tests pass. What it guards is in <c>DropStaleTotal</c>.
        /// </para>
        /// <para>
        /// By the <em>day</em>, not by the stamp. Every anchor is day-granular and <c>@end</c> stays
        /// inside its day, so a token's answer can only change when the local date does - and comparing
        /// stamps instead would recount on every window, which is a query per scroll.
        /// </para>
        /// </remarks>
        internal static bool OutlivedTheDay<TItem>(IReadOnlyList<ColumnBase<TItem>> columns,
            DateTime countedOn, DateTimeOffset now)
        {
            if (countedOn == now.Date)
            {
                return false;
            }

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].HasFilter && FilterResolution.IsRelative(columns[i].CurrentFilter))
                {
                    return true;
                }
            }

            return false;
        }

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

            // Before anything reads a filter, and here rather than at this method's callers because
            // this is the one of the three routes they do not all pass through: a queryable source
            // reaches Filter without Filters ever being built.
            Resolve(columns, options.Now);

            // What QueryableExtension itself checks to decide whether OrdinalIgnoreCase comparisons are
            // available, so the two builders agree about a given source.
            var inMemory = source is EnumerableQuery;

            // Or is the case where the two groups cannot be applied separately, so it is the only case
            // that needs every descriptor kept in case they have to be applied together. And - the
            // default, and what a filter row produces - never does.
            var either = options.LogicalFilterOperator == LogicalFilterOperator.Or;

            Expression<Func<TItem, bool>>? predicate = null;
            List<CompositeFilterDescriptor>? declined = null;
            List<CompositeFilterDescriptor>? all = null;

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
                    (all ??= new List<CompositeFilterDescriptor>()).Add(descriptor!);
                }

                if (column.ApplyFilter(options.FilterCaseSensitivity, inMemory) is { } composed)
                {
                    predicate = predicate is null
                        ? composed
                        : FilterPredicate.Join(predicate, composed, options.LogicalFilterOperator);
                }
                else
                {
                    // Declined; this route absorbs it by reflection. See this class's remarks.
                    (declined ??= new List<CompositeFilterDescriptor>()).Add(descriptor ?? DescriptorFor(column));
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
        /// Composes every sort onto the query, in order of precedence. This is the expression route
        /// absorbing a decline: a column that cannot order is left out and the rest of the ordering
        /// stands, so one uncomparable column does not cost the sort the caller asked for.
        /// </summary>
        /// <remarks>
        /// <c>ordered is null</c> is a real test here and is not the same as <c>i == 0</c>: a first
        /// column that declines leaves it null, the loop carries on, and the next column starts the
        /// ordering rather than adding to one. The delegate route's loop looks identical and that is
        /// not true of it - see this class's remarks.
        /// </remarks>
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

                // The delegate route reaches the columns without going through Filter, and a pass being
                // reused means the descriptors were built at a previous instant. Resolving here is what
                // makes all three routes read the same one.
                Resolve(columns, options.Now);

                for (var i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];

                    if (!column.HasFilter)
                    {
                        continue;
                    }

                    if (column.ApplyFilterInMemory(options.FilterCaseSensitivity) is not { } composed)
                    {
                        // Handing the composition over, which for a filter is always still possible:
                        // the predicate has been built up and not applied. See this class's remarks.
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

                // Handing the composition over, at the one point where that is still possible - and
                // the reason to hand over rather than simply leave the column out, which is what this
                // route does with a later one: the other route may not decline where this one did, and
                // that is a different answer rather than a slower one. See this class's remarks.
                //
                // `i == 0` is the whole of the condition, and the return below it is what makes that
                // so: a declining first column leaves the loop here, so `ordered` can never still be
                // null on a later pass. Removing the return would make what is left wrong rather than
                // merely redundant - and restoring `ordered is null` beside it, which the expression
                // route's loop does need, would only be the same test written twice.
                if (next is null && i == 0)
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
            List<CompositeFilterDescriptor> filters, CompositionOptions options)
        {
            if (!DynamicCode.Supported)
            {
                throw DynamicCode.Unavailable(
                    $"Filtering '{filters[0].Property}' through the column's property path");
            }

            return source.Where(filters, options.LogicalFilterOperator, options.FilterCaseSensitivity);
        }

        /// <summary>
        /// The same filters in the shape <see cref="LoadDataArgs.Filters" /> is typed as, or null when
        /// nothing is filtered.
        /// </summary>
        /// <remarks>
        /// §33 made <see cref="CompositeFilterDescriptor" /> this grid's one filter currency and then met
        /// the seam where it cannot be: <c>LoadDataArgs.Filters</c> is
        /// <c>IEnumerable&lt;FilterDescriptor&gt;</c>, upstream's type and not ours to change. So the
        /// handler's structured view is projected back, while <c>LoadDataArgs.Filter</c> - the string,
        /// and the half a handler usually reads - is built from the composites themselves and keeps
        /// whatever nesting they carry.
        /// <para>
        /// A <c>FilterDescriptor</c> carries two comparisons - <c>FilterValue</c> and
        /// <c>SecondFilterValue</c>, joined by its own <c>LogicalFilterOperator</c> - so a two-condition
        /// column and a <c>Between</c> both fit exactly. Only a compound whose halves need more than two
        /// comparisons between them overflows, and such a column is <strong>left out</strong> rather
        /// than flattened.
        /// </para>
        /// <para>
        /// Leaving it out is the lesser wrong and the review is why it is stated: flattening put the
        /// parent's empty comparison through as <c>Property Equals null</c>, so a handler building a
        /// query from this list returned no rows for a filter that was on screen and correct. A missing
        /// descriptor is a handler filtering less than it was asked to; a wrong one is a blank page.
        /// <c>LoadDataArgs.Filter</c> - the string, and the half most handlers read - keeps the whole
        /// filter either way, because it is built from the composites and their nesting.
        /// </para>
        /// </remarks>
        internal static List<FilterDescriptor>? Descriptors(List<CompositeFilterDescriptor>? filters)
        {
            if (filters is null)
            {
                return null;
            }

            var descriptors = new List<FilterDescriptor>(filters.Count);

            for (var i = 0; i < filters.Count; i++)
            {
                if (Descriptor(filters[i]) is { } descriptor)
                {
                    descriptors.Add(descriptor);
                }
            }

            return descriptors;
        }

        /// <summary>One composite as a descriptor, or null when it says more than one can carry.</summary>
        static FilterDescriptor? Descriptor(CompositeFilterDescriptor filter)
        {
            var children = filter.Filters?.ToList();

            if (children is not { Count: > 0 })
            {
                return new FilterDescriptor
                {
                    Property = filter.Property,
                    FilterProperty = filter.FilterProperty,
                    FilterValue = filter.FilterValue,
                    FilterOperator = filter.FilterOperator ?? Radzen.FilterOperator.Equals,
                    Type = filter.Type,
                };
            }

            // Two comparisons is what the shape holds. A Between is two; a compound of two simple
            // conditions is two; a compound with a Between in it is three, and there is nowhere to put
            // the third.
            if (children.Count != 2 || children.Any(child => child.Filters?.Any() == true))
            {
                return null;
            }

            return new FilterDescriptor
            {
                Property = filter.Property,
                FilterProperty = filter.FilterProperty,
                Type = filter.Type,
                FilterValue = children[0].FilterValue,
                FilterOperator = children[0].FilterOperator ?? Radzen.FilterOperator.Equals,
                SecondFilterValue = children[1].FilterValue,
                SecondFilterOperator = children[1].FilterOperator ?? Radzen.FilterOperator.Equals,
                LogicalFilterOperator = filter.LogicalFilterOperator,
            };
        }

        /// <summary>
        /// One column's filter as a descriptor the rest of Radzen can read.
        /// </summary>
        /// <remarks>
        /// Nested where the owned filter says more than one condition can - a second condition, or a
        /// <c>Between</c>, which is two comparisons with one name. That is what §33 bought by making
        /// <see cref="CompositeFilterDescriptor" /> the currency: the parent carries the property and
        /// the join, the children carry the comparisons, and both the reflective builder and the string
        /// forms already walk it.
        /// </remarks>
        static CompositeFilterDescriptor DescriptorFor<TItem>(ColumnBase<TItem> column)
        {
            // The resolved filter, not the authored one: a descriptor is what a provider, a LoadData
            // handler and the OData string are built from, and none of them can read `today-6d`.
            var filter = column.ActiveFilter!;
            var first = filter.First;
            var second = filter.EffectiveSecond;

            // The simple shape, and the one almost every filtered column has: no children, and the
            // descriptor reads exactly as it did before the model existed.
            if (second is null && first.Operator != FastGridFilterOperator.Between)
            {
                return Descriptor(column, first.Operator, first.Value);
            }

            var parent = Descriptor(column, filterOperator: null, value: null);

            parent.LogicalFilterOperator = second is null
                // Between's two halves are always joined by And; the column's own operator joins its two
                // conditions and says nothing about the bounds of a range.
                ? LogicalFilterOperator.And
                : filter.LogicalFilterOperator;

            parent.Filters = second is null
                ? BetweenChildren(column, first)
                : new List<CompositeFilterDescriptor> { Child(column, first), Child(column, second) };

            return parent;
        }

        /// <summary>
        /// One condition as one child, nesting again where the condition itself is two comparisons.
        /// </summary>
        /// <remarks>
        /// The second level is the whole point and it was missing: a <c>Between</c>'s two bounds were
        /// flattened into the parent's child list beside the other condition, so
        /// <c>Between(200,300) OR IsNull</c> became three siblings joined by <c>Or</c> and the
        /// <c>And</c> that makes a range a range was gone. The typed routes were unaffected - they never
        /// see a descriptor - so an in-memory grid showed two rows while the string sent to a server
        /// asked for nearly the table. §33 bought the nesting for exactly this and then did not spend it.
        /// </remarks>
        static CompositeFilterDescriptor Child<TItem>(ColumnBase<TItem> column,
            FastGridFilterCondition condition)
        {
            if (condition.Operator != FastGridFilterOperator.Between)
            {
                return Descriptor(column, condition.Operator, condition.Value);
            }

            var range = Descriptor(column, filterOperator: null, value: null);

            // A range's bounds are joined by And whatever joins the column's own two conditions.
            range.LogicalFilterOperator = LogicalFilterOperator.And;
            range.Filters = BetweenChildren(column, condition);

            return range;
        }

        /// <summary>
        /// A range as the pair of comparisons upstream can read, since it has no operator for one.
        /// </summary>
        static List<CompositeFilterDescriptor> BetweenChildren<TItem>(ColumnBase<TItem> column,
            FastGridFilterCondition condition) =>
            new()
            {
                Descriptor(column, FastGridFilterOperator.GreaterThanOrEquals, condition.Value),
                Descriptor(column, FastGridFilterOperator.LessThanOrEquals, condition.SecondValue),
            };

        static CompositeFilterDescriptor Descriptor<TItem>(ColumnBase<TItem> column,
            FastGridFilterOperator? filterOperator, object? value) => new()
        {
            Property = column.FilterPropertyPath,

            // Names a member of the collection's element, so the predicate becomes
            // Customers.Any(c => c.Name ...) rather than a comparison against the collection.
            FilterProperty = column.FilterMemberPath,
            FilterValue = value,
            FilterOperator = filterOperator?.Upstream(),
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
            LogicalFilterOperator logicalFilterOperator, DateTimeOffset now)
        {
            AllowFiltering = allowFiltering;
            FilterCaseSensitivity = filterCaseSensitivity;
            LogicalFilterOperator = logicalFilterOperator;
            Now = now;
        }

        /// <summary>Whether the columns' filters are applied at all.</summary>
        internal bool AllowFiltering { get; }

        /// <summary>How string comparisons are cased.</summary>
        internal FilterCaseSensitivity FilterCaseSensitivity { get; }

        /// <summary>Whether the columns' filters are anded or ored together.</summary>
        internal LogicalFilterOperator LogicalFilterOperator { get; }

        /// <summary>
        /// The instant this composition reads its relative dates at - §34.
        /// </summary>
        /// <remarks>
        /// One of the settings rather than something read from a clock down here, and for the reason
        /// this struct exists at all: the grid stamps it once and it is threaded, so every column and
        /// both bounds of every range in one composition agree about when now is. A composition that
        /// read the clock per token could produce a range that excludes its own start, once a day, with
        /// no reproduction.
        /// </remarks>
        internal DateTimeOffset Now { get; }
    }
}
