using System.Linq.Expressions;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Rewrites an expression body onto another lambda's parameter, so two separately authored
    /// expressions can be composed into one the provider sees as a single tree.
    /// </summary>
    /// <remarks>
    /// Two lambdas that both say "x" are still two different parameters, and a provider handed the
    /// second one's body unchanged has nothing to bind it to.
    /// </remarks>
    internal sealed class ExpressionRebind : ExpressionVisitor
    {
        readonly ParameterExpression from;
        readonly ParameterExpression to;

        ExpressionRebind(ParameterExpression from, ParameterExpression to)
        {
            this.from = from;
            this.to = to;
        }

        internal static Expression Onto(Expression body, ParameterExpression from, ParameterExpression to) =>
            ReferenceEquals(from, to) ? body : new ExpressionRebind(from, to).Visit(body)!;

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : node;
    }
}
