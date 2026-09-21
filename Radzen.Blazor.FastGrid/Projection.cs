using System;
using System.Linq;
using System.Linq.Expressions;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Composes projections whose element type is only known at run time, for the distinct query behind
    /// a check-box-list filter.
    /// </summary>
    /// <remarks>
    /// Every member here closes a generic method over a type only known at run time, so every one of
    /// them needs code generated at run time. They are annotated rather than hidden, and every call site
    /// asks <see cref="DynamicCode" /> first.
    /// </remarks>
    internal static class Projection
    {
        /// <summary>
        /// The open generic definition of a <see cref="Queryable" /> method, taken from a lambda that
        /// calls it.
        /// </summary>
        /// <remarks>
        /// Rather than <c>typeof(Queryable).GetMethods().Single(...)</c>, which was both a signature
        /// match written out longhand and a request for every method on the type - including the
        /// annotated ones, which is what the trimmer objected to. A lambda names the method once, the
        /// compiler emits a handle for it, and the definition falls out of the closed one.
        /// </remarks>
        static MethodInfo DefinitionOf<T>(Expression<Func<IQueryable<T>, object>> call) =>
            Definition(call.Body);

        // The lambda is declared as returning object, and whether the compiler wraps the call in a
        // Convert to say so depends on what it returns: a boxing conversion gets one, a reference
        // conversion does not always. Both shapes have to be unwrapped, which is the same lesson the
        // column selectors learned - a declared object return hides the real one two different ways.
        static MethodInfo Definition(Expression body) =>
            ((MethodCallExpression)(body is UnaryExpression convert ? convert.Operand : body))
                .Method.GetGenericMethodDefinition();

        static readonly MethodInfo SelectMethod = DefinitionOf<object>(q => q.Select(x => x));

        static readonly MethodInfo SelectManyMethod =
            DefinitionOf<object>(q => q.SelectMany(x => new object[0]));

        /// <summary><c>source.Select(selector).Distinct()</c>, for a selector typed at run time.</summary>
        [RequiresDynamicCode(Reason)]
        [RequiresUnreferencedCode(Reason)]
        internal static IQueryable SelectDistinct(IQueryable source, LambdaExpression selector)
        {
            var projected = (IQueryable)SelectMethod
                .MakeGenericMethod(selector.Parameters[0].Type, selector.ReturnType)
                .Invoke(null, new object[] { source, selector })!;

            // QueryableExtension.Distinct, which composes the same call over source.ElementType. The
            // non-generic one is what is wanted here: the element type is not a type parameter.
            return projected.Distinct();
        }

        /// <summary><c>source.SelectMany(selector)</c>, for an element type known at run time.</summary>
        [RequiresDynamicCode(Reason)]
        internal static IQueryable SelectMany(IQueryable source, Type itemType, Type elementType,
            LambdaExpression selector) =>
            (IQueryable)SelectManyMethod
                .MakeGenericMethod(itemType, elementType)
                .Invoke(null, new object[] { source, selector })!;

        const string Reason =
            "Closes a LINQ method over a type only known at run time. Use a typed column - " +
            "PropertyColumn<TItem, TProp> or CollectionColumn<TItem, TElement> - which does not.";
    }
}
