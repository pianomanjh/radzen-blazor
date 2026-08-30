using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Composes projections whose element type is only known at run time, for the distinct query behind
    /// a check-box-list filter.
    /// </summary>
    internal static class Projection
    {
        static readonly MethodInfo SelectMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.Select)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                    .GetGenericArguments().Length == 2);

        static readonly MethodInfo SelectManyMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.SelectMany)
                && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments()[0]
                    .GetGenericArguments().Length == 2);

        /// <summary><c>source.Select(selector).Distinct()</c>, for a selector typed at run time.</summary>
        internal static IQueryable SelectDistinct(IQueryable source, LambdaExpression selector)
        {
            var projected = (IQueryable)SelectMethod
                .MakeGenericMethod(selector.Parameters[0].Type, selector.ReturnType)
                .Invoke(null, new object[] { source, selector })!;

            // QueryableExtension.Distinct, which composes the same call over source.ElementType.
            return projected.Distinct();
        }

        static readonly MethodInfo OrderByMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.OrderBy) && m.GetParameters().Length == 2);

        static readonly MethodInfo OrderByDescendingMethod = typeof(Queryable).GetMethods()
            .Single(m => m.Name == nameof(Queryable.OrderByDescending) && m.GetParameters().Length == 2);

        /// <summary><c>source.OrderBy(selector)</c>, for a key type known at run time.</summary>
        internal static IOrderedQueryable<T> OrderBy<T>(IQueryable<T> source, LambdaExpression selector,
            bool descending) =>
            (IOrderedQueryable<T>)(descending ? OrderByDescendingMethod : OrderByMethod)
                .MakeGenericMethod(typeof(T), selector.ReturnType)
                .Invoke(null, new object[] { source, selector })!;

        /// <summary><c>source.SelectMany(selector)</c>, for an element type known at run time.</summary>
        internal static IQueryable SelectMany(IQueryable source, Type itemType, Type elementType,
            LambdaExpression selector) =>
            (IQueryable)SelectManyMethod
                .MakeGenericMethod(itemType, elementType)
                .Invoke(null, new object[] { source, selector })!;
    }
}
