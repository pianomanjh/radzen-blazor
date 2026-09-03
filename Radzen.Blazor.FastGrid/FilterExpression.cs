using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Joins the columns' predicates into the one the query is filtered by.
    /// </summary>
    internal static class FilterPredicate
    {
        /// <summary>
        /// <paramref name="left" /> and <paramref name="right" /> as one predicate.
        /// </summary>
        /// <remarks>
        /// The bodies are joined rather than the lambdas invoked, so what a provider receives is a
        /// single Where with one boolean expression in it - which is what it can translate. Each column
        /// built its lambda over its own parameter, so the second body is rebound to the first's before
        /// they are joined; two lambdas that both say "x" are still two different parameters.
        /// </remarks>
        internal static Expression<Func<TItem, bool>> Join<TItem>(Expression<Func<TItem, bool>> left,
            Expression<Func<TItem, bool>> right, LogicalFilterOperator logical)
        {
            var parameter = left.Parameters[0];
            var body = new Rebind(right.Parameters[0], parameter).Visit(right.Body)!;

            return Expression.Lambda<Func<TItem, bool>>(
                logical == LogicalFilterOperator.Or
                    ? Expression.OrElse(left.Body, body)
                    : Expression.AndAlso(left.Body, body),
                parameter);
        }

        sealed class Rebind : ExpressionVisitor
        {
            readonly ParameterExpression from;
            readonly ParameterExpression to;

            internal Rebind(ParameterExpression from, ParameterExpression to)
            {
                this.from = from;
                this.to = to;
            }

            protected override Expression VisitParameter(ParameterExpression node) =>
                node == from ? to : node;
        }
    }

    /// <summary>
    /// Builds a column's filter predicate from the column's own typed selector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning that put <c>ApplySort</c> on the column puts this here: only the column knows
    /// <typeparamref name="TProp" />, and knowing it statically is the difference between composing a
    /// predicate and reflecting one into existence. Radzen's <c>QueryableExtension.Where</c> takes a
    /// property <em>path</em> and a runtime <see cref="Type" />, so it has to walk members by name and
    /// close generic methods with <c>MakeGenericMethod</c> - correct, and not something an ahead-of-time
    /// compiler can see through. Everything here is an ordinary generic call.
    /// </para>
    /// <para>
    /// The behaviour is meant to match that of <c>QueryableExtension.Where</c> exactly, and is checked
    /// against it row for row in <c>FilterExpressionParityTests</c> rather than by reading. That includes
    /// the parts that are surprising: a null string compares as the empty string for every operator
    /// except <c>IsNull</c>, an in-memory source compares case-insensitively with
    /// <see cref="StringComparison.OrdinalIgnoreCase" /> while a queryable provider gets
    /// <c>ToLower()</c> because it cannot translate the former, and a value that will not convert to the
    /// column's type drops out of an <c>In</c> list rather than throwing.
    /// </para>
    /// </remarks>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TProp">The filtered property's type, known statically. That is the whole point.</typeparam>
    internal static class FilterExpression<TItem, TProp>
    {
        // Captured from a typed lambda rather than looked up by name. GetMethod("Contains", ...) is a
        // reflection call the trimmer has to be told about; this is an ldtoken the compiler emits, so
        // there is nothing to root and nothing to close at run time - including for the generic ones,
        // which the compiler closes over TProp here rather than MakeGenericMethod closing them later.
        static MethodInfo MethodOf<TResult>(Expression<Func<TResult>> expression) =>
            ((MethodCallExpression)expression.Body).Method;

        static readonly MethodInfo StringContains = MethodOf(() => default(string)!.Contains(default(string)!));
        static readonly MethodInfo StringStartsWith = MethodOf(() => default(string)!.StartsWith(default(string)!));
        static readonly MethodInfo StringEndsWith = MethodOf(() => default(string)!.EndsWith(default(string)!));
        // ToLower(), not ToLowerInvariant() or ToUpperInvariant(): this MethodInfo goes into an
        // expression tree for a database to translate, and it has to be the one QueryableExtension puts
        // there or the same filter means two different things depending on which grid drew it. The
        // culture rules the analyzers are protecting are the provider's collation here, not ours.
        [SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "Must match the expression QueryableExtension builds, which a provider translates to its own collation.")]
        [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version", Justification = "Must match the expression QueryableExtension builds, which a provider translates to its own collation.")]
        static readonly MethodInfo StringToLower = MethodOf(() => default(string)!.ToLower());

        static readonly MethodInfo OrdinalEquals =
            MethodOf(() => default(string)!.Equals(default(string), default));
        static readonly MethodInfo OrdinalContains =
            MethodOf(() => default(string)!.Contains(default(string)!, default));
        static readonly MethodInfo OrdinalStartsWith =
            MethodOf(() => default(string)!.StartsWith(default(string)!, default));
        static readonly MethodInfo OrdinalEndsWith =
            MethodOf(() => default(string)!.EndsWith(default(string)!, default));

        /// <summary>The underlying type of a nullable column, or the column's own type.</summary>
        static readonly Type Underlying = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);

        static readonly bool IsNullable =
            !typeof(TProp).IsValueType || Nullable.GetUnderlyingType(typeof(TProp)) is not null;

        /// <summary>
        /// The predicate for one column's filter, or null when the operator says nothing about this
        /// column's type - a <c>StartsWith</c> on an int, say, which is a filter that cannot be built
        /// rather than one that matches nothing.
        /// </summary>
        /// <param name="selector">The column's own property expression.</param>
        /// <param name="filterOperator">The operator to compare with.</param>
        /// <param name="value">The value to compare against, already converted to the column's type.</param>
        /// <param name="caseSensitivity">Whether string comparisons ignore case.</param>
        /// <param name="inMemory">
        /// Whether the source is LINQ to Objects. Only it can use the <see cref="StringComparison" />
        /// overloads; a queryable provider cannot translate them and gets <c>ToLower()</c> instead.
        /// </param>
        internal static Expression<Func<TItem, bool>>? For(Expression<Func<TItem, TProp>> selector,
            FilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            var body = Body(selector.Body, filterOperator, value, caseSensitivity, inMemory);

            return body is null ? null : Expression.Lambda<Func<TItem, bool>>(body, selector.Parameters);
        }

        static Expression? Body(Expression property, FilterOperator filterOperator, object? value,
            FilterCaseSensitivity caseSensitivity, bool inMemory)
        {
            // The raw property, before the null-coalescing below: asking whether something is null and
            // then reading it through a coalesce that replaced the null is asking nothing.
            if (filterOperator is FilterOperator.IsNull or FilterOperator.IsNotNull)
            {
                return IsNullable
                    ? Compare(filterOperator is FilterOperator.IsNull, property,
                        Expression.Constant(null, typeof(TProp)))
                    // A non-nullable column is never null, and saying so is more useful than throwing.
                    : Expression.Constant(filterOperator is FilterOperator.IsNotNull);
            }

            if (filterOperator is FilterOperator.IsEmpty or FilterOperator.IsNotEmpty)
            {
                return typeof(TProp) == typeof(string)
                    ? Compare(filterOperator is FilterOperator.IsEmpty, property,
                        Expression.Constant(string.Empty, typeof(TProp)))
                    : null;
            }

            if (filterOperator is FilterOperator.In or FilterOperator.NotIn)
            {
                return In(property, value, filterOperator is FilterOperator.NotIn);
            }

            return typeof(TProp) == typeof(string)
                ? Text(property, filterOperator, value, caseSensitivity, inMemory)
                : Comparison(property, filterOperator, value);
        }

        static BinaryExpression Compare(bool equal, Expression left, Expression right) =>
            equal ? Expression.Equal(left, right) : Expression.NotEqual(left, right);

        // A null string is the empty string to every operator but IsNull, which is what
        // QueryableExtension does and what stops a Contains from throwing on a half-populated row.
        static Expression NotNull(Expression property) =>
            typeof(TProp) == typeof(string)
                ? Expression.Coalesce(property, Expression.Constant(string.Empty, typeof(string)))
                : property;

        [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Lowercase to match the ToLower() applied to the column, which is what QueryableExtension emits.")]
        static Expression? Text(Expression property, FilterOperator filterOperator, object? value,
            FilterCaseSensitivity caseSensitivity, bool inMemory)
        {
            var text = value as string ?? value?.ToString();
            var insensitive = caseSensitivity == FilterCaseSensitivity.CaseInsensitive;
            var target = NotNull(property);

            // OrdinalIgnoreCase is both faster and more correct than lowercasing both sides, and is
            // exactly what an in-memory source can run. A provider gets the ToLower pair because that is
            // what it can turn into SQL.
            if (insensitive && inMemory)
            {
                var constant = Expression.Constant(text, typeof(string));
                var ordinal = Expression.Constant(StringComparison.OrdinalIgnoreCase);

                return filterOperator switch
                {
                    FilterOperator.Equals => Expression.Call(target, OrdinalEquals, constant, ordinal),
                    FilterOperator.NotEquals =>
                        Expression.Not(Expression.Call(target, OrdinalEquals, constant, ordinal)),
                    FilterOperator.Contains => Expression.Call(target, OrdinalContains, constant, ordinal),
                    FilterOperator.DoesNotContain =>
                        Expression.Not(Expression.Call(target, OrdinalContains, constant, ordinal)),
                    FilterOperator.StartsWith => Expression.Call(target, OrdinalStartsWith, constant, ordinal),
                    FilterOperator.EndsWith => Expression.Call(target, OrdinalEndsWith, constant, ordinal),
                    _ => Ordered(target, filterOperator, Expression.Constant(text, typeof(string))),
                };
            }

            if (insensitive)
            {
                target = Expression.Call(target, StringToLower);
                text = text?.ToLowerInvariant();
            }

            var lowered = Expression.Constant(text, typeof(string));

            return filterOperator switch
            {
                FilterOperator.Equals => Expression.Equal(target, lowered),
                FilterOperator.NotEquals => Expression.NotEqual(target, lowered),
                FilterOperator.Contains => Expression.Call(target, StringContains, lowered),
                FilterOperator.DoesNotContain => Expression.Not(Expression.Call(target, StringContains, lowered)),
                FilterOperator.StartsWith => Expression.Call(target, StringStartsWith, lowered),
                FilterOperator.EndsWith => Expression.Call(target, StringEndsWith, lowered),
                _ => Ordered(target, filterOperator, lowered),
            };
        }

        static BinaryExpression? Comparison(Expression property, FilterOperator filterOperator, object? value)
        {
            if (Coerce(value) is not { } constant)
            {
                return null;
            }

            var target = NotNull(property);

            return filterOperator switch
            {
                FilterOperator.Equals => Expression.Equal(target, constant),
                FilterOperator.NotEquals => Expression.NotEqual(target, constant),

                // Contains and the rest are string operators; on anything else there is no predicate to
                // build, and building the wrong one silently is worse than declining.
                FilterOperator.Contains or FilterOperator.DoesNotContain
                    or FilterOperator.StartsWith or FilterOperator.EndsWith => null,
                _ => Ordered(target, filterOperator, constant),
            };
        }

        static BinaryExpression? Ordered(Expression target, FilterOperator filterOperator, Expression constant) =>
            filterOperator switch
            {
                FilterOperator.LessThan => Expression.LessThan(target, constant),
                FilterOperator.LessThanOrEquals => Expression.LessThanOrEqual(target, constant),
                FilterOperator.GreaterThan => Expression.GreaterThan(target, constant),
                FilterOperator.GreaterThanOrEquals => Expression.GreaterThanOrEqual(target, constant),
                _ => null,
            };

        /// <summary>The filter value as a constant of the column's own type, or null if it is not one.</summary>
        static ConstantExpression? Coerce(object? value)
        {
            if (value is null)
            {
                return IsNullable ? Expression.Constant(null, typeof(TProp)) : null;
            }

            if (value is TProp typed)
            {
                return Expression.Constant(typed, typeof(TProp));
            }

            return Converted(value) is { } converted
                ? Expression.Constant(converted, typeof(TProp))
                : null;
        }

        /// <summary>
        /// The value as the column's underlying type. Reached when a filter value arrives from somewhere
        /// that did not know the column's type - a declared <c>FilterValue</c> of the wrong width, a
        /// stored setting read back from JSON, an int for an enum.
        /// </summary>
        static object? Converted(object value)
        {
            try
            {
                return Underlying.IsEnum
                    ? Enum.ToObject(Underlying, value)
                    : Convert.ChangeType(value, Underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException
                or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// The same filter as <see cref="For" />, composed as a delegate over the column's compiled
        /// getter rather than as an expression tree.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a source that is already a list, the expression tree is the expensive way round. Handing
        /// one to <c>Queryable.Where</c> over a <c>List&lt;T&gt;</c> wraps it in an
        /// <c>EnumerableQuery</c>, which rewrites and recompiles the tree <em>every time the result is
        /// enumerated</em>. Measured at 1000 rows: 1,117 us and 11.8 KB that way, 38 us and 0.07 KB
        /// through a delegate - and the whole bare render is 1,800 us, so an in-memory grid was paying
        /// most of a second render just to filter.
        /// </para>
        /// <para>
        /// Composed rather than compiled, too: <c>Expression.Compile()</c> costs about 250 us here, and
        /// under Native AOT it cannot emit code at all and falls back to the interpreter. Closures need
        /// neither.
        /// </para>
        /// <para>
        /// Two implementations of sixteen operators is a real risk of divergence, so they are checked
        /// against each other row for row in <c>FilterExpressionParityTests</c>, the same way both are
        /// checked against the reflective builder they replaced.
        /// </para>
        /// </remarks>
        internal static Func<TItem, bool>? PredicateFor(Func<TItem, TProp> selector,
            FilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity)
        {
            if (filterOperator is FilterOperator.IsNull or FilterOperator.IsNotNull)
            {
                if (!IsNullable)
                {
                    var constant = filterOperator is FilterOperator.IsNotNull;

                    return _ => constant;
                }

                return filterOperator is FilterOperator.IsNull
                    ? item => selector(item) is null
                    : item => selector(item) is not null;
            }

            if (typeof(TProp) == typeof(string))
            {
                return TextPredicate((Func<TItem, string?>)(object)selector, filterOperator, value,
                    caseSensitivity);
            }

            if (filterOperator is FilterOperator.IsEmpty or FilterOperator.IsNotEmpty)
            {
                return null;
            }

            if (filterOperator is FilterOperator.In or FilterOperator.NotIn)
            {
                return InPredicate(selector, value, filterOperator is FilterOperator.NotIn);
            }

            return ComparisonPredicate(selector, filterOperator, value);
        }

        static Func<TItem, bool>? TextPredicate(Func<TItem, string?> selector,
            FilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity)
        {
            var text = value as string ?? value?.ToString();

            // Length rather than IsNullOrEmpty, and the difference is the point: the expression builder
            // compares the raw property to string.Empty, so a null is *not* empty. IsNullOrEmpty would
            // quietly disagree with it for exactly the rows this operator exists to sort out.
            if (filterOperator is FilterOperator.IsEmpty)
            {
                return item => selector(item)?.Length == 0;
            }

            if (filterOperator is FilterOperator.IsNotEmpty)
            {
                return item => selector(item)?.Length != 0;
            }

            if (filterOperator is FilterOperator.In or FilterOperator.NotIn)
            {
                return InPredicate((Func<TItem, TProp>)(object)selector, value,
                    filterOperator is FilterOperator.NotIn);
            }

            // OrdinalIgnoreCase, matching what the expression builder emits for an in-memory source -
            // which is the only kind that reaches this at all.
            var comparison = caseSensitivity == FilterCaseSensitivity.CaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // A null string is the empty string to every operator but IsNull, as it is over there.
            return filterOperator switch
            {
                FilterOperator.Equals => item => (selector(item) ?? "").Equals(text, comparison),
                FilterOperator.NotEquals => item => !(selector(item) ?? "").Equals(text, comparison),
                FilterOperator.Contains => item => (selector(item) ?? "").Contains(text ?? "", comparison),
                FilterOperator.DoesNotContain =>
                    item => !(selector(item) ?? "").Contains(text ?? "", comparison),
                FilterOperator.StartsWith => item => (selector(item) ?? "").StartsWith(text ?? "", comparison),
                FilterOperator.EndsWith => item => (selector(item) ?? "").EndsWith(text ?? "", comparison),
                _ => null,
            };
        }

        static Func<TItem, bool>? ComparisonPredicate(Func<TItem, TProp> selector,
            FilterOperator filterOperator, object? value)
        {
            if (Coerce(value) is not { } constant)
            {
                return null;
            }

            var key = (TProp?)constant.Value;

            return filterOperator switch
            {
                FilterOperator.Equals => item => EqualityComparer<TProp>.Default.Equals(selector(item), key!),
                FilterOperator.NotEquals =>
                    item => !EqualityComparer<TProp>.Default.Equals(selector(item), key!),
                FilterOperator.Contains or FilterOperator.DoesNotContain
                    or FilterOperator.StartsWith or FilterOperator.EndsWith => null,
                _ => OrderedPredicate(selector, filterOperator, key),
            };
        }

        /// <summary>
        /// An ordering comparison, with the null handling a lifted operator has rather than the one
        /// <see cref="Comparer{T}" /> has: over a nullable column, <c>x &lt; 3</c> is false for a row
        /// whose value is missing, where the comparer would sort it below everything.
        /// </summary>
        static Func<TItem, bool>? OrderedPredicate(Func<TItem, TProp> selector,
            FilterOperator filterOperator, TProp? key)
        {
            if (key is null)
            {
                return null;
            }

            var comparer = Comparer<TProp>.Default;

            return filterOperator switch
            {
                FilterOperator.LessThan =>
                    item => selector(item) is { } v && comparer.Compare(v, key) < 0,
                FilterOperator.LessThanOrEquals =>
                    item => selector(item) is { } v && comparer.Compare(v, key) <= 0,
                FilterOperator.GreaterThan =>
                    item => selector(item) is { } v && comparer.Compare(v, key) > 0,
                FilterOperator.GreaterThanOrEquals =>
                    item => selector(item) is { } v && comparer.Compare(v, key) >= 0,
                _ => null,
            };
        }

        static Func<TItem, bool> InPredicate(Func<TItem, TProp> selector, object? value, bool negate)
        {
            if (value is not IEnumerable sequence || value is string)
            {
                return _ => true;
            }

            var values = Listed(sequence);

            // The rule NotNull applies on the expression side, and that every other predicate in this
            // builder applies inline: a null string is the empty string to every operator but IsNull.
            // In was the one that did not, so a grid over a List answered a check-box-list filter
            // differently from the same grid over a queryable - and the list was the side disagreeing
            // with QueryableExtension.
            var read = NotNullRead(selector);

            return negate
                ? item => !values.Contains(read(item))
                : item => values.Contains(read(item));
        }

        /// <summary>
        /// <see cref="NotNull" /> for a delegate: the selector, reading a null string as the empty one.
        /// </summary>
        static Func<TItem, TProp> NotNullRead(Func<TItem, TProp> selector)
        {
            if (typeof(TProp) != typeof(string))
            {
                return selector;
            }

            var empty = (TProp)(object)string.Empty;

            return item => selector(item) is { } read ? read : empty;
        }

        static readonly MethodInfo ListContains =
            MethodOf(() => default(List<TProp>)!.Contains(default!));

        /// <summary>The sequence as a list of the column's own type, dropping what will not convert.</summary>
        /// <remarks>
        /// A null in the list is dropped rather than matched: it is what an untouched check box puts
        /// there, not a request to match rows whose value is missing.
        /// </remarks>
        static List<TProp> Listed(IEnumerable sequence)
        {
            var values = new List<TProp>();

            foreach (var item in sequence)
            {
                if (item is null)
                {
                    continue;
                }

                if (item is TProp typed)
                {
                    values.Add(typed);
                }
                else if (Converted(item) is { } converted)
                {
                    values.Add((TProp)converted);
                }
            }

            return values;
        }

        static Expression? In(Expression property, object? value, bool negate)
        {
            // Not a sequence, so there is nothing to be in. QueryableExtension answers true here rather
            // than null - the filter is not expressible, so it narrows nothing.
            if (value is not IEnumerable sequence || value is string)
            {
                return Expression.Constant(true);
            }

            var values = Listed(sequence);

            var constant = Expression.Constant(values, typeof(List<TProp>));
            var contains = (Expression)Expression.Call(constant, ListContains, NotNull(property));

            return negate ? Expression.Not(contains) : contains;
        }
    }
}
