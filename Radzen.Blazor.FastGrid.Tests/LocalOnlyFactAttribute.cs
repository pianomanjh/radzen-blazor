using System;
using Xunit;

namespace Radzen.Blazor.FastGrid.Tests
{
    /// <summary>
    /// A fact that does not run on a shared build agent, because it cannot measure there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the timing gates only, and only where the margin has been shown to close. The pass-cost gate
    /// is the case: correct code reads 1.48 to 2.20 against its own layout time over 53 runs spanning a
    /// quiet machine and ten saturated cores, and the fault it exists to catch reads 2.74 to 3.17. A
    /// GitHub-hosted runner measured <strong>2.54</strong> on correct code - above the correct band and
    /// below the fault's, which leaves no budget that tells the two apart.
    /// </para>
    /// <para>
    /// <strong>So this is not the usual "quarantine the flaky test".</strong> Widening the budget to let
    /// the runner through would put it inside the fault's own range, which is worse than not running it:
    /// a gate that cannot fail is a gate that reports success. The check still runs on every developer
    /// machine and in any self-hosted job, which is where it has always caught things.
    /// </para>
    /// <para>
    /// It follows the rule <c>fastgrid-parity.yml</c> already states for the browser - a thing the
    /// environment cannot provide should not be able to fail a job about something else.
    /// </para>
    /// </remarks>
    public sealed class LocalOnlyFactAttribute : FactAttribute
    {
        public LocalOnlyFactAttribute()
        {
            // Set by GitHub Actions, and by every other CI system worth naming.
            if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 })
            {
                Skip = "A timing ratio a shared runner cannot measure: correct code reads 2.54 there, " +
                       "between the correct band (1.48-2.20) and the fault's (2.74-3.17).";
            }
        }
    }
}
