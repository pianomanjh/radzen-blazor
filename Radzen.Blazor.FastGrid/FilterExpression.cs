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
            var body = ExpressionRebind.Onto(right.Body, right.Parameters[0], parameter);

            return Expression.Lambda<Func<TItem, bool>>(
                logical == LogicalFilterOperator.Or
                    ? Expression.OrElse(left.Body, body)
                    : Expression.AndAlso(left.Body, body),
                parameter);
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
        /// Whether this column's type has an ordering at all, asked once per closed generic type.
        /// </summary>
        /// <remarks>
        /// <c>Expression.LessThan</c> throws for a type with no <c>&lt;</c> - a string, a Guid - and the
        /// builder used to let it: §32's review drove a stored <c>GreaterThan</c> on a string column into
        /// an <c>InvalidOperationException</c> inside the render. Declining is what the neighbouring case
        /// already does, where <c>Comparison</c> refuses <c>Contains</c> on a non-string "because
        /// building the wrong one silently is worse than declining". This is that rule, applied to the
        /// mirror it had missed - and <c>Between</c> needs it, being ordered by definition.
        /// </remarks>
        static readonly bool Orderable = CanOrder();

        static bool CanOrder()
        {
            try
            {
                Expression.LessThan(Expression.Default(typeof(TProp)), Expression.Default(typeof(TProp)));

                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

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
            FastGridFilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            var body = Body(selector.Body, filterOperator, value, second: null, caseSensitivity, inMemory);

            return body is null ? null : Expression.Lambda<Func<TItem, bool>>(body, selector.Parameters);
        }

        /// <summary>
        /// The predicate for a whole filter - one condition, or two joined - or null where none of it
        /// can be built.
        /// </summary>
        /// <remarks>
        /// <strong>A filter of one condition produces exactly the tree it always did.</strong> §33 makes
        /// that a gate rather than an intention: composing <c>c1 ⊕ c2</c> unconditionally, with a
        /// constant-true second half, would make every filtered column pay for a feature almost nobody
        /// switches on - on every row, forever. So the join only happens when there is something to join.
        /// <para>
        /// A second condition that cannot be built is dropped and the first stands, which is the
        /// opposite of what a *restore* does with the same situation. The two are different questions:
        /// restoring asks whether the user's filter can be reproduced, and a half-reproduced compound is
        /// a filter nobody wrote; composing asks what this provider can be told, and one condition it
        /// cannot express does not make the other meaningless.
        /// </para>
        /// </remarks>
        internal static Expression<Func<TItem, bool>>? For(Expression<Func<TItem, TProp>> selector,
            FastGridFilter filter, FilterCaseSensitivity caseSensitivity, bool inMemory)
        {
            var body = ConditionBody(selector.Body, filter.First, caseSensitivity, inMemory);

            if (filter.EffectiveSecond is { } second
                && ConditionBody(selector.Body, second, caseSensitivity, inMemory) is { } other)
            {
                body = body is null
                    ? other
                    : filter.LogicalFilterOperator == LogicalFilterOperator.Or
                        ? Expression.OrElse(body, other)
                        : Expression.AndAlso(body, other);
            }

            return body is null ? null : Expression.Lambda<Func<TItem, bool>>(body, selector.Parameters);
        }

        static Expression? ConditionBody(Expression property, FastGridFilterCondition condition,
            FilterCaseSensitivity caseSensitivity, bool inMemory) =>
            Body(property, condition.Operator, condition.Value, condition.SecondValue, caseSensitivity,
                inMemory);

        static Expression? Body(Expression property, FastGridFilterOperator filterOperator, object? value,
            object? second, FilterCaseSensitivity caseSensitivity, bool inMemory)
        {
            // The raw property, before the null-coalescing below: asking whether something is null and
            // then reading it through a coalesce that replaced the null is asking nothing.
            if (filterOperator is FastGridFilterOperator.IsNull or FastGridFilterOperator.IsNotNull)
            {
                return IsNullable
                    ? Compare(filterOperator is FastGridFilterOperator.IsNull, property,
                        Expression.Constant(null, typeof(TProp)))
                    // A non-nullable column is never null, and saying so is more useful than throwing.
                    : Expression.Constant(filterOperator is FastGridFilterOperator.IsNotNull);
            }

            if (filterOperator is FastGridFilterOperator.IsEmpty or FastGridFilterOperator.IsNotEmpty)
            {
                return typeof(TProp) == typeof(string)
                    ? Compare(filterOperator is FastGridFilterOperator.IsEmpty, property,
                        Expression.Constant(string.Empty, typeof(TProp)))
                    : null;
            }

            if (filterOperator is FastGridFilterOperator.Between)
            {
                return BetweenBody(property, value, second);
            }

            if (filterOperator is FastGridFilterOperator.In or FastGridFilterOperator.NotIn)
            {
                return In(property, value, filterOperator is FastGridFilterOperator.NotIn);
            }

            return typeof(TProp) == typeof(string)
                ? Text(property, filterOperator, value, caseSensitivity, inMemory)
                : Comparison(property, filterOperator, value);
        }

        /// <summary>An inclusive range, or null when this column's type has no ordering to range over.</summary>
        static BinaryExpression? BetweenBody(Expression property, object? from, object? to)
        {
            if (Coerce(from) is not { } lower || Coerce(to) is not { } upper)
            {
                return null;
            }

            var target = NotNull(property);

            if (Ordered(target, FastGridFilterOperator.GreaterThanOrEquals, lower) is not { } atLeast
                || Ordered(target, FastGridFilterOperator.LessThanOrEquals, upper) is not { } atMost)
            {
                return null;
            }

            return Expression.AndAlso(atLeast, atMost);
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
        static Expression? Text(Expression property, FastGridFilterOperator filterOperator, object? value,
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
                    FastGridFilterOperator.Equals => Expression.Call(target, OrdinalEquals, constant, ordinal),
                    FastGridFilterOperator.NotEquals =>
                        Expression.Not(Expression.Call(target, OrdinalEquals, constant, ordinal)),
                    FastGridFilterOperator.Contains => Expression.Call(target, OrdinalContains, constant, ordinal),
                    FastGridFilterOperator.DoesNotContain =>
                        Expression.Not(Expression.Call(target, OrdinalContains, constant, ordinal)),
                    FastGridFilterOperator.StartsWith => Expression.Call(target, OrdinalStartsWith, constant, ordinal),
                    FastGridFilterOperator.EndsWith => Expression.Call(target, OrdinalEndsWith, constant, ordinal),
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
                FastGridFilterOperator.Equals => Expression.Equal(target, lowered),
                FastGridFilterOperator.NotEquals => Expression.NotEqual(target, lowered),
                FastGridFilterOperator.Contains => Expression.Call(target, StringContains, lowered),
                FastGridFilterOperator.DoesNotContain => Expression.Not(Expression.Call(target, StringContains, lowered)),
                FastGridFilterOperator.StartsWith => Expression.Call(target, StringStartsWith, lowered),
                FastGridFilterOperator.EndsWith => Expression.Call(target, StringEndsWith, lowered),
                _ => Ordered(target, filterOperator, lowered),
            };
        }

        static BinaryExpression? Comparison(Expression property, FastGridFilterOperator filterOperator, object? value)
        {
            if (Coerce(value) is not { } constant)
            {
                return null;
            }

            var target = NotNull(property);

            return filterOperator switch
            {
                FastGridFilterOperator.Equals => Expression.Equal(target, constant),
                FastGridFilterOperator.NotEquals => Expression.NotEqual(target, constant),

                // Contains and the rest are string operators; on anything else there is no predicate to
                // build, and building the wrong one silently is worse than declining.
                FastGridFilterOperator.Contains or FastGridFilterOperator.DoesNotContain
                    or FastGridFilterOperator.StartsWith or FastGridFilterOperator.EndsWith => null,
                _ => Ordered(target, filterOperator, constant),
            };
        }

        static BinaryExpression? Ordered(Expression target, FastGridFilterOperator filterOperator, Expression constant) =>
            !Orderable ? null : filterOperator switch
            {
                FastGridFilterOperator.LessThan => Expression.LessThan(target, constant),
                FastGridFilterOperator.LessThanOrEquals => Expression.LessThanOrEqual(target, constant),
                FastGridFilterOperator.GreaterThan => Expression.GreaterThan(target, constant),
                FastGridFilterOperator.GreaterThanOrEquals => Expression.GreaterThanOrEqual(target, constant),
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
        /// The same filter as <see cref="For(System.Linq.Expressions.Expression{System.Func{TItem, TProp}}, FastGridFilter, FilterCaseSensitivity, bool)" />, composed as a delegate over the column's compiled
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
            FastGridFilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity) =>
            PredicateFor(selector, filterOperator, value, second: null, caseSensitivity);

        /// <summary>
        /// The delegate for a whole filter, with the same rule <see cref="For(System.Linq.Expressions.Expression{System.Func{TItem, TProp}}, FastGridFilter, FilterCaseSensitivity, bool)" /> follows: a filter of
        /// one condition composes exactly the delegate it always did, and the join only happens when
        /// there is something to join.
        /// </summary>
        internal static Func<TItem, bool>? PredicateFor(Func<TItem, TProp> selector, FastGridFilter filter,
            FilterCaseSensitivity caseSensitivity)
        {
            var predicate = ConditionPredicate(selector, filter.First, caseSensitivity);

            if (filter.EffectiveSecond is { } condition
                && ConditionPredicate(selector, condition, caseSensitivity) is { } other)
            {
                if (predicate is null)
                {
                    return other;
                }

                var first = predicate;

                predicate = filter.LogicalFilterOperator == LogicalFilterOperator.Or
                    ? item => first(item) || other(item)
                    : item => first(item) && other(item);
            }

            return predicate;
        }

        static Func<TItem, bool>? ConditionPredicate(Func<TItem, TProp> selector,
            FastGridFilterCondition condition, FilterCaseSensitivity caseSensitivity) =>
            PredicateFor(selector, condition.Operator, condition.Value, condition.SecondValue,
                caseSensitivity);

        static Func<TItem, bool>? PredicateFor(Func<TItem, TProp> selector,
            FastGridFilterOperator filterOperator, object? value, object? second,
            FilterCaseSensitivity caseSensitivity)
        {
            if (filterOperator is FastGridFilterOperator.Between)
            {
                return BetweenPredicate(selector, value, second);
            }

            if (filterOperator is FastGridFilterOperator.IsNull or FastGridFilterOperator.IsNotNull)
            {
                if (!IsNullable)
                {
                    var constant = filterOperator is FastGridFilterOperator.IsNotNull;

                    return _ => constant;
                }

                return filterOperator is FastGridFilterOperator.IsNull
                    ? item => selector(item) is null
                    : item => selector(item) is not null;
            }

            if (typeof(TProp) == typeof(string))
            {
                return TextPredicate((Func<TItem, string?>)(object)selector, filterOperator, value,
                    caseSensitivity);
            }

            if (filterOperator is FastGridFilterOperator.IsEmpty or FastGridFilterOperator.IsNotEmpty)
            {
                return null;
            }

            if (filterOperator is FastGridFilterOperator.In or FastGridFilterOperator.NotIn)
            {
                return InPredicate(selector, value, filterOperator is FastGridFilterOperator.NotIn);
            }

            return ComparisonPredicate(selector, filterOperator, value);
        }

        /// <summary>An inclusive range, matching what <see cref="BetweenBody" /> builds.</summary>
        /// <remarks>
        /// Both bounds through <see cref="Coerce" />, and the lifted-operator null handling
        /// <see cref="OrderedPredicate" /> already spells out: over a nullable column a row with no value
        /// is outside every range, where a comparer would sort it below everything.
        /// </remarks>
        static Func<TItem, bool>? BetweenPredicate(Func<TItem, TProp> selector, object? from, object? to)
        {
            if (!Orderable || Coerce(from) is not { } lower || Coerce(to) is not { } upper)
            {
                return null;
            }

            var least = (TProp?)lower.Value;
            var most = (TProp?)upper.Value;

            if (least is null || most is null)
            {
                return null;
            }

            var comparer = Comparer<TProp>.Default;

            return item => selector(item) is { } v
                && comparer.Compare(v, least) >= 0
                && comparer.Compare(v, most) <= 0;
        }

        static Func<TItem, bool>? TextPredicate(Func<TItem, string?> selector,
            FastGridFilterOperator filterOperator, object? value, FilterCaseSensitivity caseSensitivity)
        {
            var text = value as string ?? value?.ToString();

            // Length rather than IsNullOrEmpty, and the difference is the point: the expression builder
            // compares the raw property to string.Empty, so a null is *not* empty. IsNullOrEmpty would
            // quietly disagree with it for exactly the rows this operator exists to sort out.
            if (filterOperator is FastGridFilterOperator.IsEmpty)
            {
                return item => selector(item)?.Length == 0;
            }

            if (filterOperator is FastGridFilterOperator.IsNotEmpty)
            {
                return item => selector(item)?.Length != 0;
            }

            if (filterOperator is FastGridFilterOperator.In or FastGridFilterOperator.NotIn)
            {
                return InPredicate((Func<TItem, TProp>)(object)selector, value,
                    filterOperator is FastGridFilterOperator.NotIn);
            }

            // OrdinalIgnoreCase, matching what the expression builder emits for an in-memory source -
            // which is the only kind that reaches this at all.
            var comparison = caseSensitivity == FilterCaseSensitivity.CaseInsensitive
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // A null string is the empty string to every operator but IsNull, as it is over there.
            return filterOperator switch
            {
                FastGridFilterOperator.Equals => item => (selector(item) ?? "").Equals(text, comparison),
                FastGridFilterOperator.NotEquals => item => !(selector(item) ?? "").Equals(text, comparison),
                FastGridFilterOperator.Contains => item => (selector(item) ?? "").Contains(text ?? "", comparison),
                FastGridFilterOperator.DoesNotContain =>
                    item => !(selector(item) ?? "").Contains(text ?? "", comparison),
                FastGridFilterOperator.StartsWith => item => (selector(item) ?? "").StartsWith(text ?? "", comparison),
                FastGridFilterOperator.EndsWith => item => (selector(item) ?? "").EndsWith(text ?? "", comparison),
                _ => null,
            };
        }

        static Func<TItem, bool>? ComparisonPredicate(Func<TItem, TProp> selector,
            FastGridFilterOperator filterOperator, object? value)
        {
            if (Coerce(value) is not { } constant)
            {
                return null;
            }

            var key = (TProp?)constant.Value;

            return filterOperator switch
            {
                FastGridFilterOperator.Equals => item => EqualityComparer<TProp>.Default.Equals(selector(item), key!),
                FastGridFilterOperator.NotEquals =>
                    item => !EqualityComparer<TProp>.Default.Equals(selector(item), key!),
                FastGridFilterOperator.Contains or FastGridFilterOperator.DoesNotContain
                    or FastGridFilterOperator.StartsWith or FastGridFilterOperator.EndsWith => null,
                _ => OrderedPredicate(selector, filterOperator, key),
            };
        }

        /// <summary>
        /// An ordering comparison, with the null handling a lifted operator has rather than the one
        /// <see cref="Comparer{T}" /> has: over a nullable column, <c>x &lt; 3</c> is false for a row
        /// whose value is missing, where the comparer would sort it below everything.
        /// </summary>
        static Func<TItem, bool>? OrderedPredicate(Func<TItem, TProp> selector,
            FastGridFilterOperator filterOperator, TProp? key)
        {
            if (key is null)
            {
                return null;
            }

            var comparer = Comparer<TProp>.Default;

            return filterOperator switch
            {
                FastGridFilterOperator.LessThan =>
                    item => selector(item) is { } v && comparer.Compare(v, key) < 0,
                FastGridFilterOperator.LessThanOrEquals =>
                    item => selector(item) is { } v && comparer.Compare(v, key) <= 0,
                FastGridFilterOperator.GreaterThan =>
                    item => selector(item) is { } v && comparer.Compare(v, key) > 0,
                FastGridFilterOperator.GreaterThanOrEquals =>
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
            //
            // Inlined into each lambda rather than wrapped around the selector, which is why this is
            // written twice: a wrapper would be a second delegate call on every row of every filter,
            // and it would be paid by the columns that need no coalescing at all. TextPredicate spells
            // its own out for the same reason.
            if (typeof(TProp) == typeof(string))
            {
                var empty = (TProp)(object)string.Empty;

                return negate
                    ? item => !values.Contains(selector(item) is { } read ? read : empty)
                    : item => values.Contains(selector(item) is { } read ? read : empty);
            }

            return negate
                ? item => !values.Contains(selector(item))
                : item => values.Contains(selector(item));
        }

        static readonly MethodInfo ListContains =
            MethodOf(() => default(List<TProp>)!.Contains(default!));

        /// <summary>The sequence as a list of the column's own type, dropping what will not convert.</summary>
        /// <remarks>
        /// <para>
        /// <strong>§36 gave a null a meaning here, and this comment used to deny it one.</strong> It
        /// said a null was what an untouched check box puts in the list rather than a request to match
        /// the rows whose value is missing - which was true while nothing could put one there on
        /// purpose. §36's blank entry is that request, and it arrives as a null exactly as §14's
        /// <c>SelectedKeys</c> has carried one for a nullable lookup key since it was built.
        /// </para>
        /// <para>
        /// It survives only where the column can hold one, and <em>where</em> is
        /// <c>default(TProp) is null</c> - every reference type and every <c>Nullable&lt;T&gt;</c>,
        /// which is the same set <c>ColumnBase.FilterNullable</c> offers the blank entry for. The two
        /// have to agree: written as a <c>Nullable.GetUnderlyingType</c> test this dropped the null on
        /// a column declared as <c>object</c>, so its blank ticked, committed, and narrowed to no rows
        /// at all - a box that filters nothing while saying it filters something. On a non-nullable
        /// value type a null read as <c>default</c> would filter to the rows whose value happens to be
        /// zero while the list showed nothing ticked, which is §14's own trap; those are still
        /// dropped.
        /// </para>
        /// <para>
        /// A string is the exception and it is this builder's existing rule rather than a new one: a
        /// null string is the empty string to every operator but <c>IsNull</c>, <c>In</c> coalesces the
        /// column through <c>NotNull</c>, and <c>InPredicate</c> reads a null row as empty. So the blank
        /// on a string column joins the list as the empty string, which is what a null row compares as
        /// on both sides.
        /// </para>
        /// </remarks>
        static List<TProp> Listed(IEnumerable sequence)
        {
            var values = new List<TProp>();

            foreach (var item in sequence)
            {
                if (item is null)
                {
                    if (typeof(TProp) == typeof(string))
                    {
                        values.Add((TProp)(object)string.Empty);
                    }
                    else if (default(TProp) is null)
                    {
                        values.Add(default!);
                    }

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
