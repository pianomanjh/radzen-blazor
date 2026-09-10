using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Whether this component may reach a member by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Almost everything this grid does is composed from the columns' own typed expressions, which an
    /// ahead-of-time compiler can see through and a trimmer can follow. Four things cannot be: a filter
    /// on a template column's string path, a collection column whose element type is not a type
    /// parameter, the distinct scan behind a check-box-list filter, and the drop-down's value and text
    /// properties named as strings. Each reaches a member by name, which means reflection.
    /// </para>
    /// <para>
    /// Rather than mark the whole component as needing dynamic code - which would put a warning on every
    /// consumer, including the ones using none of those four - the reflective paths sit behind this
    /// switch. <c>FeatureGuard</c> tells the AOT analyzer that a branch guarded by it is only reachable
    /// where dynamic code is, so those warnings go without anything being suppressed.
    /// </para>
    /// <para>
    /// It guards <c>RequiresDynamicCode</c> and not <c>RequiresUnreferencedCode</c>, which is not an
    /// omission: the analyzer rejects every guard offered for the latter, including
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported" /> itself. The reason is sound - a switch read
    /// at run time cannot promise the trimmer anything at build time, because the trimmer has already
    /// finished by then. Trimming warnings are removed by not calling reflective code, which is what
    /// the typed filter composition in <c>FilterExpression</c> does, rather than by guarding it.
    /// </para>
    /// <para>
    /// Under Native AOT the reflective branches are removed from the application entirely, and a column
    /// that would have needed one declines - a sort that does not sort, a check-box list with no values
    /// to offer - rather than throwing from inside a render. Where declining is not available, the
    /// message from <see cref="Unavailable" /> names what was asked for and what to use instead.
    /// </para>
    /// </remarks>
    internal static class DynamicCode
    {
        /// <summary>Whether this runtime can generate the code these paths need.</summary>
        /// <remarks>
        /// The condition itself and nothing else. An earlier version and-ed an application-settable
        /// switch onto it; the analyzer rejects that, and rightly - a guard it cannot evaluate to a
        /// constant is not a guard. It also turned out to buy nothing, because the condition is already
        /// exactly right: false under Native AOT, true wherever a lambda can still be compiled.
        /// </remarks>
        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        internal static bool Supported => RuntimeFeature.IsDynamicCodeSupported;

        /// <summary>
        /// Thrown when an application has turned the switch off and something still needs a member
        /// reached by name. Says which column and what to do about it, because the alternative - the
        /// filter quietly matching everything - is a bug the developer would find much later.
        /// </summary>
        internal static NotSupportedException Unavailable(string what) =>
            new($"{what} needs code generated at run time, which Native AOT does not do. Use a typed " +
                "column - PropertyColumn<TItem, TProp> or CollectionColumn<TItem, TElement> - which " +
                "composes its own expression and needs none.");
    }
}
