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
        public static string? For<TItem, TProp>(Expression<Func<TItem, TProp>> expression)
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
    }
}
