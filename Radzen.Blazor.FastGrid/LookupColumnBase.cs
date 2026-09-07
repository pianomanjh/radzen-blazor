using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace Radzen.FastGrid
{
    /// <summary>
    /// What a column that displays names and carries ids does whatever the cardinality of its key:
    /// resolve the lookup once, draw the filter from it rather than from the data, and compare ids.
    /// </summary>
    /// <remarks>
    /// The split between this and its two derived columns is cardinality, which is knowable at compile
    /// time from the property's type and changes how a column renders, filters and sorts. Provenance -
    /// where the names come from - is not, and is one parameter of a closed type instead.
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TKey">The type of the id the row carries.</typeparam>
    public abstract class LookupColumnBase<TItem, TKey> : ColumnBase<TItem>
    {
        /// <summary>Where the names come from.</summary>
        [Parameter, EditorRequired] public FastGridLookup<TKey> Lookup { get; set; } = default!;

        /// <summary>
        /// What to sort by. Without it the column is not sortable.
        /// </summary>
        /// <remarks>
        /// Sorting by the id puts the categories in insertion order under a column showing names
        /// alphabetically - a wrong answer that looks like a working feature. Where a navigation
        /// property exists, <c>SortBy="@(FastGridSort&lt;Product&gt;.By(p =&gt; p.Category.Name))"</c> is
        /// the honest answer, and the author is the one who knows whether it is there.
        /// </remarks>
        [Parameter] public FastGridSort<TItem>? SortBy { get; set; }

        /// <summary>The names, or null while a query lookup has not answered yet.</summary>
        private protected IReadOnlyDictionary<TKey, string>? Names { get; private set; }

        // What the names were asked for. Reload moves it on, and an answer arriving against an older
        // one is about a lookup nobody is showing any more.
        int generation;

        bool outstanding;
        List<object>? entries;

        static readonly IReadOnlyDictionary<TKey, string> Unresolved = LookupNames.None<TKey>();

        /// <inheritdoc />
        internal override FastGridSort<TItem>? SortSource => SortBy;

        /// <inheritdoc />
        /// <remarks>
        /// Not the base's answer, which asks whether there is a path: a sort over a computed key has
        /// none and orders rows all the same.
        /// </remarks>
        public override bool CanSort => Sortable && SortBy is not null;

        /// <inheritdoc />
        public override Type FilterElementType => typeof(TKey);

        /// <summary>
        /// A lookup column always compares ids, and always as a set: the check-box list ticks them and
        /// simple mode matches text against the names and emits the ids it hits.
        /// </summary>
        internal override FastGridFilterOperator DefaultFilterOperator => FastGridFilterOperator.In;

        /// <inheritdoc />
        /// <remarks>
        /// A set, because that is what this column filters by - §31's table says so and
        /// <see cref="DefaultFilterOperator" /> has said so since §14. The key type would have answered
        /// with a number's operators, which is a menu offering "less than" over ids the reader never
        /// sees.
        /// <para>
        /// Unconditional, where the base asks <c>EditedAsSet</c>: a lookup filters by ids
        /// whatever editor is drawing, and has since §14. Its own two arrays are gone - §36 put the pair
        /// and its nullable tail in <c>FastGridFilterOperators</c>, so the nullable append is written
        /// once for every column that offers a set rather than once here and once there.
        /// </para>
        /// </remarks>
        internal override FastGridFilterOperator[] MenuOperators =>
            FastGridFilterOperators.Set(KeyCanBeNull);

        /// <inheritdoc />
        protected override void OnDerive()
        {
            if (FilterLookupData is not null)
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} draws its check-box list from Lookup, so FilterLookupData has " +
                    "nothing to supply. Remove FilterLookupData, or use a PropertyColumn if the values " +
                    "offered are meant to differ from the names the cells show.");
            }

            EnsureLookup();

            // Nothing above this today, and chained anyway: the rule OnDerive states is that a link in
            // the chain calls the next one, and a link that only happens to be last is how it stops
            // being true.
            base.OnDerive();
        }

        /// <summary>
        /// Resolves the lookup, once. Deliberately not on whether the parameter is the same instance:
        /// Razor rebuilds a query lookup's expressions on every render and <c>Expression</c> does not
        /// override <c>Equals</c>, so a cache keyed on the lookup's identity would refetch it every
        /// time - which is the defect the check-box list's own distinct scan had, in a feature designed
        /// to avoid it. <see cref="RadzenFastGrid{TItem}.Reload" /> is what drops it.
        /// </summary>
        void EnsureLookup()
        {
            if (Names is not null || outstanding || Lookup is null)
            {
                return;
            }

            // What another column on this grid already resolved this same lookup to. Record equality
            // does the matching, so sharing is an optimization nobody has to name or think about.
            if (Grid?.SharedNames(Lookup) is IReadOnlyDictionary<TKey, string> shared)
            {
                SetNames(shared);

                return;
            }

            SetNames(Lookup.Resolve());

            if (Names is null)
            {
                outstanding = true;

                Grid?.QueueNames(this);

                return;
            }

            Grid?.ShareNames(Lookup, Names);
        }

        void SetNames(IReadOnlyDictionary<TKey, string>? resolved)
        {
            Names = resolved;

            // The list the filter offers is built from them, so it goes with them.
            entries = null;
            entriesBlank = null;
        }

        /// <inheritdoc />
        internal override bool NamesOutstanding => outstanding;

        /// <inheritdoc />
        internal override async Task<bool> FetchNamesAsync(IFastGridQueryExecutor? executor,
            CancellationToken cancellationToken)
        {
            var asked = generation;

            try
            {
                var fetched = await Lookup.FetchAsync(executor, cancellationToken);

                if (generation == asked)
                {
                    // The coalesce is the seam between a case that fetches and one that does not: the
                    // base FetchAsync answers with Resolve(), which is null for a case that needs
                    // fetching and forgot to override it. Left null the column would go back on the
                    // queue for an answer it has already given, and each redraw would ask again.
                    SetNames(fetched ?? Unresolved);
                }

                // Redraw either way: when the answer stands, to show it, and when it does not, because
                // the render is what puts this column back on the queue.
                return true;
            }
            catch (OperationCanceledException)
            {
                // The grid is going away. Nothing will render, and nothing needs to.
                return false;
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Every provider throws its own, and a narrow catch here would be a catch for one of
                // them. The rows are drawn and correct and only the names are missing, so the grid
                // stays up - and resolves to no names, which draws every id. That is what a missing
                // entry already draws, and for the same reason: a column of blanks would be a fault
                // nobody can see. Reload is what tries again.
                if (generation == asked)
                {
                    SetNames(Unresolved);
                }

                return true;
            }
            finally
            {
                // Settled on every way out - the answer, the throw, and the drop that overtook it - or
                // the auto-fit this defers would be owed forever and never run. An answer, empty
                // included, is what clears it. A drop that overtook this one leaves the names still
                // missing, so it stays set and the column asks again itself: waiting for a parameter
                // set would be waiting on something a retained component may never get.
                outstanding = Names is null;

                if (outstanding)
                {
                    Grid?.QueueNames(this);
                }
            }
        }

        /// <inheritdoc />
        internal override void DropNames()
        {
            SetNames(null);

            generation++;

            // Straight away rather than on the next parameter set, for the same reason: a map is
            // resolved again here and now, and a query goes back on the queue. A fetch still in flight
            // is left alone - its answer is against the old generation and is discarded, and it
            // re-queues itself on the way out.
            EnsureLookup();
        }

        /// <summary>
        /// The name an id stands for, or the id itself when the lookup has no entry for it - a deleted
        /// row, a lookup narrowed by a <c>Where</c>, or a fetch that failed. That is a fault, and the id
        /// is the only thing that lets anyone diagnose it, so it is deliberately not a blank.
        /// </summary>
        /// <remarks>
        /// A null id is the other thing entirely - a row with no category - and answers null. The order
        /// is as load-bearing as the answers: a dictionary throws when asked about a null key,
        /// <c>Nullable&lt;T&gt;</c> included.
        /// </remarks>
        private protected string? NameOf(TKey? id) =>
            id is null || Names is null ? null
                : Names.TryGetValue(id, out var text) ? text
                : id.ToString();

        /// <summary>
        /// Whether an id can be missing at all, which is what makes "no category" a value a row can
        /// hold and a null among the filter's values something to keep rather than to drop.
        /// </summary>
        private protected static readonly bool KeyCanBeNull =
            !typeof(TKey).IsValueType || Nullable.GetUnderlyingType(typeof(TKey)) is not null;

        /// <inheritdoc />
        /// <remarks>
        /// Only where a key can be null: "which products have no category" is a question, and <c>In</c>
        /// over a nullable key answers it.
        /// <para>
        /// <strong>The base agrees today and this stays anyway.</strong> §36's mutation loop found that
        /// deleting this override changes nothing: <see cref="FilterElementType" /> is
        /// <typeparamref name="TKey" />, so the base's <c>FilterNullable</c> reduces to exactly
        /// <see cref="KeyCanBeNull" />. An earlier draft of this remark claimed the base "would answer
        /// for the wrong one", which was a guess and is false. It is kept because it says the thing
        /// directly - a lookup's blank is about its key - where the base reaches the same answer through
        /// <c>EffectiveFilterType</c>, and a column whose key is <c>object</c> would send that through
        /// the filter path instead.
        /// </para>
        /// </remarks>
        private protected override bool OffersBlank => KeyCanBeNull;

        /// <inheritdoc />
        /// <remarks>
        /// The lookup itself, so no <c>SELECT DISTINCT</c> runs for a lookup column. The list is
        /// therefore complete and stable rather than only what the current rows hold: a filter
        /// control whose options move as the data does moves under the reader, and that is worth more
        /// than a shorter list.
        /// </remarks>
        internal override IEnumerable? FilterValues
        {
            get
            {
                // Rebuilt when the word for the blank entry changes, which a culture change does as
                // well as the parameter: every other string this grid draws is read per render, and one
                // baked into a list built once would otherwise stay on screen for good.
                var blank = OffersBlank ? Grid?.BlankFilterText ?? string.Empty : null;

                if (entries is null || !string.Equals(entriesBlank, blank, StringComparison.Ordinal))
                {
                    entriesBlank = blank;
                    entries = BuildEntries(blank);
                }

                return entries;
            }
        }

        string? entriesBlank;

        List<object> BuildEntries(string? blank)
        {
            var built = new List<object>((Names?.Count ?? 0) + 1);

            if (Names is not null)
            {
                foreach (var pair in Names)
                {
                    built.Add(new FastGridLookupEntry<TKey>(pair.Key, pair.Value));
                }
            }

            // By name, because that is what the reader is reading. The dictionary's own order is
            // whatever the source happened to answer in.
            built.Sort(static (left, right) =>
                StringComparer.CurrentCulture.Compare(left.ToString(), right.ToString()));

            if (blank is not null)
            {
                // First, and not sorted among the names: it is the absence of one.
                built.Insert(0, new FastGridLookupEntry<TKey>(default, blank));
            }

            return built;
        }

        /// <inheritdoc />
        /// <remarks>
        /// The check-box list is bound to entries and the column filters by ids, so the ticks have to be
        /// found again. Scanned rather than indexed: this is the filter row, once per render, over a
        /// selection of a few against a lookup of a few hundred.
        /// </remarks>
        internal override object? SelectionOf(object? value)
        {
            if (value is not IEnumerable selected || value is string
                || FilterValues is not List<object> offered)
            {
                return null;
            }

            var ticked = new List<object>();

            foreach (var id in selected)
            {
                if (EntryFor(offered, id) is { } entry)
                {
                    ticked.Add(entry);
                }
            }

            return ticked;
        }

        /// <summary>
        /// The entry a filter value stands for, or null when none does. A null value means the entry
        /// for the rows carrying no id, which a column whose key cannot be null does not offer - read
        /// as <c>default(TKey)</c> it would tick the entry whose id happens to be zero.
        /// </summary>
        static object? EntryFor(List<object> offered, object? value)
        {
            for (var i = 0; i < offered.Count; i++)
            {
                if (offered[i] is not FastGridLookupEntry<TKey> entry)
                {
                    continue;
                }

                var matched = value is null
                    ? entry.Key is null
                    : value is TKey typed && EqualityComparer<TKey>.Default.Equals(entry.Key!, typed);

                if (matched)
                {
                    return offered[i];
                }
            }

            return null;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Typed at <typeparamref name="TKey" /> rather than closed over a run-time type, so nothing
        /// here needs code generated at run time - and a null key survives, which is what makes the
        /// blank entry a filter rather than a tick that narrows nothing.
        /// </remarks>
        internal override object FilterValueFromSelection(IEnumerable selected)
        {
            var keys = new List<TKey?>();

            foreach (var item in selected)
            {
                if (item is FastGridLookupEntry<TKey> entry)
                {
                    keys.Add(entry.Key);
                }
            }

            return keys;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Typed at <typeparamref name="TKey" /> without closing a generic over a run-time type, and a
        /// null key survives - the entry for the rows carrying no id is a real choice on this column,
        /// which is what <c>SelectedKeys</c> reads it back as.
        /// </remarks>
        internal override object RestoredSelection(IReadOnlyList<object?> values)
        {
            var keys = new List<TKey?>();

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is TKey typed)
                {
                    keys.Add(typed);
                }
                else if (values[i] is null && KeyCanBeNull)
                {
                    // The entry for the rows carrying no id, and only where this column offers one - the
                    // same guard SelectedKeys applies, for the same reason: read as default(TKey) on a
                    // column whose key cannot be null, a stray null filters to the rows whose id happens
                    // to be zero. Without this arm a ticked "(none)" was simply lost on the way back.
                    keys.Add(default);
                }
            }

            return keys;
        }

        /// <summary>
        /// The single condition this column composes for itself, or null when it must decline.
        /// </summary>
        /// <remarks>
        /// These columns know one operator - <c>In</c>, and its negation - and compose it out of a typed
        /// list of ids. A compound is declined whole rather than half-composed: the reflective builder
        /// takes nested composites since §33, so <c>In [...] OR IsNull</c> is answered correctly there,
        /// and answering the first half here and losing the second would be the silent kind of wrong.
        /// It costs that column the typed route, which it had already lost the moment it declined.
        /// </remarks>
        private protected FastGridFilterCondition? SoleCondition =>
            ActiveFilter is { Second: null } filter ? filter.First : null;

        /// <inheritdoc />
        /// <remarks>
        /// Overridden because the base's promise does not hold here. It checks the shape only, on the
        /// grounds that the predicate builders convert the elements - which <c>FilterExpression.Listed</c>
        /// does and <see cref="SelectedKeys" /> does not: it keeps what is already a
        /// <typeparamref name="TKey" /> and drops the rest on the floor. So a stored id that came back
        /// as anything else - a widened number from a serializer that reads every JSON integer as
        /// <see cref="long" />, a <c>JValue</c>, a descriptor from a <c>RadzenDataFilter</c> - left this
        /// column composing <c>Contains</c> over an empty list. That is a grid showing **no** rows while
        /// reporting itself filtered, which is the direction §32's gate calls the unsafe one.
        /// <para>
        /// Converted once here rather than on every render inside <see cref="SelectedKeys" />, which is
        /// on the composition path.
        /// </para>
        /// </remarks>
        internal override object? RestoredSequence(object value)
        {
            if (value is not IEnumerable sequence || value is string)
            {
                return null;
            }

            var keys = new List<TKey?>();
            var rebuilt = 0;
            var lost = false;

            foreach (var item in sequence)
            {
                if (item is TKey typed)
                {
                    keys.Add(typed);
                }
                else if (item is null)
                {
                    // A null cannot be carried in this list at all where TKey is a value type: TKey is
                    // unconstrained, so List<TKey?> is List<int> for an int key and default(TKey) is
                    // *zero*. Adding it ticked the entry whose id happens to be zero - the precise fault
                    // SelectedKeys' own comment warns about, and a test caught it here. Nothing about
                    // this list needs rebuilding for a null, so the stored one is handed back untouched
                    // and SelectedKeys applies KeyCanBeNull on the composition path as it always has.
                    return value;
                }
                else if (Converted(item, typeof(TKey), typeof(TKey)) is TKey converted)
                {
                    keys.Add(converted);
                    rebuilt++;
                }
                else
                {
                    lost = true;
                }
            }

            // Nothing was an id and nothing became one, so there is no filter here to rebuild. Dropping
            // it shows every row, where passing it on leaves SelectedKeys composing Contains over an
            // empty list and showing none.
            if (lost && keys.Count == 0)
            {
                return null;
            }

            // Only stand in for the stored list where converting actually changed something. A list this
            // column could already read is left as it is, so nothing downstream sees a new object for a
            // filter that did not change.
            return rebuilt > 0 ? keys : value;
        }

        /// <summary>
        /// The ids a filter box's text means: the names are matched in memory and the ids they carry
        /// are emitted as an <c>In</c>. Matching the ids as text would be useless - nobody types 47
        /// looking for Toys - and refusing simple mode outright would leave <c>FilterMode</c> with a
        /// value that does nothing on one kind of column.
        /// </summary>
        /// <remarks>
        /// Text matching two hundred names emits two hundred ids, and providers have parameter limits,
        /// so the number of ids is capped at <see cref="MatchLimit" /> rather than being a surprise at
        /// run time. Past it the filter is the first <see cref="MatchLimit" /> by name.
        /// </remarks>
        internal override object? FilterValueFromText(string? text)
        {
            if (string.IsNullOrEmpty(text) || FilterValues is not List<object> offered)
            {
                return null;
            }

            var keys = new List<TKey?>();

            for (var i = 0; i < offered.Count && keys.Count < MatchLimit; i++)
            {
                // Names only. The blank entry - the one with no id - is labelled by the grid rather
                // than by the lookup, and it is a different word in every culture Radzen ships, so
                // matching it would make what a typed filter finds depend on the language of the page.
                if (offered[i] is FastGridLookupEntry<TKey> { Key: not null } entry
                    && entry.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(entry.Key);
                }
            }

            return keys;
        }

        /// <summary>
        /// How many ids a typed filter emits before it stops. SQL Server takes 2,100 parameters and
        /// other providers less; this leaves room under all of them for the rest of the query.
        /// </summary>
        public const int MatchLimit = 500;

        /// <inheritdoc />
        /// <remarks>
        /// Text that matched no name is a filter that matches no row, not an absent filter. An empty
        /// selection means the two opposite things on the two controls this column offers: no box
        /// ticked is no filter, and a typed name nothing answers to is an empty answer. What tells them
        /// apart is that only the box records what was typed.
        /// </remarks>
        public override bool HasFilter =>
            CanFilter && (AppliedFilterText is { Length: > 0 } || base.HasFilter);

        /// <summary>
        /// <c>List&lt;TKey?&gt;.Contains</c>, captured from a typed lambda rather than looked up by
        /// name: an ldtoken the compiler emits, closed over <typeparamref name="TKey" /> where it is
        /// still a type parameter, so there is nothing for a trimmer to root and nothing closed at run
        /// time. Both derived columns compose their <c>In</c> out of it.
        /// </summary>
        private protected static readonly MethodInfo ListContains =
            ((MethodCallExpression)((Expression<Func<List<TKey?>, TKey?, bool>>)(
                (keys, id) => keys.Contains(id))).Body).Method;

        /// <summary>The ids a check-box list or a typed filter settled on, as this column's own type.</summary>
        private protected List<TKey?> SelectedKeys()
        {
            var keys = new List<TKey?>();

            if (FirstCondition?.Value is not IEnumerable selected || FirstCondition.Value is string)
            {
                return keys;
            }

            foreach (var value in selected)
            {
                if (value is TKey typed)
                {
                    keys.Add(typed);
                }
                else if (value is null && KeyCanBeNull)
                {
                    // The entry for the rows carrying no id, which a column whose key cannot be one
                    // does not offer. These values are not always ones this column produced -
                    // ApplyFilters takes descriptors from a RadzenDataFilter and from stored settings -
                    // and read as default(TKey) a stray null filters to the rows whose id happens to be
                    // zero while the check-box list beside it shows nothing ticked. That is the rule the
                    // picker already applies; this is the method that composes the predicate.
                    keys.Add(default);
                }
            }

            return keys;
        }
    }
}
