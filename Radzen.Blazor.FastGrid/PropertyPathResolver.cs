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
        public static string? For<TItem, TProp>(Expression<Func<TItem, TProp>> expression) =>
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

            var body = expression.Body;

            // Expression<Func<T, object>> wraps a value type in a Convert. Strip it so both the typed
            // and the boxed authoring styles resolve to the same path.
            while (body is UnaryExpression unary
                && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                body = unary.Operand;
            }

            var parts = new List<string>();

            while (body is MemberExpression member)
            {
                parts.Insert(0, member.Member.Name);
                body = member.Expression!;
            }

            return body is ParameterExpression && parts.Count > 0 ? string.Join(".", parts) : null;
        }

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

            var path = For(left);

            return path is not null && path == For(right);
        }
    }
}
