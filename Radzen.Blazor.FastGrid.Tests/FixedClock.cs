using System;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// A clock that says what it is told to.
    /// </summary>
    /// <remarks>
    /// What §34's <c>Clock</c> parameter exists for, on the testing side: §9's protocol has nowhere to
    /// put a test of <em>yesterday</em> against a real clock, and against this one a relative date is an
    /// ordinary test of a pure rule. <c>GetLocalNow</c> reads <see cref="LocalTimeZone" />, so both are
    /// overridden - a fake that answered only <c>GetUtcNow</c> would be read in the machine's zone and
    /// the test would pass or fail by where it ran.
    /// </remarks>
    public sealed class FixedClock : TimeProvider
    {
        readonly TimeZoneInfo zone;
        DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
            zone = TimeZoneInfo.CreateCustomTimeZone("fixed", now.Offset, "fixed", "fixed");
        }

        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }
}
