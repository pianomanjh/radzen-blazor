using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Derives a dotted property path from a column's authored expression.
    /// </summary>
    /// <remarks>
    /// Columns are authored as expressions, but four things in the Radzen ecosystem consume property
    /// name strings and can do nothing with an expression: <c>LoadDataArgs.OrderBy</c>, OData
    /// <c>$orderby</c>, settings persistence keyed by property name, and <c>FilterDescriptor.Property</c>
    /// (which is what RadzenDataFilter emits). The path is derived once when the column initialises and
    /// cached, so the component gets compile-time-checked authoring and a stable string identity both.
    /// </remarks>
    public static class PropertyPathResolver
    {
        /// <summary>
        /// Returns the dotted path for <paramref name="expression" />, or <c>null</c> when it is not a
        /// simple member access — for example <c>p =&gt; p.First + " " + p.Last</c>. A column with no
        /// path renders normally but cannot sort server-side, round-trip through <c>LoadData</c>, or
        /// persist in settings.
        /// </summary>
        public static string? For<TItem, TProp>(Expression<Func<TItem, TProp>>? expression) =>
            For((LambdaExpression?)expression);

        /// <summary>
        /// The same, for a lambda whose types are only known at run time.
        /// </summary>
        public static string? For(LambdaExpression? expression)
        {
            if (expression is null)
            {
                return null;
            }

            var body = Unwrap(expression.Body);
            var parts = new List<string>();

            while (body is MemberExpression member)
            {
                parts.Insert(0, member.Member.Name);
                body = member.Expression!;
            }

            return body is ParameterExpression && parts.Count > 0 ? string.Join(".", parts) : null;
        }

        /// <summary>
        /// Strips the conversion an <c>Expression&lt;Func&lt;T, object&gt;&gt;</c> wraps its body in, so the
        /// typed and the boxed authoring styles are read the same way. One copy of the rule, because
        /// both the path derivation and the member selectors need it.
        /// </summary>
        public static Expression Unwrap(Expression body)
        {
            while (body is UnaryExpression unary
                && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            return body;
        }

        /// <summary>
        /// The type a dotted path reaches from <paramref name="root" />, or null when it does not
        /// resolve. The inverse of <see cref="For(LambdaExpression)" />, and deliberately next to it:
        /// a producer and a consumer of the same string that disagree fail silently.
        /// </summary>
        public static Type? TypeOf(Type root, string path) => PropertyAccess.GetPropertyType(root, path);

        /// <summary>
        /// Whether two authored expressions mean the same thing. Reference equality is not enough:
        /// Razor rebuilds every expression tree on every render, so a column authored in markup gets a
        /// new instance each time and recompiling on that alone means a compile per column per render.
        /// </summary>
        /// <remarks>
        /// A path is only derived for a plain member chain, which is exactly the shape that cannot
        /// capture anything - so two expressions with the same non-null path are interchangeable, and
        /// the compiled delegate for one is correct for the other. Anything computed has no path, is
        /// never treated as equivalent, and is recompiled.
        /// </remarks>
        public static bool Equivalent(LambdaExpression? left, LambdaExpression? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            // Walked rather than compared as strings: this runs for every expression parameter of every
            // column on every render, and deriving two paths to throw them away allocated a list and a
            // joined string each time. The rule is the one For applies, member name by member name.
            var a = Unwrap(left.Body);
            var b = Unwrap(right.Body);
            var matched = false;

            while (a is MemberExpression first && b is MemberExpression second
                && string.Equals(first.Member.Name, second.Member.Name, StringComparison.Ordinal))
            {
                matched = true;
                a = first.Expression!;
                b = second.Expression!;
            }

            // Both have to bottom out at the parameter with at least one member read along the way: a
            // chain that ends early is a different path, and a computed body has no path at all.
            return matched && a is ParameterExpression && b is ParameterExpression;
        }
    }
}
